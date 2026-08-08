using System.Text;
using dockdev.Controls;
using dockdev.Models;
using dockdev.Services;
using dockdev.Services.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.Storage.Streams;

namespace dockdev.ToolPages;

/// <summary>
/// Base64 encode/decode (design doc §14.5). After a successful decode, magic-byte sniffing
/// renders an inline image preview — with the decoded size checked before handing bytes to the
/// image decoder. Invalid Base64 produces a clear inline message, never a silent truncation.
/// </summary>
public sealed class Base64Page : EditorToolPage
{
    private const int MaxPreviewBytes = 8 * 1024 * 1024;

    private readonly CodeEditor _input = new();
    private readonly CodeView _output = new();
    private readonly Image _preview = new() { Visibility = Visibility.Collapsed, MaxHeight = 300, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, Margin = new Thickness(12) };
    private readonly RadioButton _encode = new() { Content = "" };
    private readonly RadioButton _decode = new() { Content = "" };
    private readonly CheckBox _urlSafe = new() { Content = "" };
    private readonly CheckBox _mime76 = new() { Content = "" };
    private byte[]? _fileBytes;
    private bool _isDirty;

    public Base64Page()
    {
        _input.AccessibleName = Loc.Get("Common.Input");
        _output.AccessibleName = Loc.Get("Common.Output");
        _encode.GroupName = _decode.GroupName = "Base64Mode";
        _encode.Content = Loc.Get("Base64.Encode");
        _decode.Content = Loc.Get("Base64.Decode");
        _urlSafe.Content = Loc.Get("Base64.UrlSafe");
        _mime76.Content = Loc.Get("Base64.Mime76");
        _encode.IsChecked = true;
        _encode.Checked += (_, _) => Run();
        _decode.Checked += (_, _) => Run();
        _urlSafe.Checked += (_, _) => Run();
        _urlSafe.Unchecked += (_, _) => Run();
        _mime76.Checked += (_, _) => Run();
        _mime76.Unchecked += (_, _) => Run();

        var chooseFile = new Button { Content = Loc.Get("Base64.ChooseFile") };
        chooseFile.Click += async (_, _) => await ChooseFileAsync();

        var options = OptionsBar();
        options.Children.Add(_encode);
        options.Children.Add(_decode);
        options.Children.Add(_urlSafe);
        options.Children.Add(_mime76);
        options.Children.Add(chooseFile);
        SetOptions(options);

        _input.TextChanged += (_, _) =>
        {
            _fileBytes = null;
            _isDirty = _input.Text.Length > 0;
            StatusBar.SetCounts(_input.Text);
            Run();
        };

        var outputPane = new Grid();
        outputPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outputPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_output, 0);
        Grid.SetRow(_preview, 1);
        outputPane.Children.Add(_output);
        outputPane.Children.Add(_preview);

        var inputSurface = Pane(_input);
        var outputSurface = Pane(outputPane, secondary: true);

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(inputSurface, 0);
        Grid.SetColumn(outputSurface, 1);
        split.Children.Add(inputSurface);
        split.Children.Add(outputSurface);

        SetBody(split);
        StatusBar.SetUntouched();
        InitializeChrome();
    }

    public override ToolKind Kind => ToolKind.Base64;
    public override bool IsDirty => _isDirty;

    public override IReadOnlyList<ToolCommand> Commands =>
    [
        ToolCommand.Run(Run),
        ToolCommand.Copy(CopyOutput),
        ToolCommand.Clear(Clear),
        ToolCommand.Swap(SwapInOut),
    ];

    public override bool AcceptsClipboardText(string text) => text.Length > 0;

    public override void PasteClipboardText(string text)
    {
        _decode.IsChecked = true;
        _input.Text = text;
    }

    private async Task ChooseFileAsync()
    {
        var path = await FilePickers.PickOpenFileAsync(HostHwnd);
        if (path is null)
            return;

        // Bounded read (§21/§22). Base64 is the worst case for an unbounded one: the bytes are held
        // whole and then turned into a string a third larger again.
        var read = await InputLimits.ReadBytesAsync(path);
        if (!read.Ok)
        {
            StatusBar.SetMessage(read.Error, isError: true);
            return;
        }

        _fileBytes = read.Bytes;
        _encode.IsChecked = true;
        _input.Text = $"[{Path.GetFileName(path)} — {_fileBytes.Length:N0} bytes]";
        _isDirty = true;
        Run();
    }

    private void Run()
    {
        _preview.Visibility = Visibility.Collapsed;
        _preview.Source = null;

        if (_encode.IsChecked == true)
        {
            var bytes = _fileBytes ?? Encoding.UTF8.GetBytes(_input.Text);
            if (bytes.Length == 0)
            {
                _output.Clear();
                StatusBar.SetUntouched();
                return;
            }
            var encoded = Base64Tools.Encode(bytes, _urlSafe.IsChecked == true, _mime76.IsChecked == true);
            _output.SetContent(encoded);
            StatusBar.SetCounts(encoded);
            StatusBar.SetValid();
            return;
        }

        var text = _input.Text;
        if (text.Length == 0)
        {
            _output.Clear();
            StatusBar.SetUntouched();
            return;
        }

        if (!Base64Tools.TryDecode(text, strict: false, out var decoded, out var error))
        {
            _output.SetContent(error);
            StatusBar.SetError(1, 1);
            return;
        }

        var kind = Base64Tools.SniffImage(decoded);
        if (kind != ImageKind.None && decoded.Length <= MaxPreviewBytes)
        {
            _ = ShowPreviewAsync(decoded);
            _output.SetContent(Loc.Format("Base64.DecodedBytes", decoded.Length));
        }
        else
        {
            _output.SetContent(TryUtf8(decoded));
        }
        StatusBar.SetCounts(_output.Text);
        StatusBar.SetValid();
    }

    private async Task ShowPreviewAsync(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await bmp.SetSourceAsync(stream);
            _preview.Source = bmp;
            _preview.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Diag.Log("Base64Page preview failed: " + ex.Message);
        }
    }

    private static string TryUtf8(byte[] bytes)
    {
        try
        {
            var decoder = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return decoder.GetString(bytes);
        }
        catch
        {
            return Convert.ToHexString(bytes);
        }
    }

    /// <summary>
    /// Feeds the output back in as the input and flips the direction — the point of the command:
    /// what you just encoded, you now want decoded.
    /// <para>
    /// The mode is read <em>before</em> the input is replaced and applied by checking the opposite
    /// radio button, which un-checks its partner for us. Deriving the new state from the old one
    /// in place ("encode = decode is not checked") looked equivalent and was not: with encode on,
    /// decode is off, so that expression set encode back to on and the direction never changed in
    /// either direction.
    /// </para>
    /// </summary>
    private void SwapInOut()
    {
        if (_output.Text.Length == 0)
            return;

        bool wasEncoding = _encode.IsChecked == true;
        _input.Text = _output.Text;
        if (wasEncoding)
            _decode.IsChecked = true;
        else
            _encode.IsChecked = true;
        Run();
    }

    private void CopyOutput()
    {
        if (_output.Text.Length == 0)
            return;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(_output.Text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void Clear()
    {
        _input.Text = "";
        _fileBytes = null;
        _output.Clear();
        _preview.Visibility = Visibility.Collapsed;
        StatusBar.SetUntouched();
    }
}
