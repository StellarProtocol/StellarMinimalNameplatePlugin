using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using UnityEngine;

namespace Stellar.MinimalNameplate;

// Class-icon sprite resolution for the overlay.
//
// The icons are picked up PIGGYBACK: we never call the shared async loader (that would perturb other plugins' icon
// loads), we only look through the sprites the game has ALREADY loaded. The consequence is that a profession whose
// atlas is not loaded simply never resolves.
//
// PERF (2026-09-05): the old gate was `every 1 s while ANY tracked profession is uncached`, with no memo. A single
// unresolvable profession therefore pinned Resources.FindObjectsOfTypeAll<Sprite>() — a full loaded-object scan that
// materialises an Il2CppInterop managed wrapper per element — at 1 Hz for the entire session, which is the prime
// suspect for the owner's multi-second stalls and long frames. Now the scan gives up per profession
// (ClassIconRules.ShouldSeekSprite) and backs off 1 → 2 → 5 → 10 s (ClassIconRules.NextScanDelaySeconds), and a
// profession id we have never tracked re-arms both.
internal sealed partial class ClassIconOverlay
{
    // Per-profession icon cache (populated by the piggyback sprite scan — never the shared loader).
    private readonly Dictionary<int, (object tex, UvRect uv)> _iconCache = new();
    private readonly Dictionary<int, string> _profSprite = new();
    private double _scanTimer;

    private readonly Dictionary<int, int> _spriteMisses = new();
    private readonly HashSet<int> _seenProfessions = new();
    private int _consecutiveScanMisses;

    private double ScanDelaySeconds => ClassIconRules.NextScanDelaySeconds(_consecutiveScanMisses);

    // Called by RebuildPlayers for every tracked player. A profession id we have never seen re-arms the scan (drops
    // the per-profession memo and resets the interval) because the game may have loaded its atlas since we gave up.
    private void NoteProfession(int prof)
    {
        if (prof <= 0 || !_seenProfessions.Add(prof)) return;
        _spriteMisses.Clear();
        _consecutiveScanMisses = 0;
        _scanTimer = ScanDelaySeconds;   // scan on the next update
    }

    private bool AnyUncached()
    {
        foreach (var (_, prof) in _players)
            if (!_iconCache.ContainsKey(prof) && ClassIconRules.ShouldSeekSprite(prof, MissesFor(prof)))
                return true;
        return false;
    }

    private int MissesFor(int prof) => _spriteMisses.TryGetValue(prof, out var m) ? m : 0;

    private void ScanSprites()
    {
        try
        {
            var needed = CollectNeededSprites();
            if (needed.Count == 0) { _consecutiveScanMisses++; return; }

            int matched = MatchLoadedSprites(needed);
            foreach (var prof in needed.Values) BumpMiss(prof);
            _consecutiveScanMisses = matched > 0 ? 0 : _consecutiveScanMisses + 1;

            if (matched > 0 && Diag)
                _services.Log.Info($"[MinimalNameplate] sprite scan: matched={matched} cached={_iconCache.Count} needed={needed.Count}");
        }
        catch (Exception ex) { _services.Log.Warning($"[MinimalNameplate] ScanSprites error: {ex.Message}"); }
    }

    // Sprite name → profession id for every tracked profession we still want and have not given up on.
    private Dictionary<string, int> CollectNeededSprites()
    {
        var needed = new Dictionary<string, int>();
        foreach (var (_, prof) in _players)
        {
            if (_iconCache.ContainsKey(prof)) continue;
            int misses = MissesFor(prof);
            if (!ClassIconRules.ShouldSeekSprite(prof, misses)) continue;
            if (!_profSprite.TryGetValue(prof, out var nm))
            {
                try { var ip = _services.GameData.Combat.GetProfession(prof)?.IconPath; nm = string.IsNullOrEmpty(ip) ? null! : LastSeg(ip!); }
                catch { nm = null!; }
                if (!string.IsNullOrEmpty(nm)) _profSprite[prof] = nm;
            }
            if (!string.IsNullOrEmpty(nm)) needed[nm] = prof;
            else _spriteMisses[prof] = misses + 1;   // no icon path to look for — that counts as a miss
        }
        return needed;
    }

    // The expensive half: every all[i] materialises an Il2CppInterop wrapper. There is no cheaper name compare —
    // Sprite.name goes through the wrapper, and reading it off the raw il2cpp object would need unsafe pointer
    // walking plus a per-element string marshal anyway — so the memo and backoff above are what bound the cost.
    private int MatchLoadedSprites(Dictionary<string, int> needed)
    {
        var all = Resources.FindObjectsOfTypeAll<Sprite>();
        int scanned = all?.Length ?? 0, matched = 0;
        for (int i = 0; i < scanned; i++)
        {
            var s = all![i];
            if (s == null) continue;
            if (!needed.TryGetValue(s.name, out var prof) || _iconCache.ContainsKey(prof)) continue;
            var tex = s.texture;
            if (tex == null) continue;
            float tw = tex.width, th = tex.height;
            if (tw <= 0 || th <= 0) continue;
            var r = s.textureRect;
            _iconCache[prof] = (tex, new UvRect(r.x / tw, r.y / th, r.width / tw, r.height / th));
            matched++;
        }
        return matched;
    }

    private void BumpMiss(int prof)
    {
        if (_iconCache.ContainsKey(prof)) { _spriteMisses.Remove(prof); return; }
        _spriteMisses[prof] = MissesFor(prof) + 1;
    }

    private static string LastSeg(string p) { int i = p.LastIndexOf('/'); return i < 0 ? p : p.Substring(i + 1); }
}
