using System.Text.RegularExpressions;
using DevDX.Controls;
using DevDX.Models;
using DevDX.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using RegexRunner = DevDX.Services.Tools.RegexRunner;

namespace DevDX.ToolPages;

/// <summary>
/// Regex Tester (design doc §14.12), .NET flavour. The subject stays editable (§10.2's "input is
/// plain" rule) with a read-only colourised echo underneath showing match/group spans, a groups
/// table, and a replacement preview. Every evaluation runs through <see cref="RegexRunner"/>,
/// which enforces the match timeout.
/// </summary>
public sealed class RegexPage : EditorToolPage
{
    private readonly TextBox _pattern = new() { PlaceholderText = Loc.Get("Regex.PatternPlaceholder"), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") };
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

        var top = new Grid();
        top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_pattern, 0);
        Grid.SetRow(optionsRow, 1);
        Grid.SetRow(_error, 2);
        top.Children.Add(_pattern);
        top.Children.Add(optionsRow);
        top.Children.Add(_error);

        var subjectPane = new Grid();
        subjectPane.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        subjectPane.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_subject, 0);
        Grid.SetColumn(_matchView, 1);
        subjectPane.Children.Add(_subject);
        subjectPane.Children.Add(_matchView);

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

        Grid.SetColumn(groupsPane, 0);
        Grid.SetColumn(replacePane, 1);
        bottom.Children.Add(groupsPane);
        bottom.Children.Add(replacePane);

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
