using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;

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

    // Sources we DELIBERATELY mirror — the intentional, gameplay-meaningful per-player hides where the game removes an
    // individual plate and our overlay should vanish too. This is an ALLOWLIST: every other source is IGNORED
    // (fail-open → draw). Why an allowlist and not a denylist:
    //   The mask is built by post-fixing the game's SetHudInVisible/SetHudSwitchValue setters, so we only ever see a
    //   bit *set* when the game calls that setter. At login the game runs a scene/layer transition and streams players
    //   in — it sets transient hides (ESwitchLayer(7) layer switch, ERide(11) mounted, ERevive(9)) and then CLEARS
    //   them by rebuilding/re-registering the plate, NOT by calling the setter with isHide=false. We record the set,
    //   never the clear → the bit sticks → the badge is skipped until relog. A denylist mirrored all those transient
    //   sources; an allowlist can't get stuck, because an unexpected/uncleared source outside it never suppresses a
    //   badge. Sources left OUT on purpose: ELod(0)/EDistance(3) (overlay tracks past nameplate range itself),
    //   EDead(5) (dead shown deliberately — red name + grayed badge), EPhoto(1)/ECutScene(2) (already covered by the
    //   global HUD switch), ESwitchLayer(7)/ERevive(9)/ESelfCtrl(10)/ERide(11) (transient, cleared via unobserved
    //   rebuild paths → the stuck-bit-at-login bug).
    //   EDialog(6)    — the player is in an NPC dialog / interaction.
    //   EDisappear(8) — the player is deliberately made to disappear (stealth/vanish gameplay).
    //   EHideSeek(12) — TAG / hide-and-seek hides the hider's plate. NOTE: hide-and-seek's ACTUAL hide path is the
    //                   model-visibility HideSeekComponent (mirrored live in ClassIconOverlay.Visibility.cs via
    //                   IsHiddenByHideSeek); this EHideSeek HUD-source bit is effectively never set by the game and is
    //                   kept in MirroredSources only for completeness — do not remove it.
    private const int MirroredSources = (1 << 6) | (1 << 8) | (1 << 12);

    private const int MaskTypeTitle = 0;   // EHudMaskType.Title — the name slot our overlay stands in for

    /// <summary>True if the game has hidden this player's plate for a source we mirror (tag, disappear, dialog).</summary>
    public static bool IsPlateHiddenByGame(long uuid)
        => _hideMask.TryGetValue(uuid, out var m) && (m & MirroredSources) != 0;

    /// <summary>Drop all tracked hide state — call on logout so a fresh session never inherits a stale bit.</summary>
    public static void ClearHideMask() => _hideMask.Clear();

    internal static void InstallHudVisibleTracking(Harmony harmony, Action<string> log)
    {
        var hudUtil = StellarInterop.FindType("Panda.Hud.HudUtility");
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
