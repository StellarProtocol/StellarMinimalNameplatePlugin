using System;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.MinimalNameplate;

// Mirror the game's OWN nameplate visibility so our overlay hides exactly when the game's plate would. Two layers,
// both read live off Panda.Hud.HudMgr (a ZSingleton):
//   (1) Global HUD switch — HudMgr.IsEnabled (backed by hudDisabledFlag_, toggled per EHudAvailableSource:
//       EUi / EGm / ECamera / ECutScene / EInteraction). Covers the HideUI key, cutscenes, photo/camera mode,
//       GM close, and full-screen menus. When off → draw nothing.
//   (2) Per entity-type SETTING — HudMgr.GetHudSettingsShow(EHudSettingEntityType, EHudSettingFuncType). Honors the
//       options-menu "player / other-player head info" toggles: self → EPlayer, other players → EChar, name piece.
// EVERYTHING is fail-open: if a member can't be resolved we behave as before (draw), so a lookup miss never blanks
// the overlay. See Knowledge Base\Nameplate-HUD.md.
internal sealed partial class ClassIconOverlay
{
    private bool          _hudMgrResolved;
    private PropertyInfo?  _piHudMgrInstance;    // HudMgr.Instance (static)
    private PropertyInfo?  _piHudIsEnabled;      // HudMgr.IsEnabled (bool) — global HUD on/off
    private FieldInfo?     _fiHudDisabledFlag;   // HudMgr.hudDisabledFlag_ (int) — 0 == shown (fallback)
    private FieldInfo?     _fiHudIsActive;       // HudMgr.IsActive (bool public field) — last-resort fallback
    private MethodInfo?    _miGetHudSettingsShow; // HudMgr.GetHudSettingsShow(EHudSettingEntityType, EHudSettingFuncType)
    private object?        _entPlayer;           // EHudSettingEntityType.EPlayer (self)          = 1
    private object?        _entChar;             // EHudSettingEntityType.EChar   (other players) = 2
    private object?        _funcName;            // EHudSettingFuncType.EName                       = 2

    private void EnsureHudMgr()
    {
        if (_hudMgrResolved) return;
        _hudMgrResolved = true;
        try
        {
            var t = StellarInterop.FindType("Panda.Hud.HudMgr");
            if (t == null) { _services.Log.Warning("[MinimalNameplate] hud-vis: HudMgr not found — visibility mirror off"); return; }

            const BindingFlags pubInst = BindingFlags.Public | BindingFlags.Instance;
            _piHudMgrInstance     = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            _piHudIsEnabled       = t.GetProperty("IsEnabled", pubInst);
            _fiHudIsActive        = t.GetField("IsActive", pubInst);
            _fiHudDisabledFlag    = t.GetField("hudDisabledFlag_", BindingFlags.NonPublic | BindingFlags.Instance);
            _miGetHudSettingsShow = t.GetMethod("GetHudSettingsShow", pubInst);

            var entEnum  = StellarInterop.FindType("Panda.ZGame.EHudSettingEntityType");
            var funcEnum = StellarInterop.FindType("Panda.ZGame.EHudSettingFuncType");
            if (entEnum  != null) { _entPlayer = Enum.ToObject(entEnum, 1); _entChar = Enum.ToObject(entEnum, 2); }
            if (funcEnum != null) _funcName = Enum.ToObject(funcEnum, 2);

            _services.Log.Info($"[MinimalNameplate] hud-vis api: mgr={_piHudMgrInstance != null} isEnabled={_piHudIsEnabled != null} " +
                $"disFlag={_fiHudDisabledFlag != null} isActive={_fiHudIsActive != null} " +
                $"settings={_miGetHudSettingsShow != null && _entChar != null && _funcName != null}");
        }
        catch (Exception ex) { _services.Log.Warning($"[MinimalNameplate] hud-vis resolve failed: {ex.Message}"); }
    }

    private object? HudMgrInstance()
    {
        EnsureHudMgr();
        return _piHudMgrInstance?.GetValue(null);
    }

    // (1) Global HUD switch. Shown unless the game has disabled the HUD (HideUI / cutscene / camera / GM / menu).
    private bool GameHudShown()
    {
        try
        {
            var inst = HudMgrInstance();
            if (inst == null) return true;                                   // fail-open
            if (_piHudIsEnabled != null && _piHudIsEnabled.GetValue(inst) is bool en) return en;
            if (_fiHudDisabledFlag != null)
            {
                var raw = _fiHudDisabledFlag.GetValue(inst);
                if (raw != null) return Convert.ToInt32(raw) == 0;           // 0 == nothing disabling
            }
            if (_fiHudIsActive != null && _fiHudIsActive.GetValue(inst) is bool act) return act;
            return true;
        }
        catch { return true; }
    }

    // (2) Per entity-type nameplate SETTING (options: player / other head info). Self → EPlayer, others → EChar,
    // name piece (EName). True (show) if the setting API can't be resolved.
    private bool GameShowsPlateFor(long uuid)
    {
        try
        {
            if (_miGetHudSettingsShow == null || _funcName == null) return true;   // fail-open
            var inst = HudMgrInstance();
            if (inst == null) return true;
            bool self = uuid == _services.CombatSnapshot.LocalEntityId.Value;
            var ent = self ? _entPlayer : _entChar;
            if (ent == null) return true;
            return _miGetHudSettingsShow.Invoke(inst, new object[] { ent, _funcName }) is not bool b || b;
        }
        catch { return true; }
    }

    // (3) Hide-and-seek (TAG) MODEL-visibility mirror — a SEPARATE path from the HUD-source mirror in
    // NameplateIconPatch.HudVisible.cs. Hide-and-seek does NOT toggle a per-entity HUD plate source
    // (EHudVisibleSource.EHideSeek=12 is effectively never set by the game), and the ECS HideSeekComponent camp reads
    // come back empty at runtime. The observable mechanic: a hider TRANSFORMS INTO A PROP, which swaps the entity's
    // active model — ZEntity.ModelId (active) diverges from ZEntity.MainModelId (base). Both getters are
    // client-readable for other players, so we suppress the badge when the two ids differ.
    // ZHideSeekUtils.IsHideSeekScene() gates the whole thing so all model work is skipped outside hide-seek.
    // Fail-open everywhere (unresolved reflection or a lookup miss → draw), matching this file's convention. See
    // Knowledge Base\Nameplate-HUD.md.
    private bool          _hideSeekResolved;
    private MethodInfo?    _miIsHideSeekScene;       // ZHideSeekUtils.IsHideSeekScene() — static bool, no args
    private PropertyInfo?  _piModelId;                // ZEntity.ModelId (int) — currently-active model
    private PropertyInfo?  _piMainModelId;            // ZEntity.MainModelId (int) — true base model

    private void EnsureHideSeek()
    {
        if (_hideSeekResolved) return;
        _hideSeekResolved = true;
        try
        {
            var utils = StellarInterop.FindType("Panda.ZGame.ZHideSeekUtils");
            if (utils != null)
                _miIsHideSeekScene = utils.GetMethod("IsHideSeekScene",
                    BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);

            var zent = StellarInterop.FindType("Panda.ZGame.ZEntity");
            if (zent != null)
            {
                const BindingFlags pubInst = BindingFlags.Public | BindingFlags.Instance;
                _piModelId     = zent.GetProperty("ModelId", pubInst);
                _piMainModelId = zent.GetProperty("MainModelId", pubInst);
            }

            if (_miIsHideSeekScene == null || _piModelId == null || _piMainModelId == null)
                _services.Log.Warning("[MinimalNameplate] hide-seek api incomplete — model-visibility mirror off");
            _services.Log.Info($"[MinimalNameplate] hide-seek api: scene={_miIsHideSeekScene != null} " +
                $"modelId={_piModelId != null} mainModelId={_piMainModelId != null}");
        }
        catch (Exception ex) { _services.Log.Warning($"[MinimalNameplate] hide-seek resolve failed: {ex.Message}"); }
    }

    // True for a TRANSFORMED hider during a hide-and-seek scene (active model swapped to a prop; see rationale above).
    // FAIL-OPEN: any unresolved member, missing entity, or exception → false (draw). A miss must never suppress a badge.
    private bool IsHiddenByHideSeek(long uuid)
    {
        try
        {
            EnsureHideSeek();
            if (_miIsHideSeekScene == null) return false;                              // no gate → not in hide-seek → draw
            if (_miIsHideSeekScene.Invoke(null, null) is not bool inScene || !inScene) return false;
            if (!TryGetModelIds(uuid, out int model, out int main)) return false;
            return model != main;   // active model swapped to a prop → transformed hider → suppress badge
        }
        catch { return false; }   // fail-open = draw
    }

    // Returns true if both model ids were read. modelId != mainModelId ⇒ the entity's active model was swapped (transformed into a prop).
    private bool TryGetModelIds(long uuid, out int modelId, out int mainModelId)
    {
        modelId = mainModelId = int.MinValue;
        try
        {
            EnsureHideSeek();
            if (_piModelId == null || _piMainModelId == null) return false;
            var ent = GetEntityObj(uuid);
            if (ent == null) return false;
            var mv = _piModelId.GetValue(ent);
            var bv = _piMainModelId.GetValue(ent);
            if (mv == null || bv == null) return false;
            modelId     = Convert.ToInt32(mv);
            mainModelId = Convert.ToInt32(bv);
            return true;
        }
        catch { modelId = mainModelId = int.MinValue; return false; }   // fail-open = draw
    }
}
