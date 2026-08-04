using System.Text;
using DevDX.Models;
using DevDX.Services;
using DevDX.Services.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace DevDX.ToolPages;

/// <summary>
/// Hash &amp; HMAC (design doc §14 Encode &amp; Decode / Form archetype): every algorithm computed
/// simultaneously over text or a chosen file, each with a copy button. HMAC mode takes a key. A
/// "compare to expected" box uses a constant-time comparison. MD5/SHA-1 are labelled checksum-only.
/// </summary>
public sealed class HashPage : FormToolPage
{
    private readonly TextBox _input = new() { AcceptsReturn = true, Height = 100, TextWrapping = TextWrapping.Wrap, PlaceholderText = Loc.Get("Hash.InputPlaceholder") };
    private readonly CheckBox _hmac = new() { Content = Loc.Get("Hash.UseHmac") };
    private readonly TextBox _key = new() { PlaceholderText = Loc.Get("Hash.KeyPlaceholder"), IsEnabled = false };
    private readonly TextBox _compare = new() { PlaceholderText = Loc.Get("Hash.ComparePlaceholder") };
    private readonly TextBlock _compareResult = new();
    private readonly Dictionary<string, TextBox> _results = new();
    private byte[]? _fileBytes;
    private string? _fileName;
    private bool _isDirty;

    public HashPage()
    {
        AddRow(SectionHeader(Loc.Get("Hash.Source")));
        AddRow(LabelledRow(Loc.Get("Hash.Text"), _input));

        var chooseFile = new Button { Content = Loc.Get("Base64.ChooseFile") };
        chooseFile.Click += async (_, _) => await ChooseFileAsync();
        var clearFile = new Button { Content = Loc.Get("Tool.Clear") };
        clearFile.Click += (_, _) => { _fileBytes = null; _fileName = null; Recompute(); };
        var fileRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fileRow.Children.Add(chooseFile);
        fileRow.Children.Add(clearFile);
        AddRow(fileRow);

        _hmac.Checked += (_, _) => { _key.IsEnabled = true; Recompute(); };
        _hmac.Unchecked += (_, _) => { _key.IsEnabled = false; Recompute(); };
        AddRow(_hmac);
        AddRow(LabelledRow(Loc.Get("Hash.Key"), _key));

        AddRow(SectionHeader(Loc.Get("Hash.Results")));
        foreach (var algo in HashTools.Algorithms)
        {
            var (row, value, _) = ResultRow(algo + (algo is "MD5" or "SHA-1" ? "  —  " + Loc.Get("Hash.ChecksumOnly") : ""));
            _results[algo] = value;
            AddRow(row);
        }

        AddRow(SectionHeader(Loc.Get("Hash.Compare")));
        AddRow(LabelledRow(Loc.Get("Hash.CompareToExpected"), _compare));
        AddRow(_compareResult);

        _input.TextChanged += (_, _) => { _isDirty = _input.Text.Length > 0; Recompute(); };
        _key.TextChanged += (_, _) => Recompute();
        _compare.TextChanged += (_, _) => UpdateCompare();
    }

    public override ToolKind Kind => ToolKind.Hash;
    public override bool IsDirty => _isDirty;

    public override IReadOnlyList<ToolCommand> Commands =>
    [
        ToolCommand.Clear(Clear),
    ];

    public override bool AcceptsClipboardText(string text) => text.Length > 0;
    public override void PasteClipboardText(string text) => _input.Text = text;

    private async Task ChooseFileAsync()
    {
        var path = await FilePickers.PickOpenFileAsync(HostHwnd);
        if (path is null)
            return;
        _fileBytes = await File.ReadAllBytesAsync(path);
        _fileName = Path.GetFileName(path);
        _isDirty = true;
        Recompute();
    }

    private byte[] CurrentData() => _fileBytes ?? Encoding.UTF8.GetBytes(_input.Text);

    private void Recompute()
    {
        var data = CurrentData();
        if (data.Length == 0)
        {
            foreach (var box in _results.Values)
                box.Text = "";
            UpdateCompare();
            return;
        }

        bool hmac = _hmac.IsChecked == true && _key.Text.Length > 0;
        var keyBytes = Encoding.UTF8.GetBytes(_key.Text);
        foreach (var algo in HashTools.Algorithms)
        {
            _results[algo].Text = hmac && algo != "CRC32"
                ? HashTools.ComputeHmac(algo, data, keyBytes)
                : HashTools.Compute(algo, data);
        }
        UpdateCompare();
    }

    private void UpdateCompare()
    {
        if (_compare.Text.Length == 0)
        {
            _compareResult.Text = "";
            return;
        }
        bool anyMatch = _results.Values.Any(v => v.Text.Length > 0 && HashTools.ConstantTimeEquals(v.Text, _compare.Text));
        _compareResult.Text = anyMatch ? Loc.Get("Hash.Match") : Loc.Get("Hash.NoMatch");
    }

    private void Clear()
    {
        _input.Text = "";
        _fileBytes = null;
        _fileName = null;
        _compare.Text = "";
        Recompute();
    }
}
