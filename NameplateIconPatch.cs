using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;

namespace Stellar.MinimalNameplate;

/// <summary>
/// Hides the game's overhead nameplate for players so our own badge/name overlay replaces it. The plate is a
/// mesh-rendered HUD (Panda.Hud): entries live in HudComp → HudTitleRender.Instance.GetTitle(compId_) → HudTitle.
/// We postfix every method that (re)builds a plate and RemoveTitle every entry, and prefix updateBuffTips to skip
/// buff icons (which render outside the title dict). While hiding, we CAPTURE the display name from the plate so
/// the overlay can show it. See Knowledge Base\Nameplate-HUD.md.
/// </summary>
internal static partial class NameplateIconPatch
{
    public static bool HidePlate = false;   // hide the game's overhead plate for players (our overlay replaces it)

    private const int SlotPlayerNameValue = 0;  // EHudTitleType.EPlayerName — present only on player plates

    private static Action<string>? _log;

    private static PropertyInfo? _piRenderInstance;   // HudTitleRender.Instance (static)
    private static MethodInfo?   _miGetTitle;         // HudTitleRender.GetTitle(long)
    private static PropertyInfo? _piHudTitleDict;     // HudTitleRender.hudTitleDict_
    private static FieldInfo?    _fiHudTitleDict;
    private static MethodInfo?   _miContainsTitle;    // HudTitle.ContainsTitle(EHudTitleType)
    private static MethodInfo?   _miRemoveTitle;      // HudTitle.RemoveTitle(EHudTitleType)
    private static PropertyInfo? _piEntryDic;         // HudTitle.titleEntryDic_
    private static FieldInfo?    _fiEntryDic;
    private static PropertyInfo? _piEntryTitleType;   // HudTextBaseEntry.TitleType
    private static PropertyInfo? _piEntryValidText;   // HudTextBaseEntry.ValidText
    private static PropertyInfo? _piCompId;           // HudComp.compId_
    private static FieldInfo?    _fiCompId;
    private static bool          _compIdResolved;
    private static object?       _slotPlayerName;     // boxed EPlayerName

    // compId (== entity uuid) → the player's displayed name, captured from the EPlayerName entry's ValidText.
    public static readonly Dictionary<long, string> Names = new();

    internal static bool Install(Harmony harmony, Action<string> log)
    {
        _log = log;

        var hudCompType = StellarInterop.FindType("Panda.ZGame.HudComp");
        if (hudCompType == null) { log("[MinimalNameplate] HudComp not found — patch skipped"); return false; }

        const BindingFlags anyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var target = FindNoArg(hudCompType, "setHudTitle", anyInstance);
        if (target == null) { log("[MinimalNameplate] setHudTitle not found — patch skipped"); return false; }
        if (!ResolveHudApi(log)) return false;

        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(NameplateIconPatch), nameof(PostfixSetHudTitle)));
            log("[MinimalNameplate] setHudTitle postfix patched");

            // HP updates re-add blood/name via OnHpChanged() (NOT setHudTitle); combat state changes rebuild via
            // rebuildCharHudShow/OnCharHudChange/setEntCharHud. Patch each with the same re-hide postfix so a hidden
            // plate doesn't flash back mid-fight. (OnPartHpChanged/OnBuffTips take `in` structs → patching crashes the
            // Harmony trampoline, so they're reached transitively via these instead.)
            foreach (var rebuild in new[] { "OnHpChanged", "rebuildCharHudShow", "OnCharHudChange", "setEntCharHud" })
            {
                var m = FindNoArg(hudCompType, rebuild, anyInstance);
                if (m == null) { log($"[MinimalNameplate] {rebuild} not found"); continue; }
                try { harmony.Patch(m, postfix: new HarmonyMethod(typeof(NameplateIconPatch), nameof(PostfixSetHudTitle))); log($"[MinimalNameplate] {rebuild} postfix patched"); }
                catch (Exception ex) { log($"[MinimalNameplate] {rebuild} patch failed: {ex.Message}"); }
            }

            // Buff icons render outside titleEntryDic_ → ClearAll can't remove them. Skip updateBuffTips for hidden
            // player plates (scoped via Names so NPC/monster buff tips are unaffected).
            var buffTarget = FindNoArg(hudCompType, "updateBuffTips", anyInstance);
            if (buffTarget != null)
            {
                try { harmony.Patch(buffTarget, prefix: new HarmonyMethod(typeof(NameplateIconPatch), nameof(PrefixUpdateBuffTips))); log("[MinimalNameplate] updateBuffTips prefix patched"); }
                catch (Exception ex) { log($"[MinimalNameplate] updateBuffTips patch failed: {ex.Message}"); }
            }
            else log("[MinimalNameplate] updateBuffTips not found");

            // Track the game's per-entity plate hiding (tag/hide-and-seek, disappear, dialog, …) so the overlay mirrors it.
            InstallHudVisibleTracking(harmony, log);
            return true;
        }
        catch (Exception ex)
        {
            log($"[MinimalNameplate] setHudTitle patch failed: {ex.Message}");
            return false;
        }
    }

    // Harmony teardown is owned by IHarmonyHost (auto-unpatches on plugin dispose); reset only transient state here.
    internal static void Uninstall()
    {
        _piRenderInstance = null; _miGetTitle = null; _piHudTitleDict = null; _fiHudTitleDict = null;
        _miContainsTitle  = null; _miRemoveTitle = null; _piEntryDic = null; _fiEntryDic = null;
        _piEntryTitleType = null; _piEntryValidText = null; _piCompId = null; _fiCompId = null;
        _compIdResolved   = false; _slotPlayerName = null; _errCount = 0;
        _hideMask.Clear();
        _lastClearFrame.Clear();
    }

    private static bool ResolveHudApi(Action<string> log)
    {
        var enumType   = StellarInterop.FindType("Panda.Hud.EHudTitleType");
        var renderType = StellarInterop.FindType("Panda.Hud.HudTitleRender");
        var titleType  = StellarInterop.FindType("Panda.Hud.HudTitle");
        if (enumType == null || renderType == null || titleType == null)
        {
            log($"[MinimalNameplate] type resolve failed enum={enumType != null} render={renderType != null} title={titleType != null}");
            return false;
        }

        _slotPlayerName = Enum.ToObject(enumType, SlotPlayerNameValue);
        _piRenderInstance = renderType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

        foreach (var m in renderType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.Name == "GetTitle" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(long))
            { _miGetTitle = m; break; }

        foreach (var m in titleType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name == "ContainsTitle" && _miContainsTitle == null && m.GetParameters().Length == 1) _miContainsTitle = m;
            else if (m.Name == "RemoveTitle" && _miRemoveTitle == null && m.GetParameters().Length == 1) _miRemoveTitle = m;
        }

        const BindingFlags anyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        _piEntryDic = titleType.GetProperty("titleEntryDic_", anyInst);
        if (_piEntryDic == null) _fiEntryDic = titleType.GetField("titleEntryDic_", anyInst);
        _piHudTitleDict = renderType.GetProperty("hudTitleDict_", anyInst);
        if (_piHudTitleDict == null) _fiHudTitleDict = renderType.GetField("hudTitleDict_", anyInst);

        bool ok = _piRenderInstance != null && _miGetTitle != null && _miRemoveTitle != null
                  && (_piEntryDic != null || _fiEntryDic != null);
        log($"[MinimalNameplate] api resolve: inst={_piRenderInstance != null} getTitle={_miGetTitle != null} " +
            $"remove={_miRemoveTitle != null} contains={_miContainsTitle != null} " +
            $"entryDic={_piEntryDic != null || _fiEntryDic != null} titleDict={_piHudTitleDict != null || _fiHudTitleDict != null}");
        return ok;
    }

    // Per-compId frame stamp: this postfix is attached to FIVE methods (incl. OnHpChanged, which spams during
    // load-in), so the SAME plate can rebuild many times in one frame. We only need to capture+clear it once per
    // frame — a genuine rebuild on a LATER frame has a different Time.frameCount and still runs, so dedupe never
    // suppresses a needed clear across frames (any single-frame residual is caught next frame + by the 2 Hz
    // ReapplyAll sweep). Main-thread only (these postfixes run on the game thread) → Time.frameCount is safe here.
    private static readonly Dictionary<long, int> _lastClearFrame = new();

    // __instance = HudComp. Runs after the game (re)builds this plate — capture the name, then clear the plate.
    private static void PostfixSetHudTitle(object __instance)
    {
        if (!HidePlate) return;
        try
        {
            long compId = GetCompId(__instance);
            if (compId == 0) return;
            int frame = UnityEngine.Time.frameCount;
            if (_lastClearFrame.TryGetValue(compId, out var lf) && lf == frame) return;   // already handled this frame
            _lastClearFrame[compId] = frame;
            var render = _piRenderInstance!.GetValue(null);
            if (render == null) return;
            var title = _miGetTitle!.Invoke(render, new object[] { compId });
            if (title == null || !IsPlayerPlate(title)) return;
            CaptureName(title, compId);   // read the display name BEFORE clearing
            ClearAll(title);
        }
        catch (Exception ex) { LogStep("postfix", ex); }
    }

    private static bool IsPlayerPlate(object title)
        => _miContainsTitle == null || (bool)_miContainsTitle.Invoke(title, new object?[] { _slotPlayerName })!;

    // Skip buff-tip rendering (return false) for a HIDDEN player plate; scoped to players via Names.
    private static bool PrefixUpdateBuffTips(object __instance)
    {
        if (!HidePlate) return true;
        try { long id = GetCompId(__instance); if (id != 0 && Names.ContainsKey(id)) return false; }
        catch { }
        return true;
    }

    private static void CaptureName(object title, long compId)
    {
        try
        {
            var dic = _piEntryDic != null ? _piEntryDic.GetValue(title) : _fiEntryDic?.GetValue(title);
            var values = dic?.GetType().GetProperty("Values")?.GetValue(dic);
            if (values == null) return;
            foreach (var entry in WalkIl2Cpp(values))
            {
                var et = entry.GetType();
                _piEntryTitleType ??= et.GetProperty("TitleType", BindingFlags.Public | BindingFlags.Instance);
                _piEntryValidText ??= et.GetProperty("ValidText", BindingFlags.Public | BindingFlags.Instance);
                var tt = _piEntryTitleType?.GetValue(entry);
                if (tt != null && Convert.ToInt32(tt) == SlotPlayerNameValue)
                {
                    if (_piEntryValidText?.GetValue(entry) is string s && !string.IsNullOrEmpty(s)) Names[compId] = s;
                    return;
                }
            }
        }
        catch { }
    }

    // Reused single-element arg buffer for the RemoveTitle Invoke — avoids a per-key allocation in the clear loop.
    // Main-thread only, and RemoveTitle never re-enters our walk, so reuse across the loop is safe.
    private static readonly object?[] _removeArgs = new object?[1];

    // Removes every entry currently on the plate (name, blood, tags, …) by RemoveTitle-ing each slot key.
    private static void ClearAll(object title)
    {
        var dic = _piEntryDic != null ? _piEntryDic.GetValue(title) : _fiEntryDic?.GetValue(title);
        var keys = dic?.GetType().GetProperty("Keys")?.GetValue(dic);
        if (keys == null) return;
        // WalkIl2Cpp returns the shared _walkBuf; we consume it fully here (RemoveTitle doesn't re-walk), so removing
        // during the loop is safe — the game dict isn't the thing being iterated.
        foreach (var k in WalkIl2Cpp(keys))
        {
            try { _removeArgs[0] = k; _miRemoveTitle?.Invoke(title, _removeArgs); }
            catch { }
        }
    }

    /// <summary>Re-hide (or nothing, if not hiding) every live player plate — call from the periodic sweep so plates
    /// that came back via an unpatched rebuild path get cleared without waiting for a scene/AOI rebuild.</summary>
    internal static void ReapplyAll()
    {
        if (!HidePlate || _piRenderInstance == null || (_piHudTitleDict == null && _fiHudTitleDict == null)) return;
        try
        {
            var render = _piRenderInstance.GetValue(null);
            if (render == null) return;
            var dict = _piHudTitleDict != null ? _piHudTitleDict.GetValue(render) : _fiHudTitleDict!.GetValue(render);
            var values = dict?.GetType().GetProperty("Values")?.GetValue(dict);
            if (values == null) return;
            // Fresh list (not the shared _walkBuf): each iteration calls ClearAll → WalkIl2Cpp(keys), a nested walk
            // that reuses _walkBuf. Materializing the outer walk first keeps that nesting safe. See WalkIl2Cpp.
            foreach (var title in WalkIl2CppFresh(values))
            {
                try { if (IsPlayerPlate(title)) ClearAll(title); }
                catch (Exception ex) { LogStep("reapply", ex); }
            }
        }
        catch (Exception ex) { LogStep("reapplyAll", ex); }
    }

    private static long GetCompId(object hudComp)
    {
        if (!_compIdResolved)
        {
            var t = hudComp.GetType();
            _piCompId = t.GetProperty("compId_", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_piCompId == null) _fiCompId = t.GetField("compId_", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _compIdResolved = true;
        }
        var raw = _piCompId != null ? _piCompId.GetValue(hudComp) : _fiCompId?.GetValue(hudComp);
        return raw == null ? 0 : Convert.ToInt64(raw);
    }

    // Reflection walk of an Il2CppSystem key/value collection. Same discipline as ClassIconOverlay.EntityRead.cs:
    // the result buffer is REUSED (Clear, not new) and the enumerator members are resolved once per collection Type
    // (re-resolved only when the Type changes) instead of once per call. Walked twice per PostfixSetHudTitle
    // (CaptureName on values + ClearAll on keys) and once per title in ReapplyAll, so at load-in this ran hot.
    //
    // ⚠️ RE-ENTRANCY (this is a STATIC class → the buffer/cache below are shared): only ONE walk may be live at a
    // time on _walkBuf. CaptureName (values) then ClearAll (keys) run SEQUENTIALLY in PostfixSetHudTitle — safe, each
    // consumes _walkBuf fully before the next walk. But ReapplyAll walks the title VALUES and, for EACH title, calls
    // ClearAll → which walks that title's KEYS: a NESTED walk. If ReapplyAll's outer loop iterated _walkBuf, the
    // inner ClearAll's Clear() would clobber it mid-iteration. So ReapplyAll uses WalkIl2CppFresh (a one-off new list,
    // 2 Hz → allocation negligible) for its OUTER walk and leaves _walkBuf to the inner leaf walk. The shared type
    // cache is still safe because the outer list is fully materialized before any nested walk mutates the cache.
    private static readonly List<object> _walkBuf = new();
    private static Type?         _walkType;
    private static MethodInfo?   _walkGetEnum;
    private static MethodInfo?   _walkMoveNext;
    private static PropertyInfo? _walkCurrent;

    // Reused buffer walk — for the LEAF walks (CaptureName/ClearAll), which are never nested inside one another.
    private static List<object> WalkIl2Cpp(object collection)
    {
        _walkBuf.Clear();
        WalkInto(collection, _walkBuf);
        return _walkBuf;
    }

    // Fresh-list walk — for the ONE nesting site (ReapplyAll's outer title loop), so the inner ClearAll's reuse of
    // _walkBuf can't corrupt the outer enumeration. See the RE-ENTRANCY note above.
    private static List<object> WalkIl2CppFresh(object collection)
    {
        var res = new List<object>();
        WalkInto(collection, res);
        return res;
    }

    private static void WalkInto(object collection, List<object> into)
    {
        var t = collection.GetType();
        if (!ReferenceEquals(t, _walkType))
        {
            _walkType = t;
            _walkGetEnum = FindNoArg(t, "GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
            _walkMoveNext = null;
            _walkCurrent = null;
        }
        var en = _walkGetEnum?.Invoke(collection, null);
        if (en == null) return;
        if (_walkMoveNext == null || _walkCurrent == null)
        {
            var enT = en.GetType();
            _walkMoveNext = FindNoArg(enT, "MoveNext", BindingFlags.Public | BindingFlags.Instance);
            _walkCurrent = enT.GetProperty("Current");
            if (_walkMoveNext == null || _walkCurrent == null) return;
        }
        while ((bool)_walkMoveNext.Invoke(en, null)!)
        {
            var v = _walkCurrent.GetValue(en);
            if (v != null) into.Add(v);
        }
    }

    private static int _errCount;
    private static void LogStep(string step, Exception ex)
    {
        if (_errCount >= 10) return;
        _errCount++;
        var inner = ex.InnerException ?? ex;
        _log?.Invoke($"[MinimalNameplate] {step} error: {inner.Message}");
    }

    private static MethodInfo? FindNoArg(Type t, string name, BindingFlags flags)
    {
        foreach (var m in t.GetMethods(flags))
            if (m.Name == name && m.GetParameters().Length == 0) return m;
        return null;
    }
}
