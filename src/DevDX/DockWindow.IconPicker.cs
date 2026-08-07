using DevDX.Models;
using DevDX.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevDX;

/// <summary>What the icon picker returned: a built-in glyph, or a path to a custom image file
/// picked via "Browse for an image…". Never both.</summary>
internal readonly record struct IconSelection(string? Glyph, string? FilePath);

/// <summary>
/// The icon picker: a flyout offering every glyph in <see cref="IconChoices"/> as a swatch grid,
/// plus a "Browse for an image…" fallback to the file picker. Shared by every "Change icon…"
/// entry point — an item's own context menu — so picking an icon looks and behaves the same
/// everywhere in DevDX. Partial
/// of <see cref="DockWindow"/>.
/// </summary>
public sealed partial class DockWindow
{
    private const int IconPickerColumns = 6;

    /// <summary>Opens the picker anchored to <paramref name="anchor"/> and reports the choice
    /// through <paramref name="onSelected"/>; reports nothing if the user dismisses it.</summary>
    private void ShowIconPicker(FrameworkElement anchor, Action<IconSelection> onSelected)
    {
        var flyout = new Flyout();
        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(4), MaxWidth = 260 };
        panel.Children.Add(FlyoutHeader(Loc.Get("IconPicker.Title")));
        panel.Children.Add(BuildSwatchGrid(flyout, onSelected));

        var browse = new Button
        {
            Content = Loc.Get("IconPicker.Browse"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        browse.Click += async (_, _) =>
        {
            flyout.Hide();
            if (await PickIconFileAsync() is { } path)
                onSelected(new IconSelection(null, path));
        };
        panel.Children.Add(browse);

        flyout.Content = panel;
        flyout.ShowAt(anchor);
    }

    private Grid BuildSwatchGrid(Flyout flyout, Action<IconSelection> onSelected)
    {
        var choices = IconChoices.All;
        int columns = Math.Min(IconPickerColumns, choices.Count);
        int rows = (choices.Count + columns - 1) / columns;

        var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        for (int c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];
            var swatch = new Button
            {
                Style = (Style)RootGrid.Resources["DockGlassButtonStyle"],
                Width = 36,
                Height = 36,
                MinWidth = 0,
                MinHeight = 0,
                Content = new FontIcon
                {
                    Glyph = choice.Glyph,
                    FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                    FontSize = 16,
                },
            };
            ToolTipService.SetToolTip(swatch, choice.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, choice.Name);
            var captured = choice;
            swatch.Click += (_, _) =>
            {
                flyout.Hide();
                onSelected(new IconSelection(captured.Glyph, null));
            };
            Grid.SetColumn(swatch, i % columns);
            Grid.SetRow(swatch, i / columns);
            grid.Children.Add(swatch);
        }
        return grid;
    }

    /// <summary>Applies a picker result to an existing item: a glyph or a file path, never both,
    /// and each clears whichever the other kind of custom icon was set.</summary>
    private void ApplyIconSelection(ToolDockItem item, IconSelection selection)
    {
        if (selection.Glyph is not null)
            SetCustomGlyph(item, selection.Glyph);
        else if (selection.FilePath is not null)
            SetCustomIcon(item, selection.FilePath);
    }

    /// <summary>Opens the OS file picker for an icon image, returning the chosen path or null if
    /// cancelled or the picker itself failed.</summary>
    private async Task<string?> PickIconFileAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            // Formats the XAML imaging stack decodes. Deliberately no .exe/.dll: pulling an icon
            // out of a binary means choosing an index too, which is a picker of its own.
            foreach (var ext in new[] { ".png", ".ico", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" })
                picker.FileTypeFilter.Add(ext);

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            Diag.Log($"Change icon failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
