using System.Text.RegularExpressions;
using dockdev.Controls;
using dockdev.Models;
using dockdev.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.System;
using RegexRunner = dockdev.Services.Tools.RegexRunner;

namespace dockdev.ToolPages;

/// <summary>
/// Regex Tester (design doc §14.12), .NET flavour. The subject stays editable (§10.2's "input is
/// plain" rule) with a read-only colourised echo underneath showing match/group spans, a groups
/// table, and a replacement preview. Every evaluation runs through <see cref="RegexRunner"/>,
/// which enforces the match timeout.
/// </summary>
public sealed class RegexPage : EditorToolPage
{
    private readonly TextBox _pattern = new() { PlaceholderText = Loc.Get("Regex.PatternPlaceholder"), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), MinWidth = 240 };
    private readonly CodeEditor _subject = new() { AccessibleName = Loc.Get("Regex.Subject") };
    private readonly CodeView _matchView = new() { ShowLineNumbers = false, AccessibleName = Loc.Get("Regex.Matches") };
    private readonly TextBox _replacement = new() { PlaceholderText = Loc.Get("Regex.ReplacementPlaceholder"), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") };
    private readonly TextBlock _replacementPreview = new() { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new Thickness(8) };
    private readonly ListView _groupsTable = new() { SelectionMode = ListViewSelectionMode.None };
    private readonly CheckBox _ignoreCase = new() { Content = "IgnoreCase" };
    private readonly CheckBox _multiline = new() { Content = "Multiline" };
    private readonly CheckBox _singleline = new() { Content = "Singleline" };
    private readonly CheckBox _ignoreWhitespace = new() { Content = "IgnorePatternWhitespace" };
    private readonly CheckBox _ecmaScript = new() { Content = "ECMAScript" };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = false };
    private bool _isDirty;

    public RegexPage()
    {
        foreach (var box in new[] { _ignoreCase, _multiline, _singleline, _ignoreWhitespace, _ecmaScript })
            box.Checked += (_, _) => Run();
        foreach (var box in new[] { _ignoreCase, _multiline, _singleline, _ignoreWhitespace, _ecmaScript })
            box.Unchecked += (_, _) => Run();

        _pattern.TextChanged += (_, _) => { _isDirty = true; Run(); };
        _subject.TextChanged += (_, _) => { _isDirty = _subject.Text.Length > 0; Run(); };
        _replacement.TextChanged += (_, _) => Run();

        var optionsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12, 4, 12, 0) };
        foreach (var box in new[] { _ignoreCase, _multiline, _singleline, _ignoreWhitespace, _ecmaScript })
            optionsRow.Children.Add(box);

        SetOptions(_pattern);
        // SetOptions just forced Left; the field should read as one continuous bar — pattern field
        // filling the row, Clear button anchored at the far right — not a short box with dead space
        // beside it.
        _pattern.HorizontalAlignment = HorizontalAlignment.Stretch;
        // A stock CommandBar's Content cell isn't guaranteed to stretch on its own even with
        // HorizontalAlignment.Stretch — the cell itself can size to content — so the width is also
        // driven explicitly off the page's own size as a reliable fallback.
        Loaded += (_, _) => UpdatePatternWidth();
        SizeChanged += (_, _) => UpdatePatternWidth();

        var top = new Grid();
        top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(optionsRow, 0);
        Grid.SetRow(_error, 1);
        top.Children.Add(optionsRow);
        top.Children.Add(_error);

        var subjectPane = new Grid();
        subjectPane.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        subjectPane.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var subjectSurface = Pane(_subject);
        var matchSurface = Pane(_matchView, secondary: true);
        Grid.SetColumn(subjectSurface, 0);
        Grid.SetColumn(matchSurface, 1);
        subjectPane.Children.Add(subjectSurface);
        subjectPane.Children.Add(matchSurface);

        var bottom = new Grid();
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var groupsPane = new Grid();
        groupsPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        groupsPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var groupsHeader = new TextBlock { Text = Loc.Get("Regex.Groups"), Margin = new Thickness(8, 4, 8, 0), Opacity = 0.7 };
        Grid.SetRow(groupsHeader, 0);
        Grid.SetRow(_groupsTable, 1);
        groupsPane.Children.Add(groupsHeader);
        groupsPane.Children.Add(_groupsTable);

        var replacePane = new Grid();
        replacePane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        replacePane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_replacement, 0);
        Grid.SetRow(_replacementPreview, 1);
        replacePane.Children.Add(_replacement);
        replacePane.Children.Add(_replacementPreview);

        var groupsSurface = Pane(groupsPane, secondary: true);
        var replaceSurface = Pane(replacePane, secondary: true);
        Grid.SetColumn(groupsSurface, 0);
        Grid.SetColumn(replaceSurface, 1);
        bottom.Children.Add(groupsSurface);
        bottom.Children.Add(replaceSurface);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(top, 0);
        Grid.SetRow(subjectPane, 1);
        Grid.SetRow(bottom, 2);
        body.Children.Add(top);
        body.Children.Add(subjectPane);
        body.Children.Add(bottom);

        SetBody(body);
        StatusBar.SetUntouched();
        InitializeChrome();
    }

    /// <summary>
    /// Recomputes <see cref="_pattern"/>'s width from the Clear button's actual rendered position.
    /// A first pass at this reserved a guessed 150px for the button and the CommandBar's chrome,
    /// and that guess turned out too large, leaving dead space before the button — a second guess
    /// would just as easily miss in the other direction. Measuring where the real button ended up
    /// (via <see cref="EditorToolPage.CommandBar"/>) removes the guesswork entirely: whatever the
    /// CommandBar template actually reserves, the field's width self-corrects to it.
    /// </summary>
    private void UpdatePatternWidth()
    {
        if (CommandBar.PrimaryCommands.Count > 0 &&
            CommandBar.PrimaryCommands[0] is FrameworkElement clearButton &&
            clearButton.ActualWidth > 0)
        {
            const double Gap = 12;
            var clearOrigin = clearButton.TransformToVisual(this).TransformPoint(new Point(0, 0));
            var patternOrigin = _pattern.TransformToVisual(this).TransformPoint(new Point(0, 0));
            _pattern.Width = Math.Max(240, clearOrigin.X - patternOrigin.X - Gap);
            return;
        }

        // The Clear button hasn't been through a layout pass yet (can legitimately happen on the
        // very first Loaded firing, before layout settles) — fall back so the field isn't left
        // tiny for that one brief window. 80px covers the button's own known 40px width
        // (EditorToolPage.IconOnlyButtonWidth) plus a tighter, more realistic chrome allowance
        // than the old 150px guess. SizeChanged keeps firing afterward, so the precise path above
        // takes over as soon as the button actually has a size.
        _pattern.Width = Math.Max(240, ActualWidth - 80);
    }

    public override ToolKind Kind => ToolKind.RegexTester;
    public override bool IsDirty => _isDirty;

    public override IReadOnlyList<ToolCommand> Commands =>
    [
        ToolCommand.Clear(Clear),
    ];

    private RegexOptions BuildOptions()
    {
        var options = RegexOptions.None;
        if (_ignoreCase.IsChecked == true) options |= RegexOptions.IgnoreCase;
        if (_multiline.IsChecked == true) options |= RegexOptions.Multiline;
        if (_singleline.IsChecked == true) options |= RegexOptions.Singleline;
        if (_ignoreWhitespace.IsChecked == true) options |= RegexOptions.IgnorePatternWhitespace;
        if (_ecmaScript.IsChecked == true) options |= RegexOptions.ECMAScript;
        return options;
    }

    /// <summary>
    /// Serial number of the most recent evaluation. Every keystroke starts a background match, and
    /// two of them can finish out of order — a cheap pattern typed after an expensive one comes
    /// back first, and the slow result then lands on top of it. Only the newest run is allowed to
    /// write to the UI; anything older is dropped where it returns.
    /// </summary>
    private int _runId;

    private async void Run()
    {
        int runId = ++_runId;

        var pattern = _pattern.Text;
        var subject = _subject.Text;
        StatusBar.SetCounts(subject);

        if (pattern.Length == 0 || subject.Length == 0)
        {
            _matchView.SetContent(subject);
            _groupsTable.ItemsSource = null;
            _replacementPreview.Text = "";
            _error.IsOpen = false;
            StatusBar.SetUntouched();
            return;
        }

        var options = BuildOptions();
        var replacement = _replacement.Text.Length > 0 ? _replacement.Text : null;
        var result = await Task.Run(() => RegexRunner.Run(pattern, subject, options, replacement));

        // Superseded while we were matching, or the window closed underneath us.
        if (runId != _runId || PageClosing.IsCancellationRequested)
            return;

        if (!result.Success)
        {
            _error.IsOpen = true;
            _error.Message = result.Error ?? "Invalid pattern.";
            _matchView.SetContent(subject);
            StatusBar.SetError(1, 1);
            return;
        }

        _error.IsOpen = false;
        _matchView.SetContent(subject, result.Tokens);
        _groupsTable.ItemsSource = result.Groups
            .Select(g => $"[{g.Index}] {(string.IsNullOrEmpty(g.Name) || g.Name == g.Index.ToString() ? "" : g.Name + " ")}@{g.Position}: {g.Value}")
            .ToList();
        _replacementPreview.Text = result.Replacement ?? "";
        StatusBar.SetValid();
    }

    private void Clear()
    {
        _pattern.Text = "";
        _subject.Text = "";
        _replacement.Text = "";
        StatusBar.SetUntouched();
    }
}
