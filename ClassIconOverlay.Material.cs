using UnityEngine;

namespace Stellar.MinimalNameplate;

// Material-parameter plumbing for the borrowed game HUD material (shader BlueProtocol/HUD/Sprite). Its texture slot is
// _Tex0 — NOT _MainTex, which is why our quads were invisible under the game material — and it reconstructs scene depth
// in-shader from _HDRExposure / _ReverseY / _FarNearParam to occlude PP-independently (the scene depth itself is a
// bound GLOBAL set by the game in HudRenderPass, not a material property, so we never touch it). MPB overrides are
// per-draw and any prop we leave unset falls back to the material's serialized default — wrong if the game drives
// these via its own MPB — so we copy the three live values into EVERY borrowed-material draw. On the Sprites/Default
// fallback none of this applies (_texPropId = _MainTex, _borrowedMat = false, no depth params).
internal sealed partial class ClassIconOverlay
{
    private static readonly int Tex0Id         = Shader.PropertyToID("_Tex0");
    private static readonly int HdrExposureId  = Shader.PropertyToID("_HDRExposure");
    private static readonly int ReverseYId     = Shader.PropertyToID("_ReverseY");
    private static readonly int FarNearParamId = Shader.PropertyToID("_FarNearParam");

    private bool    _borrowedMat;             // true when _hudMat is the borrowed game material (→ _Tex0 + depth params)
    private int     _texPropId = MainTexId;   // texture MPB prop for the active material: _Tex0 (game) or _MainTex (fallback)
    private float   _matHdr, _matRevY;        // cached once per frame from the borrowed material
    private Vector4 _matFarNear;
    private bool    _liveParamsLogged;

    // Configure the shared MPB for one draw: texture on the ACTIVE texture prop + color, plus (borrowed game material
    // only) the depth/exposure params the shader needs. Used by every badge/icon/name/marker draw.
    private void SetDrawMpb(Texture tex, Color color)
    {
        _mpb!.Clear();
        _mpb.SetTexture(_texPropId, tex);
        _mpb.SetColor(ColorId, color);
        if (_borrowedMat)
        {
            _mpb.SetFloat(HdrExposureId, _matHdr);
            _mpb.SetFloat(ReverseYId, _matRevY);
            _mpb.SetVector(FarNearParamId, _matFarNear);
        }
    }

    // Read the borrowed material's live depth/exposure params once per frame (cheap; cached for the frame's draws). If
    // a getter throws, that param keeps its previous cached value. Logs the values ONCE so we can tell whether the game
    // keeps them on the material (non-zero here) or drives them via its own MPB/globals (zero here → next diagnostic).
    private void RefreshBorrowedMatParams()
    {
        if (_hudMat == null) return;
        try { _matHdr     = _hudMat.GetFloat(HdrExposureId); }   catch { }
        try { _matRevY    = _hudMat.GetFloat(ReverseYId); }      catch { }
        try { _matFarNear = _hudMat.GetVector(FarNearParamId); } catch { }
        if (!_liveParamsLogged)
        {
            _liveParamsLogged = true;
            bool tex0set = false;
            try { tex0set = _hudMat.GetTexture(Tex0Id) != null; } catch { }
            _services.Log.Info($"[MinimalNameplate][mat] live params: hdr={_matHdr} revY={_matRevY} farNear={_matFarNear.x},{_matFarNear.y},{_matFarNear.z},{_matFarNear.w} tex0set={tex0set}");
        }
    }
}
