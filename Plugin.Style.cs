using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MinimalNameplate;

// House style (Stellar Design Language, KB Stellar-Design-Language.md) for the settings window. Copied from the reference
// kit (StellarMultiToolPlugin Plugin.Style.cs / StellarMaestroPlugin Plugin.Style*.cs) and trimmed to what this window
// uses: theme-live tokens, SectionPanel, the clickable on/off chip, the accent icon tile, ToolHeader and the round-knob
// slider. Needs SDK >= 2.29.0 (BubbleElement.FillColorFn, Bare selectables, SquareHandle sliders).
//
// Toolkit limits behind some choices (WindowBuilder-Patterns.md):
//   • Plain TextElement ignores FontSize on Menu windows (fixed 14px) — only the styled path (Bold/Emphasis) honours it.
//   • Row/Column Gap 0 means the DEFAULT gap (8/12), never zero — every container here passes an explicit Gap.
//   • Bold can't be toggled live → the on/off chip is two complete elements swapped by a ConditionalElement wrapping the
//     WHOLE chip, both in the same fixed-width cell so the swap never moves its neighbours.
public sealed partial class Plugin
{
    // ── palette: LIVE from the current framework theme ────────────────────────────────────────────────────────────
    // Derived from _services.Theme.Colors on every call and handed to elements as Funcs, so a theme switch repaints on
    // the next refresh. Only SEMANTIC status colours are fixed (the "on" green).
    private static readonly ColorRgba PanelShade = new(0f, 0f, 0f, 0.20f);   // panel = background darkened
    private const float TileTextMix      = 0.08f;   // raised tile = panel + 8% MenuText …
    private const float TileAccentMix    = 0.06f;   // … + a 6% accent tint
    private const float NeutralMix       = 0.22f;   // neutral fill = panel + 22% MenuMuted …
    private const float NeutralAccentMix = 0.08f;   // … + an 8% accent tint, like the tiles
    private const float PillMinContrast  = 4.5f;    // WCAG AA for labels on tinted fills
    private const float ChipWashAlpha    = 0.22f;   // "on" chip = bold green text on a 22% pill of the green
    private const float ChipOffAlpha     = 0.15f;   // "off" chip = muted text on a 15% pill of MenuMuted
    private const float ChipHoverAlpha   = 0.12f;   // hover adds this to the chip's wash (keeps the chip's shape)
    private const float HoverMix         = 0.10f;   // hover = MenuText@0.10 over the fill

    private static readonly ColorRgba StatusGreen = Rgb(0x5fd38d);
    private static readonly ColorRgba White       = new(1f, 1f, 1f);
    private static readonly ColorRgba Transparent = new(0f, 0f, 0f, 0f);

    private IThemeMenuColors Menu => _services.Theme.Colors;

    private ColorRgba TextColor()   => Menu.MenuText;
    private ColorRgba MutedColor()  => Menu.MenuMuted;
    private ColorRgba AccentColor() => Menu.MenuAccent;
    private ColorRgba PanelColor()  => Over(PanelShade, Menu.MenuBackground);
    private ColorRgba TileColor()   => Over(Menu.MenuAccent with { A = TileAccentMix },
                                            Over(Menu.MenuText with { A = TileTextMix }, PanelColor()));
    private ColorRgba NeutralFill() => Over(Menu.MenuAccent with { A = NeutralAccentMix },
                                            Over(Menu.MenuMuted with { A = NeutralMix }, PanelColor()));
    private ColorRgba NeutralText() => EnsureContrast(TextColor(), NeutralFill(), PillMinContrast);

    // Nudges fg toward white (dark bg) or black (light bg) in 15% steps until it reaches minRatio against the opaque bg.
    private static ColorRgba EnsureContrast(ColorRgba fg, ColorRgba bg, float minRatio)
    {
        float lb = Luminance(bg);
        var target = lb < 0.179f ? new ColorRgba(1f, 1f, 1f) : new ColorRgba(0f, 0f, 0f);
        for (int i = 0; i < 12 && ContrastRatio(Luminance(fg), lb) < minRatio; i++) fg = Mix(fg, target, 0.15f);
        return fg with { A = 1f };
    }

    private static float ContrastRatio(float la, float lb)
        => (MathF.Max(la, lb) + 0.05f) / (MathF.Min(la, lb) + 0.05f);

    private static float Luminance(ColorRgba c) => 0.2126f * Lin(c.R) + 0.7152f * Lin(c.G) + 0.0722f * Lin(c.B);
    private static float Lin(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    // a → b by t (0 = a, 1 = b), opaque.
    private static ColorRgba Mix(ColorRgba a, ColorRgba b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);

    // Alpha-composite fg (its A = coverage) over an opaque bg.
    private static ColorRgba Over(ColorRgba fg, ColorRgba bg) => Mix(bg, fg with { A = 1f }, fg.A);

    private static ColorRgba Rgb(uint rgb, float a = 1f) =>
        new(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, a);

    // ── hover that follows the shape ──────────────────────────────────────────────────────────────────────────────
    // A normal SelectableElement paints a SQUARE hover wash over its rect. Every clickable here is a Bare selectable whose
    // OnHover flips a flag, and the surrounding Bubble's FillColorFn shows the hover — so it keeps the round shape.
    private sealed class HoverFlag { public bool On; }

    // The corner radius for a ROUND bubble of height `side`: side / 2 − 1, NOT side / 2. The bubble's 9-slice background
    // reports a MIN size of its summed sprite borders (radius + 1 each side), so side / 2 made every round control 2 px
    // taller than its content (the 24 px "?" drew 24 × 26; Maestro, measured in-game 2026-11-06).
    private static float RoundRadius(float side) => side / 2f - 1f;

    // Bold (TMP) labels sit ~1.5–2 px HIGH when centred; a 2 px spacer above moves the text down 1 px (Maestro measured).
    private static HudElement DropBold(HudElement label) => new ColumnElement(new HudElement[]
    {
        new SpacerElement(0f, 2f),
        label,
    }, Gap: 0.01f);

    // ── section panel ─────────────────────────────────────────────────────────────────────────────────────────────
    // A rounded panel (background darkened, live): small UPPERCASE caption + right-aligned muted hint, then the children.
    // Directly in a Column it FILLS the width (a Bubble only hugs inside a Row). ⚠️ If a trailing-control variant is ever
    // needed, give it its OWN NAME — a second params overload silently swallows the first child (KB §4).
    private HudElement SectionPanel(Func<string> caption, Func<string> hint, params HudElement[] children)
    {
        var items = new List<HudElement>(children.Length + 1)
        {
            new RowElement(new HudElement[]
            {
                new TextElement(() => caption().ToUpperInvariant(), Color: () => MutedColor(), NoWrap: true, FontSize: 12) { Bold = true },
                new SpacerElement(0f),   // flexible: pushes the hint to the right edge
                new TextElement(hint, Color: () => MutedColor(), NoWrap: true),
            }, Gap: 6f),
        };
        items.AddRange(children);
        return new BubbleElement(new ColumnElement(items, Gap: 6f))
        {
            FillColorFn = () => PanelColor(), CornerRadius = 6f, PaddingX = 10f, PaddingY = 8f,
        };
    }

    // ── clickable on/off chip ─────────────────────────────────────────────────────────────────────────────────────
    // 64 × 24 in BOTH states: on = bold StatusGreen on a 22% green wash, off = regular MenuMuted on a muted wash. Two
    // complete chips swapped by a Conditional wrapping the WHOLE chip (never a Conditional inside a bubble's measured
    // child — the bubble re-hug gotcha), inside one fixed-width cell. Each chip = Bubble (fill + hover) → Bare Selectable
    // → Row[24 px height strut, label], so the bubble is exactly 24 tall (Bare = zero padding; PaddingY 0) and the whole
    // pill is the click target. MaxWidth pins the width: a MaxWidth-0 bubble under a Conditional caps itself at the
    // 100 px default (KB WindowBuilder-Patterns, Dungeon Confirm 6b19310). Both states share one HoverFlag.
    private const float ChipWidth  = 64f;
    private const float ChipHeight = 24f;
    // Dimmed chip (a dependent setting whose parent is off — still clickable, so NOT the recessed "disabled" look of
    // rule 22): label pulled halfway toward the panel colour, wash at half strength. Colours only — same size.
    private const float ChipDimTextMix = 0.5f;
    private const float ChipDimWash    = 0.5f;

    // `dim` (optional, polled) greys the chip out while keeping it clickable and the same 64 × 24.
    private HudElement ToggleChip(Func<bool> on, Action toggle, Func<bool>? dim = null)
    {
        var hover = new HoverFlag();
        bool Dim() => dim != null && dim();
        ColorRgba Label(ColorRgba c) => Dim() ? Mix(PanelColor(), c, ChipDimTextMix) : c;
        float Wash(float a) => (a + (hover.On ? ChipHoverAlpha : 0f)) * (Dim() ? ChipDimWash : 1f);
        return new CellElement(new ConditionalElement(on,
            ChipShell(DropBold(new TextElement(() => _loc.T("mn.on"), Color: () => Label(StatusGreen), Width: ChipWidth,
                    Align: TextAlign.Center, NoWrap: true, FontSize: 13) { Bold = true }),
                () => StatusGreen with { A = Wash(ChipWashAlpha) }, hover, toggle),
            ChipShell(new TextElement(() => _loc.T("mn.off"), Color: () => Label(MutedColor()), Width: ChipWidth,
                    Align: TextAlign.Center, NoWrap: true),
                () => MutedColor() with { A = Wash(ChipOffAlpha) }, hover, toggle)),
            Width: ChipWidth);
    }

    private static HudElement ChipShell(HudElement label, Func<ColorRgba> fill, HoverFlag hover, Action onClick)
        => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ChipHeight),   // width 0 + height > 0 = a non-flexible height strut
                label,
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => fill(), CornerRadius = RoundRadius(ChipHeight), PaddingX = 0f, PaddingY = 0f, MaxWidth = ChipWidth,
        };

    // ── icon tile + tool header ───────────────────────────────────────────────────────────────────────────────────
    // A rounded tile in the LIVE theme accent with the flat white glyph PNG centred at ~62 % (art can't follow the theme,
    // the tile does). The fixed-width Cell pins the column (Cell force-expands a child's width) and the inner Row keeps
    // the image square.
    private const float ToolIconSize   = 40f;
    private const float IconGlyphShare = 0.62f;

    private HudElement IconBox(Func<byte[]?> png, float size)
    {
        int glyph = (int)MathF.Round(size * IconGlyphShare);
        float pad = (size - glyph) / 2f;
        return new CellElement(new BubbleElement(
            new RowElement(new HudElement[] { new ImageElement(png, glyph, glyph) }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => AccentColor(), CornerRadius = MathF.Round(size * 0.22f), PaddingX = pad, PaddingY = pad,
        }, Width: size);
    }

    // Window title block: [40 px icon] [bold title over a live status line] … [trailing control, e.g. the master chip].
    // The text column sits in a weighted Cell so a long status (any locale) ellipsizes instead of pushing the chip out.
    private HudElement ToolHeader(Func<byte[]?> icon, Func<string> title, HudElement status, HudElement trailing)
        => new RowElement(new HudElement[]
        {
            IconBox(icon, ToolIconSize),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(title, Color: () => TextColor(), Emphasis: true, NoWrap: true) { Ellipsize = true },
                status,
            }, Gap: 2f), Weight: 1f),
            trailing,
        }, Gap: 10f);

    // ── round-knob slider ─────────────────────────────────────────────────────────────────────────────────────────
    // SquareHandle = a knob exactly HandleSize square (the default handle is stretched to the row height = a capsule).
    private const float SliderKnobSize = 14f;

    private static SliderElement RoundSlider(Func<float> get, Action<float> set, float min, float max)
        => new(get, set, min, max) { SquareHandle = true, HandleSize = SliderKnobSize };
}
