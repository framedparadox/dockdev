using dockdev.Models;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace dockdev.Services;

/// <summary>
/// Resolves a dock item's visual icon (design doc §26):
/// every tool's default look is a bundled Segoe Fluent glyph resolved instantly and synchronously
/// via <see cref="ToolCatalog"/> (see <c>ToolDockItem.Glyph</c>) — there is no shell-icon
/// extraction and no favicon fetch, so the only asynchronous work left is decoding a user-supplied
/// custom icon image file picked from the icon picker.
/// </summary>
public static class ToolIconProvider
{
    public static async Task<ImageSource?> LoadCustomIconAsync(ToolDockItem item)
    {
        if (string.IsNullOrWhiteSpace(item.CustomIconPath) || !File.Exists(item.CustomIconPath))
            return null;
        try
        {
            // Bounded before the decoder sees it (§21): this is the one place dockdev hands a
            // user-supplied file to an image decoder, and the result is drawn at 44 px. A file too
            // big to be an icon is refused rather than decoded to find out.
            var read = await InputLimits.ReadBytesAsync(item.CustomIconPath, InputLimits.MaxIconBytes);
            if (!read.Ok)
            {
                Diag.Log($"ToolIconProvider: refused '{item.CustomIconPath}': {read.Error}");
                return null;
            }
            return await DecodeAsync(read.Bytes);
        }
        catch (Exception ex)
        {
            Diag.Log($"ToolIconProvider: failed to decode '{item.CustomIconPath}': {ex.Message}");
            return null;
        }
    }

    private static async Task<ImageSource?> DecodeAsync(byte[] bytes)
    {
        var bmp = new BitmapImage();
        var tcs = new TaskCompletionSource<bool>();
        void OnOpened(object s, Microsoft.UI.Xaml.RoutedEventArgs e) => tcs.TrySetResult(true);
        void OnFailed(object s, Microsoft.UI.Xaml.ExceptionRoutedEventArgs e) => tcs.TrySetResult(false);
        bmp.ImageOpened += OnOpened;
        bmp.ImageFailed += OnFailed;

        try
        {
            using (var ras = new InMemoryRandomAccessStream())
            {
                using (var writer = new DataWriter(ras))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                ras.Seek(0);
                await bmp.SetSourceAsync(ras);
            }

            // SetSourceAsync completes only once the decode has finished, so whichever of the two
            // events was going to fire has fired by now. Resolving the source here is what keeps
            // the await below bounded: a decoder that reported neither outcome left this task
            // pending for the life of the process, holding the bitmap, the byte[] and both
            // handlers — and the caller's await with them. TrySetResult cannot overwrite a failure
            // OnFailed has already recorded.
            tcs.TrySetResult(true);
            return await tcs.Task ? bmp : null;
        }
        catch (Exception ex)
        {
            Diag.Log("ToolIconProvider: could not decode a custom icon: " + ex.Message);
            return null;
        }
        finally
        {
            // Detached on every path. A BitmapImage that failed to decode is dropped here, and a
            // handler left on it would keep this method's closure alive with it.
            bmp.ImageOpened -= OnOpened;
            bmp.ImageFailed -= OnFailed;
        }
    }
}
