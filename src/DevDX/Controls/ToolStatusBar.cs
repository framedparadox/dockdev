using DevDX.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace DevDX.Controls;

/// <summary>
/// The status-bar contract every editor-shaped tool shares (design doc §9.3): character/byte
/// count, a validation chip, and the structural path of the current selection where meaningful.
/// The chip is a live region so Narrator announces validation changes without the user going to
/// look for them.
/// </summary>
public sealed class ToolStatusBar : Grid
{
    private readonly TextBlock _counts = new() { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _chip = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly TextBlock _path = new()
    {
        Opacity = 0.7,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12, 0, 0, 0),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public ToolStatusBar()
    {
        Padding = new Thickness(12, 6, 12, 6);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_counts);
        panel.Children.Add(_chip);
        panel.Children.Add(_path);
        Children.Add(panel);

        AutomationProperties.SetLiveSetting(_chip, AutomationLiveSetting.Polite);
        // Addressable by UI automation: the chip is how a test asks "did that parse?" without
        // reading pixels, and the counts are how it confirms text actually landed in the editor.
        // See AutomationIds for why these are constants.
        AutomationProperties.SetAutomationId(_chip, AutomationIds.StatusChip);
        AutomationProperties.SetAutomationId(_counts, AutomationIds.StatusCounts);
        // The error colour is derived from the theme this control renders in, so it has to be
        // re-derived when that changes under it (Settings ▸ Appearance, or the OS flipping while
        // "Match Windows" is on).
        ActualThemeChanged += (_, _) => ApplyChipColor();
    }

    public void SetCounts(string text)
    {
        int chars = text.Length;
        int bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        _counts.Text = Loc.Format("Status.Counts", chars, bytes);
    }

    public void SetValid()
    {
        _chip.Text = Loc.Get("Status.Valid");
        _isError = false;
        ApplyChipColor();
    }

    public void SetUntouched()
    {
        _chip.Text = "";
        _isError = false;
        ApplyChipColor();
    }

    public void SetError(int line, int column)
    {
        _chip.Text = Loc.Format("Tool.InvalidAt", line, column);
        _isError = true;
        ApplyChipColor();
    }

    public void SetPath(string? path) => _path.Text = path ?? "";

    /// <summary>Whether the chip is currently reporting an error, so its colour can be re-derived
    /// when the theme changes underneath it.</summary>
    private bool _isError;

    /// <summary>
    /// Paints the chip in Fluent's <c>SystemFillColorCritical</c> for the theme this control is
    /// actually rendering in. The old code hard-coded <c>#C42B1C</c> — which is only the Light
    /// value, and is a muddy near-black red on a dark surface, where the chip was the one thing on
    /// the status bar you could not read. Colour is not the only signal either way: the chip spells
    /// out the line and column (§10.3).
    /// </summary>
    private void ApplyChipColor()
    {
        if (!_isError)
        {
            _chip.ClearValue(TextBlock.ForegroundProperty);
            return;
        }

        _chip.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            ActualTheme == ElementTheme.Light
                ? Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C)
                : Windows.UI.Color.FromArgb(255, 0xFF, 0x99, 0xA4));
    }
}
