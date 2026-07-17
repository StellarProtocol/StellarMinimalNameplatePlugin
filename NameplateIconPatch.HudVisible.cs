using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Stellar.MinimalNameplate;

// Tracks the game's OWN per-entity nameplate hiding so the overlay can mirror it — e.g. TAG / hide-and-seek hides the
// hider's plate (EHudVisibleSource.EHideSeek), and disappear/dialog/etc. hide individual plates too. There is no
// public getter for that per-entity invisible mask, so instead we postfix the two setters the game uses:
//   HudUtility.SetHudInVisible(ZEntity|ZModel, EHudVisibleSource source, bool isHide)      — whole plate off from `source`
//   HudUtility.SetHudSwitchValue(ZEntity|ZModel, EHudMaskType, EHudVisibleSource, isHide)  — masks one slot; only the
//                                                                                            name slot (Title) matters
// and keep a per-uuid bitmask of the active hide-sources. The overlay skips any uuid whose mask (minus the sources it
// deliberately overrides) is non-zero. Fail-open: unresolved → nothing tracked (overlay draws as before).
internal static partial class NameplateIconPatch
{
    // uuid -> bitmask of EHudVisibleSource values currently hiding this entity's plate (bit = 1 << source).
    private static readonly Dictionary<long, int> _hideMask = new();

    // Sources we do NOT mirror, because the overlay handles them itself:
    //   ELod(0), EDistance(3) — the overlay intentionally tracks players past the game's nameplate range.
    //   EDead(5)              — dead players are shown deliberately (red name + grayed badge).
    private const int ExcludedSources = (1 << 0) | (1 << 3) | (1 << 5);

    private const int MaskTypeTitle = 0;   // EHudMaskType.Title — the name slot our overlay stands in for

    /// <summary>True if the game has hidden this player's plate for a source we mirror (tag, disappear, dialog, …).</summary>
    public static bool IsPlateHiddenByGame(long uuid)
        => _hideMask.TryGetValue(uuid, out var m) && (m & ~ExcludedSources) != 0;

    internal static void InstallHudVisibleTracking(Harmony harmony, Action<string> log)
    {
        var hudUtil = FindType("Panda.Hud.HudUtility");
        if (hudUtil == null) { log("[MinimalNameplate] HudUtility not found — per-entity hide mirror off"); return; }

        int patched = 0;
        foreach (var m in hudUtil.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            var ps = m.GetParameters();
            try
            {
                if (m.Name == "SetHudInVisible" && ps.Length == 3)
                {
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(NameplateIconPatch), nameof(PostfixSetHudInVisible)));
                    patched++;
                }
                else if (m.Name == "SetHudSwitchValue" && ps.Length == 4)
                {
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(NameplateIconPatch), nameof(PostfixSetHudSwitchValue)));
                    patched++;
                }
            }
            catch (Exception ex) { log($"[MinimalNameplate] {m.Name} patch failed: {ex.Message}"); }
        }
        log($"[MinimalNameplate] per-entity hide mirror: {patched} setter(s) patched");
    }

    // SetHudInVisible(ZEntity|ZModel __0, EHudVisibleSource __1, bool __2) — whole plate invisible from `source`.
    private static void PostfixSetHudInVisible(object __0, object __1, bool __2)
    {
        try
        {
            long uuid = ResolveUuid(__0);
            if (uuid != 0) ApplyHide(uuid, Convert.ToInt32(__1), __2);
        }
        catch { }
    }

    // SetHudSwitchValue(ZEntity|ZModel __0, EHudMaskType __1, EHudVisibleSource __2, bool __3) — only the name slot counts.
    private static void PostfixSetHudSwitchValue(object __0, object __1, object __2, bool __3)
    {
        try
        {
            if (Convert.ToInt32(__1) != MaskTypeTitle) return;
            long uuid = ResolveUuid(__0);
            if (uuid != 0) ApplyHide(uuid, Convert.ToInt32(__2), __3);
        }
        catch { }
    }

    private static void ApplyHide(long uuid, int source, bool isHide)
    {
        int bit = 1 << source;
        _hideMask.TryGetValue(uuid, out var m);
        int nm = isHide ? (m | bit) : (m & ~bit);
        if (nm == 0) _hideMask.Remove(uuid); else _hideMask[uuid] = nm;
    }

    // Both ZEntity and ZModel expose a long Uuid (on the shared ZPureEntity base) — resolve per-arg via reflection.
    private static long ResolveUuid(object? arg)
    {
        if (arg == null) return 0;
        try
        {
            var pi = arg.GetType().GetProperty("Uuid",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            var v = pi?.GetValue(arg);
            return v == null ? 0 : Convert.ToInt64(v);
        }
        catch { return 0; }
    }
}
