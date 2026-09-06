namespace dockdev.Services;

/// <summary>
/// The <c>WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd)</c> dance, extracted once
/// (design doc §26) instead of repeated in every editor-shaped tool's Open/Save command — required
/// because an unpackaged app must tell WinRT which window owns the picker.
/// <para>
/// <b>Neither method throws</b>, the same contract <see cref="InputLimits"/> keeps: a cancelled
/// pick and a failed one both mean "no file", and both are answered with null. The failures are
/// real — <c>InitializeWithWindow</c> refuses an owner handle that has gone, and the shell refuses
/// a second picker while one is already up (double-clicking "Choose file…" is enough) — and every
/// caller is an <c>async void</c> event handler, where a throw is an unhandled exception. Two of
/// the four call sites wrapped this themselves and two did not, which is the drift the guard
/// belongs here to prevent.
/// </para>
/// </summary>
public static class FilePickers
{
    public static async Task<string?> PickOpenFileAsync(nint hwnd, IEnumerable<string>? extensions = null)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            bool any = false;
            foreach (var ext in extensions ?? [])
            {
                picker.FileTypeFilter.Add(ext);
                any = true;
            }
            if (!any)
                picker.FileTypeFilter.Add("*");

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            Diag.Log($"FilePickers.PickOpenFileAsync failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public static async Task<string?> PickSaveFileAsync(nint hwnd, string suggestedName, string extension, string displayName)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            picker.SuggestedFileName = suggestedName;
            picker.FileTypeChoices.Add(displayName, [extension]);

            var file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            Diag.Log($"FilePickers.PickSaveFileAsync failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
