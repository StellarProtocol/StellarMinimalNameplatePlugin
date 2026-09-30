using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MinimalNameplate;

// Relationship-to-the-LOCAL-player detection, used to draw Friend / Guild markers in front of a player's name.
// Single game singleton Panda.ZUi.LuaDataMgr (a ZSingleton) exposes two O(1) HashSet lookups:
//     public bool IsFriend(long charId)      — charId on the local player's friend list
//     public bool IsUnionMember(long charId) — charId in the local player's guild (the game calls a guild a "Union")
//
// ⚠️ KEY = roleId = uuid >> 16 (NOT the full uuid). The C# param is named "charId"; every caller passes uuid>>16.
//    This is the SAME key convention as PartyRoster.CharId == uuid >> 16 used elsewhere in this overlay.
// ⚠️ SYNC TIMING: these sets are populated by server friend/union data pushes and may be EMPTY until that data has
//    synced (possibly not until the player has opened the Friends/Union panel once). So a false/missing result is
//    "unknown → no marker", NEVER a positive assertion of "not a friend / not in guild". This is naturally fail-open
//    (draw the name without markers).
// ⚠️ SELF-GUARD: never report a relationship for the LOCAL player — relationship-to-self is meaningless and
//    IsUnionMember(self) can return true.
// Fail-open everywhere: any unresolved reflection / null / exception → false (no marker). See
// Knowledge Base\Nameplate-HUD.md (ZSingleton Instance pattern).
internal sealed partial class ClassIconOverlay
{
    private bool          _relationResolved;
    private PropertyInfo? _piLuaDataMgrInstance;   // LuaDataMgr.Instance (static)
    private MethodInfo?   _miIsFriend;             // LuaDataMgr.IsFriend(long)
    private MethodInfo?   _miIsUnionMember;        // LuaDataMgr.IsUnionMember(long)

    private void EnsureRelation()
    {
        if (_relationResolved) return;
        _relationResolved = true;
        try
        {
            var t = StellarInterop.FindType("Panda.ZUi.LuaDataMgr");
            if (t == null) { _services.Log.Warning("[MinimalNameplate] relation: LuaDataMgr not found — relation markers off"); return; }

            _piLuaDataMgrInstance = t.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

            const BindingFlags pubInst = BindingFlags.Public | BindingFlags.Instance;
            _miIsFriend      = t.GetMethod("IsFriend",      pubInst, null, new[] { typeof(long) }, null);
            _miIsUnionMember = t.GetMethod("IsUnionMember", pubInst, null, new[] { typeof(long) }, null);

            if (_piLuaDataMgrInstance == null || _miIsFriend == null || _miIsUnionMember == null)
                _services.Log.Warning("[MinimalNameplate] relation api incomplete — some markers off");
            _services.Log.Info($"[MinimalNameplate] relation api: mgr={_piLuaDataMgrInstance != null} " +
                $"isFriend={_miIsFriend != null} isUnion={_miIsUnionMember != null}");
        }
        catch (Exception ex) { _services.Log.Warning($"[MinimalNameplate] relation resolve failed: {ex.Message}"); }
    }

    // Friend/guild are CACHED per uuid and resolved on the overlay's 0.5s rebuild cadence (RefreshRelation, called
    // from RebuildPlayers), NOT per drawn badge per frame. The old per-frame path did up to 2 Invokes × 2 key forms
    // × 2 relations for EVERY badge every frame (thousands of reflection calls/allocations per second in a crowd).
    // The per-frame reads below are now pure Dictionary lookups — no reflection in the draw loop. Trade-off (same as
    // dead/profession): a membership change shows on the marker within 0.5s. FAIL-OPEN: a missing entry → false → no
    // marker (never a positive "not a friend" assertion).
    private readonly Dictionary<long, bool> _friendCache = new();
    private readonly Dictionary<long, bool> _guildCache  = new();
    private readonly object[] _relationArgs = new object[1];   // reused Invoke buffer — main thread only

    // Which key form the game's LuaDataMgr sets are keyed by. KB says roleId = uuid>>16, but we confirm at runtime:
    // test BOTH forms until the FIRST positive latches the form, then only ever invoke that one. If neither ever
    // hits (genuine non-member), we stay Unknown and keep testing both — that is just the fail-open "no marker" path.
    private enum RelKey { Unknown, RoleId, FullUuid }
    private RelKey _relKeyForm = RelKey.Unknown;

    /// <summary>Drop the relation caches — called from RebuildPlayers so a membership change is picked up within 0.5s.</summary>
    internal void ClearRelationCache() { _friendCache.Clear(); _guildCache.Clear(); }

    // Resolve + cache both relations for one player. Called at the 2 Hz rebuild rate. FAIL-OPEN: unresolved / self /
    // null / exception → false for both. Self is excluded (relationship-to-self is meaningless, and IsUnionMember(self)
    // can wrongly return true).
    internal void RefreshRelation(long uuid)
    {
        bool friend = false, guild = false;
        try
        {
            EnsureRelation();
            if (_piLuaDataMgrInstance != null && uuid != _services.CombatSnapshot.LocalEntityId.Value)
            {
                var inst = _piLuaDataMgrInstance.GetValue(null);
                if (inst != null)
                {
                    friend = ResolveRelation(_miIsFriend, inst, uuid);
                    guild  = ResolveRelation(_miIsUnionMember, inst, uuid);
                }
            }
        }
        catch { friend = false; guild = false; }
        _friendCache[uuid] = friend;
        _guildCache[uuid]  = guild;
    }

    // Invoke one relation predicate, respecting the latched key form. A set keyed by one form can't contain the other,
    // so while the form is Unknown, testing both and returning true on EITHER is safe (and latches the winning form).
    private bool ResolveRelation(MethodInfo? mi, object inst, long uuid)
    {
        if (mi == null) return false;
        long roleId = uuid >> 16;
        switch (_relKeyForm)
        {
            case RelKey.RoleId:   return InvokeRel(mi, inst, roleId);
            case RelKey.FullUuid: return InvokeRel(mi, inst, uuid);
            default:
                if (InvokeRel(mi, inst, roleId)) { _relKeyForm = RelKey.RoleId;   return true; }
                if (InvokeRel(mi, inst, uuid))   { _relKeyForm = RelKey.FullUuid; return true; }
                return false;
        }
    }

    private bool InvokeRel(MethodInfo mi, object inst, long key)
    {
        try { _relationArgs[0] = key; return mi.Invoke(inst, _relationArgs) is bool b && b; }
        catch { return false; }
    }

    // Per-frame reads — pure cache lookups, no reflection. Missing entry → false (fail-open, no marker).
    private bool IsFriendPlayer(long uuid) => _friendCache.TryGetValue(uuid, out var v) && v;
    private bool IsGuildPlayer(long uuid)  => _guildCache.TryGetValue(uuid, out var v) && v;
}
