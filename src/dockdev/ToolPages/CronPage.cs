using System.Globalization;
using dockdev.Controls;
using dockdev.Models;
using dockdev.Services;
using dockdev.Services.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace dockdev.ToolPages;

/// <summary>
/// Cron Expression parser: reads a five-field expression back in plain English and lists the next
/// times it fires, which is the only reliable way to tell <c>0 0 * * 0</c> from <c>0 0 0 * *</c>
/// at a glance. Times are shown in local time, since that is the question being asked ("when will
/// this actually run?").
/// </summary>
public sealed class CronPage : FormToolPage
{
    private const int OccurrenceCount = 10;

    private readonly TextBox _expression = new() { PlaceholderText = Loc.Get("Cron.Placeholder"), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _next = new()
    {
        IsReadOnly = true,
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        Height = 240,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    public CronPage()
    {
        AddRow(SectionHeader(Loc.Get("Tool.Cron.Name")));
        AddRow(LabelledRow(Loc.Get("Cron.Expression"), _expression));
        AddRow(new TextBlock { Text = Loc.Get("Cron.FieldHint"), Opacity = 0.75, TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12 });
        AddRow(_summary);

        // Built by hand rather than via ResultRow: the copy button needs to sit beside the "Next N
        // runs" title itself, not beside the bottom of the 240px box the title sits above.
        var nextTitle = Loc.Format("Cron.NextRuns", OccurrenceCount);
        var nextHeader = new TextBlock { Text = nextTitle, Opacity = 0.8, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center };
        var nextCopy = new CopyButton { GetText = () => _next.Text, VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(nextCopy, Loc.Get("Tool.CopyOutput") + ": " + nextTitle);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_next, nextTitle);

        var nextHeaderRow = new Grid { ColumnSpacing = 8 };
        nextHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nextHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(nextHeader, 0);
        Grid.SetColumn(nextCopy, 1);
        nextHeaderRow.Children.Add(nextHeader);
        nextHeaderRow.Children.Add(nextCopy);

        AddRow(nextHeaderRow);
        AddRow(_next);

        _expression.TextChanged += (_, _) => Parse();
        _expression.Text = "*/15 9-17 * * mon-fri";
    }

    public override ToolKind Kind => ToolKind.Cron;
    public override bool IsDirty => false;

    public override IReadOnlyList<ToolCommand> Commands =>
    [
        ToolCommand.Copy(() => Clipboard(_next.Text)),
        ToolCommand.Clear(() => _expression.Text = ""),
    ];

    public override bool AcceptsClipboardText(string text) =>
        text.Length is > 0 and < 120 && CronTools.Parse(text).Success;

    public override void PasteClipboardText(string text) => _expression.Text = text;

    private void Parse()
    {
        var schedule = CronTools.Parse(_expression.Text);
        if (!schedule.Success)
        {
            _summary.Text = schedule.Error;
            _next.Text = "";
            return;
        }

        _summary.Text = CronTools.Describe(schedule);
        var occurrences = CronTools.NextOccurrences(schedule, DateTime.Now, OccurrenceCount);
        _next.Text = occurrences.Count == 0
            ? Loc.Get("Cron.Never")
            : string.Join('\n', occurrences.Select(o => o.ToString("yyyy-MM-dd HH:mm  ddd", CultureInfo.CurrentCulture)));
    }

    private static void Clipboard(string text)
    {
        if (text.Length == 0)
            return;
        ClipboardService.TrySetText(text);
    }
}
