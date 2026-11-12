using System;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.MinimalNameplate;

// Game-side reflection for the overlay: entity/camera resolution and the per-player head anchor. Split out of
// ClassIconOverlay.cs verbatim (self-contained, and that file is at the 500-LoC cap).
internal sealed partial class ClassIconOverlay
{
    // ── Game-side reflection: ZEntityMgr.GetEntity(uuid) → Model → ModelGoComp → Position/height. ──
    private bool          _resolved;
    private bool          _resolveFailed;
    private PropertyInfo? _piEntMgrInstance;
    private MethodInfo?   _miGetEntity;
    private PropertyInfo? _piEntityDict;
    private FieldInfo?    _fiEntityDict;
    private PropertyInfo? _piModel;
    private PropertyInfo? _piModelGoComp;
    private PropertyInfo? _piHeadPos;
    private bool          _headPosResolved;
    private PropertyInfo? _piPosition;   // ModelGoComp.Position (root/foot — stable, no animation bob)
    private PropertyInfo? _piHudHeight;  // per-character HUD height
    private PropertyInfo? _piCamInstance;
    private PropertyInfo? _piMainCamera;
    private MethodInfo?   _miGetZModelCompHud;
    private MethodInfo?   _miGetHudPos;
    private bool          _hudPosFailed;

    private const float FallbackHeadHeight = 1.9f;
    private readonly object[] _headArgs = new object[1];   // TryGetHeadWorld runs per tracked player per frame

    // A STABLE anchor above the head: model-root Position + HUD height (NOT the animated head bone, so the badge
    // follows movement/jumps but doesn't bob). Position tracks to the model draw distance — unlike GetHudPos, which
    // the game freezes past nameplate range — so far-but-visible players still track.
    private bool TryGetHeadWorld(long uuid, out Vector3 head)
    {
        head = default;
        if (!Resolve()) return false;

        var mgr = FrameEntMgr();   // once per frame, not once per player (.FrameCache.cs)
        if (mgr == null) return false;
        _headArgs[0] = uuid;                       // reused buffer — main thread only, see ClassIconOverlay.EntityRead.cs
        var ent = _miGetEntity!.Invoke(mgr, _headArgs);
        if (ent == null) return false;
        var model = _piModel!.GetValue(ent);
        if (model == null) return false;

        var go = _piModelGoComp!.GetValue(model);
        if (go != null && !_headPosResolved)
        {
            const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;
            var t = go.GetType();
            _piPosition = t.GetProperty("Position", pub);
            _piHudHeight = t.GetProperty("LogicHeight", pub) ?? t.GetProperty("Height", pub)
                        ?? t.GetProperty("ReferenceHeight", pub) ?? t.GetProperty("CommonHudHeight", pub);
            _piHeadPos = t.GetProperty("HeadPos", pub);
            _headPosResolved = true;
        }

        if (go != null && _piPosition?.GetValue(go) is Vector3 pos)
        {
            float h = _piHudHeight != null ? Convert.ToSingle(_piHudHeight.GetValue(go)) : 0f;
            if (h < 0.5f) h = FallbackHeadHeight;
            head = new Vector3(pos.x, pos.y + h, pos.z);
            return true;
        }

        // Fallback: the game's HUD anchor (mount/pose aware, but frozen past nameplate range).
        if (!_hudPosFailed && _miGetZModelCompHud != null && _miGetHudPos != null)
        {
            try
            {
                var hc = _miGetZModelCompHud.Invoke(model, null);
                if (hc != null && _miGetHudPos.Invoke(hc, null) is Vector3 hpos && hpos != Vector3.zero)
                { head = hpos; return true; }
            }
            catch (Exception ex) { _hudPosFailed = true; _services.Log.Warning($"[MinimalNameplate] GetHudPos call failed: {ex.Message}"); }
        }

        if (go != null && _piHeadPos?.GetValue(go) is Vector3 hp) { head = hp; return true; }
        return false;
    }

    private Camera? MainCamera()
    {
        if (!Resolve() || _piCamInstance == null || _piMainCamera == null) return null;
        var mgr = _piCamInstance.GetValue(null);
        return mgr == null ? null : _piMainCamera.GetValue(mgr) as Camera;
    }

    private bool Resolve()
    {
        if (_resolved) return !_resolveFailed;
        _resolved = true;
        try
        {
            var entMgr = StellarInterop.FindType("Panda.ZGame.ZEntityMgr");
            var camMgr = StellarInterop.FindType("Panda.ZGame.CameraManager");
            var ent    = StellarInterop.FindType("Panda.ZGame.ZEntity");
            var model  = StellarInterop.FindType("Panda.ZGame.ZModel");
            if (entMgr == null || camMgr == null || ent == null || model == null)
            {
                _services.Log.Warning($"[MinimalNameplate] type resolve failed entMgr={entMgr != null} cam={camMgr != null} ent={ent != null} model={model != null}");
                _resolveFailed = true; return false;
            }

            _piEntMgrInstance = entMgr.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            foreach (var m in entMgr.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name == "GetEntity" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(long))
                { _miGetEntity = m; break; }
            const BindingFlags anyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _piEntityDict = entMgr.GetProperty("entityDict_", anyInst);
            if (_piEntityDict == null) _fiEntityDict = entMgr.GetField("entityDict_", anyInst);
            _piModel       = ent.GetProperty("Model", BindingFlags.Public | BindingFlags.Instance);
            _piModelGoComp = model.GetProperty("ModelGoComp", BindingFlags.Public | BindingFlags.Instance);
            _piCamInstance = camMgr.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            _piMainCamera  = camMgr.GetProperty("MainCamera", BindingFlags.Public | BindingFlags.Instance);

            try
            {
                var hudCompType = StellarInterop.FindType("Panda.ZGame.HudComp");
                if (hudCompType != null)
                {
                    foreach (var m in model.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        if (m.Name == "GetZModelComp" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                        { _miGetZModelCompHud = m.MakeGenericMethod(hudCompType); break; }
                    _miGetHudPos = hudCompType.GetMethod("GetHudPos", BindingFlags.Public | BindingFlags.Instance);
                }
            }
            catch (Exception ex) { _services.Log.Warning($"[MinimalNameplate] GetHudPos resolve failed: {ex.Message}"); }

            bool ok = _piEntMgrInstance != null && _miGetEntity != null && _piModel != null
                      && _piModelGoComp != null && _piCamInstance != null && _piMainCamera != null;
            _services.Log.Info($"[MinimalNameplate] resolve ok={ok}");
            _resolveFailed = !ok;
            return ok;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[MinimalNameplate] Resolve error: {ex.Message}");
            _resolveFailed = true; return false;
        }
    }
}
