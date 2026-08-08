namespace dockdev.Services;

/// <summary>
/// The <c>WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd)</c> dance, extracted once
/// (design doc §26) instead of repeated in every editor-shaped tool's Open/Save command — required
/// because an unpackaged app must tell WinRT which window owns the picker.
/// </summary>
public static class FilePickers
{
    public static async Task<string?> PickOpenFileAsync(nint hwnd, IEnumerable<string>? extensions = null)
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

    public static async Task<string?> PickSaveFileAsync(nint hwnd, string suggestedName, string extension, string displayName)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
        picker.SuggestedFileName = suggestedName;
        picker.FileTypeChoices.Add(displayName, [extension]);

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}
