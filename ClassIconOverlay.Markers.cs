using System;
using UnityEngine;

namespace Stellar.MinimalNameplate;

// Relation-marker rendering for the name overlay: the pre-colored Friend (heart) and Guild(Union) (shield) PNGs drawn
// in front of a player's name. Split out of ClassIconOverlay.HudPoc.cs (self-contained, and that file is at the 500-LoC
// cap). The marker fields (_friendTex/_unionTex) and the shared HUD command buffer/material live in HudPoc.cs; these
// are partial-class members of the same type, so they resolve across the split.
internal sealed partial class ClassIconOverlay
{
    // Texture aspect (width/height) for sizing a marker quad; falls back to 1 (square) if the texture is missing.
    private static float MarkerAspect(Texture2D? t) => (t != null && t.height > 0) ? (float)t.width / t.height : 1f;

    // Draw one relation marker, billboarded with camRot, at an explicit width×height (width follows the texture aspect
    // so the wide heart and tall shield read equal). The relation PNGs are pre-colored so callers pass Color.white
    // (no tint); the tint param stays for generality.
    private void DrawMarker(Vector3 center, Quaternion rot, float width, float height, Texture2D? tex, Color tint)
    {
        if (_hudCmd == null || _hudMat == null || tex == null) return;
        _mpb!.Clear();
        _mpb.SetTexture(MainTexId, tex);
        _mpb.SetColor(ColorId, tint);
        _hudCmd.DrawMesh(BgQuad(), Matrix4x4.TRS(center, rot, new Vector3(width, height, 1f)), _hudMat, 0, 0, _mpb);
    }

    private Texture2D? FriendTex() => _friendTex ??= LoadMarkerPng("friend-icon.png", "friend");
    private Texture2D? UnionTex()  => _unionTex  ??= LoadMarkerPng("guild-icon.png", "guild");

    // Load a pre-colored relation marker from an embedded PNG into a mip-mapped Texture2D (cached by the caller). The
    // PNG carries its own colors + alpha, so it draws untinted. Fails safe: on a missing stream or decode failure, logs
    // and returns null (DrawMarker no-ops on a null texture → simply no marker, never a crash).
    private Texture2D? LoadMarkerPng(string fileName, string label)
    {
        try
        {
            byte[]? bytes;
            using (var s = typeof(ClassIconOverlay).Assembly.GetManifestResourceStream("Stellar.MinimalNameplate." + fileName))
            {
                if (s == null) { _services.Log.Warning($"[MinimalNameplate] {label} icon load failed"); return null; }
                using var ms = new System.IO.MemoryStream();
                s.CopyTo(ms);
                bytes = ms.ToArray();
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true)
            { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            if (!ImageConversion.LoadImage(tex, bytes))
            {
                _services.Log.Warning($"[MinimalNameplate] {label} icon load failed");
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            tex.filterMode = FilterMode.Bilinear;   // LoadImage can reset sampler state; mips come from mipChain:true
            return tex;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[MinimalNameplate] {label} icon load failed: {ex.Message}");
            return null;
        }
    }
}
