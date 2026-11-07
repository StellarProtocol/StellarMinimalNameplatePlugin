using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MinimalNameplate;

// Per-setting help (house help popover; reference StellarMaestroPlugin Plugin.Help*.cs). The window framework has no
// hover tooltip, so every setting row ends in a round "?" that opens ONE shared floating popover anchored under it:
//   [32 px accent tile with a white bold "?"] [bold setting name / muted "SECTION · HELP"] … [round ✕]
//   then the help text in one raised tile, 360 px wide.
// No framework title bar (the header carries the title and the close); with ShowTitleBar = false the whole frame is the
// drag handle. Re-clicking the open "?" closes it. Content is registered by key at build time (HelpDot).
public sealed partial class Plugin
{
    private readonly Dictionary<string, (Func<string> Title, Func<string> Body, string SectionKey)> _helpFns = new();
    private readonly Dictionary<string, bool> _helpHover = new();

    private IWindowControl _tipWindow = null!;
    private string _tipKey = "";   // which help entry the popover shows; "" = closed
    private const float TipWidth    = 360f;
    private const float TipIconSize = 32f;
    private const float HelpSize    = 24f;   // the round "?" — exactly 24 × 24 (RoundRadius, Plugin.Style.cs)
    private WindowRect _tipRect;     // where the popover should sit (re-applied for a few frames after opening)
    private int _tipRepositionTicks;

    // Registers help content for `key` and returns its "?". Append it as the LAST child of a row so the "?"s line up in
    // the panel's right-edge column. `sectionKey` = the Lang key of the section caption (the popover subtitle).
    private HudElement HelpDot(string key, string sectionKey, Func<string> title, Func<string> body)
    {
        _helpFns[key] = (title, body, sectionKey);
        return new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                // Height strut = the side; the label box sets the width. NOTHING taller than 24 may sit inside, or the
                // circle turns oval. CenterGlyphs centres the drawn "?" in its box.
                new SpacerElement(0f, HelpSize),
                new TextElement(() => "?", Color: () => NeutralText(), Width: HelpSize, Align: TextAlign.Center,
                    NoWrap: true, FontSize: 12) { Bold = true, CenterGlyphs = true },
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { })                       // real work is in OnClickWithRect (needs the button's rect)
            {
                Bare = true,
                OnHover = over => _helpHover[key] = over,
                OnClickWithRect = r => ToggleTip(key, r),
            })
        {
            FillColorFn = () => _tipKey == key && _tipWindow.IsShown ? Over(AccentColor() with { A = 0.35f }, NeutralFill())
                              : _helpHover.TryGetValue(key, out var h) && h ? Over(TextColor() with { A = HoverMix }, NeutralFill())
                              : NeutralFill(),
            CornerRadius = RoundRadius(HelpSize), PaddingX = 0f, PaddingY = 0f,
        };
    }

    private void ToggleTip(string key, WindowRect r)
    {
        if (_tipKey == key && _tipWindow.IsShown) { CloseTip(); return; }
        _tipKey = key;
        // OnClickWithRect reports SCREEN PIXELS; SetRect takes CANVAS units — scale first, or the popover drifts off its
        // "?" whenever the UI scale isn't 1 (WindowBuilder-Patterns.md, click-to-open tooltip).
        float k = _services.Framework.ScreenWidth > 0 && _services.Framework.CanvasWidth > 1f
            ? _services.Framework.CanvasWidth / _services.Framework.ScreenWidth : 1f;
        r = new WindowRect(r.X * k, r.Y * k, r.Width * k, r.Height * k);
        float canvasH = _services.Framework.CanvasHeight > 1f ? _services.Framework.CanvasHeight : _services.Framework.ScreenHeight;
        // Below the "?", right-aligned to it, so it opens to the LEFT of the "?" column instead of covering the setting.
        float x = Math.Max(10f, r.X + r.Width - TipWidth);
        float y = Math.Min(r.Y + r.Height + 4f, canvasH - 40f);
        _tipRect = new WindowRect(x, y, TipWidth, 0f);
        _tipWindow.SetVisible(true);
        _tipWindow.SetRect(_tipRect);
        _tipWindow.BringToFront();   // may already be visible (switching between "?"s) → won't remount, needs the resort
        _tipWindow.MarkDirty();      // re-pull title / body for the new key this frame
        // First-ever open mounts the window and applies its DefaultRect AFTER this SetRect → re-apply the anchored rect
        // for the next few frames so it snaps to the "?" on first open too.
        _tipRepositionTicks = 4;
    }

    private void CloseTip()
    {
        _tipKey = "";
        _tipWindow.SetVisible(false);
    }

    // IFramework.Update: re-assert the popover position while the reposition window is open (early-out otherwise).
    private void TipRepositionTick(float _)
    {
        if (_tipRepositionTicks <= 0) return;
        _tipRepositionTicks--;
        if (_tipWindow.IsShown) _tipWindow.SetRect(_tipRect);
    }

    private string TipTitle() => _helpFns.TryGetValue(_tipKey, out var e) ? e.Title() : "";
    private string TipBody()  => _helpFns.TryGetValue(_tipKey, out var e) ? e.Body()  : "";

    private string TipSubtitle()
    {
        string help = _loc.T("mn.tip.help");
        return _helpFns.TryGetValue(_tipKey, out var e) && e.SectionKey.Length > 0
            ? (_loc.T(e.SectionKey) + " · " + help).ToUpperInvariant()
            : help.ToUpperInvariant();
    }

    private IWindowControl RegisterTipWindow()
    {
        IWindowControl w = null!;
        w = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "minimalnameplate.tip",
                Title:       _loc.T("mn.tip.help"),
                DefaultRect: new WindowRect(_services.Framework.ScreenWidth - TipWidth - 20f, 20f, TipWidth, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false, ShowTitleBar = false,
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                   && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            Root: BuildTipRoot(),
            OnClose: () => { _tipKey = ""; w!.SetVisible(false); }));
        return w;
    }

    private readonly HoverFlag _tipCloseHover = new();

    private HudElement BuildTipRoot() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            // 32 px accent tile with a white bold "?" (the IconBox look, drawn with text instead of a PNG).
            new CellElement(new BubbleElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(0f, TipIconSize),
                    new TextElement(() => "?", Color: () => White, Width: TipIconSize, Align: TextAlign.Center, NoWrap: true,
                        FontSize: 18) { Bold = true, CenterGlyphs = true },
                }, Gap: 0.01f, Justify: RowJustify.Center))
            {
                FillColorFn = () => AccentColor(), CornerRadius = MathF.Round(TipIconSize * 0.22f), PaddingX = 0f, PaddingY = 0f,
            }, Width: TipIconSize),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(TipTitle, Color: () => TextColor(), NoWrap: true, FontSize: 14) { Bold = true, Ellipsize = true },
                new TextElement(TipSubtitle, Color: () => MutedColor(), NoWrap: true, FontSize: 11) { Bold = true, Ellipsize = true },
            }, Gap: 1f), Weight: 1f),
            // Icon-only ✕ (plain text — bold ✕ can draw blank): a round 24 px hover wash around a Bare selectable.
            new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(0f, 24f),
                    new TextElement(() => "✕", Color: () => _tipCloseHover.On ? TextColor() : MutedColor(), Width: 24f,
                        Align: TextAlign.Center, NoWrap: true),
                }, Gap: 0.01f, Justify: RowJustify.Center),
                OnClick: CloseTip) { Bare = true, OnHover = over => _tipCloseHover.On = over })
            {
                FillColorFn = () => _tipCloseHover.On ? Menu.MenuText with { A = HoverMix } : Transparent,
                CornerRadius = RoundRadius(24f), PaddingX = 0f, PaddingY = 0f,
            },
        }, Gap: 10f),
        // The help text: one raised tile (house Tile colour, theme-live), wrapping inside the 360 px popover.
        new BubbleElement(new TextElement(TipBody, Color: () => TextColor()))
        {
            FillColorFn = () => TileColor(), CornerRadius = 6f, PaddingX = 10f, PaddingY = 8f,
        },
    }, Gap: 10f);
}
