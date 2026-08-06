using DevDX.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DevDX.ToolPages;

/// <summary>
/// One of the two page archetypes (design doc §9.2): one or two text panes, an optional auxiliary
/// panel, a command bar and a status bar. Concrete pages assemble their own pane layout (they
/// differ in shape — JWT's three decoded sections, Regex's pattern+subject+groups table, Text
/// Diff's two inputs — so this base supplies the shared chrome rather than a single rigid pane
/// grid) via <see cref="SetBody"/>, then call <see cref="InitializeChrome"/> once from the end of
/// their constructor.
/// </summary>
public abstract class EditorToolPage : ToolPage
{
    /// <summary>Width of a label-less command button — enough for the 20px glyph plus a normal
    /// touch-friendly margin, against the 68px an AppBarButton reserves for a label.</summary>
    private const double IconOnlyButtonWidth = 40;

    protected ToolStatusBar StatusBar { get; } = new();

    private readonly CommandBar _commandBar = new()
    {
        DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
        IsOpen = false,
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
        HorizontalContentAlignment = HorizontalAlignment.Left,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private readonly Grid _root = new();

    protected EditorToolPage()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_commandBar, 0);
        Grid.SetRow(StatusBar, 2);
        _root.Children.Add(_commandBar);
        _root.Children.Add(StatusBar);
        base.Content = _root;
    }

    /// <summary>
    /// Puts a page's own option controls — encode/decode, ignore-whitespace, mask profile —
    /// on the <em>left</em> of the command bar, sharing the one toolbar row with the commands,
    /// which stay right-aligned. A tool's settings and its actions belong on the same line; a
    /// second options strip above the editor only ate vertical space and read as a separate thing.
    /// Call once, from the derived constructor.
    /// </summary>
    protected void SetOptions(FrameworkElement options)
    {
        options.HorizontalAlignment = HorizontalAlignment.Left;
        _commandBar.Content = options;
    }

    /// <summary>An options strip laid out the way <see cref="SetOptions"/> expects.</summary>
    protected static StackPanel OptionsBar() => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12, 0, 8, 0),
    };

    /// <summary>A caption sitting beside a control in an <see cref="OptionsBar"/>.</summary>
    protected static TextBlock OptionLabel(string text) =>
        new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>
    /// A caption sitting beside a control in an <see cref="OptionsBar"/>, the same as
    /// <see cref="OptionLabel"/> — except it also sets <paramref name="control"/>'s
    /// <c>AutomationProperties.Name</c> to the same text, the toolbar-row equivalent of
    /// <see cref="FormToolPage.LabelledRow"/>. A <c>Header</c> is the right fix for a vertical form
    /// row (<see cref="FormToolPage"/>); it is the wrong one here, since it stacks the caption
    /// above the control and breaks the toolbar's single-line layout, so this pairs the same
    /// <see cref="TextBlock"/> caption with an explicit automation name instead of a visual
    /// <c>Header</c> — the visible label was never wired to the control it sits beside, so Narrator
    /// read the control with no name at all.
    /// </summary>
    protected static TextBlock OptionLabelFor(FrameworkElement control, string text)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, text);
        return OptionLabel(text);
    }

    /// <summary>
    /// Wraps a pane in its own surface. Input and output sit on slightly different backgrounds so
    /// the eye can tell at a glance which half it is reading — a one-step tint apart, not a
    /// contrast jump, since both are still just text on the window's glass.
    /// </summary>
    /// <param name="secondary">True for the output/derived side of a two-pane tool.</param>
    protected static Border Pane(FrameworkElement content, bool secondary = false) => new()
    {
        Child = content,
        Background = ThemeBrush(secondary
            ? "CardBackgroundFillColorSecondaryBrush"
            : "CardBackgroundFillColorDefaultBrush"),
        BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
        BorderThickness = secondary ? new Thickness(1, 0, 0, 0) : new Thickness(0),
    };

    /// <summary>A theme resource brush, or null if this build's resource dictionary lacks it —
    /// a missing tint is a pane that looks like the window, not a crash on startup.</summary>
    private static Microsoft.UI.Xaml.Media.Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value)
            ? value as Microsoft.UI.Xaml.Media.Brush
            : null;

    /// <summary>Sets the page's own pane layout. Call once, from the derived constructor.</summary>
    protected void SetBody(FrameworkElement body)
    {
        Grid.SetRow(body, 1);
        _root.Children.Add(body);
    }

    /// <summary>
    /// The commands this page was built with, captured by <see cref="InitializeChrome"/>. Most
    /// pages spell <c>Commands</c> as a collection expression, so every read of it allocates a
    /// fresh set of <see cref="ToolCommand"/> objects and re-runs their <c>Loc.Get</c> lookups —
    /// and the tunnelling key handler below reads it on <em>every keystroke</em>. Capturing once
    /// also means the buttons on the bar and the shortcuts that fire them are the same objects
    /// rather than two parallel sets that merely happen to close over the same lambdas.
    /// </summary>
    private IReadOnlyList<ToolCommand> _boundCommands = [];

    /// <summary>Builds the command bar and keyboard accelerators from <see cref="ToolPage.Commands"/>.
    /// Call once, at the end of the derived constructor, after every field a command's lambda
    /// closes over has been assigned.</summary>
    protected void InitializeChrome()
    {
        _boundCommands = Commands;

        // Commands are matched on the way *down* to the focused control, not on the way back up.
        // A KeyboardAccelerator alone is not enough once the focused control is a text editor: the
        // editor handles the key first (Ctrl+Enter inserts a break in a rich-edit document) and the
        // accelerator never runs. Tunnelling gets the tool's own shortcuts in first; the
        // accelerators below stay because they are what renders the "Ctrl+Enter" hint on a button.
        PreviewKeyDown += OnPreviewKeyDown;

        _commandBar.PrimaryCommands.Clear();
        foreach (var command in _boundCommands)
        {
            // A command carrying a control rather than an action (the formatter's indent spinner)
            // rides in the same row as its neighbours instead of being exiled to the left strip,
            // so an option that belongs next to a specific button can sit next to it.
            if (command.Content is { } inline)
            {
                _commandBar.PrimaryCommands.Add(new AppBarElementContainer
                {
                    Content = inline,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 4, 0),
                });
                continue;
            }

            var button = new AppBarButton
            {
                Label = command.Label,
                Icon = new FontIcon { Glyph = command.Glyph },
            };

            // The command's stable id, not its translated label, is what UI automation addresses
            // this button by. See ToolCommand.Id.
            if (command.Id.Length > 0)
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, command.Id);

            // An icon-only command shows its glyph alone; the label survives as the tooltip and
            // as the automation name, so the button stays discoverable and readable to Narrator.
            if (command.IconOnly)
            {
                button.LabelPosition = CommandBarLabelPosition.Collapsed;
                // An AppBarButton is 68px wide so its label has room. With no label that is mostly
                // empty space, and five of them in a row read as unrelated buttons rather than one
                // group of actions.
                button.Width = IconOnlyButtonWidth;
                ToolTipService.SetToolTip(button, command.Label);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, command.Label);
            }

            button.Click += (_, _) => command.Invoke();
            _commandBar.PrimaryCommands.Add(button);

            if (command.Key is { } key)
            {
                var accelerator = new KeyboardAccelerator { Key = key, Modifiers = command.Modifiers };
                accelerator.Invoked += (_, e) =>
                {
                    command.Invoke();
                    e.Handled = true;
                };
                KeyboardAccelerators.Add(accelerator);
            }
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled)
            return;

        var modifiers = CurrentModifiers();
        foreach (var command in _boundCommands)
        {
            if (command.Content is not null || command.Key != e.Key || command.Modifiers != modifiers)
                continue;
            command.Invoke();
            e.Handled = true;
            return;
        }
    }

    /// <summary>The modifier keys held right now. Read from the input system rather than from the
    /// event, which carries no modifier state of its own.</summary>
    private static Windows.System.VirtualKeyModifiers CurrentModifiers()
    {
        var modifiers = Windows.System.VirtualKeyModifiers.None;
        if (IsDown(Windows.System.VirtualKey.Control))
            modifiers |= Windows.System.VirtualKeyModifiers.Control;
        if (IsDown(Windows.System.VirtualKey.Shift))
            modifiers |= Windows.System.VirtualKeyModifiers.Shift;
        if (IsDown(Windows.System.VirtualKey.Menu))
            modifiers |= Windows.System.VirtualKeyModifiers.Menu;
        return modifiers;

        static bool IsDown(Windows.System.VirtualKey key) =>
            Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(key)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }
}
