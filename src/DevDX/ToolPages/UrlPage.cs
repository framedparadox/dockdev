using DevDX.Controls;
using DevDX.Models;
using DevDX.Services;
using DevDX.Services.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace DevDX.ToolPages;

/// <summary>
/// URL &amp; Encoding (design doc §14.6): three sections in one page — percent-encoding (both RFC
/// 3986 and form encoding, since the difference trips people up), HTML entities, and a URL parser
/// with an editable query-parameter table.
/// </summary>
public sealed class UrlPage : EditorToolPage
{
    private readonly CodeEditor _percentInput = new() { AccessibleName = Loc.Get("Common.Input") };
    private readonly CodeView _rfc3986Output = new();
    private readonly CodeView _formOutput = new();

    private readonly CodeEditor _htmlInput = new() { AccessibleName = Loc.Get("Common.Input") };
    private readonly CodeView _htmlOutput = new() { AccessibleName = Loc.Get("Url.Html") };

    private readonly TextBox _urlInput = new() { PlaceholderText = "https://example.com/path?x=1#frag" };
    private readonly TextBlock _urlSummary = new() { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), TextWrapping = TextWrapping.Wrap };
    private readonly ItemsControl _queryTable = new();
    private readonly TextBlock _rebuilt = new() { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };

    private UrlTools.ParsedUrl? _parsed;
    private bool _isDirty;

    private readonly Button _percentButton = new() { Content = Loc.Get("Url.Percent") };
    private readonly Button _htmlButton = new() { Content = Loc.Get("Url.Html") };
    private readonly Button _parserButton = new() { Content = Loc.Get("Url.Parser") };
    private readonly Grid _sectionHost = new();

    /// <summary>Segoe Fluent Icons "Delete", for dropping a query parameter. Spelled as an escape
    /// rather than pasted in: the glyph here was an empty string, which renders as nothing — the
    /// button was invisible, and with no label or tooltip either there was no way to tell it was
    /// there at all.</summary>
    private const string RemoveGlyph = "\uE74D";

    public UrlPage()
    {
        // Built once each, up front, so every section's TextChanged/Click wiring stays live no
        // matter which one is currently on screen — the selector below only ever toggles
        // Visibility, it never rebuilds a section.
        var percentSection = BuildPercentSection();
        var htmlSection = BuildHtmlSection();
        var parserSection = BuildParserSection();
        htmlSection.Visibility = Visibility.Collapsed;
        parserSection.Visibility = Visibility.Collapsed;
        _sectionHost.Children.Add(percentSection);
        _sectionHost.Children.Add(htmlSection);
        _sectionHost.Children.Add(parserSection);

        void SelectSection(int index)
        {
            percentSection.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            htmlSection.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            parserSection.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

            var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
            _percentButton.Style = index == 0 ? accent : null;
            _htmlButton.Style = index == 1 ? accent : null;
            _parserButton.Style = index == 2 ? accent : null;
        }

        _percentButton.Click += (_, _) => SelectSection(0);
        _htmlButton.Click += (_, _) => SelectSection(1);
        _parserButton.Click += (_, _) => SelectSection(2);

        // No separate caption: unlike Masker's profile/threshold combos, each button here already
        // names the section it switches to ("Percent-Encoding", "HTML entities", "URL parser"), the
        // same text sighted users read off the old Pivot headers — so the button's own label carries
        // the same meaning a caption would, and the accent style marks which one is active.
        var options = OptionsBar();
        options.Children.Add(_percentButton);
        options.Children.Add(_htmlButton);
        options.Children.Add(_parserButton);
        SetOptions(options);

        SelectSection(0);

        SetBody(_sectionHost);
        StatusBar.SetUntouched();
        InitializeChrome();
    }

    private FrameworkElement BuildPercentSection()
    {
        _percentInput.TextChanged += (_, _) =>
        {
            _isDirty = _percentInput.Text.Length > 0;
            _rfc3986Output.SetContent(UrlTools.EncodeRfc3986(_percentInput.Text));
            _formOutput.SetContent(UrlTools.EncodeForm(_percentInput.Text));
            StatusBar.SetCounts(_percentInput.Text);
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var inputSurface = Pane(_percentInput);
        Grid.SetColumnSpan(inputSurface, 2);
        Grid.SetRow(inputSurface, 0);

        var rfcPane = LabelledPane(Loc.Get("Url.Rfc3986"), _rfc3986Output);
        var formPane = LabelledPane(Loc.Get("Url.Form"), _formOutput);
        Grid.SetRow(rfcPane, 1);
        Grid.SetColumn(rfcPane, 0);
        Grid.SetRow(formPane, 1);
        Grid.SetColumn(formPane, 1);

        grid.Children.Add(inputSurface);
        grid.Children.Add(rfcPane);
        grid.Children.Add(formPane);
        return grid;
    }

    private FrameworkElement BuildHtmlSection()
    {
        _htmlInput.TextChanged += (_, _) =>
        {
            _isDirty = _htmlInput.Text.Length > 0;
            _htmlOutput.SetContent(UrlTools.EncodeHtml(_htmlInput.Text));
            StatusBar.SetCounts(_htmlInput.Text);
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var inputSurface = Pane(_htmlInput);
        var outputSurface = Pane(_htmlOutput, secondary: true);
        Grid.SetColumn(inputSurface, 0);
        Grid.SetColumn(outputSurface, 1);
        grid.Children.Add(inputSurface);
        grid.Children.Add(outputSurface);
        return grid;
    }

    private FrameworkElement BuildParserSection()
    {
        _urlInput.TextChanged += (_, _) =>
        {
            _isDirty = _urlInput.Text.Length > 0;
            ParseUrl();
        };

        var addRow = new Button { Content = Loc.Get("Url.AddParam") };
        addRow.Click += (_, _) =>
        {
            _parsed?.Query.Add(("", ""));
            RenderQueryTable();
        };

        var stack = new StackPanel { Spacing = 10, Padding = new Thickness(16) };
        stack.Children.Add(_urlInput);
        stack.Children.Add(_urlSummary);
        stack.Children.Add(new TextBlock { Text = Loc.Get("Url.QueryParams"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        stack.Children.Add(_queryTable);
        stack.Children.Add(addRow);
        stack.Children.Add(new TextBlock { Text = Loc.Get("Url.Rebuilt"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        stack.Children.Add(_rebuilt);

        return new ScrollViewer { Content = stack };
    }

    private void ParseUrl()
    {
        if (!UrlTools.TryParse(_urlInput.Text, out var parsed))
        {
            _urlSummary.Text = Loc.Get("Url.ParseError");
            _parsed = null;
            _queryTable.ItemsSource = null;
            _rebuilt.Text = "";
            StatusBar.SetError(1, 1);
            return;
        }
        _parsed = parsed;
        _urlSummary.Text =
            $"scheme: {parsed.Scheme}\nhost: {parsed.Host}\nport: {(parsed.Port > 0 ? parsed.Port : "(default)")}\npath: {parsed.Path}\nfragment: {parsed.Fragment}";
        RenderQueryTable();
        StatusBar.SetValid();
    }

    private void RenderQueryTable()
    {
        if (_parsed is null)
            return;

        // Captured once, so every row's handlers act on the parse they were built from. Reading
        // the field back inside a lambda is a use-after-free waiting to happen: a row survives
        // just long enough for a failed re-parse to null `_parsed` out from under it.
        var parsed = _parsed;

        var rows = new List<FrameworkElement>();
        for (int i = 0; i < parsed.Query.Count; i++)
        {
            int idx = i;
            var keyBox = new TextBox { Text = parsed.Query[i].Key, Width = 160, PlaceholderText = Loc.Get("Url.QueryKeyPlaceholder") };
            var valueBox = new TextBox { Text = parsed.Query[i].Value, Width = 220, PlaceholderText = Loc.Get("Url.QueryValuePlaceholder") };
            // Formatted rather than concatenated (design doc §20: "no string concatenation for
            // sentences") and numbered per row, since a table of unnamed "Key"/"Value" boxes reads
            // as one pair to Narrator no matter which of several rows it is currently on.
            AutomationProperties.SetName(keyBox, Loc.Format("Url.QueryKeyFor", idx + 1));
            AutomationProperties.SetName(valueBox, Loc.Format("Url.QueryValueFor", idx + 1));

            // Guarded on the index as well: a row that outlives a Remove elsewhere in the table
            // would otherwise index past the end of the list it was built from.
            keyBox.TextChanged += (_, _) =>
            {
                if (idx >= parsed.Query.Count)
                    return;
                parsed.Query[idx] = (keyBox.Text, parsed.Query[idx].Value);
                _rebuilt.Text = parsed.Rebuild();
            };
            valueBox.TextChanged += (_, _) =>
            {
                if (idx >= parsed.Query.Count)
                    return;
                parsed.Query[idx] = (parsed.Query[idx].Key, valueBox.Text);
                _rebuilt.Text = parsed.Rebuild();
            };

            var remove = new Button
            {
                Content = new FontIcon { Glyph = RemoveGlyph, FontSize = 14 },
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(remove, Loc.Get("Url.RemoveParam"));
            AutomationProperties.SetName(remove, Loc.Get("Url.RemoveParam"));
            remove.Click += (_, _) =>
            {
                if (idx >= parsed.Query.Count)
                    return;
                parsed.Query.RemoveAt(idx);
                RenderQueryTable();
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(keyBox);
            row.Children.Add(valueBox);
            row.Children.Add(remove);
            rows.Add(row);
        }
        _queryTable.ItemsSource = rows;
        _rebuilt.Text = parsed.Rebuild();
    }

    private static FrameworkElement LabelledPane(string label, FrameworkElement content)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new TextBlock { Text = label, Margin = new Thickness(8, 4, 8, 0), Opacity = 0.7 };
        Grid.SetRow(header, 0);
        Grid.SetRow(content, 1);
        grid.Children.Add(header);
        grid.Children.Add(content);

        // The visible header above never reached the content itself, so Narrator read the header
        // and the (nameless) content as two unrelated things instead of one labelled section.
        switch (content)
        {
            case CodeView view: view.AccessibleName = label; break;
            case CodeEditor editor: editor.AccessibleName = label; break;
            default: AutomationProperties.SetName(content, label); break;
        }

        return Pane(grid, secondary: true);
    }

    public override ToolKind Kind => ToolKind.UrlEncoding;
    public override bool IsDirty => _isDirty;

    public override IReadOnlyList<ToolCommand> Commands =>
    [
        ToolCommand.Clear(Clear),
    ];

    public override bool AcceptsClipboardText(string text) => text.Contains("://") || text.Contains('%');

    public override void PasteClipboardText(string text)
    {
        if (text.Contains("://"))
            _urlInput.Text = text;
        else
            _percentInput.Text = text;
    }

    private void Clear()
    {
        _percentInput.Text = "";
        _htmlInput.Text = "";
        _urlInput.Text = "";
        StatusBar.SetUntouched();
    }
}
