using DevDX.Controls;
using DevDX.Models;
using DevDX.Services;
using DevDX.Services.Formats;
using DevDX.Services.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace DevDX.ToolPages;

/// <summary>
/// JWT Decoder (design doc §14.7): splits header/payload/signature, renders them as colourised
/// JSON, humanises exp/iat/nbf with a live chip, warns on <c>alg: none</c> and a mismatched
/// <c>typ</c>. <b>Explicitly does not verify signatures</b>, and the disclaimer beside Clear,
/// in the command bar, says so.
/// </summary>
public sealed class JwtPage : EditorToolPage
{
    private readonly CodeEditor _input = new()
    {
        PlaceholderText = "eyJhbGciOi...header.eyJzdWIi...payload.signature",
        AccessibleName = Loc.Get("Common.Input"),
    };
    private readonly CodeView _headerView = new();
    private readonly CodeView _payloadView = new();
    private readonly TextBlock _signature = new() { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new Thickness(8) };
    private readonly InfoBar _warnings = new() { Severity = InfoBarSeverity.Warning, IsClosable = false, IsOpen = false };
    private readonly TextBlock _expiry = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(8, 0, 8, 0) };
    private bool _isDirty;

    public JwtPage()
    {
        // The "this never verifies signatures" disclaimer used to be a full-width InfoBar row of
        // its own above the input, which left the whole left half of the command bar — the row
        // Clear already sits in — empty. A permanent disclaimer for a tool that only ever shows
        // this one message doesn't need an InfoBar's full chrome, so it rides in the command bar
        // instead: a small info glyph plus the same localized sentence, trimmed to one line with
        // the untrimmed text on the tooltip and as the automation name, so it still reads in full
        // on hover/focus even when the window is narrow. Severity stays informational (not a
        // warning glyph) because that's what the message always was — a fact about the tool, not
        // an alert about the current token.
        var neverVerifiesText = Loc.Get("Jwt.NeverVerifies");
        var neverVerifies = OptionsBar();
        neverVerifies.Children.Add(new FontIcon { Glyph = "", FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 });
        neverVerifies.Children.Add(new TextBlock
        {
            Text = neverVerifiesText,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 460,
            Opacity = 0.8,
        });
        ToolTipService.SetToolTip(neverVerifies, neverVerifiesText);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(neverVerifies, neverVerifiesText);
        SetOptions(neverVerifies);

        _input.TextChanged += (_, _) =>
        {
            _isDirty = _input.Text.Length > 0;
            StatusBar.SetCounts(_input.Text);
            Decode();
        };

        var claimsBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };
        claimsBar.Children.Add(_expiry);

        var sections = new Grid();
        sections.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sections.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sections.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sections.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var headerPane = Pane(Loc.Get("Jwt.Header"), _headerView);
        var payloadPane = Pane(Loc.Get("Jwt.Payload"), _payloadView);
        Grid.SetColumn(headerPane, 0);
        Grid.SetColumn(payloadPane, 1);
        var sigPane = Pane(Loc.Get("Jwt.Signature"), _signature);
        Grid.SetColumnSpan(sigPane, 2);
        Grid.SetRow(sigPane, 1);
        sections.Children.Add(headerPane);
        sections.Children.Add(payloadPane);
        sections.Children.Add(sigPane);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_input, 0);
        Grid.SetRow(sections, 1);
        Grid.SetRow(claimsBar, 2);
        Grid.SetRow(_warnings, 3);
        body.Children.Add(_input);
        body.Children.Add(sections);
        body.Children.Add(claimsBar);
        body.Children.Add(_warnings);

        SetBody(body);
        StatusBar.SetUntouched();
        InitializeChrome();
    }

    private static FrameworkElement Pane(string label, FrameworkElement content)
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
            default: Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(content, label); break;
        }

        return grid;
    }

    public override ToolKind Kind => ToolKind.Jwt;
    public override bool IsDirty => _isDirty;

    public override IReadOnlyList<ToolCommand> Commands =>
    [
        ToolCommand.Clear(Clear),
    ];

    public override bool AcceptsClipboardText(string text) => text.Count(c => c == '.') == 2 && !text.Contains(' ');

    public override void PasteClipboardText(string text) => _input.Text = text.Trim();

    private void Decode()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0)
        {
            _headerView.Clear();
            _payloadView.Clear();
            _signature.Text = "";
            _expiry.Text = "";
            _warnings.IsOpen = false;
            StatusBar.SetUntouched();
            return;
        }

        if (!JwtTools.TryDecode(text, out var decoded, out var error))
        {
            _headerView.SetContent(error);
            _payloadView.Clear();
            _signature.Text = "";
            StatusBar.SetError(1, 1);
            return;
        }

        _headerView.SetContent(decoded.HeaderJson, JsonTokenizerTokens(decoded.HeaderJson));
        _payloadView.SetContent(decoded.PayloadJson, JsonTokenizerTokens(decoded.PayloadJson));
        _signature.Text = decoded.Signature;

        _expiry.Text = decoded.Exp is { } exp ? JwtTools.HumanizeRelative(exp, DateTimeOffset.UtcNow) : "";

        _warnings.IsOpen = decoded.Warnings.Count > 0;
        _warnings.Message = string.Join(" ", decoded.Warnings);
        StatusBar.SetValid();
    }

    private static IReadOnlyList<Services.Syntax.Token> JsonTokenizerTokens(string json) =>
        FormatRegistry.Json.Tokenizer.Tokenize(json);

    private void Clear()
    {
        _input.Text = "";
        StatusBar.SetUntouched();
    }
}
