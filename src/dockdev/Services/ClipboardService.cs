using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace dockdev.Services;

/// <summary>
/// Safe clipboard access service. Handles Win32 <c>CLIPBRD_E_CANT_OPEN</c> (0x800401D0) errors
/// that occur when another process (RDP, clipboard managers, Excel, antivirus) holds the clipboard lock.
/// Retries with exponential backoff and never throws an unhandled exception to the UI thread.
/// </summary>
public static class ClipboardService
{
    /// <summary>
    /// Attempts to set plain text to the system clipboard with a retry loop.
    /// </summary>
    /// <param name="text">The string to copy.</param>
    /// <param name="retries">Number of retry attempts if locked.</param>
    /// <param name="delayMs">Initial delay between attempts in milliseconds.</param>
    /// <returns>True if copied successfully, false otherwise.</returns>
    public static bool TrySetText(string text, int retries = 3, int delayMs = 30)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        for (int attempt = 0; attempt < retries; attempt++)
        {
            try
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
                return true;
            }
            catch (COMException ex)
            {
                Diag.Log($"ClipboardService: attempt {attempt + 1}/{retries} failed with COMException (0x{ex.HResult:X8}): {ex.Message}");
                if (attempt < retries - 1)
                    Thread.Sleep(delayMs * (attempt + 1));
            }
            catch (Exception ex)
            {
                Diag.Log($"ClipboardService: attempt {attempt + 1}/{retries} failed with {ex.GetType().Name}: {ex.Message}");
                if (attempt < retries - 1)
                    Thread.Sleep(delayMs * (attempt + 1));
            }
        }
        return false;
    }
}

