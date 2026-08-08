using dockdev.Controls;
using dockdev.Models;
using dockdev.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace dockdev.ToolPages;

/// <summary>
/// Text Diff (design doc §14.11): two inputs, a unified diff view (see <see cref="DiffView"/>'s
/// remarks for how it simplifies the two-synchronised-panes spec), and ignore-whitespace/
/// case/blank-line options.
/// </summary>
public sealed class DiffPage : EditorToolPage
{
    private readonly CodeEditor _left = new()
    {
        PlaceholderText = Loc.Get("Diff.LeftPlaceholder"),
        AccessibleName = Loc.Get("Diff.Original"),
    };
    private readonly CodeEditor _right = new()
    {
        PlaceholderText = Loc.Get("Diff.RightPlaceholder"),
        AccessibleName = Loc.Get("Diff.Changed"),
    };
    private readonly DiffView _diff = new() { AccessibleName = Loc.Get("Diff.Result") };
    private readonly CheckBox _ignoreWhitespace = new() { Content = Loc.Get("Diff.IgnoreWhitespace") };
    private readonly CheckBox _ignoreCase = new() { Content = Loc.Get("Diff.IgnoreCase") };
    private readonly CheckBox _ignoreBlankLines = new() { Content = Loc.Get("Diff.IgnoreBlankLines") };
    private bool _isDirty;

    /// <summary>How long typing has to pause before the diff is recomputed. Diffing is the one
    /// thing on this page that costs real work, and it costs it over both documents at once — at a
    /// few thousand lines a side that is tens of milliseconds a keystroke, which is felt. Matches
    /// the editor's own highlight debounce so the two settle together.</summary>
    private static readonly TimeSpan RecomputeDelay = TimeSpan.FromMilliseconds(180);

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _recomputeTimer;

    public DiffPage()
    {
        _recomputeTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _recomputeTimer.IsRepeating = false;
        _recomputeTimer.Interval = RecomputeDelay;
        _recomputeTimer.Tick += (_, _) => Compute();
        // A timer the dispatcher is holding would otherwise keep this page — and its whole visual
        // tree — alive after the window closed, and fire into it once more on the way.
        PageClosing.Register(_recomputeTimer.Stop);

        _left.TextChanged += (_, _) => { _isDirty = _left.Text.Length > 0 || _right.Text.Length > 0; ScheduleCompute(); };
        _right.TextChanged += (_, _) => { _isDirty = _left.Text.Length > 0 || _right.Text.Length > 0; ScheduleCompute(); };
        // The option boxes are a single deliberate click, not a burst, so they recompute at once.
        _ignoreWhitespace.Checked += (_, _) => Compute();
        _ignoreWhitespace.Unchecked += (_, _) => Compute();
        _ignoreCase.Checked += (_, _) => Compute();
        _ignoreCase.Unchecked += (_, _) => Compute();
        _ignoreBlankLines.Checked += (_, _) => Compute();
        _ignoreBlankLines.Unchecked += (_, _) => Compute();

        var options = OptionsBar();
        options.Children.Add(_ignoreWhitespace);
        options.Children.Add(_ignoreCase);
        options.Children.Add(_ignoreBlankLines);
        SetOptions(options);
        options.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right;

        // Both editors and the diff below stretch to fill: each is the only child of its cell and
        // every cell is a star row/column, so the three panes always divide the whole window.
        var leftSurface = Pane(_left);
        var rightSurface = Pane(_right, secondary: true);

        var inputs = new Grid { VerticalAlignment = VerticalAlignment.Stretch };
        inputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(leftSurface, 0);
        Grid.SetColumn(rightSurface, 1);
        inputs.Children.Add(leftSurface);
        inputs.Children.Add(rightSurface);

        var diffSurface = Pane(_diff, secondary: true);
        diffSurface.BorderThickness = new Thickness(0, 1, 0, 0); // the divider runs along the top here

        var body = new Grid { VerticalAlignment = VerticalAlignment.Stretch };
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(inputs, 0);
        Grid.SetRow(diffSurface, 1);
        body.Children.Add(inputs);
        body.Children.Add(diffSurface);

        SetBody(body);
        StatusBar.SetUntouched();
        InitializeChrome();
    }

    public override ToolKind Kind => ToolKind.TextDiff;
    public override bool IsDirty => _isDirty;

    public override IReadOnlyList<ToolCommand> Commands =>
    [
        ToolCommand.Clear(Clear),
    ];

    /// <summary>Restarts the countdown: a burst of typing recomputes once, at the end of it.</summary>
    private void ScheduleCompute()
    {
        // The character counts are cheap and are what tells the user their keystroke registered,
        // so they stay live; only the diff itself waits.
        StatusBar.SetCounts(_left.Text + _right.Text);
        _recomputeTimer.Stop();
        _recomputeTimer.Start();
    }

    private void Compute()
    {
        _recomputeTimer.Stop();
        _diff.Compute(_left.Text, _right.Text, _ignoreWhitespace.IsChecked == true, _ignoreCase.IsChecked == true, _ignoreBlankLines.IsChecked == true);
        StatusBar.SetCounts(_left.Text + _right.Text);
        StatusBar.SetValid();
    }

    private void Clear()
    {
        _left.Text = "";
        _right.Text = "";
        StatusBar.SetUntouched();
    }
}
