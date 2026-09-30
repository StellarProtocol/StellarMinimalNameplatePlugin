using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.Rendering;

namespace Stellar.MinimalNameplate;

// Renders the badge + name by submitting a CommandBuffer into the game's own HUD render pass
// (Panda.Hud.HudRenderPass.AddHudCommandBuffer). That pass runs AFTER the DLSS/FSR upscale (so our quads rasterize
// at native resolution → CRISP, DLSS-free) but still has the camera DEPTH buffer bound (so a ZTest-LEqual draw is
// occluded by world geometry) — exactly how the game's nameplate is both crisp AND hidden behind terrain.
//
// Everything (badge bg, icon, name) draws with Sprites/Default (hardcoded ZTest LEqual). The name is CPU-composited
// into an RGBA texture (white glyphs + black outline) because the dynamic-font atlas is alpha-only and the fixed-
// function GUI/Text Shader can't be ZTest-forced under SRP. See Knowledge Base\HUD-RenderPass-Crisp-Occluded.md.
internal sealed partial class ClassIconOverlay
{
    private PropertyInfo? _piHudInstance;   // HudRenderPass.Instance (static, from the singleton base)
    private MethodInfo?   _miAddHudCmd;     // HudRenderPass.AddHudCommandBuffer(CommandBuffer)
    private bool          _hudPassResolved;
    private bool          _hudPassOk;

    private CommandBuffer?         _hudCmd;
    private Material?              _hudMat;      // badge + name quads material — game HUD mat or fallback (BeginHudFrame)
    private bool                   _hudMatOwned; // true ONLY when WE created the Sprites/Default fallback → we destroy it
    private MaterialPropertyBlock? _mpb;
    private Mesh?                  _bgQuad;
    private readonly Dictionary<int, Mesh> _iconQuads = new();
    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    private static readonly int ColorId   = Shader.PropertyToID("_Color");

    // Spike toggle: borrow the game's own HUD material (ui/material/hud_sprite) so our quads inherit the native plate's
    // Post-Processing-INDEPENDENT occlusion — its shader samples a GLOBAL depth texture in-shader and discards, so it
    // occludes at ALL PP settings; Sprites/Default relies on hardware ZTest against a depth attachment that only exists
    // when PP's ZCopyDepthPass runs → see-through at Low/Off PP. false (or unresolved game mat) → Sprites/Default.
    internal static bool UseGameHudMaterial = false;   // DEFAULTED OFF: hud_sprite drew blank in-game; under diagnosis (see LogGameMaterialOnce)

    // Relation markers: real pre-colored PNG icons (loaded once) — a heart for Friend, a shield/crest for Guild(Union).
    // The PNGs already contain their own colors + transparency, so they draw UNTINTED (Color.white; _Color would
    // multiply and distort them).
    private Texture2D? _friendTex, _unionTex;

    // Reference name-line height (px) the relation markers are sized to. Constant so every player's marker is the
    // SAME size (the per-name baked texture height nt.h varies with the glyphs); still scales via worldPerPx.
    private const float MarkerRefPx = 58f;

    private bool EnsureHudPass()
    {
        if (_hudPassResolved) return _hudPassOk;
        _hudPassResolved = true;
        try
        {
            var t = StellarInterop.FindType("Panda.Hud.HudRenderPass");
            if (t != null)
            {
                _piHudInstance = t.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                _miAddHudCmd = t.GetMethod("AddHudCommandBuffer", BindingFlags.Public | BindingFlags.Instance);
                _hudPassOk = _piHudInstance != null && _miAddHudCmd != null;
            }
            HookFontRebuilt();
        }
        catch (Exception ex) { _services.Log.Warning($"[MinimalNameplate] HUD-pass resolve failed: {ex.Message}"); }
        _services.Log.Info($"[MinimalNameplate] HUD-pass api ok={_hudPassOk}");
        return _hudPassOk;
    }

    // The game's own HUD font (Panda.Hud.HudMgr.HudFont) — a dynamic Font with a full glyph set (incl. Japanese).
    // We build the name from its glyph atlas; the name is drawn via a CPU-composited texture (see BakeNameCpu).
    private Font? _hudFont;
    private bool  _hudFontResolved;
    private Font? HudFont()
    {
        if (_hudFontResolved) return _hudFont;
        _hudFontResolved = true;
        try
        {
            var t = StellarInterop.FindType("Panda.Hud.HudMgr");
            var inst = t?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
            _hudFont = t?.GetProperty("HudFont", BindingFlags.Public | BindingFlags.Instance)?.GetValue(inst) as Font;
            _services.Log.Info($"[MinimalNameplate] HUD font={(_hudFont != null)}");
        }
        catch (Exception ex) { _services.Log.Warning($"[MinimalNameplate] HudFont resolve failed: {ex.Message}"); }
        return _hudFont;
    }

    // The game's own HUD sprite material — Panda.Hud.HudMgr.Mat (= ui/material/hud_sprite), same ZSingleton pattern
    // HudFont() uses. Borrowed READ-ONLY and GAME-OWNED — never destroyed, never mutated (texture/color go through the
    // MPB, not the material). Cached only when non-null, so a transient null (singleton not ready) or a destroyed
    // HudMgr (scene reload → Unity null-overload) re-resolves next time instead of latching us to the fallback.
    private Material? _gameHudMat;
    private Material? GameHudMaterial()
    {
        if (_gameHudMat != null) return _gameHudMat;
        try
        {
            var t = StellarInterop.FindType("Panda.Hud.HudMgr");
            var inst = t?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
            _gameHudMat = t?.GetProperty("Mat", BindingFlags.Public | BindingFlags.Instance)?.GetValue(inst) as Material;
            if (_gameHudMat != null) LogGameMaterialOnce(_gameHudMat);   // one-shot introspection — see ClassIconOverlay.MatDiag.cs
        }
        catch (Exception ex) { _services.Log.Warning($"[MinimalNameplate] HUD material resolve failed: {ex.Message}"); }
        return _gameHudMat;
    }

    // A baked name is a standalone RGBA Texture2D (SetPixels32'd from a coverage buffer), INDEPENDENT of the atlas
    // layout once composited — it does not sample the atlas at draw time. So a font-atlas repack does NOT invalidate a
    // cached name; baked names survive a repack (see OnFontRepacked). Each BakeNameCpu reads the CURRENT atlas fresh —
    // no snapshot cache (a cached snapshot dropped glyphs packed into free space without a textureRebuilt → missing
    // characters); the per-frame bake budget below bounds the readback cost.
    private const int NameFontPx = 48;   // font pixel size the name is baked at (higher = crisper CPU composite)
    private readonly Dictionary<long, (string text, Texture2D tex, int w, int h)> _nameTex = new();

    // A name whose bake keeps returning null (glyphs won't fit / atlas unreadable) is given up on after a few tries so
    // it can't consume the per-frame bake budget every frame and starve valid new names. Reset on a repack (a grown
    // atlas may now fit) via OnFontRepacked, and on teardown via ClearNameTex. Rarely trips now that bakes read fresh.
    private readonly Dictionary<long, int> _nullBakeMisses = new();
    private const int MaxNullBakeMisses = 5;

    // FIX B — per-frame budget on NEW name bakes so a crowd load-in spreads its rasterizations over several frames
    // instead of stalling one. Reset in BeginHudFrame; only cache MISSES that actually bake consume it (hits are free).
    // Each bake does a fresh full-atlas ReadPixels + GetPixels32 (a GPU sync + managed alloc), so cap readbacks per
    // frame; baked names persist across repacks, so steady state is 0 bakes/frame.
    public static int MaxNameBakesPerFrame = 3;
    private int _nameBakesThisFrame;

    private Il2CppSystem.Action<Font>? _fontRebuiltCb;
    private bool _fontRebuiltHooked;
    private void HookFontRebuilt()
    {
        if (_fontRebuiltHooked) return;
        _fontRebuiltHooked = true;
        try
        {
            _fontRebuiltCb = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Font>>(new Action<Font>(_ => OnFontRepacked()));
            Font.add_textureRebuilt(_fontRebuiltCb);
        }
        catch (Exception ex) { _services.Log.Warning($"[MinimalNameplate] font-rebuilt hook failed: {ex.Message}"); }
    }

    // Font atlas repacked (textureRebuilt fires). A baked name texture is independent of the atlas once composited, so
    // we do NOT wipe _nameTex here — that full wipe (the old behavior) collided with the per-frame bake budget: during
    // a crowd load-in, new names add glyphs → the atlas repacks repeatedly → each repack wiped all names and only a
    // few could re-bake per frame, so some never appeared and others flickered (wipe → re-bake → wipe). All we do is
    // clear the null-bake caps so a name that didn't fit the old atlas gets one more try against the grown one.
    private void OnFontRepacked()
    {
        _nullBakeMisses.Clear();
    }

    // Teardown only (DestroyHudPoc): destroy every cached name texture. NOT called on a repack.
    private void ClearNameTex()
    {
        foreach (var e in _nameTex.Values) { try { if (e.tex != null) UnityEngine.Object.Destroy(e.tex); } catch { } }
        _nameTex.Clear();
        _nullBakeMisses.Clear();
    }

    // Start a fresh frame: (re)create + clear the command buffer and lazily build the shared material/MPB.
    private void BeginHudFrame()
    {
        _nameBakesThisFrame = 0;   // FIX B — reset the per-frame name-bake budget (BeginHudFrame runs once per frame)
        if (!_matDiagLogged) GameHudMaterial();   // one-shot: resolve + introspect the game HUD material even with the toggle off (MatDiag)
        if (_hudCmd == null) _hudCmd = new CommandBuffer { name = "StellarMinimalNameplateHud" };
        _hudCmd.Clear();
        _mpb ??= new MaterialPropertyBlock();
        if (_hudMat == null)
        {
            // Prefer the game's own HUD material for PP-independent occlusion; fall back to Sprites/Default otherwise.
            var game = UseGameHudMaterial ? GameHudMaterial() : null;
            if (game != null)
            {
                _hudMat = game;
                _hudMatOwned = false;   // GAME-OWNED — must NOT be destroyed
                _services.Log.Info("[MinimalNameplate] hud material: game(ui/material/hud_sprite)");
            }
            else
            {
                var sh = Shader.Find("Sprites/Default");
                if (sh == null) { _services.Log.Warning("[MinimalNameplate] Sprites/Default shader not found — rendering disabled"); return; }
                _hudMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                _hudMatOwned = true;    // WE created it → DestroyHudPoc destroys it
                _services.Log.Info("[MinimalNameplate] hud material: fallback Sprites/Default");
            }
        }
    }

    // Append one player's badge (class icon) and/or name to this frame's command buffer, billboarded + pixel-scaled.
    // The badge and the name are independent (ShowClassIcon / ShowName). When both are on the badge floats above and
    // the name sits under it; with only the name, the name is centered on the head anchor.
    private void AppendHudBadge(long uuid, Vector3 head, float headOff, Vector3 camPos, Quaternion camRot, Camera cam, int professionId)
    {
        if (_hudCmd == null || _hudMat == null) return;
        bool showIcon = ShowClassIcon;
        bool showN    = ShowName;
        if (!showIcon && !showN) return;

        var anchor = head + new Vector3(0f, headOff, 0f);
        var sA = cam.WorldToScreenPoint(anchor);
        if (sA.z <= 0f) return;                                   // behind camera
        var up = camRot * Vector3.up;
        var sB = cam.WorldToScreenPoint(anchor + up);
        float ppw = Mathf.Abs(sB.y - sA.y);                       // screen px per world unit at this depth
        if (ppw < 0.001f) return;
        float dist  = Vector3.Distance(camPos, anchor);
        float persp = Mathf.Clamp(Mathf.Pow(RefDistance / Mathf.Max(dist, 0.5f), DistanceScale), 0.35f, 2.5f);
        float worldH = SizePixels * _resScale / ppw * persp;      // world size that renders as SizePixels px on screen

        // Lift the badge only when BOTH are shown, so the name occupies the head spot below it.
        float lift = (showIcon && showN) ? SizePixels * _resScale * 0.9f : 0f;
        var badgeCenter = anchor + up * (lift / ppw);

        if (showIcon)
        {
            // bg — rounded mask tinted by class color (grayed when dead; the white class logo on top stays visible)
            _mpb!.Clear();
            _mpb.SetTexture(MainTexId, RoundedTex());
            _mpb.SetColor(ColorId, BadgeColor(uuid, professionId));
            _hudCmd.DrawMesh(BgQuad(), Matrix4x4.TRS(badgeCenter, camRot, new Vector3(worldH, worldH, worldH)), _hudMat, 0, 0, _mpb);

            // icon — atlas sub-rect (drawn after bg so it composites on top at equal depth)
            if (_iconCache.TryGetValue(professionId, out var ci) && ci.tex is Texture2D tex)
            {
                float iconH = worldH * 0.72f;
                _mpb.Clear();
                _mpb.SetTexture(MainTexId, tex);
                _mpb.SetColor(ColorId, Color.white);
                _hudCmd.DrawMesh(IconQuad(professionId, ci.uv), Matrix4x4.TRS(badgeCenter, camRot, new Vector3(iconH, iconH, iconH)), _hudMat, 0, 0, _mpb);
            }
        }

        // name — CPU-composited RGBA texture (white glyphs + black outline), drawn with Sprites/Default (ZTest LEqual)
        // → crisp + occluded. _Color tints the white fill (party/dead colors); the outline stays black.
        if (showN)
        {
            var nm = ResolveName(uuid);
            var nt = string.IsNullOrEmpty(nm) ? default : GetNameTex(uuid, nm!);
            if (nt.tex != null)
            {
                float worldPerPx = (NameSize * _resScale / ppw * persp) / IconPixels * (32f / NameFontPx);
                float nW = nt.w * worldPerPx, nH = nt.h * worldPerPx;
                // Under the badge (worldH*0.5 + gap + nH*0.5) when the icon is shown, else centered on the head anchor.
                var namePos = showIcon ? badgeCenter - up * (worldH * 0.625f + nH * 0.5f) : anchor;

                // Relation markers IN FRONT OF the name: Friend heart and/or Guild(Union) shield. The name stays
                // centered on the head anchor; markers hang off to the LEFT of the name's left edge (they do NOT shift
                // the name). (Detection fails open → no marker; see ClassIconOverlay.Relation.cs.)
                bool fr = ShowFriendIcon && IsFriendPlayer(uuid);
                bool gd = ShowGuildIcon  && IsGuildPlayer(uuid);
                int count = (fr ? 1 : 0) + (gd ? 1 : 0);
                if (count > 0)
                {
                    var right = camRot * Vector3.right;
                    float markerH = MarkerRefPx * worldPerPx;             // constant reference height → same size for every player
                                                                          // (worldPerPx already scales by distance + NameSize; independent of the per-name baked nt.h)
                    float gap = markerH * 0.18f;
                    Vector3 nameLeft = namePos - right * (nW * 0.5f);      // name's left edge (name stays centered)
                    // Order left→right: Friend, Guild, name (guild adjacent to the name). Walk right→left.
                    // Each marker uses the common height; WIDTH follows the texture aspect so the wide heart and tall shield read equal.
                    float cursor = -gap;
                    if (gd)
                    {
                        var t = UnionTex(); float w = markerH * MarkerAspect(t);
                        cursor -= w * 0.5f;
                        DrawMarker(nameLeft + right * cursor, camRot, w, markerH, t, Color.white);
                        cursor -= w * 0.5f + gap;
                    }
                    if (fr)
                    {
                        var t = FriendTex(); float w = markerH * MarkerAspect(t);
                        cursor -= w * 0.5f;
                        DrawMarker(nameLeft + right * cursor, camRot, w, markerH, t, Color.white);
                        cursor -= w * 0.5f + gap;
                    }
                }

                var nc = IsDead(uuid) ? DeadNameColor : (IsParty(uuid) ? PartyNameColor : OutsideNameColor);
                _mpb!.Clear();
                _mpb.SetTexture(MainTexId, nt.tex);
                _mpb.SetColor(ColorId, nc);
                _hudCmd.DrawMesh(BgQuad(), Matrix4x4.TRS(namePos, camRot, new Vector3(nW, nH, 1f)), _hudMat, 0, 0, _mpb);
            }
        }
    }

    // Hand this frame's command buffer to the game's HUD pass (executes after upscale, with depth bound).
    private void SubmitHudFrame()
    {
        try
        {
            var inst = _piHudInstance?.GetValue(null);
            if (inst != null && _hudCmd != null) _miAddHudCmd!.Invoke(inst, new object[] { _hudCmd });
        }
        catch (Exception ex) { _services.Log.Warning($"[MinimalNameplate] AddHudCommandBuffer failed: {ex.InnerException?.Message ?? ex.Message}"); }
    }

    // Player name: self→PlayerState.Name, party→PartyRoster, then CombatLookup.GetEntityName; nameplate ValidText
    // is a last fallback. Cached per uuid.
    private readonly Dictionary<long, string> _nameCache = new();
    private string ResolveName(long uuid)
    {
        if (_nameCache.TryGetValue(uuid, out var c) && !string.IsNullOrEmpty(c)) return c;
        string? n = null;
        try
        {
            if (uuid == _services.CombatSnapshot.LocalEntityId.Value)
            {
                var ps = _services.PlayerState.Name;
                if (!string.IsNullOrEmpty(ps)) n = ps;
            }
            if (string.IsNullOrEmpty(n))
            {
                long charId = uuid >> 16;
                foreach (var m in _services.PartyRoster.Members)
                    if (m.CharId == charId && !string.IsNullOrEmpty(m.Name)) { n = m.Name; break; }
            }
            if (string.IsNullOrEmpty(n))
            {
                var el = _services.CombatLookup.GetEntityName(new EntityId(uuid));
                if (!string.IsNullOrEmpty(el)) n = el;
            }
        }
        catch { }
        if (string.IsNullOrEmpty(n) && NameplateIconPatch.Names.TryGetValue(uuid, out var pn)) n = pn;
        if (!string.IsNullOrEmpty(n)) { _nameCache[uuid] = n!; return n!; }
        return _nameCache.TryGetValue(uuid, out var c2) ? c2 : "";
    }

    private const int NameOutlinePx = 2;
    private (Texture2D? tex, int w, int h) GetNameTex(long uuid, string text)
    {
        if (_nameTex.TryGetValue(uuid, out var e) && e.text == text && e.tex != null) return (e.tex, e.w, e.h);
        // Given up on this name (repeated null bakes) → don't retry, don't spend budget. Reset by OnFontRepacked.
        _nullBakeMisses.TryGetValue(uuid, out var miss);
        if (miss >= MaxNullBakeMisses) return (null, 0, 0);
        // Cache MISS → a bake. FIX B: over the per-frame budget, skip baking this frame (return no texture → the name
        // and its markers simply don't draw this frame and compose one/two frames later). Cache hits above never reach
        // here, so a warm name always draws and never consumes budget.
        if (_nameBakesThisFrame >= MaxNameBakesPerFrame) return (null, 0, 0);
        _nameBakesThisFrame++;
        var baked = BakeNameCpu(text);
        if (baked.tex != null) { _nameTex[uuid] = (text, baked.tex, baked.w, baked.h); _nullBakeMisses.Remove(uuid); }
        else _nullBakeMisses[uuid] = miss + 1;
        return baked;
    }

    // CPU-composite the name into an RGBA Texture2D: white glyph fill + black outline, straight alpha. Reads the
    // (alpha-only, GPU-only) font atlas via a blit + ReadPixels so we can build a real-RGB texture that Sprites/Default
    // (ZTest LEqual) draws crisp + occluded. _Color later tints white→name color; the black outline stays black.
    //
    // The atlas is read FRESH every bake (no snapshot cache): a new glyph is often packed into existing atlas free
    // space WITHOUT firing textureRebuilt and without changing the Texture ref or dims, so any cached snapshot would
    // be stale and drop that glyph (missing character). The per-frame bake budget (GetNameTex) bounds the cost.
    private (Texture2D? tex, int w, int h) BakeNameCpu(string text)
    {
        long __t0 = PerfDiag ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;   // #3 perf — see ClassIconOverlay.Perf.cs
        var font = HudFont() ?? NameFont();
        if (font == null || string.IsNullOrEmpty(text)) return (null, 0, 0);
        try { font.RequestCharactersInTexture(text, NameFontPx, FontStyle.Normal); } catch { }
        var atlas = font.material?.mainTexture;
        if (atlas == null) return (null, 0, 0);
        int aw = atlas.width, ah = atlas.height;

        // GPU atlas → CPU pixels (works even though the atlas isn't CPU-readable). Read AFTER RequestCharactersInTexture
        // so this name's glyphs are present in the atlas we sample.
        Color32[] apx;
        var tmp = RenderTexture.GetTemporary(aw, ah, 0, RenderTextureFormat.ARGB32);
        var prevRT = RenderTexture.active;
        try
        {
            Graphics.Blit(atlas, tmp);
            RenderTexture.active = tmp;
            var acpu = new Texture2D(aw, ah, TextureFormat.RGBA32, false);
            acpu.ReadPixels(new Rect(0, 0, aw, ah), 0, 0);
            acpu.Apply();
            apx = acpu.GetPixels32();
            UnityEngine.Object.Destroy(acpu);
        }
        catch (Exception ex) { _services.Log.Warning($"[MinimalNameplate] atlas read failed: {ex.Message}"); return (null, 0, 0); }
        finally { RenderTexture.active = prevRT; RenderTexture.ReleaseTemporary(tmp); }

        // measure
        int maxY = 1, minY = 0; float penW = 0;
        foreach (char c in text)
        {
            if (!font.GetCharacterInfo(c, out var ci, NameFontPx, FontStyle.Normal)) continue;
            maxY = Mathf.Max(maxY, ci.maxY); minY = Mathf.Min(minY, ci.minY); penW += ci.advance;
        }
        int pad = NameOutlinePx + 1;
        int W = Mathf.CeilToInt(penW) + pad * 2;
        int H = (maxY - minY) + pad * 2;
        var cov = new float[W * H];   // white coverage accumulator

        // rasterize glyphs into the coverage buffer
        float penX = pad; int baseline = pad - minY;
        foreach (char c in text)
        {
            if (!font.GetCharacterInfo(c, out var ci, NameFontPx, FontStyle.Normal)) { continue; }
            int gw = ci.maxX - ci.minX, gh = ci.maxY - ci.minY;
            // Bilinear across all 4 UV corners — the atlas packer ROTATES some glyphs, so their corners aren't
            // axis-aligned; an axis-aligned rect samples those from the wrong region (letters land too high/garbled).
            Vector2 bl = ci.uvBottomLeft, br = ci.uvBottomRight, tl = ci.uvTopLeft, tr = ci.uvTopRight;
            for (int gy = 0; gy < gh; gy++)
            for (int gx = 0; gx < gw; gx++)
            {
                float s = (gx + 0.5f) / gw, t = (gy + 0.5f) / gh;
                float u = (1 - s) * (1 - t) * bl.x + s * (1 - t) * br.x + (1 - s) * t * tl.x + s * t * tr.x;
                float v = (1 - s) * (1 - t) * bl.y + s * (1 - t) * br.y + (1 - s) * t * tl.y + s * t * tr.y;
                int ax = Mathf.Clamp((int)(u * aw), 0, aw - 1), ay = Mathf.Clamp((int)(v * ah), 0, ah - 1);
                float a = apx[ay * aw + ax].a / 255f;
                if (a <= 0f) continue;
                int dx = (int)penX + ci.minX + gx, dy = baseline + ci.minY + gy;
                if (dx < 0 || dx >= W || dy < 0 || dy >= H) continue;
                int di = dy * W + dx;
                if (a > cov[di]) cov[di] = a;
            }
            penX += ci.advance;
        }

        // compose: white fill where covered; black outline (dilate) elsewhere
        var outp = new Color32[W * H];
        int r = NameOutlinePx;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = y * W + x;
            float wa = cov[i];
            if (wa > 0f) { outp[i] = new Color32(255, 255, 255, (byte)(wa * 255f)); continue; }
            float oa = 0f;   // outline = max nearby coverage
            for (int oy = -r; oy <= r && oa < 1f; oy++)
            for (int ox = -r; ox <= r; ox++)
            {
                int sx = x + ox, sy = y + oy;
                if (sx < 0 || sx >= W || sy < 0 || sy >= H) continue;
                float sc = cov[sy * W + sx];
                if (sc > oa) oa = sc;
            }
            if (oa > 0f) outp[i] = new Color32(0, 0, 0, (byte)(oa * 255f));
        }

        var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
        tex.SetPixels32(outp);
        tex.Apply();
        if (PerfDiag) PerfLogNameBake(__t0, text.Length, aw, ah);
        return (tex, W, H);
    }

    private Mesh BgQuad() => _bgQuad ??= MakeQuad(new Rect(0f, 0f, 1f, 1f));
    private Mesh IconQuad(int prof, UvRect uv)
    {
        if (_iconQuads.TryGetValue(prof, out var m)) return m;
        m = MakeQuad(new Rect(uv.X, uv.Y, uv.W, uv.H));
        _iconQuads[prof] = m;
        return m;
    }

    // A centered unit quad (±0.5 in XY) with the given UV sub-rect. Il2CPP needs Il2CppStructArray for mesh channels.
    private static Mesh MakeQuad(Rect uv)
    {
        var m = new Mesh { hideFlags = HideFlags.HideAndDontSave };
        var v = new Il2CppStructArray<Vector3>(4);
        v[0] = new Vector3(-0.5f, -0.5f, 0f); v[1] = new Vector3(0.5f, -0.5f, 0f);
        v[2] = new Vector3(-0.5f,  0.5f, 0f); v[3] = new Vector3(0.5f,  0.5f, 0f);
        m.vertices = v;
        var t = new Il2CppStructArray<Vector2>(4);
        t[0] = new Vector2(uv.xMin, uv.yMin); t[1] = new Vector2(uv.xMax, uv.yMin);
        t[2] = new Vector2(uv.xMin, uv.yMax); t[3] = new Vector2(uv.xMax, uv.yMax);
        m.uv = t;
        var tri = new Il2CppStructArray<int>(6);
        tri[0] = 0; tri[1] = 2; tri[2] = 1; tri[3] = 2; tri[4] = 3; tri[5] = 1;
        m.triangles = tri;
        m.RecalculateBounds();
        return m;
    }

    // Relation-marker rendering (MarkerAspect / DrawMarker / FriendTex / UnionTex / LoadMarkerPng) lives in the sibling
    // partial ClassIconOverlay.Markers.cs (split out to keep this file under the 500-LoC cap).

    private void DestroyHudPoc()
    {
        try { _hudCmd?.Dispose(); } catch { }
        _hudCmd = null;
        // Only destroy the material if WE own it (the Sprites/Default fallback); the borrowed game HUD material is
        // game-owned — just drop our reference.
        if (_hudMat != null) { if (_hudMatOwned) { try { UnityEngine.Object.Destroy(_hudMat); } catch { } } _hudMat = null; _hudMatOwned = false; }
        if (_bgQuad != null) { try { UnityEngine.Object.Destroy(_bgQuad); } catch { } _bgQuad = null; }
        foreach (var m in _iconQuads.Values) { try { UnityEngine.Object.Destroy(m); } catch { } }
        _iconQuads.Clear();
        if (_friendTex != null) { try { UnityEngine.Object.Destroy(_friendTex); } catch { } _friendTex = null; }
        if (_unionTex != null) { try { UnityEngine.Object.Destroy(_unionTex); } catch { } _unionTex = null; }
        ClearNameTex();
        _mpb = null;
    }
}
