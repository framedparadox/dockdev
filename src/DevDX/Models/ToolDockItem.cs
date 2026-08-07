using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using DevDX.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DevDX.Models;

/// <summary>
/// One entry in the dock. Every launchable value maps to exactly one built-in tool page — there is
/// no "arbitrary target" concept. Serializable data lives on the public settable properties; the
/// resolved <see cref="IconImage"/> is a runtime-only visual and is not persisted.
/// </summary>
public sealed class ToolDockItem : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public ToolKind Kind { get; set; } = ToolKind.Json;

    private string _displayName = "";
    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Optional path to a user-supplied icon overriding the catalog's default glyph. Set from the
    /// item's right-click menu ("Change icon…"); cleared by "Use the default icon". Mutually
    /// exclusive with <see cref="CustomGlyph"/> — the icon picker sets one and clears the other,
    /// since only one can be shown at a time.
    /// </summary>
    public string? CustomIconPath { get; set; }

    private string? _customGlyph;

    /// <summary>
    /// Optional built-in glyph (a Segoe Fluent Icons character, chosen from the icon picker's
    /// swatch grid) overriding the catalog's default <see cref="Glyph"/>. Needs no network/shell
    /// resolution, so it renders immediately.
    /// </summary>
    public string? CustomGlyph
    {
        get => _customGlyph;
        set
        {
            if (_customGlyph == value)
                return;
            _customGlyph = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Glyph));
            OnPropertyChanged(nameof(HasCustomIcon));
            OnPropertyChanged(nameof(GlyphFontFamily));
            OnPropertyChanged(nameof(GlyphFontScale));
            OnPropertyChanged(nameof(RenderGlyphSizeScaled));
        }
    }

    /// <summary>
    /// When true the item stays in the config (and in the Settings ▸ Tools list) but is not
    /// rendered on the dock. Toggled from the Settings window's per-tool show/hide switch.
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// An optional per-item system-wide shortcut, in the readable form <c>HotkeyGesture</c> parses
    /// (e.g. <c>"Ctrl+Alt+1"</c>). Empty means none. Kept as a string so a hand-edited config stays
    /// legible, and a combination that no longer parses degrades to "no shortcut" rather than
    /// failing the whole load.
    /// </summary>
    public string? Hotkey { get; set; }

    // ---- Runtime-only visual state (never serialized) ----------------------

    private ImageSource? _iconImage;

    [JsonIgnore]
    public ImageSource? IconImage
    {
        get => _iconImage;
        set
        {
            _iconImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ImageVisibility));
            OnPropertyChanged(nameof(GlyphVisibility));
        }
    }

    // ---- Open-window state (runtime-only, driven by ToolWindowManager events, no polling) ------

    private bool _isOpen;
    private string _openStatusText = "";

    /// <summary>True while at least one scratch window for this item's tool is open.</summary>
    [JsonIgnore]
    public bool IsOpen => _isOpen;

    /// <summary>
    /// Updates the open state. The localized status text is passed in rather than looked up here
    /// so the model stays free of the string table — the dock owns that.
    /// </summary>
    public void SetOpen(bool open, string statusText)
    {
        if (_isOpen == open && _openStatusText == statusText)
            return;
        _isOpen = open;
        _openStatusText = statusText;
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(OpenIndicatorVisibility));
        OnPropertyChanged(nameof(OpenStatus));
    }

    /// <summary>The dot under the icon: only for an open, launchable item.</summary>
    [JsonIgnore]
    public Visibility OpenIndicatorVisibility =>
        _isOpen && !IsSeparator ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Announced by Narrator alongside the item's name (AutomationProperties.ItemStatus), so the
    /// dot conveys the same thing to assistive technology as it does visually.
    /// </summary>
    [JsonIgnore]
    public string OpenStatus => _isOpen ? _openStatusText : "";

    /// <summary>
    /// The glyph shown when no bitmap icon is available: <see cref="CustomGlyph"/> if the user
    /// picked one from the icon picker, otherwise the catalog's default for <see cref="Kind"/>.
    /// </summary>
    [JsonIgnore]
    public string Glyph => !string.IsNullOrEmpty(CustomGlyph) ? CustomGlyph : Kind switch
    {
        ToolKind.Separator => "",
        _ => ToolCatalog.Get(Kind)?.Glyph ?? "",
    };

    /// <summary>
    /// The font a bound <see cref="FontIcon"/> needs to draw <see cref="Glyph"/>: the icon font for
    /// a real Segoe Fluent Icons codepoint, or a normal UI font for literal text like Base64's "01"
    /// or Xml's "&lt;/&gt;", which the icon font's private-use-area charset does not cover. Hardcoded
    /// to the icon font's actual family name (matching <see cref="Controls.CopyButton"/>) rather than
    /// looked up via <c>Application.Current.Resources["SymbolThemeFontFamily"]</c>, since this
    /// re-evaluates per dock tile.
    /// </summary>
    [JsonIgnore]
    public FontFamily GlyphFontFamily =>
        new(GlyphFonts.IsTextGlyph(Glyph) ? "Segoe UI" : "Segoe Fluent Icons");

    /// <summary>A text glyph runs visually wider than a single icon character, so it is drawn a
    /// touch smaller to avoid looking oversized in a cell sized for one icon glyph.</summary>
    [JsonIgnore]
    public double GlyphFontScale => GlyphFonts.IsTextGlyph(Glyph) ? 0.8 : 1.0;

    [JsonIgnore]
    public Visibility ImageVisibility => _iconImage is null ? Visibility.Collapsed : Visibility.Visible;

    [JsonIgnore]
    public Visibility GlyphVisibility => _iconImage is null ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public bool IsSeparator => Kind == ToolKind.Separator;

    /// <summary>
    /// A stable handle for UI automation — <c>DockItem.Json</c>, <c>DockItem.Base64</c>. The cell's
    /// automation <em>name</em> is <see cref="DisplayName"/>, which is both translated and
    /// user-renameable, so it identifies this icon to a person but cannot identify it to a test.
    /// Derived from <see cref="Kind"/>, which is neither. See <c>Controls.AutomationIds</c>.
    /// </summary>
    [JsonIgnore]
    public string AutomationId => "DockItem." + Kind;

    /// <summary>True when the user has pinned an icon of their own onto this item — either a
    /// custom image file or a built-in glyph chosen from the icon picker.</summary>
    [JsonIgnore]
    public bool HasCustomIcon =>
        !string.IsNullOrWhiteSpace(CustomIconPath) || !string.IsNullOrEmpty(CustomGlyph);

    /// <summary>
    /// True when this item carries anything the user put on it by hand: an icon of their own, a
    /// caption they renamed, or a system-wide shortcut they assigned. None of that is recoverable
    /// once the item is gone — there is no undo on the dock — so it is what decides whether
    /// switching a tool off may delete the item or must merely hide it
    /// (<see cref="DevDX.DockWindow.SetToolActive"/>).
    /// <para>
    /// The name counts as customized only when it differs from the catalog's, since every item is
    /// born holding a copy of the catalog caption; comparing against the catalog rather than
    /// against emptiness is what keeps an untouched item from looking hand-edited. A separator has
    /// no catalog entry and no caption worth keeping, so only its icon and shortcut count.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public bool HasUserCustomization =>
        HasCustomIcon ||
        !string.IsNullOrWhiteSpace(Hotkey) ||
        (!IsSeparator &&
         !string.IsNullOrWhiteSpace(DisplayName) &&
         !string.Equals(DisplayName, ToolCatalog.Get(Kind)?.DisplayName, StringComparison.Ordinal));

    /// <summary>
    /// How much room this item takes along the strip's flow, in DIPs. A separator is a thin
    /// divider rather than a launchable cell, so it gets a much narrower slot than the
    /// taskbar-sized icons around it. Used both to size the dock window and to map a drag
    /// position onto a slot while reordering, so the two can never disagree about where a cell
    /// starts.
    /// </summary>
    [JsonIgnore]
    public double CellExtent => IsSeparator ? DockMetrics.SeparatorExtent : DockMetrics.Cell;

    /// <summary>The rounded corner on this cell's hover/press chrome.</summary>
    [JsonIgnore]
    public CornerRadius CellCorner => new(DockMetrics.CellCorner);

    // ---- Hover (runtime-only) ----------------------------------------------

    private bool _hovered;

    /// <summary>
    /// Marks this cell as the one the cursor is in, which is what draws its highlight. Set from
    /// the strip's own pointer tracking rather than from the cell's <c>Button</c>, whose
    /// <c>PointerOver</c> state does not arrive on a repeater-realized cell.
    /// </summary>
    public void SetHovered(bool hovered)
    {
        if (_hovered == hovered)
            return;
        _hovered = hovered;
        OnPropertyChanged(nameof(HoverOpacity));
    }

    /// <summary>
    /// The highlight's opacity: on or off, nothing in between. A separator never lights up — it is
    /// a divider, not something to click.
    /// </summary>
    [JsonIgnore]
    public double HoverOpacity => _hovered && !IsSeparator ? 1 : 0;

    // ---- Magnification (runtime-only) --------------------------------------

    private double _magnify = 1;

    /// <summary>
    /// Scales this item's icon within its (fixed-size) cell as the cursor passes over the strip —
    /// the macOS-dock swell, kept inside the cell so the window itself never has to resize.
    /// </summary>
    public void SetMagnification(double scale)
    {
        if (Math.Abs(_magnify - scale) < 0.001)
            return;
        _magnify = scale;
        OnPropertyChanged(nameof(RenderIconSize));
        OnPropertyChanged(nameof(RenderGlyphSize));
        OnPropertyChanged(nameof(RenderGlyphSizeScaled));
    }

    /// <summary>
    /// The icon's drawn size: the density's icon size, swelled by any magnification, but never
    /// past the cell it lives in (a cell is fixed, so an unbounded swell would just clip).
    /// </summary>
    [JsonIgnore]
    public double RenderIconSize => Math.Min(DockMetrics.Icon * _magnify, DockMetrics.Cell - 2);

    /// <summary>The fallback glyph's size, magnified on the same curve as <see cref="RenderIconSize"/>.</summary>
    [JsonIgnore]
    public double RenderGlyphSize => Math.Min(DockMetrics.Glyph * _magnify, (DockMetrics.Cell - 2) * 0.72);

    /// <summary><see cref="RenderGlyphSize"/>, further scaled down for a text glyph like "01" or
    /// "&lt;/&gt;" so it does not look oversized in a cell sized for one icon character.</summary>
    [JsonIgnore]
    public double RenderGlyphSizeScaled => RenderGlyphSize * GlyphFontScale;

    /// <summary>The open-window indicator's length, which tracks the density.</summary>
    [JsonIgnore]
    public double IndicatorLength => DockMetrics.IndicatorLength;

    /// <summary>
    /// Re-reads every geometry-derived property after the app-wide density changed. The dock
    /// calls this on each of its items rather than every item subscribing to
    /// <see cref="DockMetrics.Changed"/> itself — items outlive no window, but a static event
    /// they never unsubscribe from would keep every removed item alive forever.
    /// </summary>
    public void RefreshMetrics()
    {
        OnPropertyChanged(nameof(CellWidth));
        OnPropertyChanged(nameof(CellHeight));
        OnPropertyChanged(nameof(CellCorner));
        OnPropertyChanged(nameof(RenderIconSize));
        OnPropertyChanged(nameof(RenderGlyphSize));
        OnPropertyChanged(nameof(RenderGlyphSizeScaled));
        OnPropertyChanged(nameof(IndicatorLength));
        OnPropertyChanged(nameof(SeparatorLineWidth));
        OnPropertyChanged(nameof(SeparatorLineHeight));
    }

    /// <summary>The launch button is shown for everything except a separator.</summary>
    [JsonIgnore]
    public Visibility ButtonVisibility => IsSeparator ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The thin divider line is shown only for a separator.</summary>
    [JsonIgnore]
    public Visibility SeparatorVisibility => IsSeparator ? Visibility.Visible : Visibility.Collapsed;

    // Whether the strip currently flows top-to-bottom rather than left-to-right. Orientation is
    // a dock-wide property, but the cell sizes below are per-item template bindings, so the dock
    // pushes it down onto every item rather than the template reaching back up for it.
    private bool _flowVertical;

    /// <summary>Re-orients this item's cell. No-op when the orientation is unchanged.</summary>
    public void SetFlowVertical(bool vertical)
    {
        if (_flowVertical == vertical)
            return;
        _flowVertical = vertical;
        OnPropertyChanged(nameof(CellWidth));
        OnPropertyChanged(nameof(CellHeight));
        OnPropertyChanged(nameof(SeparatorLineWidth));
        OnPropertyChanged(nameof(SeparatorLineHeight));
    }

    /// <summary>Cell width: the narrow side only when a separator sits in a horizontal strip.</summary>
    [JsonIgnore]
    public double CellWidth =>
        IsSeparator && !_flowVertical ? DockMetrics.SeparatorExtent : DockMetrics.Cell;

    /// <summary>Cell height: the narrow side only when a separator sits in a vertical strip.</summary>
    [JsonIgnore]
    public double CellHeight =>
        IsSeparator && _flowVertical ? DockMetrics.SeparatorExtent : DockMetrics.Cell;

    /// <summary>A separator's hairline lies across the flow, so its sides swap with orientation.</summary>
    [JsonIgnore]
    public double SeparatorLineWidth => _flowVertical ? DockMetrics.DividerLength : 1;

    [JsonIgnore]
    public double SeparatorLineHeight => _flowVertical ? 1 : DockMetrics.DividerLength;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
