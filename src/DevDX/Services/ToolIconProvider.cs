using DevDX.Models;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace DevDX.Services;

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
            return await DecodeAsync(await File.ReadAllBytesAsync(item.CustomIconPath));
        }
        catch (Exception ex)
        {
            Diag.Log($"ToolIconProvider: failed to decode '{item.CustomIconPath}': {ex.Message}");
            return null;
        }
    }

    private static async Task<ImageSource?> DecodeAsync(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            var tcs = new TaskCompletionSource<bool>();
            void OnOpened(object s, Microsoft.UI.Xaml.RoutedEventArgs e) => tcs.TrySetResult(true);
            void OnFailed(object s, Microsoft.UI.Xaml.ExceptionRoutedEventArgs e) => tcs.TrySetResult(false);
            bmp.ImageOpened += OnOpened;
            bmp.ImageFailed += OnFailed;

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

            bool ok = await tcs.Task;
            bmp.ImageOpened -= OnOpened;
            bmp.ImageFailed -= OnFailed;
            return ok ? bmp : null;
        }
        catch
        {
            return null;
        }
    }
}
