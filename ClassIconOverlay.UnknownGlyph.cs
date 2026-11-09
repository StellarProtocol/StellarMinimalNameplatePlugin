using System;
using System.Collections.Generic;
using UnityEngine;

namespace Stellar.MinimalNameplate;

// White "?" drawn in the class-logo slot for a profession whose icon is DEFINITIVELY unavailable.
//
// WHY (2026-11-09): ProfessionTable row 10 has an EMPTY ProfessionIcon. The framework then guesses a HUD path that
// points at the old atlas folder (and, in the live folder, hud01 is Shield Knight's crest), so LoadProfessionIcon
// keeps returning null and the badge drew as a bare coloured square that looked like a load bug. The "?" says "this
// class has no icon" instead. ClassIconRules.ShowUnknownGlyph decides WHEN — never during a normal pending load — and
// a real icon resolving later replaces it (CacheIcon clears the flag; the draw path prefers _iconCache).
//
// ART: generated procedurally ONCE (signed-distance arc + stem + dot → anti-aliased white mask with mipmaps), not via
// the name-bake path: the baked name texture carries a black outline and a glyph-dependent aspect, while class logos
// are plain white silhouettes in a square quad. Drawn with the same quad size/tint (Color.white) as a class logo.
internal sealed partial class ClassIconOverlay
{
    private readonly HashSet<int> _unknownProfs = new();          // professions currently showing the "?"
    private readonly Dictionary<int, bool> _profHasIconPath = new(); // cached only once the profession row is readable
    private Texture2D? _unknownTex;

    // Called from RefreshIcons (2 Hz) for a tracked profession that has no loaded icon this rebuild.
    private void UpdateUnknownGlyph(int prof, IconState st)
    {
        double pending = st.PendingSince < 0 ? -1 : _iconClock - st.PendingSince;
        bool? hasPath = HasIconPath(prof);
        if (!ClassIconRules.ShowUnknownGlyph(prof, hasPath, pending)) return;
        if (_unknownProfs.Add(prof))
            _services.Log.Info($"[MinimalNameplate] [IconDiag] prof={prof} state=unknown-glyph " +
                               $"iconPath={(hasPath == null ? "?" : hasPath.Value ? "1" : "0")} pending={pending:F1}s");
    }

    // true/false once the profession's table row is readable; null while it isn't (row missing / GameData not ready),
    // which leaves the decision to the long window instead of caching a premature "no path".
    private bool? HasIconPath(int prof)
    {
        if (_profHasIconPath.TryGetValue(prof, out var has)) return has;
        try
        {
            var row = _services.GameData.Combat.GetProfession(prof);
            if (row == null) return null;
            has = !string.IsNullOrEmpty(row.Value.IconPath);
        }
        catch { return null; }
        _profHasIconPath[prof] = has;
        return has;
    }

    // Draw-path half: same centre, size and tint as the class logo (AppendHudBadge, iconH = worldH * 0.72).
    private void DrawUnknownGlyph(Vector3 badgeCenter, Quaternion camRot, float worldH)
    {
        var tex = UnknownGlyphTex();
        if (tex == null) return;
        float iconH = worldH * 0.72f;
        SetDrawMpb(tex, Color.white);
        _hudCmd!.DrawMesh(BgQuad(), Matrix4x4.TRS(badgeCenter, camRot, new Vector3(iconH, iconH, iconH)), _hudMat, 0, 0, _mpb);
    }

    // Glyph geometry in unit texture space (y up): a hook arc over the top, a stem down to the middle, a dot below.
    private const float QArcCx = 0.5f, QArcCy = 0.66f, QArcR = 0.17f;
    private const float QArcFromDeg = -50f, QArcToDeg = 165f;      // counter-clockwise from lower-right round to left
    private const float QHalfStroke = 0.065f;
    private static readonly Vector2 QStemMid = new(0.5f, 0.42f), QStemEnd = new(0.5f, 0.33f);
    private static readonly Vector2 QDot = new(0.5f, 0.17f);
    private const float QDotR = 0.075f;

    private Texture2D? UnknownGlyphTex()
    {
        if (_unknownTex != null) return _unknownTex;   // Unity == → also rebuilds after an external destroy
        try
        {
            const int s = 128;
            var t = new Texture2D(s, s, TextureFormat.RGBA32, true)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, hideFlags = HideFlags.HideAndDontSave };
            float fromRad = QArcFromDeg * Mathf.Deg2Rad, toRad = QArcToDeg * Mathf.Deg2Rad;
            var arcA = new Vector2(QArcCx + QArcR * Mathf.Cos(fromRad), QArcCy + QArcR * Mathf.Sin(fromRad));
            var arcB = new Vector2(QArcCx + QArcR * Mathf.Cos(toRad), QArcCy + QArcR * Mathf.Sin(toRad));
            var px = new Color[s * s];
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    var p = new Vector2((x + 0.5f) / s, (y + 0.5f) / s);
                    // distance to the arc's centre-line (endpoints outside the swept angle → round caps)
                    var d = p - new Vector2(QArcCx, QArcCy);
                    float ang = Mathf.Atan2(d.y, d.x);
                    float dArc = ang >= fromRad && ang <= toRad
                        ? Mathf.Abs(d.magnitude - QArcR)
                        : Mathf.Min(Vector2.Distance(p, arcA), Vector2.Distance(p, arcB));
                    float dStroke = Mathf.Min(dArc, Mathf.Min(SegDist(p, arcA, QStemMid), SegDist(p, QStemMid, QStemEnd))) - QHalfStroke;
                    float dDot = Vector2.Distance(p, QDot) - QDotR;
                    float sd = Mathf.Min(dStroke, dDot);                 // signed distance, < 0 inside
                    px[y * s + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - sd * s));   // 1-px AA edge
                }
            t.SetPixels(px);
            t.Apply(true);   // build mips: the glyph renders at ~20-40 px, far below 128
            _unknownTex = t;
        }
        catch (Exception ex) { _services.Log.Warning($"[MinimalNameplate] unknown-glyph texture failed: {ex.Message}"); }
        return _unknownTex;
    }

    private static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Vector2.Dot(ab, ab));
        return Vector2.Distance(p, a + ab * t);
    }

    private void DestroyUnknownGlyph()
    {
        if (_unknownTex != null) { try { UnityEngine.Object.Destroy(_unknownTex); } catch { } }
        _unknownTex = null;
        _unknownProfs.Clear();
    }
}
