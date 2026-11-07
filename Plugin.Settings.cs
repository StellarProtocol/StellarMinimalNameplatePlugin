using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.MinimalNameplate;

// The settings window body (minimalnameplate.main), house style — user-approved design 2026-11-07:
//   ToolHeader: [accent icon tile] [bold "Minimal Nameplate" / ● live status] … [master On/Off chip]
//   SHOW ABOVE PLAYERS (click a chip to switch): label … [chip] [?] × 5
//   SIZE (drag to adjust): muted label · round-knob slider · bold value · [?] × 3
// Every chip / value has a fixed width, so toggling or dragging never resizes the window.
public sealed partial class Plugin
{
    // Slider label column = the longest size label in any locale (fil "Laki ng pangalan" / th "จำนวนผู้เล่นสูงสุด"
    // ≈ 110 px at the fixed 14 px face) + headroom; Ellipsize covers a font that measures wider. Value column fits "200".
    private const float SliderLabelW = 124f;
    private const float SliderValueW = 36f;

    private byte[]? _iconPng;
    private bool    _iconLoaded;

    // Read once: ImageElement Funcs are polled. A missing resource latches null instead of re-reading every poll.
    private byte[]? IconPng()
    {
        if (!_iconLoaded) { _iconLoaded = true; _iconPng = LoadIconPng(); }
        return _iconPng;
    }

    private HudElement BuildRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(IconPng, () => _loc.T("mn.header"), HeaderStatus(), ToggleChip(() => _enabled, () => SetEnabled(!_enabled))),
        SectionPanel(() => _loc.T("mn.sec.show"), () => _loc.T("mn.sec.show.hint"),
            ChipRow("showicon", "mn.showIcon", "mn.help.showIcon", () => _showIcon, v =>
            {
                _showIcon = v;
                ClassIconOverlay.ShowClassIcon = v;
                _cfg.Set<bool>("minimalnameplate_showicon", v);
                _cfg.Save();
            }),
            ChipRow("showname", "mn.showName", "mn.help.showName", () => _showName, v =>
            {
                _showName = v;
                ClassIconOverlay.ShowName = v;
                _cfg.Set<bool>("minimalnameplate_showname", v);
                _cfg.Save();
            }),
            ChipRow("showfriend", "mn.showFriend", "mn.help.showFriend", () => _showFriendIcon, v =>
            {
                _showFriendIcon = v;
                ClassIconOverlay.ShowFriendIcon = v;
                _cfg.Set<bool>("minimalnameplate_showfriend", v);
                _cfg.Save();
            }),
            ChipRow("showguild", "mn.showGuild", "mn.help.showGuild", () => _showGuildIcon, v =>
            {
                _showGuildIcon = v;
                ClassIconOverlay.ShowGuildIcon = v;
                _cfg.Set<bool>("minimalnameplate_showguild", v);
                _cfg.Save();
            }),
            ChipRow("showrevivesealed", "mn.showReviveSealed", "mn.help.showReviveSealed", () => _showReviveSealed, v =>
            {
                _showReviveSealed = v;
                ClassIconOverlay.ShowReviveSealed = v;   // purple badge + name + "(42s)" on revive-sealed corpses
                _cfg.Set<bool>("minimalnameplate_showrevivesealed", v);
                _cfg.Save();
            })),
        SectionPanel(() => _loc.T("mn.sec.size"), () => _loc.T("mn.sec.size.hint"),
            SliderRow("badgesize", "mn.badgeSize", "mn.help.badgeSize",
                RoundSlider(() => ClassIconOverlay.SizePixels,
                    v => { ClassIconOverlay.SizePixels = v; _cfg.Set<float>("minimalnameplate_sizepx", v); _cfg.Save(); },
                    16f, 160f),
                () => $"{ClassIconOverlay.SizePixels:F0}"),
            SliderRow("namesize", "mn.nameSize", "mn.help.nameSize",
                RoundSlider(() => ClassIconOverlay.NameSize,
                    v => { ClassIconOverlay.NameSize = v; _cfg.Set<float>("minimalnameplate_namepx", v); _cfg.Save(); },
                    16f, 160f),
                () => $"{ClassIconOverlay.NameSize:F0}"),
            SliderRow("maxplayers", "mn.maxPlayers", "mn.help.maxPlayers",
                RoundSlider(() => ClassIconOverlay.MaxIcons, v =>
                {
                    int n = (int)Math.Round(v);
                    if (n < 5) n = 5; else if (n > 200) n = 200;   // 200 = the AOI-tracking safety bound
                    ClassIconOverlay.MaxIcons = n;
                    _cfg.Set<float>("minimalnameplate_maxicons", n);
                    _cfg.Save();
                }, 5f, 200f),
                () => $"{ClassIconOverlay.MaxIcons}")),
    }, Gap: 10f);

    // Master switch: our overlay on + the game's own plate hidden, or the reverse (unchanged from the old toggle).
    private void SetEnabled(bool v)
    {
        _enabled = v;
        _overlay.SetEnabled(v);
        NameplateIconPatch.HidePlate = v;   // hide the game's plate while our overlay is on
        NameplateIconPatch.ReapplyAll();
        _cfg.Set<bool>("minimalnameplate_enabled", v);
        _cfg.Save();
    }

    // ● On · game nameplates hidden  /  ● Off · using the game's nameplates. One line, ellipsized, so the header height
    // never changes with the state or the locale.
    private HudElement HeaderStatus() => new RowElement(new HudElement[]
    {
        new TextElement(() => "●", Color: () => _enabled ? StatusGreen : MutedColor(), NoWrap: true),
        new CellElement(new TextElement(() => _loc.T(_enabled ? "mn.status.on" : "mn.status.off"),
            Color: () => MutedColor(), NoWrap: true) { Ellipsize = true }, Weight: 1f),
    }, Gap: 6f);

    // label … [On/Off chip] [?]. The help title is the row label.
    private HudElement ChipRow(string key, string labelKey, string helpKey, Func<bool> get, Action<bool> set)
    {
        Func<string> label = () => _loc.T(labelKey);
        return new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(label, Color: () => TextColor(), NoWrap: true) { Ellipsize = true }, Weight: 1f),
            ToggleChip(get, () => set(!get())),
            HelpDot(key, "mn.sec.show", label, () => _loc.T(helpKey)),
        }, Gap: 6f);
    }

    // [muted label] [round-knob slider] [bold value, right] [?].
    private HudElement SliderRow(string key, string labelKey, string helpKey, HudElement slider, Func<string> value)
    {
        Func<string> label = () => _loc.T(labelKey);
        return new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(label, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true }, Width: SliderLabelW),
            new CellElement(slider, Weight: 1f),
            new CellElement(new TextElement(value, Color: () => TextColor(), Align: TextAlign.Right, NoWrap: true) { Bold = true },
                Width: SliderValueW),
            HelpDot(key, "mn.sec.size", label, () => _loc.T(helpKey)),
        }, Gap: 6f);
    }
}
