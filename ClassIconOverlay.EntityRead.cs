using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MinimalNameplate;

// Live reads off the game's entity layer for the class-icon overlay: the ZEntity attribute store (profession, HP) and
// the ZEntityMgr entity dictionary walk.
//
// PERF CONTRACT (2026-09-05 — owner-reported stutter + a 14 MB/session Player.log):
//
//  * THREADING. Every member here is reachable ONLY from ClassIconOverlay.OnUpdate, which the Unity render callbacks
//    (LateUpdate / Camera.onPreCull / RenderPipelineManager.beginCameraRendering / Canvas.willRenderCanvases) drive on
//    the MAIN thread. The plugin starts no thread, task or timer, and nothing outside those callbacks calls in (the
//    only other entry points, SetEnabled/Dispose, come from the overlay's own uGUI window). That is what makes the
//    reusable argument arrays below safe: they are written immediately before each Invoke, and no call in this file
//    re-enters another one mid-invoke.
//
//  * ATTRIBUTE TYPE. GetAttr<T> must be closed over the attribute's real storage type (ClassIconRules.AttrClrType).
//    The wrong T does not throw — the game Debug.LogErrors one `arr type err` line, WITH a stack capture, PER CALL,
//    and returns 0. Reading the Int32 profession through the long closure at 2 Hz per AOI player produced 42,353 such
//    lines in one of the owner's sessions and meant the live profession read never once succeeded.
//
//  * DEAD STATE is resolved at most once per uuid per RebuildPlayers (2 Hz), never per drawn badge per frame.
internal sealed partial class ClassIconOverlay
{
    private const int AttrDeadType = 78;   // EAttrType.AttrDeadType

    // Reused reflection argument buffers — see THREADING above. Rewritten in place before each Invoke.
    private readonly object[] _attrArgs = new object[2];
    private readonly object[] _entityArgs = new object[1];
    private static readonly object BoxedTrue = true;   // GetAttr's `checkInherit` — boxed once, not per call

    // Dead state for the CURRENT rebuild window; RebuildPlayers clears it, so it is at most 0.5 s old. The draw loop
    // asks up to 3× per badge per frame (head offset, badge tint, name colour); resolving it live there cost roughly
    // 3,800 reflection invokes and 7,600 allocations per second at 21 tracked players × 60 fps. The deliberate
    // trade-off: a death (or a revive) shows on the badge up to 0.5 s late.
    private readonly Dictionary<long, bool> _deadCache = new();

    private bool IsDead(long uuid)
    {
        if (_deadCache.TryGetValue(uuid, out var cached)) return cached;
        bool dead = ResolveDead(uuid);
        _deadCache[uuid] = dead;
        return dead;
    }

    // Dead detection. PRIMARY is a LIVE read off the ZEntity (GetAttr<long>(AttrHp/AttrMaxHp)) — the entity's own
    // attribute store, which updates on revive (unlike the EntityDetail snapshot, stale at dt=2 / hp stripped).
    private bool ResolveDead(long uuid)
    {
        if (TryLiveDead(uuid, out var live)) return live;
        try
        {
            var attrs = _services.EntityDetail.GetAttributes(new EntityId(uuid));
            if (attrs != null)
            {
                if (attrs.TryGetValue(ClassIconRules.AttrHpId, out var hp)) return hp <= 0;
                if (attrs.TryGetValue(AttrDeadType, out var dt)) return dt > 0;
            }
        }
        catch { }
        try { var v = _services.CombatLookup.GetVitals(new EntityId(uuid)); return v.IsKnown && v.MaxHp > 0 && v.Hp <= 0; }
        catch { return false; }
    }

    private bool TryLiveDead(long uuid, out bool dead)
    {
        dead = false;
        if (!EnsureAttrApi()) return false;
        var ent = GetEntityObj(uuid);
        if (ent == null) return false;
        try
        {
            long maxhp = Convert.ToInt64(InvokeAttr(_miGetAttrLong!, ent, _attrMaxHpBox!));
            if (maxhp <= 0) return false;
            dead = Convert.ToInt64(InvokeAttr(_miGetAttrLong!, ent, _attrHpBox!)) <= 0;
            return true;
        }
        catch { return false; }
    }

    private bool TryLiveProfession(long uuid, out int prof)
    {
        prof = 0;
        if (!EnsureAttrApi() || _attrProfBox == null) return false;
        var ent = GetEntityObj(uuid);
        if (ent == null) return false;
        // Int32 closure. Through the long one this call logged `arr type err` and returned 0 every time, so the whole
        // live path silently fell through to the EntityDetail snapshot in ResolveProfessionLive below.
        try { prof = Convert.ToInt32(InvokeAttr(_miGetAttrInt!, ent, _attrProfBox)); return prof > 0; }
        catch { return false; }
    }

    private object? InvokeAttr(MethodInfo getAttr, object entity, object attrBox)
    {
        _attrArgs[0] = attrBox;
        _attrArgs[1] = BoxedTrue;   // checkInherit
        return getAttr.Invoke(entity, _attrArgs);
    }

    // ZEntity.GetAttr<T>(EAttrType, bool checkInherit) — resolved once, with boxed enum keys. TWO closures are kept
    // because the game's attribute store is typed PER ATTRIBUTE (ClassIconRules.AttrClrType): HP/max-HP are Int64,
    // the profession id is Int32.
    private MethodInfo? _miGetAttrLong;
    private MethodInfo? _miGetAttrInt;
    private object? _attrHpBox;
    private object? _attrMaxHpBox;
    private object? _attrProfBox;
    private bool _attrApiResolved;
    private bool _attrApiOk;

    private bool EnsureAttrApi()
    {
        if (_attrApiResolved) return _attrApiOk;
        _attrApiResolved = true;
        try
        {
            var entT = StellarInterop.FindType("Panda.ZGame.ZEntity");
            var attrEnum = StellarInterop.FindType("Zproto.EAttrType");
            if (entT != null && attrEnum != null)
            {
                CloseGetAttr(entT, attrEnum);
                _attrHpBox = Enum.ToObject(attrEnum, ClassIconRules.AttrHpId);
                _attrMaxHpBox = Enum.ToObject(attrEnum, ClassIconRules.AttrMaxHpId);
                _attrProfBox = Enum.ToObject(attrEnum, ClassIconRules.AttrProfessionId);
                _attrApiOk = _miGetAttrLong != null && _miGetAttrInt != null && _attrHpBox != null && _attrMaxHpBox != null;
            }
        }
        catch (Exception ex) { _services.Log.Warning($"[MinimalNameplate] live-attr api resolve failed: {ex.Message}"); _attrApiOk = false; }
        _services.Log.Info($"[MinimalNameplate] live-attr api ok={_attrApiOk} (int+long closures)");
        return _attrApiOk;
    }

    private void CloseGetAttr(Type entityType, Type attrEnum)
    {
        foreach (var m in entityType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name != "GetAttr" || !m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length != 2 || ps[0].ParameterType != attrEnum || ps[1].ParameterType != typeof(bool)) continue;
            _miGetAttrLong = m.MakeGenericMethod(ClassIconRules.AttrClrType(ClassIconRules.AttrHpId));
            _miGetAttrInt = m.MakeGenericMethod(ClassIconRules.AttrClrType(ClassIconRules.AttrProfessionId));
            return;
        }
    }

    private object? GetEntityObj(long uuid)
    {
        try
        {
            if (!Resolve()) return null;
            var mgr = _piEntMgrInstance!.GetValue(null);
            if (mgr == null) return null;
            _entityArgs[0] = uuid;
            return _miGetEntity!.Invoke(mgr, _entityArgs);
        }
        catch { return null; }
    }

    // Profession for any player. Players can SWITCH class, so the cache is not frozen — a NEW live value replaces the
    // cached one after it persists across a couple of rebuilds (ProfConfirmCount): lets real switches through (~1s)
    // and rejects a 1-sample transient wrong read. A live value of 0 (e.g. mounted) keeps the last known.
    private readonly Dictionary<long, int> _profCache = new();
    private readonly Dictionary<long, (int prof, int count)> _profPending = new();
    private const int ProfConfirmCount = 2;

    private int ResolveProfession(long uuid)
    {
        int live = ResolveProfessionLive(uuid);
        _profCache.TryGetValue(uuid, out var cached);

        if (live <= 0) { _profPending.Remove(uuid); return cached; }
        if (live == cached) { _profPending.Remove(uuid); return cached; }
        if (cached == 0) { _profCache[uuid] = live; _profPending.Remove(uuid); return live; }

        int count = (_profPending.TryGetValue(uuid, out var pv) && pv.prof == live) ? pv.count + 1 : 1;
        if (count >= ProfConfirmCount) { _profCache[uuid] = live; _profPending.Remove(uuid); return live; }
        _profPending[uuid] = (live, count);
        return cached;
    }

    private int ResolveProfessionLive(long uuid)
    {
        if (TryLiveProfession(uuid, out var lp) && lp > 0) return lp;

        if (uuid == _services.CombatSnapshot.LocalEntityId.Value)
        {
            int sp = _services.PlayerState.Profession;
            if (sp > 0) return sp;
        }
        long charId = uuid >> 16;
        foreach (var m in _services.PartyRoster.Members)
            if (m.CharId == charId && m.Profession > 0) return m.Profession;

        try
        {
            var attrs = _services.EntityDetail.GetAttributes(new EntityId(uuid));
            if (attrs != null && attrs.TryGetValue(ClassIconRules.AttrProfessionId, out var p) && p > 0) return (int)p;
        }
        catch { }
        return 0;
    }

    // Reflection walk of an Il2CppSystem key collection. The result buffer is REUSED (Clear, not new) and the
    // enumerator members are resolved once per collection type instead of once per call.
    //
    // A typed (allocation-free) Il2CppInterop enumerator is NOT reachable here: the collection is
    // Il2CppSystem.Collections.Generic.Dictionary<long, Panda.ZGame.ZEntity>.KeyCollection, and this plugin has no
    // compile-time reference to ZEntity, so the generic type cannot be named. The remaining boxing is one boxed long
    // plus one boxed bool per entity — ~42/s at 21 players and 2 Hz, versus the 60 Hz it used to run at.
    private readonly List<object> _walkBuf = new();
    private Type? _walkType;
    private MethodInfo? _walkGetEnum;
    private MethodInfo? _walkMoveNext;
    private PropertyInfo? _walkCurrent;

    private List<object> WalkIl2Cpp(object collection)
    {
        _walkBuf.Clear();
        var t = collection.GetType();
        if (!ReferenceEquals(t, _walkType))
        {
            _walkType = t;
            _walkGetEnum = FindNoArg(t, "GetEnumerator");
            _walkMoveNext = null;
            _walkCurrent = null;
        }
        var en = _walkGetEnum?.Invoke(collection, null);
        if (en == null) return _walkBuf;
        if (_walkMoveNext == null || _walkCurrent == null)
        {
            var enT = en.GetType();
            _walkMoveNext = FindNoArg(enT, "MoveNext");
            _walkCurrent = enT.GetProperty("Current");
            if (_walkMoveNext == null || _walkCurrent == null) return _walkBuf;
        }
        while ((bool)_walkMoveNext.Invoke(en, null)!)
        {
            var v = _walkCurrent.GetValue(en);
            if (v != null) _walkBuf.Add(v);
        }
        return _walkBuf;
    }

    private static MethodInfo? FindNoArg(Type t, string name)
    {
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.Name == name && m.GetParameters().Length == 0) return m;
        return null;
    }
}
