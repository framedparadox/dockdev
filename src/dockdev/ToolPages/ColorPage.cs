using System.Globalization;
using dockdev.Models;
using dockdev.Services;
using dockdev.Services.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace dockdev.ToolPages;

/// <summary>
/// Colour Converter: one forgiving input (hex, rgb(), hsl(), hsv()) and every other notation as a
/// copyable result, with a live swatch and the WCAG contrast ratio against black and white — the
/// number you actually need when picking text colour for a background.
/// </summary>
public sealed class ColorPage : FormToolPage
{
    private readonly TextBox _input = new() { PlaceholderText = Loc.Get("Color.Placeholder"), FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private readonly Border _swatch = new() { Height = 64, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
    private readonly TextBox _hex;
    private readonly TextBox _rgb;
    private readonly TextBox _hsl;
    private readonly TextBox _hsv;
    private readonly TextBox _contrast;

    public ColorPage()
    {
        AddRow(SectionHeader(Loc.Get("Tool.Color.Name")));

        // The input keeps the star column so the picker buttons don't eat into its typing room;
        // both buttons sit on its own row rather than below it, matching ResultRow's label+action
        // layout elsewhere on this page.
        var inputRow = new Grid { ColumnSpacing = 8 };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labelledInput = LabelledRow(Loc.Get("Color.Input"), _input);
        var pickRound = CreatePickerButton(ColorSpectrumShape.Ring, "\uE790", "Color.PickRound");
        var pickSquare = CreatePickerButton(ColorSpectrumShape.Box, "\uE8D3", "Color.PickSquare");
        pickRound.VerticalAlignment = VerticalAlignment.Bottom;
        pickSquare.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(labelledInput, 0);
        Grid.SetColumn(pickRound, 1);
        Grid.SetColumn(pickSquare, 2);
        inputRow.Children.Add(labelledInput);
        inputRow.Children.Add(pickRound);
        inputRow.Children.Add(pickSquare);
        AddRow(inputRow);

        _swatch.BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        AddRow(_swatch);

        (var hexRow, _hex, _) = ResultRow(Loc.Get("Color.Hex"));
        (var rgbRow, _rgb, _) = ResultRow(Loc.Get("Color.Rgb"));
        (var hslRow, _hsl, _) = ResultRow(Loc.Get("Color.Hsl"));
        (var hsvRow, _hsv, _) = ResultRow(Loc.Get("Color.Hsv"));
        (var contrastRow, _contrast, _) = ResultRow(Loc.Get("Color.Contrast"));
        AddRow(PairRow(hexRow, rgbRow));
        AddRow(PairRow(hslRow, hsvRow));
        AddRow(contrastRow);

        _input.TextChanged += (_, _) => Convert();
        _input.Text = "#2D7FF9";
    }

    public override ToolKind Kind => ToolKind.Color;
    public override bool IsDirty => false;

    public override IReadOnlyList<ToolCommand> Commands =>
    [
        ToolCommand.Clear(() => _input.Text = ""),
    ];

    public override bool AcceptsClipboardText(string text) =>
        text.Length is > 0 and < 64 && ColorTools.TryParse(text, out _);

    public override void PasteClipboardText(string text) => _input.Text = text;

    private void Convert()
    {
        if (!ColorTools.TryParse(_input.Text, out var color))
        {
            _swatch.Background = null;
            _hex.Text = _rgb.Text = _hsl.Text = _hsv.Text = "";
            _contrast.Text = _input.Text.Length == 0 ? "" : Loc.Get("Color.Unrecognised");
            return;
        }

        _swatch.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B));
        _hex.Text = color.ToHex();
        _rgb.Text = color.ToRgbString();

        var (h, s, l) = ColorTools.ToHsl(color);
        _hsl.Text = string.Create(CultureInfo.InvariantCulture, $"hsl({h:0}, {s * 100:0.#}%, {l * 100:0.#}%)");
        var (hv, sv, v) = ColorTools.ToHsv(color);
        _hsv.Text = string.Create(CultureInfo.InvariantCulture, $"hsv({hv:0}, {sv * 100:0.#}%, {v * 100:0.#}%)");

        var opaque = color with { A = 255 };
        double onWhite = ColorTools.ContrastRatio(opaque, new Rgba(255, 255, 255, 255));
        double onBlack = ColorTools.ContrastRatio(opaque, new Rgba(0, 0, 0, 255));
        _contrast.Text = Loc.Format("Color.ContrastValue",
            onWhite.ToString("0.00", CultureInfo.CurrentCulture),
            onBlack.ToString("0.00", CultureInfo.CurrentCulture));
    }

    /// <summary>
    /// An icon button that opens a <see cref="ColorPicker"/> flyout of the given spectrum shape.
    /// Picking a colour writes it back into <see cref="_input"/> as the same hex text
    /// <see cref="Rgba.ToHex"/> produces for the result rows, so the existing
    /// <c>_input.TextChanged</c> → <see cref="Convert"/> pipeline is what actually updates the
    /// swatch and the rest of the results — this method never touches them directly.
    /// </summary>
    private Button CreatePickerButton(ColorSpectrumShape shape, string glyph, string tooltipKey)
    {
        var picker = new ColorPicker { ColorSpectrumShape = shape, IsAlphaEnabled = true };
        picker.ColorChanged += (_, args) =>
            _input.Text = new Rgba(args.NewColor.R, args.NewColor.G, args.NewColor.B, args.NewColor.A).ToHex();

        // Seeded from whatever _input currently parses to, each time the flyout opens, so the
        // picker always starts from the colour actually on screen rather than wherever the last
        // pick left it.
        var flyout = new Flyout { Content = picker };

        // These buttons sit right under the custom title bar, and Flyout's default (unset)
        // placement prefers opening *above* its target when it decides there's room — which here
        // means straight into the title bar strip, underneath the minimize/maximize/close buttons
        // WinUI draws there (those are non-client chrome, always on top of in-window content, so a
        // flyout that opens into that area renders behind them rather than over them). Pinning the
        // placement downward keeps the popup entirely inside the page, away from the title bar.
        flyout.Placement = FlyoutPlacementMode.Bottom;

        // The color spectrum + slider popup is tall enough to still reach up under the title bar
        // strip on smaller windows even when opening downward, and a root-constrained popup renders
        // behind that non-client chrome. Letting it escape the XamlRoot's bounds moves it onto its
        // own top-level surface, which draws above the caption buttons instead of under them.
        flyout.ShouldConstrainToRootBounds = false;

        flyout.Opening += (_, _) => picker.Color = ColorTools.TryParse(_input.Text, out var color)
            ? Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B)
            : Windows.UI.Color.FromArgb(255, 45, 127, 249);

        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, Flyout = flyout };
        var label = Loc.Get(tooltipKey);
        ToolTipService.SetToolTip(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        return button;
    }

    /// <summary>Lays two result rows side by side in a 2-column grid, so related conversions (e.g.
    /// hex/rgb, hsl/hsv) read as a pair instead of each claiming a full-width row.</summary>
    private static Grid PairRow(FrameworkElement left, FrameworkElement right)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }
}
