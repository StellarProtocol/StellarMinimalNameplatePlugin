using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.MinimalNameplate;

public sealed class Plugin : IStellarPlugin
{
    public string Name => "MinimalNameplate";

    private readonly IPluginServices  _services;
    private readonly ILocalization    _loc;
    private readonly IConfigSection   _cfg;
    private readonly ClassIconOverlay _overlay;
    private readonly IWindowControl   _window;
    private readonly IDisposable      _launcher;

    private bool _enabled;
    private bool _showIcon;
    private bool _showName;
    private bool _showFriendIcon;
    private bool _showGuildIcon;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _loc = services.Localization;
        _cfg = _services.Config.GetSection("settings");

        _enabled  = _cfg.Get<bool> ("minimalnameplate_enabled",  false);
        _showIcon = _cfg.Get<bool> ("minimalnameplate_showicon", true);
        _showName = _cfg.Get<bool> ("minimalnameplate_showname", true);
        _showFriendIcon = _cfg.Get<bool>("minimalnameplate_showfriend", _cfg.Get<bool>("minimalnameplate_showrelation", true));
        _showGuildIcon  = _cfg.Get<bool>("minimalnameplate_showguild",  _cfg.Get<bool>("minimalnameplate_showrelation", true));
        ClassIconOverlay.SizePixels    = _cfg.Get<float>("minimalnameplate_sizepx", 50f);
        ClassIconOverlay.NameSize      = _cfg.Get<float>("minimalnameplate_namepx", 64f);
        ClassIconOverlay.MaxIcons      = (int)_cfg.Get<float>("minimalnameplate_maxicons", 100f);
        ClassIconOverlay.ShowClassIcon = _showIcon;
        ClassIconOverlay.ShowName      = _showName;
        ClassIconOverlay.ShowFriendIcon = _showFriendIcon;
        ClassIconOverlay.ShowGuildIcon  = _showGuildIcon;

        NameplateIconPatch.Install(_services.Harmony.Create("nameplate"), _services.Log.Info);
        NameplateIconPatch.HidePlate = _enabled;   // hide the game's plate while our overlay is on

        _overlay = new ClassIconOverlay(_services);
        _overlay.SetEnabled(_enabled);

        _window = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "minimalnameplate.main",
                Title:       _loc.T("mn.title"),
                DefaultRect: new WindowRect(_services.Framework.ScreenWidth - 460f, 20f, 440f, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false,
              // Gameplay tool: draw only while in-world, and hide during loading screens.
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                   && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            Root: BuildRoot(),
            OnClose: () => _window!.SetVisible(false)));

        _launcher = _services.Launcher.Register(new LauncherEntry(
            Title:   _loc.T("mn.title"),
            IconPng: LoadIconPng(),
            IconKey: null,
            OnOpen:  () => _window.SetVisible(true))
        { Group = LauncherGroup.Plugin,
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World });

        _services.Log.Info("[MinimalNameplate] constructed");
    }

    private HudElement BuildRoot() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => _loc.T("mn.header"), Emphasis: true),
        new RowElement(new HudElement[]
        {
            new ToggleElement(
                Label: () => "",
                Get:   () => _enabled,
                Set:   v  =>
                {
                    _enabled = v;
                    _overlay.SetEnabled(v);
                    NameplateIconPatch.HidePlate = v;   // hide the game's plate while our overlay is on
                    NameplateIconPatch.ReapplyAll();
                    _cfg.Set<bool>("minimalnameplate_enabled", v);
                    _cfg.Save();
                }),
            new TextElement(() => _loc.T("mn.enable")),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new ToggleElement(
                Label: () => "",
                Get:   () => _showIcon,
                Set:   v  =>
                {
                    _showIcon = v;
                    ClassIconOverlay.ShowClassIcon = v;
                    _cfg.Set<bool>("minimalnameplate_showicon", v);
                    _cfg.Save();
                }),
            new TextElement(() => _loc.T("mn.showIcon")),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new ToggleElement(
                Label: () => "",
                Get:   () => _showName,
                Set:   v  =>
                {
                    _showName = v;
                    ClassIconOverlay.ShowName = v;
                    _cfg.Set<bool>("minimalnameplate_showname", v);
                    _cfg.Save();
                }),
            new TextElement(() => _loc.T("mn.showName")),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new ToggleElement(
                Label: () => "",
                Get:   () => _showFriendIcon,
                Set:   v  =>
                {
                    _showFriendIcon = v;
                    ClassIconOverlay.ShowFriendIcon = v;
                    _cfg.Set<bool>("minimalnameplate_showfriend", v);
                    _cfg.Save();
                }),
            new TextElement(() => _loc.T("mn.showFriend")),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new ToggleElement(
                Label: () => "",
                Get:   () => _showGuildIcon,
                Set:   v  =>
                {
                    _showGuildIcon = v;
                    ClassIconOverlay.ShowGuildIcon = v;
                    _cfg.Set<bool>("minimalnameplate_showguild", v);
                    _cfg.Save();
                }),
            new TextElement(() => _loc.T("mn.showGuild")),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => _loc.T("mn.badgeSize"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 80f),
            new CellElement(new SliderElement(
                Get: () => ClassIconOverlay.SizePixels,
                Set: v  => { ClassIconOverlay.SizePixels = v; _cfg.Set<float>("minimalnameplate_sizepx", v); _cfg.Save(); },
                Min: 16f, Max: 160f), Weight: 1f),
            new CellElement(new TextElement(() => $"{ClassIconOverlay.SizePixels:F0}"), Width: 52f),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => _loc.T("mn.nameSize"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 80f),
            new CellElement(new SliderElement(
                Get: () => ClassIconOverlay.NameSize,
                Set: v  => { ClassIconOverlay.NameSize = v; _cfg.Set<float>("minimalnameplate_namepx", v); _cfg.Save(); },
                Min: 16f, Max: 160f), Weight: 1f),
            new CellElement(new TextElement(() => $"{ClassIconOverlay.NameSize:F0}"), Width: 52f),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => _loc.T("mn.maxShown"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 80f),
            new CellElement(new SliderElement(
                Get: () => ClassIconOverlay.MaxIcons,
                Set: v  =>
                {
                    int n = (int)Math.Round(v);
                    if (n < 5) n = 5; else if (n > 200) n = 200;   // 200 = the AOI-tracking safety bound
                    ClassIconOverlay.MaxIcons = n;
                    _cfg.Set<float>("minimalnameplate_maxicons", n);
                    _cfg.Save();
                },
                Min: 5f, Max: 200f), Weight: 1f),
            new CellElement(new TextElement(() => $"{ClassIconOverlay.MaxIcons}"), Width: 52f),
        }, Gap: 6f),
    }, Gap: 8f);

    public void Dispose()
    {
        NameplateIconPatch.HidePlate = false;
        NameplateIconPatch.ReapplyAll();
        NameplateIconPatch.Uninstall();
        _overlay.Dispose();
        _launcher.Dispose();
        _window.Remove();
    }

    private static byte[]? LoadIconPng()
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.MinimalNameplate.nameplate-icon.png");
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
