using DevDX.Controls;
using DevDX.Models;
using DevDX.Services;
using DevDX.Services.Masking;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace DevDX.ToolPages;

/// <summary>
/// Data Masker (design doc §15): one editable pane beside the findings list. The findings list is
/// the heart of the tool — one row per detection with a per-finding strategy dropdown and
/// include/exclude checkbox.
/// <para>
/// <b>One window, not two.</b> Masking rewrites the editor in place instead of filling a separate
/// output pane. The unmasked text the user pasted stays in <see cref="_source"/>, in memory only,
/// because every re-mask (toggling a finding, changing a strategy) has to start from the original
/// — but it is never shown again and never copyable, which is exactly the §15.4 guarantee the old
/// two-pane layout only half kept.
/// </para>
/// <b>Non-negotiables (§15.4):</b> a permanent, non-dismissible banner; the unmasked input is
/// never written to disk (<c>Save as…</c>/copy act on the masked output only, and there is
/// deliberately no "save original" affordance); nothing is logged or leaves the process.
/// </summary>
public sealed class MaskerPage : EditorToolPage
{
    private readonly CodeEditor _editor = new() { PlaceholderText = Loc.Get("Masker.InputPlaceholder") };
    private readonly ItemsControl _findingsList = new();
    private readonly TextBlock _findingCount = new() { Margin = new Thickness(8, 4, 8, 0), Opacity = 0.8 };
    private readonly ComboBox _profile = new();
    private readonly ComboBox _threshold = new();
    private readonly Masker _masker = new();

    private List<Finding> _findings = [];
    private bool _isDirty;

    /// <summary>The text as the user last typed or pasted it. Every mask is computed from here,
    /// never from the editor, whose content is already masked. In-memory only — never written to
    /// disk, never copied out (§15.4).</summary>
    private string _source = "";

    /// <summary>Set while the page writes the masked text back into the editor, so its own write
    /// isn't mistaken for the user editing.</summary>
    private bool _replacing;

    private static readonly Confidence[] Thresholds = [Confidence.Low, Confidence.Medium, Confidence.High];

    public MaskerPage()
    {
        var banner = new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            IsClosable = false,
            IsOpen = true,
            Message = Loc.Get("Masker.Disclaimer"),
        };

        foreach (var p in MaskProfile.BuiltIn)
            _profile.Items.Add(p.Name);
        _profile.SelectedIndex = 0;
        _profile.SelectionChanged += (_, _) => Analyze();

        foreach (var t in Thresholds)
            _threshold.Items.Add(Loc.Get("Masker.Threshold." + t));
        _threshold.SelectedIndex = 1; // Medium
        _threshold.SelectionChanged += (_, _) => Analyze();

        var maskAll = new Button { Content = Loc.Get("Masker.MaskAll") };
        maskAll.Click += (_, _) => SetAllIncluded(true);
        var maskNone = new Button { Content = Loc.Get("Masker.MaskNone") };
        maskNone.Click += (_, _) => SetAllIncluded(false);

        var toolbar = OptionsBar();
        toolbar.Children.Add(OptionLabel(Loc.Get("Masker.Profile")));
        toolbar.Children.Add(_profile);
        toolbar.Children.Add(OptionLabel(Loc.Get("Masker.ThresholdLabel")));
        toolbar.Children.Add(_threshold);
        toolbar.Children.Add(maskAll);
        toolbar.Children.Add(maskNone);
        SetOptions(toolbar);

        _editor.TextChanged += (_, _) =>
        {
            if (_replacing)
                return; // our own masked write-back, not the user typing
            _source = _editor.Text;
            _isDirty = _source.Length > 0;
            Analyze();
        };

        var findingsPane = new Grid();
        findingsPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        findingsPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_findingCount, 0);
        var findingsScroller = new ScrollViewer { Content = _findingsList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(findingsScroller, 1);
        findingsPane.Children.Add(_findingCount);
        findingsPane.Children.Add(findingsScroller);

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_editor, 0);
        Grid.SetColumn(findingsPane, 1);
        split.Children.Add(_editor);
        split.Children.Add(findingsPane);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(banner, 0);
        Grid.SetRow(split, 1);
        body.Children.Add(banner);
        body.Children.Add(split);

        SetBody(body);
        StatusBar.SetUntouched();
        InitializeChrome();
    }

    public override ToolKind Kind => ToolKind.DataMasker;
    public override bool IsDirty => _isDirty;

    public override IReadOnlyList<ToolCommand> Commands =>
    [
        ToolCommand.Copy(CopyMaskedOutput),
        ToolCommand.Clear(Clear),
    ];

    public override bool AcceptsClipboardText(string text) => text.Length > 0;
    public override void PasteClipboardText(string text) => SetSource(text);

    public override async Task LoadFileAsync(string path)
    {
        SetSource(await File.ReadAllTextAsync(path));
    }

    private MaskProfile CurrentProfile => MaskProfile.BuiltIn[_profile.SelectedIndex];
    private Confidence CurrentThreshold => Thresholds[_threshold.SelectedIndex];

    /// <summary>Seeds the page from outside (a paste chip, a dropped file) and analyses it.</summary>
    private void SetSource(string text)
    {
        _source = text;
        _isDirty = text.Length > 0;
        Replace(text);
        Analyze();
    }

    /// <summary>The one place the page writes into the editor, guarded so the write-back doesn't
    /// come back round as a user edit and overwrite <see cref="_source"/> with masked text.</summary>
    private void Replace(string text)
    {
        _replacing = true;
        try
        {
            _editor.Text = text;
        }
        finally
        {
            _replacing = false;
        }
    }

    private void Analyze()
    {
        var text = _source;
        StatusBar.SetCounts(text);
        if (text.Length == 0)
        {
            _findings = [];
            RenderFindingsList();
            StatusBar.SetUntouched();
            return;
        }

        var profile = CurrentProfile;
        var detected = PiiDetector.Detect(text, PiiRuleSet.Default, CurrentThreshold);
        _findings = detected.Select(f => f with { Strategy = profile.StrategyFor(f.Category) }).ToList();
        RenderFindingsList();
        Remask();
        StatusBar.SetValid();
    }

    private void RenderFindingsList()
    {
        _findingCount.Text = Loc.Format("Masker.FindingCount", _findings.Count);
        var rows = new List<FrameworkElement>();
        for (int i = 0; i < _findings.Count; i++)
        {
            int idx = i;
            var f = _findings[i];
            var preview = Excerpt(f);

            var include = new CheckBox { IsChecked = f.Included, Margin = new Thickness(0, 0, 8, 0) };
            include.Checked += (_, _) => { _findings[idx] = _findings[idx] with { Included = true }; Remask(); };
            include.Unchecked += (_, _) => { _findings[idx] = _findings[idx] with { Included = false }; Remask(); };

            var info = new StackPanel { Spacing = 2 };
            info.Children.Add(new TextBlock { Text = $"{f.Category}  ·  {f.Confidence}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12 });
            info.Children.Add(new TextBlock { Text = preview, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 11, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });

            var strategyBox = new ComboBox { Width = 160 };
            foreach (var s in Enum.GetValues<MaskStrategy>())
                strategyBox.Items.Add(s.ToString());
            strategyBox.SelectedIndex = (int)f.Strategy;
            strategyBox.SelectionChanged += (_, _) =>
            {
                _findings[idx] = _findings[idx] with { Strategy = (MaskStrategy)strategyBox.SelectedIndex };
                Remask();
            };

            var row = new Grid { Padding = new Thickness(8, 6, 8, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(include, 0);
            Grid.SetColumn(info, 1);
            Grid.SetColumn(strategyBox, 2);
            row.Children.Add(include);
            row.Children.Add(info);
            row.Children.Add(strategyBox);
            rows.Add(new Border { Child = row, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] });
        }
        _findingsList.ItemsSource = rows;
    }

    /// <summary>
    /// The snippet of the original text a finding points at, clamped to what
    /// <see cref="_source"/> actually holds. Findings are computed from the source, so in the
    /// normal case the offsets are in range — but the list survives between an edit and the
    /// re-analysis that follows it, and a row rendered against a source that has since got
    /// shorter would take an <c>ArgumentOutOfRangeException</c> straight through the UI thread.
    /// </summary>
    private string Excerpt(Finding finding)
    {
        if (finding.Start < 0 || finding.Start >= _source.Length)
            return "";
        int length = Math.Min(Math.Min(finding.Length, 40), _source.Length - finding.Start);
        return length <= 0 ? "" : _source.Substring(finding.Start, length);
    }

    private void SetAllIncluded(bool included)
    {
        for (int i = 0; i < _findings.Count; i++)
            _findings[i] = _findings[i] with { Included = included };
        RenderFindingsList();
        Remask();
    }

    private void Remask()
    {
        var output = _masker.BuildOutput(_source, _findings);
        Replace(output.Text);
    }

    private void CopyMaskedOutput()
    {
        // §15.4: the masker never surfaces the unmasked input via copy/save. Copying the editor
        // is safe precisely because the editor only ever holds masked text — the original lives
        // in _source and has no route out of the process.
        if (_editor.Text.Length == 0)
            return;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(_editor.Text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void Clear()
    {
        _source = "";
        _isDirty = false;
        Replace("");
        _findings = [];
        RenderFindingsList();
        StatusBar.SetUntouched();
    }
}
