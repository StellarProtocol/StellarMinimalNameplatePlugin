using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

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

    private static Harmony?        _harmony;
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

    internal static bool Install(string harmonyId, Action<string> log)
    {
        _log     = log;
        _harmony = new Harmony(harmonyId + ".nameplate");

        var hudCompType = FindType("Panda.ZGame.HudComp");
        if (hudCompType == null) { log("[MinimalNameplate] HudComp not found — patch skipped"); return false; }

        const BindingFlags anyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var target = FindNoArg(hudCompType, "setHudTitle", anyInstance);
        if (target == null) { log("[MinimalNameplate] setHudTitle not found — patch skipped"); return false; }
        if (!ResolveHudApi(log)) return false;

        try
        {
            _harmony.Patch(target, postfix: new HarmonyMethod(typeof(NameplateIconPatch), nameof(PostfixSetHudTitle)));
            log("[MinimalNameplate] setHudTitle postfix patched");

            // HP updates re-add blood/name via OnHpChanged() (NOT setHudTitle); combat state changes rebuild via
            // rebuildCharHudShow/OnCharHudChange/setEntCharHud. Patch each with the same re-hide postfix so a hidden
            // plate doesn't flash back mid-fight. (OnPartHpChanged/OnBuffTips take `in` structs → patching crashes the
            // Harmony trampoline, so they're reached transitively via these instead.)
            foreach (var rebuild in new[] { "OnHpChanged", "rebuildCharHudShow", "OnCharHudChange", "setEntCharHud" })
            {
                var m = FindNoArg(hudCompType, rebuild, anyInstance);
                if (m == null) { log($"[MinimalNameplate] {rebuild} not found"); continue; }
                try { _harmony.Patch(m, postfix: new HarmonyMethod(typeof(NameplateIconPatch), nameof(PostfixSetHudTitle))); log($"[MinimalNameplate] {rebuild} postfix patched"); }
                catch (Exception ex) { log($"[MinimalNameplate] {rebuild} patch failed: {ex.Message}"); }
            }

            // Buff icons render outside titleEntryDic_ → ClearAll can't remove them. Skip updateBuffTips for hidden
            // player plates (scoped via Names so NPC/monster buff tips are unaffected).
            var buffTarget = FindNoArg(hudCompType, "updateBuffTips", anyInstance);
            if (buffTarget != null)
            {
                try { _harmony.Patch(buffTarget, prefix: new HarmonyMethod(typeof(NameplateIconPatch), nameof(PrefixUpdateBuffTips))); log("[MinimalNameplate] updateBuffTips prefix patched"); }
                catch (Exception ex) { log($"[MinimalNameplate] updateBuffTips patch failed: {ex.Message}"); }
            }
            else log("[MinimalNameplate] updateBuffTips not found");

            // Track the game's per-entity plate hiding (tag/hide-and-seek, disappear, dialog, …) so the overlay mirrors it.
            InstallHudVisibleTracking(_harmony, log);
            return true;
        }
        catch (Exception ex)
        {
            log($"[MinimalNameplate] setHudTitle patch failed: {ex.Message}");
            _harmony = null;
            return false;
        }
    }

    internal static void Uninstall()
    {
        _harmony?.UnpatchSelf();
        _harmony          = null;
        _piRenderInstance = null; _miGetTitle = null; _piHudTitleDict = null; _fiHudTitleDict = null;
        _miContainsTitle  = null; _miRemoveTitle = null; _piEntryDic = null; _fiEntryDic = null;
        _piEntryTitleType = null; _piEntryValidText = null; _piCompId = null; _fiCompId = null;
        _compIdResolved   = false; _slotPlayerName = null; _errCount = 0;
        _hideMask.Clear();
    }

    private static bool ResolveHudApi(Action<string> log)
    {
        var enumType   = FindType("Panda.Hud.EHudTitleType");
        var renderType = FindType("Panda.Hud.HudTitleRender");
        var titleType  = FindType("Panda.Hud.HudTitle");
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

    // __instance = HudComp. Runs after the game (re)builds this plate — capture the name, then clear the plate.
    private static void PostfixSetHudTitle(object __instance)
    {
        if (!HidePlate) return;
        try
        {
            long compId = GetCompId(__instance);
            if (compId == 0) return;
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

    // Removes every entry currently on the plate (name, blood, tags, …) by RemoveTitle-ing each slot key.
    private static void ClearAll(object title)
    {
        var dic = _piEntryDic != null ? _piEntryDic.GetValue(title) : _fiEntryDic?.GetValue(title);
        var keys = dic?.GetType().GetProperty("Keys")?.GetValue(dic);
        if (keys == null) return;
        foreach (var k in WalkIl2Cpp(keys)) // WalkIl2Cpp materializes a list, so removing during the loop is safe
        {
            try { _miRemoveTitle?.Invoke(title, new object?[] { k }); }
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
            foreach (var title in WalkIl2Cpp(values))
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

    private static List<object> WalkIl2Cpp(object collection)
    {
        var res = new List<object>();
        var getEnum = FindNoArg(collection.GetType(), "GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
        var en = getEnum?.Invoke(collection, null);
        if (en == null) return res;
        var enT  = en.GetType();
        var move = FindNoArg(enT, "MoveNext", BindingFlags.Public | BindingFlags.Instance);
        var cur  = enT.GetProperty("Current");
        if (move == null || cur == null) return res;
        while ((bool)move.Invoke(en, null)!)
        {
            var v = cur.GetValue(en);
            if (v != null) res.Add(v);
        }
        return res;
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

    private static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName);
            if (t is not null) return t;
        }
        return null;
    }
}
