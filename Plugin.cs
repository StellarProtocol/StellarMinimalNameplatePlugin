using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.MinimalNameplate;

// Core: config load, overlay + patch install, window/launcher registration. The settings window body is
// Plugin.Settings.cs, the help popover Plugin.Help.cs, and the house-style kit Plugin.Style.cs.
public sealed partial class Plugin : IStellarPlugin
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
    private bool _showReviveSealed;

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
        _showReviveSealed = _cfg.Get<bool>("minimalnameplate_showrevivesealed", true);
        ClassIconOverlay.SizePixels    = _cfg.Get<float>("minimalnameplate_sizepx", 50f);
        ClassIconOverlay.NameSize      = _cfg.Get<float>("minimalnameplate_namepx", 64f);
        ClassIconOverlay.MaxIcons      = (int)_cfg.Get<float>("minimalnameplate_maxicons", 100f);
        ClassIconOverlay.ShowClassIcon = _showIcon;
        ClassIconOverlay.ShowName      = _showName;
        ClassIconOverlay.ShowFriendIcon = _showFriendIcon;
        ClassIconOverlay.ShowGuildIcon  = _showGuildIcon;
        ClassIconOverlay.ShowReviveSealed = _showReviveSealed;

        NameplateIconPatch.Install(_services.Harmony.Create("nameplate"), _services.Log.Info);
        NameplateIconPatch.HidePlate = _enabled;   // hide the game's plate while our overlay is on

        _overlay = new ClassIconOverlay(_services);
        _overlay.SetEnabled(_enabled);

        // The help popover first: the settings "?" fills read _tipWindow, and they may be polled during the main build.
        _tipWindow = RegisterTipWindow();
        _services.Framework.Update += TipRepositionTick;

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
            OnClose: () => { CloseTip(); _window!.SetVisible(false); }));

        // The phase stays World through the logout transition, so ShouldRender alone would leave the windows up on the
        // loading screen; close them synchronously on Logout (house rule, Stellar-Design-Language.md §5).
        _services.ClientState.Logout += OnLogoutHideWindows;

        // Title stays the fixed literal "Nameplates" — the stable pin-identity key (ILauncher.cs:49-50) —
        // so a pinned tile survives a language change; TitleProvider carries the live-localized display.
        _launcher = _services.Launcher.Register(new LauncherEntry(
            Title:   "Nameplates",
            IconPng: LoadIconPng(),
            IconKey: null,
            // BringToFront even from hidden: the help popover fronts itself (switching "?"s), and ZFront is STICKY — a
            // fronted window outranks every never-fronted one forever — so once any of our windows fronts, every open
            // must front (CLAUDE.md Rule 5 exception; WindowBuilder-Patterns.md §"ZFront is STICKY").
            OnOpen:  () => { _window.SetVisible(true); _window.BringToFront(); })
        { Group = LauncherGroup.Plugin,
          // Re-localize the tile DISPLAY on a language change; Title above never changes (pin identity).
          TitleProvider = () => _loc.T("mn.title"),
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World });

        _services.Log.Info("[MinimalNameplate] constructed");
    }

    private void OnLogoutHideWindows()
    {
        CloseTip();
        _window.SetVisible(false);
    }

    public void Dispose()
    {
        try { _services.ClientState.Logout -= OnLogoutHideWindows; } catch { }
        try { _services.Framework.Update -= TipRepositionTick; } catch { }
        _tipWindow.Remove();
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
