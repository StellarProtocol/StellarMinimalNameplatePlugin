using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.Rendering;

namespace Stellar.MinimalNameplate;

// Injected MonoBehaviour so we can run our positioning at render time (after the game moves the camera each frame).
internal sealed class LateUpdater : MonoBehaviour
{
    public LateUpdater(IntPtr ptr) : base(ptr) { }
    public Action? OnLate;
    private void LateUpdate() { try { OnLate?.Invoke(); } catch { } }
}

/// <summary>
/// Draws a role-colored class badge (+ optional player name) over players' heads by submitting draws into the
/// game's HUD render pass (Panda.Hud.HudRenderPass) — crisp (native-res, after DLSS/FSR upscale) AND occluded by
/// world geometry, exactly like the game's nameplate. See Knowledge Base\HUD-RenderPass-Crisp-Occluded.md.
///
/// Scope: every AOI player (uuid low-16 == 640) with a resolvable profession. Position, dead state, profession and
/// name are read live off the entity (ZEntity.GetAttr / ModelGoComp) so they stay correct across mount/revive/
/// class-switch. The game's own plate is hidden by NameplateIconPatch while this overlay is enabled.
/// </summary>
internal sealed partial class ClassIconOverlay
{
    // Runtime-tunable (sliders in the plugin window).
    public static float WorldHeadOffset = 0.55f;    // world units above the head anchor
    private const float DeadHeadOffset  = 0.80f;    // dead players' badge sits higher
    public static float SizePixels      = 50f;      // badge on-screen height in px at the reference distance
    public static float DistanceScale   = 0.40f;    // 0 = constant size, 1 = full perspective. 0.40 ≈ nameplate falloff.
    public static float RefDistance     = 15f;      // camera distance (m) at which the badge is exactly SizePixels
    public static bool  ShowClassIcon   = true;     // render the class badge (icon); independent of the name
    public static bool  ShowName        = false;    // render the player's name (under the badge, or on the head if no badge)
    public static bool  ShowFriendIcon  = true;     // draw the Friend (heart) marker in front of the name (needs ShowName)
    public static bool  ShowGuildIcon   = true;     // draw the Guild(Union) (shield) marker in front of the name (needs ShowName)
    public static float NameSize        = 64f;      // name on-screen size, independent of the icon Size-px slider
    private const float RefScreenHeight = 1440f;    // sizes tuned at 1440p; scaled by Screen.height/this
    private float       _resScale       = 1f;       // Screen.height / RefScreenHeight, recomputed per frame
    private const float IconPixels      = 128f;     // reference canvas-pixel size (name-scale reference)
    private const int   MaxPlayers      = 200;      // AOI players tracked per rebuild (collection safety bound)
    public static int   MaxIcons        = 100;      // badges drawn per frame, nearest-first (raid/crowd cap); user-tunable slider

    // Flip to true in source to re-enable the recurring diagnostics (periodic state dump + sprite-scan matches) in
    // LogOutput.log. Off by default so a working install stays quiet; one-time load/resolve lines always log.
    internal static bool Diag = false;

    private readonly IPluginServices _services;
    private bool _enabled;
    private bool _subscribed;

    private const int  AttrProfessionId = 220;   // broadcast attribute key holding the profession id
    private const long PlayerTypeMarker = 640;   // uuid low-16 bits == 640 → a player entity

    private Texture2D? _rounded; // shared rounded-rect mask for the badge bg + border

    // AOI player list, rebuilt on a throttle; positions update every frame.
    private readonly List<(long uuid, int prof)> _players = new();
    private double _rebuildTimer;

    // Per-profession icon cache (populated by piggyback sprite scan — never the shared loader).
    private readonly Dictionary<int, (object tex, UvRect uv)> _iconCache = new();
    private readonly Dictionary<int, string> _profSprite = new();
    private double _scanTimer;

    public ClassIconOverlay(IPluginServices services) => _services = services;

    private GameObject? _driverGo;
    private static bool _luRegistered;
    private Camera.CameraCallback? _preCullCb;
    private Il2CppSystem.Action<ScriptableRenderContext, Camera>? _beginCamCb;
    private Canvas.WillRenderCanvases? _willRenderCb;
    private int _preCullFrame = -1;

    public void SetEnabled(bool value)
    {
        if (value == _enabled) return;
        _enabled = value;
        if (value) EnsureDriver();
        else { DestroyDriver(); DestroyPool(); }
    }

    // Drive at render time (after the game moves the camera) so badges don't lag a frame behind. Multiple hooks are
    // installed and deduped per frame — willRenderCanvases is the uGUI-commit point and wins; the SRP/built-in and
    // LateUpdate hooks are fallbacks.
    private void EnsureDriver()
    {
        if (_driverGo != null || _subscribed) return;
        try
        {
            if (!_luRegistered) { ClassInjector.RegisterTypeInIl2Cpp<LateUpdater>(); _luRegistered = true; }
            _driverGo = new GameObject("StellarMinimalNameplateDriver");
            UnityEngine.Object.DontDestroyOnLoad(_driverGo);
            _driverGo.hideFlags = HideFlags.HideAndDontSave;
            _driverGo.AddComponent<LateUpdater>().OnLate = () => { if (Time.frameCount - _preCullFrame > 1) OnUpdate(Time.deltaTime); };

            try
            {
                _preCullCb = DelegateSupport.ConvertDelegate<Camera.CameraCallback>(new Action<Camera>(OnPreCull));
                Camera.onPreCull += _preCullCb;
            }
            catch (Exception ex) { _services.Log.Warning($"[MinimalNameplate] onPreCull hook failed: {ex.Message}"); _preCullCb = null; }
            try
            {
                _beginCamCb = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Camera>>(
                    new Action<ScriptableRenderContext, Camera>(OnBeginCam));
                RenderPipelineManager.add_beginCameraRendering(_beginCamCb);
            }
            catch (Exception ex) { _services.Log.Warning($"[MinimalNameplate] SRP hook failed: {ex.Message}"); _beginCamCb = null; }
            try
            {
                _willRenderCb = DelegateSupport.ConvertDelegate<Canvas.WillRenderCanvases>(new Action(OnWillRenderCanvases));
                Canvas.willRenderCanvases += _willRenderCb;
            }
            catch (Exception ex) { _services.Log.Warning($"[MinimalNameplate] willRenderCanvases hook failed: {ex.Message}"); _willRenderCb = null; }
            _services.Log.Info("[MinimalNameplate] render driver active");
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[MinimalNameplate] driver failed ({ex.Message}) — using IFramework.Update");
            _services.Framework.Update += OnUpdate; _subscribed = true;
        }
    }

    private void DestroyDriver()
    {
        if (_preCullCb != null) { try { Camera.onPreCull -= _preCullCb; } catch { } _preCullCb = null; }
        if (_beginCamCb != null) { try { RenderPipelineManager.remove_beginCameraRendering(_beginCamCb); } catch { } _beginCamCb = null; }
        if (_willRenderCb != null) { try { Canvas.willRenderCanvases -= _willRenderCb; } catch { } _willRenderCb = null; }
        if (_subscribed) { _services.Framework.Update -= OnUpdate; _subscribed = false; }
        if (_driverGo != null) { try { UnityEngine.Object.Destroy(_driverGo); } catch { } _driverGo = null; }
    }

    private void OnWillRenderCanvases() { try { DriveUpdate(); } catch { } }
    private void OnPreCull(Camera cam) { try { DriveUpdate(); } catch { } }
    private void OnBeginCam(ScriptableRenderContext ctx, Camera cam) { try { DriveUpdate(); } catch { } }

    private void DriveUpdate()
    {
        int f = Time.frameCount;
        if (f == _preCullFrame) return;   // once per frame
        _preCullFrame = f;
        OnUpdate(Time.deltaTime);
    }

    public void Dispose()
    {
        DestroyDriver();
        DestroyPool();
    }

    private double _diagTimer;
    private void OnUpdate(float dt)
    {
        try
        {
            if (!_services.ClientState.IsLoggedIn) { _players.Clear(); NameplateIconPatch.ClearHideMask(); return; }
            var cam = MainCamera();
            if (cam == null) return;
            // MainCamera() can hand back a DESTROYED camera during a scene transition — reading transform throws;
            // guard and skip the frame until a live one exists.
            Vector3 camPos; Quaternion camRot;
            try { var ct = cam.transform; camPos = ct.position; camRot = ct.rotation; }
            catch { return; }

            _rebuildTimer += dt;
            if (_players.Count == 0 || _rebuildTimer >= 0.5)
            {
                _rebuildTimer = 0;
                RebuildPlayers(camPos);
                // Re-hide any game plates that came back via a rebuild path we don't patch (dungeons/combat).
                if (NameplateIconPatch.HidePlate) NameplateIconPatch.ReapplyAll();
            }

            // Piggyback icon resolution: scan already-loaded sprites for any professions we still need.
            _scanTimer += dt;
            if (_scanTimer >= 1.0 && AnyUncached()) { _scanTimer = 0; ScanSprites(); }

            _resScale = Mathf.Max(Screen.height, 1) / RefScreenHeight;
            if (!EnsureHudPass()) return;

            // Mirror the game's own nameplate visibility. When the game hides the HUD globally (HideUI key, cutscene,
            // photo/camera mode, GM, full-screen menu) draw nothing; per player, honor the game's player/other
            // head-info setting. BeginHudFrame + an empty SubmitHudFrame clears our badges cleanly for a hidden frame.
            bool hudShown = GameHudShown();
            BeginHudFrame();
            int w = 0;
            if (hudShown)
            {
                foreach (var (uuid, professionId) in _players)
                {
                    if (w >= MaxIcons) break;
                    if (NameplateIconPatch.IsPlateHiddenByGame(uuid)) continue;   // tag/hide-and-seek, disappear, dialog, …
                    if (IsHiddenByHideSeek(uuid)) continue;   // hide-and-seek hides the MODEL (not the HUD plate) — mirror it
                    if (!GameShowsPlateFor(uuid)) continue;   // honor the game's per-type nameplate setting
                    if (!TryGetHeadWorld(uuid, out var head)) continue;
                    float headOff = IsDead(uuid) ? DeadHeadOffset : WorldHeadOffset;
                    AppendHudBadge(uuid, head, headOff, camPos, camRot, cam, professionId);
                    w++;
                }
            }
            SubmitHudFrame();

            if (Diag)
            {
                _diagTimer += dt;
                if (_diagTimer >= 5.0)
                {
                    _diagTimer = 0;
                    _services.Log.Info($"[MinimalNameplate] players={_players.Count} shown={w} hudShown={hudShown} name={ShowName}");
                }
            }
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[MinimalNameplate] update error: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    // Every AOI player (uuid low-16 == 640) with a resolvable profession, scored by camera distance. Throttled — not
    // called every frame. When more players are present than we draw (MaxIcons), the NEAREST win: a raid/crowd no
    // longer arbitrarily drops whoever enumerated first. (The old uuid-order + 40 cap left far-AND-near players blank,
    // with the game plate hidden for them too, so those players showed nothing at all.)
    private void RebuildPlayers(Vector3 camPos)
    {
        _players.Clear();
        if (!Resolve()) return;

        var scored = new List<(long uuid, int prof, float dist)>();

        var local = _services.CombatSnapshot.LocalEntityId;
        int selfProf = local.Value != 0 ? ResolveProfession(local.Value) : 0;
        if (selfProf > 0) scored.Add((local.Value, selfProf, PlayerDist(local.Value, camPos)));

        try
        {
            var mgr  = _piEntMgrInstance!.GetValue(null);
            var dict = mgr == null ? null : (_piEntityDict != null ? _piEntityDict.GetValue(mgr) : _fiEntityDict?.GetValue(mgr));
            var keys = dict?.GetType().GetProperty("Keys")?.GetValue(dict);
            if (keys != null)
            {
                foreach (var k in WalkIl2Cpp(keys))
                {
                    long uuid = Convert.ToInt64(k);
                    if ((uuid & 0xFFFF) != PlayerTypeMarker) continue; // players only
                    if (uuid == local.Value) continue;                 // self already scored
                    int prof = ResolveProfession(uuid);
                    if (prof > 0) scored.Add((uuid, prof, PlayerDist(uuid, camPos)));
                    if (scored.Count >= MaxPlayers) break;             // safety bound on tracked players
                }
            }
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[MinimalNameplate] RebuildPlayers error: {ex.InnerException?.Message ?? ex.Message}");
        }

        // Nearest first so the per-frame MaxIcons draw budget goes to the closest (most relevant) players; ties broken
        // by uuid for a stable order (entityDict order can shuffle, briefly swapping which class a badge shows).
        scored.Sort((a, b) => a.dist != b.dist ? a.dist.CompareTo(b.dist) : a.uuid.CompareTo(b.uuid));
        foreach (var s in scored) _players.Add((s.uuid, s.prof));
    }

    // Camera distance to a player's head anchor; unresolvable (out of AOI / model unloaded) sorts last.
    private float PlayerDist(long uuid, Vector3 camPos)
        => TryGetHeadWorld(uuid, out var h) ? Vector3.Distance(camPos, h) : float.MaxValue;

    private bool AnyUncached()
    {
        foreach (var (_, prof) in _players) if (!_iconCache.ContainsKey(prof)) return true;
        return false;
    }

    // Piggyback: find each needed profession's sprite among already-loaded sprites. We NEVER call the shared async
    // loader, so other plugins' icons are unaffected.
    private void ScanSprites()
    {
        try
        {
            var needed = new Dictionary<string, int>();
            foreach (var (_, prof) in _players)
            {
                if (_iconCache.ContainsKey(prof)) continue;
                if (!_profSprite.TryGetValue(prof, out var nm))
                {
                    try { var ip = _services.GameData.Combat.GetProfession(prof)?.IconPath; nm = string.IsNullOrEmpty(ip) ? null! : LastSeg(ip!); }
                    catch { nm = null!; }
                    if (!string.IsNullOrEmpty(nm)) _profSprite[prof] = nm;
                }
                if (!string.IsNullOrEmpty(nm)) needed[nm] = prof;
            }
            if (needed.Count == 0) return;

            var all = Resources.FindObjectsOfTypeAll<Sprite>();
            int scanned = all?.Length ?? 0, matched = 0;
            for (int i = 0; i < scanned; i++)
            {
                var s = all![i];
                if (s == null) continue;
                if (!needed.TryGetValue(s.name, out var prof) || _iconCache.ContainsKey(prof)) continue;
                var tex = s.texture;
                if (tex == null) continue;
                float tw = tex.width, th = tex.height;
                if (tw <= 0 || th <= 0) continue;
                var r = s.textureRect;
                _iconCache[prof] = (tex, new UvRect(r.x / tw, r.y / th, r.width / tw, r.height / th));
                matched++;
            }
            if (matched > 0 && Diag) _services.Log.Info($"[MinimalNameplate] sprite scan: matched={matched} cached={_iconCache.Count} needed={needed.Count}");
        }
        catch (Exception ex) { _services.Log.Warning($"[MinimalNameplate] ScanSprites error: {ex.Message}"); }
    }

    private static string LastSeg(string p) { int i = p.LastIndexOf('/'); return i < 0 ? p : p.Substring(i + 1); }

    // Is this player in my party? (self counts as party.) Matched by CharId = uuid >> 16.
    private bool IsParty(long uuid)
    {
        if (uuid == _services.CombatSnapshot.LocalEntityId.Value) return true;
        long charId = uuid >> 16;
        try { foreach (var m in _services.PartyRoster.Members) if (m.CharId == charId) return true; }
        catch { }
        return false;
    }

    private static readonly Color PartyNameColor   = new Color(0.45f, 0.90f, 1.00f, 1f); // cyan — party/self
    private static readonly Color OutsideNameColor = Color.white;                        // white — not in party
    private static readonly Color DeadNameColor    = new Color(1.00f, 0.25f, 0.25f, 1f); // red — dead

    private const int AttrDeadType = 78;     // EAttrType.AttrDeadType
    private const int AttrHpId      = 11310; // EAttrType.AttrHp
    private const int AttrMaxHpId   = 11320; // EAttrType.AttrMaxHp

    // Dead detection. PRIMARY is a LIVE read off the ZEntity (GetAttr<long>(AttrHp/AttrMaxHp)) — the entity's own
    // attribute store, which updates on revive (unlike the EntityDetail snapshot, stale at dt=2 / hp stripped).
    private bool IsDead(long uuid)
    {
        if (TryLiveDead(uuid, out var live)) return live;
        try
        {
            var attrs = _services.EntityDetail.GetAttributes(new EntityId(uuid));
            if (attrs != null)
            {
                if (attrs.TryGetValue(AttrHpId, out var hp)) return hp <= 0;
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
            long maxhp = Convert.ToInt64(_miGetAttrLong!.Invoke(ent, new object[] { _attrMaxHpBox!, true }));
            if (maxhp <= 0) return false;
            long hp = Convert.ToInt64(_miGetAttrLong!.Invoke(ent, new object[] { _attrHpBox!, true }));
            dead = hp <= 0;
            return true;
        }
        catch { return false; }
    }

    // ZEntity.GetAttr<long>(EAttrType, bool checkInherit) — resolved once, closed over long, with boxed enum keys.
    private MethodInfo? _miGetAttrLong;
    private object?     _attrHpBox;
    private object?     _attrMaxHpBox;
    private object?     _attrProfBox;
    private bool        _attrApiResolved;
    private bool        _attrApiOk;
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
                foreach (var m in entT.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "GetAttr" || !m.IsGenericMethodDefinition) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == attrEnum && ps[1].ParameterType == typeof(bool))
                    { _miGetAttrLong = m.MakeGenericMethod(typeof(long)); break; }
                }
                _attrHpBox    = Enum.ToObject(attrEnum, AttrHpId);
                _attrMaxHpBox = Enum.ToObject(attrEnum, AttrMaxHpId);
                _attrProfBox  = Enum.ToObject(attrEnum, AttrProfessionId);
                _attrApiOk    = _miGetAttrLong != null && _attrHpBox != null && _attrMaxHpBox != null;
            }
        }
        catch (Exception ex) { _services.Log.Warning($"[MinimalNameplate] live-attr api resolve failed: {ex.Message}"); _attrApiOk = false; }
        _services.Log.Info($"[MinimalNameplate] live-attr api ok={_attrApiOk}");
        return _attrApiOk;
    }

    private object? GetEntityObj(long uuid)
    {
        try
        {
            if (!Resolve()) return null;
            var mgr = _piEntMgrInstance!.GetValue(null);
            return mgr == null ? null : _miGetEntity!.Invoke(mgr, new object[] { uuid });
        }
        catch { return null; }
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
            if (attrs != null && attrs.TryGetValue(AttrProfessionId, out var p) && p > 0) return (int)p;
        }
        catch { }
        return 0;
    }

    private bool TryLiveProfession(long uuid, out int prof)
    {
        prof = 0;
        if (!EnsureAttrApi() || _attrProfBox == null) return false;
        var ent = GetEntityObj(uuid);
        if (ent == null) return false;
        try { prof = (int)Convert.ToInt64(_miGetAttrLong!.Invoke(ent, new object[] { _attrProfBox, true })); return prof > 0; }
        catch { return false; }
    }

    private static List<object> WalkIl2Cpp(object collection)
    {
        var res = new List<object>();
        var getEnum = FindNoArg(collection.GetType(), "GetEnumerator");
        var en = getEnum?.Invoke(collection, null);
        if (en == null) return res;
        var enT  = en.GetType();
        var move = FindNoArg(enT, "MoveNext");
        var cur  = enT.GetProperty("Current");
        if (move == null || cur == null) return res;
        while ((bool)move.Invoke(en, null)!)
        {
            var v = cur.GetValue(en);
            if (v != null) res.Add(v);
        }
        return res;
    }

    private static MethodInfo? FindNoArg(Type t, string name)
    {
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.Name == name && m.GetParameters().Length == 0) return m;
        return null;
    }

    private static Font? _font;
    private static Font? NameFont()
    {
        if (_font != null) return _font;
        try { _font = Font.CreateDynamicFontFromOSFont("Arial", 32); } catch { _font = null; }
        return _font;
    }

    // Dead players' badge background is grayed out (the white class logo on top stays visible, so the class is still
    // readable — just color-drained to read as "dead", pairing with the red name).
    private static readonly Color DeadBadgeColor = new Color(0.40f, 0.40f, 0.40f, 0.95f);
    private Color BadgeColor(long uuid, int prof) => IsDead(uuid) ? DeadBadgeColor : ClassColor(prof);

    // Class color by profession id: 1/2/3/4/11 = red, 5/13 = green, 9/12 = blue, else gray.
    private static Color ClassColor(int prof) => prof switch
    {
        1 or 2 or 3 or 4 or 11 => new Color(0.74f, 0.16f, 0.16f, 0.95f), // red
        5 or 13                => new Color(0.16f, 0.58f, 0.27f, 0.95f), // green
        9 or 12                => new Color(0.18f, 0.40f, 0.82f, 0.95f), // blue
        _                      => new Color(0.44f, 0.44f, 0.44f, 0.95f), // gray (unknown)
    };

    // A high-res anti-aliased rounded-rect white mask (tinted per-use). Generated once.
    private Texture2D RoundedTex()
    {
        if (_rounded != null) return _rounded;
        const int s = 256, r = 56;
        var t = new Texture2D(s, s, TextureFormat.RGBA32, false)
        { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color[s * s];
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float cx = Mathf.Clamp(x, r, s - 1 - r);
                float cy = Mathf.Clamp(y, r, s - 1 - r);
                float dx = x - cx, dy = y - cy;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01(r + 0.5f - dist);
                px[y * s + x] = new Color(1f, 1f, 1f, a);
            }
        t.SetPixels(px);
        t.Apply();
        _rounded = t;
        return t;
    }

    private void DestroyPool()
    {
        DestroyHudPoc();
        _iconCache.Clear(); // game-owned textures; just drop our references
        if (_rounded != null) { try { UnityEngine.Object.Destroy(_rounded); } catch { } _rounded = null; }
    }

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

    // A STABLE anchor above the head: model-root Position + HUD height (NOT the animated head bone, so the badge
    // follows movement/jumps but doesn't bob). Position tracks to the model draw distance — unlike GetHudPos, which
    // the game freezes past nameplate range — so far-but-visible players still track.
    private bool TryGetHeadWorld(long uuid, out Vector3 head)
    {
        head = default;
        if (!Resolve()) return false;

        var mgr = _piEntMgrInstance!.GetValue(null);
        if (mgr == null) return false;
        var ent = _miGetEntity!.Invoke(mgr, new object[] { uuid });
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
