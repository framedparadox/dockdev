using DevDX.Controls;
using DevDX.Models;
using DevDX.Services;
using DevDX.Services.Tools;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace DevDX.ToolPages;

/// <summary>
/// Timestamp Converter (design doc §14.14): epoch ⇄ ISO 8601 ⇄ RFC 1123 ⇄ local/UTC/named
/// timezone, a live "now" row, and a relative rendering. Auto-detects which unit a pasted number
/// is by magnitude, with an override.
/// </summary>
public sealed class TimestampPage : FormToolPage
{
    private readonly TextBox _input = new() { PlaceholderText = Loc.Get("Timestamp.InputPlaceholder") };
    private readonly ComboBox _unit = new();
    private readonly TextBlock _now = new() { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") };
    private readonly TextBox _epochSeconds;
    private readonly TextBox _epochMillis;
    private readonly TextBox _iso8601;
    private readonly TextBox _rfc1123;
    private readonly TextBox _local;
    private readonly TextBox _utc;
    private readonly TextBox _relative;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _clock;

    private static readonly EpochUnit[] Units = [EpochUnit.Seconds, EpochUnit.Milliseconds, EpochUnit.Microseconds];

    public TimestampPage()
    {
        var nowConvertRow = new Grid { ColumnSpacing = 24 };
        nowConvertRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nowConvertRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nowConvertRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nowPanel = new StackPanel { Spacing = 4 };
        nowPanel.Children.Add(SectionHeader(Loc.Get("Timestamp.Now")));
        nowPanel.Children.Add(_now);
        Grid.SetColumn(nowPanel, 1);
        var inputRow = LabelledRow(Loc.Get("Timestamp.Input"), _input);
        Grid.SetColumn(inputRow, 0);
        var reset = new Button
        {
            Content = new FontIcon { Glyph = "\uE72C", FontSize = 14 },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTipService.SetToolTip(reset, Loc.Get("Timestamp.Reset"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(reset, Loc.Get("Timestamp.Reset"));
        reset.Click += (_, _) => { _input.Text = ""; _unit.SelectedIndex = 0; Convert(); };
        Grid.SetColumn(reset, 2);
        nowConvertRow.Children.Add(nowPanel);
        nowConvertRow.Children.Add(inputRow);
        nowConvertRow.Children.Add(reset);
        AddRow(nowConvertRow);

        var useNow = new Button { Content = Loc.Get("Timestamp.UseNow") };
        useNow.Click += (_, _) => { _input.Text = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(); Convert(); };
        AddRow(useNow);

        foreach (var u in Units)
            _unit.Items.Add(Loc.Get("Timestamp.Unit." + u));
        _unit.SelectedIndex = 0;
        AddRow(LabelledRow(Loc.Get("Timestamp.UnitLabel"), _unit));

        (var epochSecondsRow, _epochSeconds, _) = ResultRow(Loc.Get("Timestamp.EpochSeconds"));
        (var epochMillisRow, _epochMillis, _) = ResultRow(Loc.Get("Timestamp.EpochMillis"));
        (var isoRow, _iso8601, _) = ResultRow(Loc.Get("Timestamp.Iso8601"));
        (var rfcRow, _rfc1123, _) = ResultRow(Loc.Get("Timestamp.Rfc1123"));
        (var localRow, _local, _) = ResultRow(Loc.Get("Timestamp.Local"));
        (var utcRow, _utc, _) = ResultRow(Loc.Get("Timestamp.Utc"));
        (var relativeRow, _relative, _) = ResultRow(Loc.Get("Timestamp.Relative"));

        AddRow(PairRow(epochSecondsRow, epochMillisRow));
        AddRow(PairRow(isoRow, rfcRow));
        AddRow(PairRow(localRow, utcRow));
        AddRow(relativeRow);

        _input.TextChanged += (_, _) =>
        {
            if (long.TryParse(_input.Text.Trim(), out var value))
                _unit.SelectedIndex = Array.IndexOf(Units, TimestampTools.DetectUnit(value));
            Convert();
        };
        _unit.SelectionChanged += (_, _) => Convert();

        _clock = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => { _now.Text = TimestampTools.ToIso8601(DateTimeOffset.Now); Convert(refreshRelativeOnly: true); };
        _clock.Start();
        // A running DispatcherQueueTimer is rooted by the dispatcher queue, and its Tick closure
        // holds this page — so without this the "now" clock goes on ticking once a second for the
        // rest of the process, keeping a closed window's whole visual tree alive with it.
        PageClosing.Register(_clock.Stop);
        _now.Text = TimestampTools.ToIso8601(DateTimeOffset.Now);

        Convert();
    }

    public override ToolKind Kind => ToolKind.Timestamp;
    public override bool IsDirty => false;
    public override IReadOnlyList<ToolCommand> Commands => [];

    public override bool AcceptsClipboardText(string text) => TimestampTools.TryParseAny(text.Trim(), out _);
    public override void PasteClipboardText(string text) { _input.Text = text.Trim(); Convert(); }

    private void Convert(bool refreshRelativeOnly = false)
    {
        if (!TimestampTools.TryParseAny(_input.Text, out var when))
        {
            if (!refreshRelativeOnly)
            {
                foreach (var box in new[] { _epochSeconds, _epochMillis, _iso8601, _rfc1123, _local, _utc, _relative })
                    box.Text = "";
            }
            return;
        }

        if (!refreshRelativeOnly)
        {
            _epochSeconds.Text = when.ToUnixTimeSeconds().ToString();
            _epochMillis.Text = when.ToUnixTimeMilliseconds().ToString();
            _iso8601.Text = TimestampTools.ToIso8601(when);
            _rfc1123.Text = TimestampTools.ToRfc1123(when);
            _local.Text = when.ToLocalTime().ToString("F");
            _utc.Text = when.ToUniversalTime().ToString("F") + " UTC";
        }
        _relative.Text = TimestampTools.ToRelative(when, DateTimeOffset.Now);
    }

    /// <summary>Lays two result rows side by side in a 2-column grid, so related conversions (e.g.
    /// epoch seconds/millis, local/UTC) read as a pair instead of each claiming a full-width row.</summary>
    private static Grid PairRow(FrameworkElement left, FrameworkElement right)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }
}
