using System.Globalization;
using System.Text.RegularExpressions;

namespace dockdev.Services.Tools;

/// <summary>A colour as 8-bit RGB plus alpha, the common currency every conversion goes through.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A)
{
    public string ToHex() => A == 255
        ? $"#{R:X2}{G:X2}{B:X2}"
        : $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    public string ToRgbString() => A == 255
        ? $"rgb({R}, {G}, {B})"
        : string.Create(CultureInfo.InvariantCulture, $"rgba({R}, {G}, {B}, {A / 255.0:0.###})");
}

/// <summary>
/// Colour notation conversion (hex ↔ rgb ↔ hsl ↔ hsv) and WCAG contrast. Parsing is deliberately
/// forgiving — the strings developers actually have in hand are copied out of CSS, a design tool
/// or a debugger, in whichever of these notations that source happened to use.
/// </summary>
public static class ColorTools
{
    /// <summary>
    /// Design doc §21: <b>every</b> <see cref="Regex"/> in the app carries an explicit
    /// <see cref="Regex.MatchTimeout"/>, this one included. The pattern is linear and this subject
    /// is a short colour literal, so the timeout is not expected to fire — but the rule is what
    /// makes a regex on untrusted text safe by default rather than by case-by-case reasoning, and
    /// <c>dockdev.Tests.Services.RegexTimeoutTests</c> enforces it across the tree.
    /// </summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex FunctionCall = new(
        @"^\s*(?<fn>rgba?|hsla?|hsva?|hsba?)\s*\(\s*(?<args>[^)]*)\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>Parses <c>#abc</c>, <c>#aabbcc[dd]</c>, <c>rgb()/rgba()</c>, <c>hsl()/hsla()</c>
    /// and <c>hsv()/hsb()</c>. Returns false rather than guessing at anything else.</summary>
    public static bool TryParse(string text, out Rgba color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        text = text.Trim();

        if (text.StartsWith('#'))
            return TryParseHex(text[1..], out color);

        var match = FunctionCall.Match(text);
        if (!match.Success)
            return TryParseHex(text, out color); // a bare "aabbcc" is still unambiguous

        var fn = match.Groups["fn"].Value.ToLowerInvariant();
        var args = match.Groups["args"].Value
            .Split([',', '/', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (args.Length is < 3 or > 4)
            return false;

        var numbers = new double[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            var token = args[i];
            bool percent = token.EndsWith('%');
            if (percent)
                token = token[..^1];
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
            if (percent)
                numbers[i] = i == 0 && fn[0] == 'h' ? numbers[i] : numbers[i] / 100.0 * (fn[0] == 'r' && i < 3 ? 255 : 1);
        }

        byte alpha = args.Length == 4 ? ToByte(numbers[3] <= 1 ? numbers[3] * 255 : numbers[3]) : (byte)255;

        if (fn.StartsWith("rgb", StringComparison.Ordinal))
        {
            color = new Rgba(ToByte(numbers[0]), ToByte(numbers[1]), ToByte(numbers[2]), alpha);
            return true;
        }

        // hsl/hsv saturation and lightness are percentages whether or not the % sign was typed.
        double s = numbers[1] > 1 ? numbers[1] / 100.0 : numbers[1];
        double x = numbers[2] > 1 ? numbers[2] / 100.0 : numbers[2];
        color = fn.StartsWith("hsl", StringComparison.Ordinal)
            ? FromHsl(numbers[0], s, x, alpha)
            : FromHsv(numbers[0], s, x, alpha);
        return true;
    }

    private static bool TryParseHex(string hex, out Rgba color)
    {
        color = default;
        if (hex.Length is not (3 or 4 or 6 or 8) || !hex.All(Uri.IsHexDigit))
            return false;

        if (hex.Length <= 4)
            hex = string.Concat(hex.Select(c => new string(c, 2)));

        byte Pair(int i) => byte.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        color = new Rgba(Pair(0), Pair(2), Pair(4), hex.Length == 8 ? Pair(6) : (byte)255);
        return true;
    }

    /// <summary>Hue in degrees [0,360), saturation and lightness in [0,1].</summary>
    public static (double H, double S, double L) ToHsl(Rgba c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, delta = max - min;
        if (delta < 1e-9)
            return (0, 0, l);

        double s = l > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        return (Hue(r, g, b, max, delta), s, l);
    }

    /// <summary>Hue in degrees [0,360), saturation and value in [0,1].</summary>
    public static (double H, double S, double V) ToHsv(Rgba c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        return (delta < 1e-9 ? 0 : Hue(r, g, b, max, delta), max < 1e-9 ? 0 : delta / max, max);
    }

    private static double Hue(double r, double g, double b, double max, double delta)
    {
        double h = max == r ? (g - b) / delta % 6
                 : max == g ? (b - r) / delta + 2
                 : (r - g) / delta + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    public static Rgba FromHsl(double h, double s, double l, byte alpha = 255)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        var (r, g, b) = HueRamp(h, c);
        double m = l - c / 2;
        return new Rgba(ToByte((r + m) * 255), ToByte((g + m) * 255), ToByte((b + m) * 255), alpha);
    }

    public static Rgba FromHsv(double h, double s, double v, byte alpha = 255)
    {
        double c = v * s;
        var (r, g, b) = HueRamp(h, c);
        double m = v - c;
        return new Rgba(ToByte((r + m) * 255), ToByte((g + m) * 255), ToByte((b + m) * 255), alpha);
    }

    private static (double R, double G, double B) HueRamp(double h, double c)
    {
        h = ((h % 360) + 360) % 360 / 60;
        double x = c * (1 - Math.Abs(h % 2 - 1));
        return (int)h switch
        {
            0 => (c, x, 0),
            1 => (x, c, 0),
            2 => (0, c, x),
            3 => (0, x, c),
            4 => (x, 0, c),
            _ => (c, 0, x),
        };
    }

    /// <summary>WCAG 2.1 relative luminance.</summary>
    public static double RelativeLuminance(Rgba c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>WCAG contrast ratio between two colours, from 1 (identical) to 21 (black on white).</summary>
    public static double ContrastRatio(Rgba a, Rgba b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
