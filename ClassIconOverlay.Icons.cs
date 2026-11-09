using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using UnityEngine;

namespace Stellar.MinimalNameplate;

// Class-icon texture resolution for the overlay.
//
// SOURCE (2026-11-09, fix/class-icon-load): the framework loader IGameAssets.LoadProfessionIcon is primary. It consumes
// the game's UniTask exactly ONCE inside the framework (GameAssetsService.PollLoadingSlot) and stores the result in a
// shared per-profession slot, so every plugin calling it gets the same cached texture — TargetLens, DungeonHub and
// CombatMeter already do. The old "never call it alongside CombatMeter" warning predates that and is OUTDATED.
//
// Why the change: the overlay used to ONLY piggyback (scan the sprites someone else had loaded). It gave up on a
// profession after 3 fruitless scans and re-armed only when a never-seen profession entered the AOI, CombatMeter's warm
// loop never covers professions 12-15, and a cached game-owned texture was never revalidated — so in a crowd a badge
// could sit glyph-less for the whole session. Now:
//   * every rebuild (2 Hz) asks the framework for each DISTINCT tracked profession still missing (cheap dict lookup);
//   * every rebuild checks each cached texture is still alive and evicts + re-requests a dead one;
//   * the piggyback scan is only a fallback (framework pending > 10 s, or it handed back a destroyed texture), backed
//     off 2 → 5 → 10 → 30 s, and only while such a profession is actually tracked — never a permanent give-up.
//
// PERF (2026-09-05, still binding): Resources.FindObjectsOfTypeAll<Sprite>() materialises an Il2CppInterop wrapper per
// loaded sprite — the scan must stay rare. The per-frame draw path only reads _iconCache.
internal sealed partial class ClassIconOverlay
{
    // Per-profession icon cache read by the draw path (AppendHudBadge). Only live textures live here.
    private readonly Dictionary<int, (object tex, UvRect uv)> _iconCache = new();
    private readonly Dictionary<int, string> _profSprite = new();

    private sealed class IconState
    {
        public string Source = "fw";      // where the cached texture came from: "fw" | "scan"
        public double PendingSince = -1;  // _iconClock when this profession started waiting (-1 = not pending)
        public bool FwDead;               // the framework's last answer was a DESTROYED texture
        public bool HasUv; public UvRect Uv;   // last uv we built the icon quad for (a change rebuilds the mesh)
        public string LastLogged = "";    // last [IconDiag] state, so we log changes only
    }
    private readonly Dictionary<int, IconState> _iconState = new();
    private readonly HashSet<int> _trackedProfs = new();
    private readonly Dictionary<string, int> _scanNeeded = new();

    private double _iconClock;            // advanced by TickThrottles; frozen while logged out
    private double _nextScanAt;
    private int _consecutiveScanMisses;
    private double _nextMissingSummaryAt;

    private const double MissingSummaryIntervalS = 60.0;

    // Called at the end of RebuildPlayers (2 Hz). Distinct professions only — never per badge, never per frame.
    private void RefreshIcons()
    {
        _trackedProfs.Clear();
        foreach (var (_, prof) in _players) if (prof > 0) _trackedProfs.Add(prof);

        _scanNeeded.Clear();
        foreach (var prof in _trackedProfs)
        {
            if (!_iconState.TryGetValue(prof, out var st)) _iconState[prof] = st = new IconState();

            if (_iconCache.TryGetValue(prof, out var ci))
            {
                if (IsLiveTexture(ci.tex)) continue;
                _iconCache.Remove(prof);
                LogIconState(prof, st, st.Source, "dead-evicted");
            }

            if (TryFrameworkIcon(prof, st)) continue;

            if (st.PendingSince < 0) st.PendingSince = _iconClock;
            if (ClassIconRules.ShouldFallbackScan(prof, st.FwDead, _iconClock - st.PendingSince))
            {
                LogIconState(prof, st, "scan", "fallback");
                var nm = SpriteNameFor(prof);
                if (!string.IsNullOrEmpty(nm)) _scanNeeded[nm!] = prof;
            }
            else LogIconState(prof, st, "fw", "pending");
        }

        if (_scanNeeded.Count == 0) _consecutiveScanMisses = 0;   // nothing needs the fallback → next need starts fresh
        else if (_iconClock >= _nextScanAt) ScanSprites();

        LogMissingSummary();
    }

    // One framework poll. The framework does its own async load + retry/backoff; null just means "not yet".
    private bool TryFrameworkIcon(int prof, IconState st)
    {
        object? tex;
        UvRect uv;
        try { tex = _services.GameAssets.LoadProfessionIcon(prof, out uv); }
        catch (Exception ex) { _services.Log.Warning($"[MinimalNameplate] LoadProfessionIcon({prof}) error: {ex.Message}"); return false; }
        if (tex == null) { st.FwDead = false; return false; }
        if (!IsLiveTexture(tex))
        {
            // The framework never revalidates a Loaded slot, so it will keep handing back this dead texture — the
            // scan fallback takes over at once (ClassIconRules.ShouldFallbackScan).
            st.FwDead = true;
            return false;
        }
        st.FwDead = false;
        CacheIcon(prof, st, tex, uv, "fw");
        return true;
    }

    private void CacheIcon(int prof, IconState st, object tex, UvRect uv, string source)
    {
        // The icon quad mesh is cached per profession with its uv baked in (HudPoc IconQuad); a different atlas rect
        // after a reload would otherwise sample the wrong cell.
        if (st.HasUv && !SameUv(st.Uv, uv)) InvalidateIconQuad(prof);
        st.HasUv = true; st.Uv = uv;
        st.Source = source;
        st.PendingSince = -1;
        _iconCache[prof] = (tex, uv);
        LogIconState(prof, st, source, "loaded");
    }

    // Draw-path escape hatch: a texture destroyed between rebuilds threw while drawing. Drop it here (the rebuild's
    // liveness check never sees it now, so this is where dead-evicted is logged); the next rebuild re-requests.
    private void DropIcon(int prof)
    {
        if (!_iconCache.Remove(prof)) return;
        if (_iconState.TryGetValue(prof, out var st)) LogIconState(prof, st, st.Source, "dead-evicted");
    }

    // A destroyed UnityEngine.Object still has a managed wrapper; Unity's == null (op_Equality → native alive check)
    // is what reports it dead. Pointer == Zero / a collected wrapper (accessor throws) are dead too.
    private static bool IsLiveTexture(object? o)
    {
        if (o is not Texture2D t) return false;
        try { return t.Pointer != IntPtr.Zero && t != null; }
        catch { return false; }
    }

    private void InvalidateIconQuad(int prof)
    {
        if (!_iconQuads.Remove(prof, out var m)) return;
        try { UnityEngine.Object.Destroy(m); } catch { }
    }

    private static bool SameUv(UvRect a, UvRect b) => a.X == b.X && a.Y == b.Y && a.W == b.W && a.H == b.H;

    private string? SpriteNameFor(int prof)
    {
        if (_profSprite.TryGetValue(prof, out var nm)) return nm;
        try { var ip = _services.GameData.Combat.GetProfession(prof)?.IconPath; nm = string.IsNullOrEmpty(ip) ? null : LastSeg(ip!); }
        catch { nm = null; }
        if (!string.IsNullOrEmpty(nm)) _profSprite[prof] = nm!;
        return nm;
    }

    // Fallback piggyback scan over _scanNeeded (sprite name → profession). Backed off; see header.
    private void ScanSprites()
    {
        // #1 perf: time the WHOLE scan body, plus the FindObjectsOfTypeAll call separately (via the out params below).
        long __t0 = PerfDiag ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        double __findAllMs = 0; int __sprites = 0, __matched = 0;
        _perfScannedThisFrame = true;
        try
        {
            __matched = MatchLoadedSprites(_scanNeeded, out __findAllMs, out __sprites);
            _consecutiveScanMisses = __matched > 0 ? 0 : _consecutiveScanMisses + 1;
            if (__matched > 0 && Diag)
                _services.Log.Info($"[MinimalNameplate] sprite scan: matched={__matched} cached={_iconCache.Count} needed={_scanNeeded.Count}");
        }
        catch (Exception ex) { _consecutiveScanMisses++; _services.Log.Warning($"[MinimalNameplate] ScanSprites error: {ex.Message}"); }
        finally
        {
            _nextScanAt = _iconClock + ClassIconRules.NextScanDelaySeconds(_consecutiveScanMisses);
            if (PerfDiag) PerfLogSpriteScan(__t0, __findAllMs, __sprites, _scanNeeded.Count, __matched);
        }
    }

    // The expensive half: every all[i] materialises an Il2CppInterop wrapper. There is no cheaper name compare —
    // Sprite.name goes through the wrapper, and reading it off the raw il2cpp object would need unsafe pointer
    // walking plus a per-element string marshal anyway — so the fallback gating + backoff are what bound the cost.
    private int MatchLoadedSprites(Dictionary<string, int> needed, out double findAllMs, out int spriteCount)
    {
        // #1 perf: isolate just the FindObjectsOfTypeAll cost (each element materialises an Il2CppInterop wrapper).
        long __t = PerfDiag ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var all = Resources.FindObjectsOfTypeAll<Sprite>();
        findAllMs = PerfDiag ? PerfMsSince(__t) : 0;
        int scanned = all?.Length ?? 0, matched = 0;
        spriteCount = scanned;
        for (int i = 0; i < scanned; i++)
        {
            var s = all![i];
            if (s == null) continue;
            if (!needed.TryGetValue(s.name, out var prof) || _iconCache.ContainsKey(prof)) continue;
            var tex = s.texture;
            if (!IsLiveTexture(tex)) continue;
            float tw = tex.width, th = tex.height;
            if (tw <= 0 || th <= 0) continue;
            var r = s.textureRect;
            CacheIcon(prof, _iconState[prof], tex, new UvRect(r.x / tw, r.y / th, r.width / tw, r.height / th), "scan");
            matched++;
        }
        return matched;
    }

    // [IconDiag] — one Info line per profession state CHANGE, so the next "glyph-less badge" report names its cause.
    private void LogIconState(int prof, IconState st, string source, string state)
    {
        string key = source + "|" + state;
        if (st.LastLogged == key) return;
        st.LastLogged = key;
        string uv = state == "loaded" ? $" uv=({st.Uv.X:F3},{st.Uv.Y:F3},{st.Uv.W:F3},{st.Uv.H:F3})" : "";
        string fw = st.FwDead ? " fwDead=1" : "";
        _services.Log.Info($"[MinimalNameplate] [IconDiag] prof={prof} source={source} state={state}{uv}{fw}");
    }

    // Once a minute while any tracked profession has no icon.
    private void LogMissingSummary()
    {
        if (_iconClock < _nextMissingSummaryAt) return;
        List<int>? missing = null;
        foreach (var p in _trackedProfs) if (!_iconCache.ContainsKey(p)) (missing ??= new List<int>()).Add(p);
        if (missing == null) return;
        _nextMissingSummaryAt = _iconClock + MissingSummaryIntervalS;
        missing.Sort();
        var tracked = new List<int>(_trackedProfs); tracked.Sort();
        _services.Log.Info($"[MinimalNameplate] [IconDiag] missing=[{string.Join(",", missing)}] tracked=[{string.Join(",", tracked)}]");
    }

    private static string LastSeg(string p) { int i = p.LastIndexOf('/'); return i < 0 ? p : p.Substring(i + 1); }
}
