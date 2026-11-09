using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.MinimalNameplate;

// Revive-sealed marker: a dead player whose corpse carries BuffTable 2110057 "Weakened: Wish Sealed" (祈愿禁止) can't be
// revived by the Renewal Prayer role skill / the "Arcane! Prayer of Life" Imagine until it runs out. The buff has
// DeleteDead=false, so it stays on the corpse. We fill the badge purple instead of the dead grey (white class logo on
// top) and draw the name in purple with a "(42s)" countdown on its own line above it. See Knowledge Base\Entity-Attributes-DeadState.md (last section).
//
// PERF: the buff list is read ONLY for DEAD players and ONLY in the 2 Hz RebuildPlayers window (RefreshSeals); we cache
// the seal END time (server ms) per uuid. Per frame the draw only subtracts the server clock. The countdown text never
// re-bakes: the glyph pieces "(", "0"-"9", "s", ")" are baked ONCE into one texture (same BakeNameCpu path/font as
// names, white → tinted by _Color) and the timer is composed from per-piece UV quads.
internal sealed partial class ClassIconOverlay
{
    public static bool ShowReviveSealed = true;   // config minimalnameplate_showrevivesealed (Plugin.cs)

    private const int ReviveSealBuffId = 2110057;    // BuffTable "Weakened: Wish Sealed" — the ONLY 祈愿禁止 row
    private const long NoCountdown     = long.MaxValue; // seal end sentinel: Duration == 0 → marker without a timer
    private const long MinSaneServerMs = 1_600_000_000_000L; // below this the framework clock isn't synced yet

    // Badge fill replaces DeadBadgeColor (same 0.95 alpha); a bit deeper than the name colour so the white logo reads.
    private static readonly Color SealBadgeColor = new Color(0.55f, 0.27f, 0.86f, 0.95f); // rgb(140,70,220)
    private static readonly Color SealNameColor = new Color(0.780f, 0.608f, 1.00f, 1f);  // #C79BFF

    // uuid → seal end (server Unix ms, or NoCountdown). Rebuilt every RebuildPlayers (2 Hz), like _deadCache, so a
    // revive / seal expiry clears within 0.5 s even if the per-frame time check didn't already hide it.
    private readonly Dictionary<long, long> _sealEnd = new();
    private readonly HashSet<long> _sealLogged = new();   // one [ReviveSeal] log line per seal instance per uuid

    // Called at the end of RebuildPlayers (2 Hz). Only the drawn set (nearest MaxIcons) and only dead players pay the
    // buff-list read; IsDead is the rebuild-cached value the draw loop uses anyway.
    private void RefreshSeals()
    {
        _sealEnd.Clear();
        if (!ShowReviveSealed) return;
        int n = Math.Min(_players.Count, MaxIcons);
        for (int i = 0; i < n; i++)
        {
            long uuid = _players[i].uuid;
            if (IsDead(uuid) && TryReadSealEnd(uuid, out long end))
            {
                _sealEnd[uuid] = end;
                if (_sealLogged.Add(uuid))
                {
                    long now = _services.CombatSnapshot.ServerNowMs;
                    string remain = end == NoCountdown ? "no-countdown" : (now >= MinSaneServerMs ? $"{end - now}" : "clock-unsynced");
                    _services.Log.Info($"[MinimalNameplate] [ReviveSeal] detected uuid={uuid} remainMs={remain}");
                }
            }
            else _sealLogged.Remove(uuid);   // re-arm the log for this uuid's next seal
        }
    }

    // Per frame (allocation-free): is this player revive-sealed, and how many seconds are left (ceil)? secs = -1 means
    // "sealed, no countdown" (Duration 0, or the server clock isn't synced yet).
    private bool TrySealSecondsLeft(long uuid, out int secs)
    {
        secs = -1;
        if (!ShowReviveSealed || !_sealEnd.TryGetValue(uuid, out long end)) return false;
        if (end == NoCountdown) return true;
        long now = _services.CombatSnapshot.ServerNowMs;   // framework clock — NEVER reflect into ZServerTime
        if (now < MinSaneServerMs) return true;
        long remain = end - now;
        if (remain <= 0) return false;                      // expired → back to the plain dead look
        secs = (int)Math.Min((remain + 999) / 1000, 99999);
        return true;
    }

    // ── Buff-list read (2 Hz, dead players only) ──
    private bool          _sealReflResolved;
    private bool          _sealReflFailLogged;
    private PropertyInfo? _piSealBuffComp;               // ZEntity.BuffComp
    private PropertyInfo? _piSealList;                   // BuffComp.pBuffList_ / buffList_ — the FULL, unfiltered list
    private Type?         _sealListType;
    private PropertyInfo? _piSealListCount, _piSealListItem;
    private Type?         _sealItemType;
    private PropertyInfo? _piSealBaseId, _piSealCreate, _piSealDuration;
    private readonly object[] _sealIdxArgs = new object[1];   // reused — main thread only (see EntityRead.cs THREADING)

    private bool TryReadSealEnd(long uuid, out long end)
    {
        end = 0;
        var ent = GetEntityObj(uuid);
        if (ent == null) return false;
        try
        {
            if (!_sealReflResolved)
            {
                _sealReflResolved = true;
                _piSealBuffComp = StellarInterop.FindType("Panda.ZGame.ZEntity")?.GetProperty("BuffComp", BindingFlags.Public | BindingFlags.Instance);
            }
            var comp = _piSealBuffComp?.GetValue(ent);
            if (comp == null) { LogSealReflFail("buffComp"); return false; }
            if (_piSealList == null)
            {
                // Full list, not the display-filtered pShowedBuffList_ (Buff-Tracking.md §2): priority pBuffList_ > buffList_.
                int best = 99;
                foreach (var p in comp.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    int pr = p.Name switch { "pBuffList_" => 0, "buffList_" => 1, _ => 99 };
                    if (pr < best) { _piSealList = p; best = pr; }
                }
                if (_piSealList == null) { LogSealReflFail("list"); return false; }
            }
            var list = _piSealList.GetValue(comp);
            if (list == null) return false;
            var lt = list.GetType();
            if (!ReferenceEquals(lt, _sealListType))
            {
                // ZList<T>/Il2Cpp List aren't IEnumerable — Count + Item indexer (resolved once per list type).
                _sealListType    = lt;
                _piSealListCount = lt.GetProperty("Count");
                _piSealListItem  = lt.GetProperty("Item");
            }
            if (_piSealListCount == null || _piSealListItem == null) { LogSealReflFail("list Count/Item"); return false; }
            int cnt = Convert.ToInt32(_piSealListCount.GetValue(list));
            for (int i = 0; i < cnt; i++)
            {
                _sealIdxArgs[0] = i;
                var item = _piSealListItem.GetValue(list, _sealIdxArgs);
                if (item == null) continue;
                var it = item.GetType();
                if (!ReferenceEquals(it, _sealItemType))
                {
                    _sealItemType = it;
                    const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    _piSealBaseId   = it.GetProperty("BuffBaseId", any);   // IL2CPP fields are properties
                    _piSealCreate   = it.GetProperty("CreateTime", any);
                    _piSealDuration = it.GetProperty("Duration",   any);
                }
                if (_piSealBaseId == null || _piSealCreate == null || _piSealDuration == null) { LogSealReflFail("BuffItem fields"); return false; }
                if (Convert.ToInt32(_piSealBaseId.GetValue(item)) != ReviveSealBuffId) continue;
                long create = Convert.ToInt64(_piSealCreate.GetValue(item));    // server ms
                long dur    = Convert.ToInt64(_piSealDuration.GetValue(item));  // ms; 0 = no countdown
                end = (dur > 0 && create > 0) ? create + dur : NoCountdown;
                return true;
            }
        }
        catch (Exception ex) { LogSealReflFail(ex.InnerException?.Message ?? ex.Message); }
        return false;
    }

    private void LogSealReflFail(string what)
    {
        if (_sealReflFailLogged) return;
        _sealReflFailLogged = true;
        _services.Log.Info($"[MinimalNameplate] [ReviveSeal] buff-list read unresolved ({what}) — revive-sealed marker inactive for this case");
    }

    // ── Pre-baked countdown glyph pieces ──
    // Piece 0 = "(", 1..10 = '0'..'9', 11 = 's', 12 = ')'. Baked as ONE string with two spaces between pieces so a
    // piece's outline/pad never bleeds into its neighbour's cell; each piece is then a UV sub-rect quad.
    private static readonly string[] SealPieceText = { "(", "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "s", ")" };
    private const int SealPieceOpen = 0, SealPieceS = 11, SealPieceClose = 12;
    private Texture2D? _sealGlyphTex;
    private int        _sealGlyphH, _sealGlyphBaseline;   // texture height + baseline row (px from bottom)
    private readonly float[] _pieceAdv = new float[13], _pieceCellOff = new float[13], _pieceCellW = new float[13];
    private readonly Mesh?[] _pieceMesh = new Mesh?[13];
    private int  _sealGlyphMisses;
    private readonly int[] _digitBuf = new int[6];

    private bool EnsureSealGlyphs()
    {
        if (_sealGlyphTex != null) return true;
        if (_sealGlyphMisses >= MaxNullBakeMisses) return false;
        if (_nameBakesThisFrame >= MaxNameBakesPerFrame) return false;   // share the per-frame bake budget (HudPoc.cs)
        var font = HudFont() ?? NameFont();
        if (font == null) return false;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < SealPieceText.Length; i++) { if (i > 0) sb.Append("  "); sb.Append(SealPieceText[i]); }
        string all = sb.ToString();
        _nameBakesThisFrame++;
        var baked = BakeNameCpu(all);   // same path/font/outline as names; requests the glyphs into the atlas first
        if (baked.tex == null) { _sealGlyphMisses++; return false; }

        // Replay BakeNameCpu's pen walk to find each piece's pen span + ink extent (identical metrics → exact cells).
        int pad = NameOutlinePx + 1, minY = 0;
        foreach (char ch in all)
            if (font.GetCharacterInfo(ch, out var ci, NameFontPx, FontStyle.Normal)) minY = Mathf.Min(minY, ci.minY);
        float pen = pad;
        int piece = 0, k = 0;
        float start = pen, inkL = float.MaxValue, inkR = float.MinValue;
        for (int i = 0; i < all.Length; i++)
        {
            // Character i belongs to the piece if it is inside that piece's text span; separators are skipped.
            bool inPiece = piece < SealPieceText.Length && k < SealPieceText[piece].Length && all[i] == SealPieceText[piece][k];
            if (inPiece && k == 0) { start = pen; inkL = float.MaxValue; inkR = float.MinValue; }
            if (font.GetCharacterInfo(all[i], out var ci, NameFontPx, FontStyle.Normal))
            {
                if (inPiece && ci.maxX > ci.minX) { inkL = Mathf.Min(inkL, pen + ci.minX); inkR = Mathf.Max(inkR, pen + ci.maxX); }
                pen += ci.advance;
            }
            if (!inPiece) continue;
            if (++k < SealPieceText[piece].Length) continue;
            float left  = Mathf.Max(0f, Mathf.Min(start, inkL) - pad);
            float right = Mathf.Min(baked.w, Mathf.Max(pen, inkR) + pad);
            _pieceAdv[piece]     = pen - start;
            _pieceCellOff[piece] = left - start;            // cell left edge relative to the pen at piece start
            _pieceCellW[piece]   = right - left;
            _pieceMesh[piece]    = MakeQuad(new Rect(left / baked.w, 0f, (right - left) / baked.w, 1f));
            piece++; k = 0;
        }
        _sealGlyphTex      = baked.tex;
        _sealGlyphH        = baked.h;
        _sealGlyphBaseline = pad - minY;
        return true;
    }

    // Pen advance (baked px) of the "(Ns)" timer; 0 = nothing to draw (no countdown or glyphs not ready yet).
    private float SealTimerAdvancePx(int secs)
    {
        if (secs < 0 || !EnsureSealGlyphs()) return 0f;
        int nd = SplitDigits(secs);
        float adv = _pieceAdv[0] + _pieceAdv[SealPieceS] + _pieceAdv[SealPieceClose];
        for (int i = 0; i < nd; i++) adv += _pieceAdv[1 + _digitBuf[i]];
        return adv;
    }

    private int SplitDigits(int v)
    {
        int n = 0;
        do { _digitBuf[n++] = v % 10; v /= 10; } while (v > 0 && n < _digitBuf.Length);
        Array.Reverse(_digitBuf, 0, n);
        return n;
    }

    // Compose "(Ns)" on its own line, centred on lineCentre (the name's usual spot under the badge), same scale
    // (worldPerPx) and colour as the name. Every piece is a full-height cell of ONE baked string, so all pieces share
    // the baked baseline when drawn at the same vertical centre. Uses no name metrics at all (see SealTimerLayout).
    private void DrawSealTimer(Vector3 lineCentre, Quaternion camRot, float worldPerPx, int secs, Color color)
    {
        if (_hudCmd == null || _hudMat == null || _sealGlyphTex == null) return;
        var right = camRot * Vector3.right;
        float pen = SealTimerLayout.PenStartPx(SealTimerAdvancePx(secs));   // baked px from the line centre
        var scaleY = _sealGlyphH * worldPerPx;
        SetDrawMpb(_sealGlyphTex, color);

        int nd = SplitDigits(secs);
        for (int i = -1; i <= nd + 1; i++)
        {
            int p = i < 0 ? SealPieceOpen : (i < nd ? 1 + _digitBuf[i] : (i == nd ? SealPieceS : SealPieceClose));
            var mesh = _pieceMesh[p];
            if (mesh == null) continue;
            float cx = pen + _pieceCellOff[p] + _pieceCellW[p] * 0.5f;
            var center = lineCentre + right * (cx * worldPerPx);
            _hudCmd.DrawMesh(mesh, Matrix4x4.TRS(center, camRot, new Vector3(_pieceCellW[p] * worldPerPx, scaleY, 1f)), _hudMat, 0, 0, _mpb);
            pen += _pieceAdv[p];
        }
    }

    private void DestroyReviveSeal()
    {
        if (_sealGlyphTex != null) { try { UnityEngine.Object.Destroy(_sealGlyphTex); } catch { } _sealGlyphTex = null; }
        for (int i = 0; i < _pieceMesh.Length; i++)
        { if (_pieceMesh[i] != null) { try { UnityEngine.Object.Destroy(_pieceMesh[i]); } catch { } _pieceMesh[i] = null; } }
        _sealGlyphMisses = 0;
        _sealEnd.Clear();
    }
}
