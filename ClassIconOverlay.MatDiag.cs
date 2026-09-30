using System;
using System.Text;
using UnityEngine;

namespace Stellar.MinimalNameplate;

// One-time diagnostic dump of the game's HUD material (Panda.Hud.HudMgr.Mat = ui/material/hud_sprite). The
// UseGameHudMaterial spike rendered our quads fully blank with this material, so we introspect its shader, keywords
// and properties once to work out why (e.g. it may not honor the _MainTex/_Color MPB props we drive, or expect
// _TintColor/_BaseMap instead). Every line carries the [MinimalNameplate][mat] prefix for grep. Purely logging —
// fully try/caught, never touches rendering. Driven from BeginHudFrame / GameHudMaterial regardless of the toggle.
internal sealed partial class ClassIconOverlay
{
    private bool _matDiagLogged;

    private void LogGameMaterialOnce(Material mat)
    {
        if (_matDiagLogged) return;
        _matDiagLogged = true;   // set FIRST so a throw below still leaves this one-shot (no per-frame retry spam)
        try
        {
            var sh = mat.shader;
            _services.Log.Info($"[MinimalNameplate][mat] shader={(sh != null ? sh.name : "(null)")} passCount={mat.passCount} renderQueue={mat.renderQueue} shaderRenderQueue={(sh != null ? sh.renderQueue : -1)}");

            // Keywords (own try — Il2CppInterop string-array marshalling could throw; must not skip the HasProperty block).
            try
            {
                var kw = mat.shaderKeywords;
                int kn = kw?.Length ?? 0;
                if (kn > 0)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < kn; i++) { if (i > 0) sb.Append(','); sb.Append(kw![i]); }
                    _services.Log.Info($"[MinimalNameplate][mat] keywords={sb}");
                }
                else _services.Log.Info("[MinimalNameplate][mat] keywords=(none)");
            }
            catch (Exception ex) { _services.Log.Info($"[MinimalNameplate][mat] keywords failed: {ex.Message}"); }

            // Full shader property list (own try — GetPropertyCount/Name/Type may not surface under Il2CppInterop; the
            // explicit HasProperty checks below are the reliable fallback that still answers the key question).
            try
            {
                int n = sh != null ? sh.GetPropertyCount() : 0;
                _services.Log.Info($"[MinimalNameplate][mat] propertyCount={n}");
                for (int i = 0; i < n; i++)
                    _services.Log.Info($"[MinimalNameplate][mat] prop[{i}] {sh!.GetPropertyName(i)} : {sh.GetPropertyType(i)}");
            }
            catch (Exception ex) { _services.Log.Info($"[MinimalNameplate][mat] property enumeration failed: {ex.Message}"); }

            // The key answer: which color/texture props this material actually exposes (drives whether our _MainTex/
            // _Color MPB even applies, or we need _TintColor/_BaseMap/etc.).
            foreach (var p in new[] { "_MainTex", "_Color", "_TintColor", "_BaseColor", "_BaseMap", "_MainTex_ST" })
                _services.Log.Info($"[MinimalNameplate][mat] HasProperty({p})={mat.HasProperty(p)}");
        }
        catch (Exception ex) { _services.Log.Info($"[MinimalNameplate][mat] introspection failed: {ex.Message}"); }
    }
}
