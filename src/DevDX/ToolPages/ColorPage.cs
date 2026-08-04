using System.Globalization;
using DevDX.Models;
using DevDX.Services;
using DevDX.Services.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevDX.ToolPages;

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
        AddRow(LabelledRow(Loc.Get("Color.Input"), _input));
        _swatch.BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        AddRow(_swatch);

        (var hexRow, _hex, _) = ResultRow(Loc.Get("Color.Hex"));
        (var rgbRow, _rgb, _) = ResultRow(Loc.Get("Color.Rgb"));
        (var hslRow, _hsl, _) = ResultRow(Loc.Get("Color.Hsl"));
        (var hsvRow, _hsv, _) = ResultRow(Loc.Get("Color.Hsv"));
        (var contrastRow, _contrast, _) = ResultRow(Loc.Get("Color.Contrast"));
        AddRow(hexRow);
        AddRow(rgbRow);
        AddRow(hslRow);
        AddRow(hsvRow);
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
}
