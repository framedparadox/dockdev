using DevDX.Controls;
using DevDX.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevDX.ToolPages;

/// <summary>
/// The second page archetype (design doc §9.2): a vertical stack of labelled input rows and
/// result rows, each result with its own copy button. Used by Hash &amp; HMAC, UUID, Timestamp
/// and Number Base.
/// </summary>
public abstract class FormToolPage : ToolPage
{
    private readonly StackPanel _stack = new() { Spacing = 14, Padding = new Thickness(20) };

    protected FormToolPage()
    {
        var scroller = new ScrollViewer
        {
            Content = _stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        base.Content = scroller;
    }

    protected void AddRow(FrameworkElement element) => _stack.Children.Add(element);

    protected static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        Margin = new Thickness(0, 8, 0, 0),
    };

    /// <summary>
    /// A labelled input row. Where the control has one — TextBox, ComboBox, NumberBox and the rest
    /// of the WinUI input family all do — the caption goes in the control's own <c>Header</c>
    /// rather than into a TextBlock stacked above it. That is the WinUI 3 pattern, and it is the
    /// difference between a caption that merely sits near a control and one the platform knows
    /// belongs to it: Narrator reads a Header when focus lands on the field, and reads nothing at
    /// all for a loose TextBlock beside it.
    /// </summary>
    protected static FrameworkElement LabelledRow(string label, FrameworkElement input)
    {
        if (TrySetHeader(input, label))
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, label);
            return input;
        }

        // Anything without a Header (a StackPanel of check boxes, say) keeps the caption above it,
        // and the caption is named as that group's label so it is not simply unlabelled.
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, Opacity = 0.8, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        panel.Children.Add(input);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(panel, label);
        return panel;
    }

    /// <summary>Sets the control's <c>Header</c> if it is one of the WinUI input controls that has
    /// one. Each declares its own — there is no shared base or interface to test against.</summary>
    private static bool TrySetHeader(FrameworkElement input, string label)
    {
        switch (input)
        {
            case TextBox box: box.Header = label; Tint(box, secondary: false); return true;
            case ComboBox combo: combo.Header = label; return true;
            case NumberBox number: number.Header = label; Tint(number, secondary: false); return true;
            case PasswordBox password: password.Header = label; Tint(password, secondary: false); return true;
            case AutoSuggestBox suggest: suggest.Header = label; return true;
            case RichEditBox rich: rich.Header = label; Tint(rich, secondary: false); return true;
            case CalendarDatePicker date: date.Header = label; return true;
            case ToggleSwitch toggle: toggle.Header = label; return true;
            case Slider slider: slider.Header = label; return true;
            default: return false;
        }
    }

    /// <summary>
    /// A one-step tint against the window's own background — the same "input vs. output" shading
    /// <see cref="EditorToolPage.Pane"/> gives its two-pane tools, applied here to the free-typed
    /// text controls a form-shaped tool is built from, which otherwise sit directly on the page and
    /// read as part of the window rather than as a field.
    /// </summary>
    private static void Tint(Control control, bool secondary) =>
        control.Background = ThemeBrush(secondary
            ? "CardBackgroundFillColorSecondaryBrush"
            : "CardBackgroundFillColorDefaultBrush");

    /// <summary>A theme resource brush, or null if this build's resource dictionary lacks it —
    /// a missing tint is a field that looks like the window, not a crash on startup.</summary>
    private static Microsoft.UI.Xaml.Media.Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value)
            ? value as Microsoft.UI.Xaml.Media.Brush
            : null;

    /// <summary>A read-only result row: label, a selectable value, and a copy button. The label is
    /// the value box's own Header, for the reasons in <see cref="LabelledRow"/>; the copy button
    /// floats to its right on the same line.</summary>
    protected static (FrameworkElement Row, TextBox Value, CopyButton Copy) ResultRow(string label)
    {
        var value = new TextBox
        {
            Header = label,
            IsReadOnly = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        Tint(value, secondary: true);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(value, label);

        var copy = new CopyButton { GetText = () => value.Text };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copy, Loc.Get("Tool.CopyOutput") + ": " + label);

        // The button sits beside the box rather than above it, so the Header stays the box's own
        // label instead of sharing a row with an unrelated control.
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(value, 0);
        Grid.SetColumn(copy, 1);
        copy.VerticalAlignment = VerticalAlignment.Bottom;
        row.Children.Add(value);
        row.Children.Add(copy);
        return (row, value, copy);
    }
}
