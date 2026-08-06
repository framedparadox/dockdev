using DevDX.Models;
using DevDX.Services;
using DevDX.Services.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DevDX.ToolPages;

/// <summary>
/// Number Base (design doc §14.15): decimal, hex, binary, octal, all live-linked; width selector;
/// signed/unsigned; a bitwise playground with a clickable bit grid.
/// </summary>
public sealed class NumberBasePage : FormToolPage
{
    private readonly TextBox _decimalBox = new() { Text = "0" };
    private readonly TextBox _hexBox = new() { Text = "0" };
    private readonly TextBox _binaryBox = new() { Text = "0" };
    private readonly TextBox _octalBox = new() { Text = "0" };
    private readonly ComboBox _width = new();
    private readonly CheckBox _signed = new() { Content = Loc.Get("NumberBase.Signed") };
    private readonly StackPanel _bitGrid = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 0, 16) };

    private static readonly int[] Widths = [8, 16, 32, 64];
    private long _value;
    private bool _updating;

    public NumberBasePage()
    {
        foreach (var w in Widths)
            _width.Items.Add(w + "-bit");
        _width.SelectedIndex = 2; // 32-bit
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_width, Loc.Get("NumberBase.Width"));

        AddRow(SectionHeader(Loc.Get("NumberBase.Title")));
        AddRow(LabelledRow(Loc.Get("NumberBase.Decimal"), _decimalBox));
        AddRow(LabelledRow(Loc.Get("NumberBase.Hex"), _hexBox));
        AddRow(LabelledRow(Loc.Get("NumberBase.Binary"), _binaryBox));
        AddRow(LabelledRow(Loc.Get("NumberBase.Octal"), _octalBox));

        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        options.Children.Add(_width);
        options.Children.Add(_signed);
        AddRow(options);

        AddRow(SectionHeader(Loc.Get("NumberBase.BitGrid")));
        AddRow(new ScrollViewer { Content = _bitGrid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });

        AddRow(SectionHeader(Loc.Get("NumberBase.Bitwise")));
        var opB = new TextBox { PlaceholderText = Loc.Get("NumberBase.OperandPlaceholder"), Width = 140 };
        var andBtn = new Button { Content = "AND" };
        var orBtn = new Button { Content = "OR" };
        var xorBtn = new Button { Content = "XOR" };
        var notBtn = new Button { Content = "NOT" };
        var shlBtn = new Button { Content = "<< 1" };
        var shrBtn = new Button { Content = ">> 1" };
        andBtn.Click += (_, _) => Apply(v => NumberBaseTools.And((ulong)v, ParseOperand(opB.Text)));
        orBtn.Click += (_, _) => Apply(v => NumberBaseTools.Or((ulong)v, ParseOperand(opB.Text)));
        xorBtn.Click += (_, _) => Apply(v => NumberBaseTools.Xor((ulong)v, ParseOperand(opB.Text)));
        notBtn.Click += (_, _) => Apply(v => NumberBaseTools.Not((ulong)v, CurrentWidth()));
        shlBtn.Click += (_, _) => Apply(v => NumberBaseTools.ShiftLeft((ulong)v, 1, CurrentWidth()));
        shrBtn.Click += (_, _) => Apply(v => NumberBaseTools.ShiftRight((ulong)v, 1));
        var bitwiseRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var b in new FrameworkElement[] { opB, andBtn, orBtn, xorBtn, notBtn, shlBtn, shrBtn })
            bitwiseRow.Children.Add(b);
        AddRow(bitwiseRow);

        _decimalBox.TextChanged += (_, _) => OnEdited(_decimalBox, 10);
        _hexBox.TextChanged += (_, _) => OnEdited(_hexBox, 16);
        _binaryBox.TextChanged += (_, _) => OnEdited(_binaryBox, 2);
        _octalBox.TextChanged += (_, _) => OnEdited(_octalBox, 8);
        _width.SelectionChanged += (_, _) => Refresh();
        _signed.Checked += (_, _) => Refresh();
        _signed.Unchecked += (_, _) => Refresh();

        Refresh();
    }

    public override ToolKind Kind => ToolKind.NumberBase;
    public override bool IsDirty => false;
    public override IReadOnlyList<ToolCommand> Commands => [];

    private int CurrentWidth() => Widths[_width.SelectedIndex];

    private static ulong ParseOperand(string text) => NumberBaseTools.TryParse(text, 10, out var v) ? (ulong)v : 0;

    private void Apply(Func<long, ulong> op)
    {
        _value = unchecked((long)op(_value));
        Refresh();
    }

    private void OnEdited(TextBox box, int fromBase)
    {
        if (_updating)
            return;
        if (!NumberBaseTools.TryParse(box.Text, fromBase, out var value))
            return;
        _value = value;
        Refresh();
    }

    private void Refresh()
    {
        _updating = true;
        int width = CurrentWidth();
        bool signed = _signed.IsChecked == true;
        ulong masked = NumberBaseTools.Mask(_value, width);

        _decimalBox.Text = signed ? NumberBaseTools.SignExtend(masked, width).ToString() : masked.ToString();
        _hexBox.Text = masked.ToString("X");
        _binaryBox.Text = System.Convert.ToString((long)masked, 2);
        _octalBox.Text = System.Convert.ToString((long)masked, 8);
        _updating = false;

        RenderBitGrid(masked, width);
    }

    private void RenderBitGrid(ulong bits, int width)
    {
        _bitGrid.Children.Clear();
        for (int i = width - 1; i >= 0; i--)
        {
            int bitIndex = i;
            bool set = ((bits >> bitIndex) & 1) != 0;
            var toggle = new ToggleButton
            {
                Content = set ? "1" : "0",
                IsChecked = set,
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
            };
            toggle.Click += (_, _) =>
            {
                _value = unchecked((long)(NumberBaseTools.Mask(_value, width) ^ (1UL << bitIndex)));
                Refresh();
            };
            _bitGrid.Children.Add(toggle);
            if (bitIndex % 8 == 0 && bitIndex != 0)
                _bitGrid.Children.Add(new Border { Width = 8 });
        }
    }
}
