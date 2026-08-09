using System;
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

    // True if this player is on the local player's friend list. FAIL-OPEN: unresolved / self / null / exception → false.
    private bool IsFriendPlayer(long uuid)
    {
        try
        {
            EnsureRelation();
            if (_miIsFriend == null || _piLuaDataMgrInstance == null) return false;
            if (uuid == _services.CombatSnapshot.LocalEntityId.Value) return false;   // relationship-to-self is meaningless
            var inst = _piLuaDataMgrInstance.GetValue(null);
            if (inst == null) return false;
            // Test BOTH key forms (roleId = uuid>>16 AND full uuid); a set keyed by one form can't contain the other,
            // so returning true if EITHER hits is safe and resolves which key the game actually uses.
            long roleId = uuid >> 16;
            return (_miIsFriend.Invoke(inst, new object[] { roleId }) is bool a && a)
                || (_miIsFriend.Invoke(inst, new object[] { uuid })   is bool b && b);
        }
        catch { return false; }
    }

    // True if this player is in the local player's guild (Union). FAIL-OPEN: unresolved / self / null / exception → false.
    private bool IsGuildPlayer(long uuid)
    {
        try
        {
            EnsureRelation();
            if (_miIsUnionMember == null || _piLuaDataMgrInstance == null) return false;
            if (uuid == _services.CombatSnapshot.LocalEntityId.Value) return false;   // IsUnionMember(self) can wrongly return true
            var inst = _piLuaDataMgrInstance.GetValue(null);
            if (inst == null) return false;
            // Test BOTH key forms (roleId = uuid>>16 AND full uuid) — see IsFriendPlayer for why EITHER hitting is safe.
            long roleId = uuid >> 16;
            return (_miIsUnionMember.Invoke(inst, new object[] { roleId }) is bool a && a)
                || (_miIsUnionMember.Invoke(inst, new object[] { uuid })   is bool b && b);
        }
        catch { return false; }
    }
}
