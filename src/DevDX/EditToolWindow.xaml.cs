using DevDX.Controls;
using DevDX.Models;
using DevDX.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevDX;

/// <summary>
/// The Edit-Tool window: what used to be two separate context-menu flyouts ("Rename…" and "Change
/// icon…") merged into a single small form, opened from an item's right-click menu ("Edit…").
/// Name and icon are staged locally and only committed — to the item and to disk — when Save is
/// pressed; closing the window any other way discards both.
/// </summary>
public sealed partial class EditToolWindow : Window
{
    private readonly DevDxManager _manager;
    private readonly DockWindow _dock;
    private readonly ToolDockItem _item;
    private readonly nint _hwnd;
    private readonly AppWindow _appWindow;

    private readonly TextBox _nameBox;
    private readonly FontIcon _previewGlyphIcon;
    private readonly Image _previewImage;

    // Staged icon change: null/null means "unchanged", otherwise exactly one of the two is set
    // (a chosen glyph, or a path to a browsed image), mirroring ToolDockItem's own
    // CustomGlyph/CustomIconPath being mutually exclusive.
    private bool _iconChanged;
    private string? _pendingGlyph;
    private string? _pendingIconPath;

    public EditToolWindow(DevDxManager manager, DockWindow dock, ToolDockItem item)
    {
        _manager = manager;
        _dock = dock;
        _item = item;
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        Title = Loc.Get("EditTool.Title");
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        WindowChrome.UseTallTitleBar(_appWindow);

        var titleBar = new AppTitleBar("", Loc.Get("EditTool.Title"), accentGlyph: true);

        // ---- Header: icon + title/description on the left, an icon-only Save on the right ----
        var heroIcon = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(10),
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
        };
        heroIcon.Child = new FontIcon { Glyph = "", FontSize = 20, Foreground = new SolidColorBrush(Colors.White) };
        var heroText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        heroText.Children.Add(new TextBlock { Text = Loc.Get("EditTool.Title"), Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });
        heroText.Children.Add(new TextBlock
        {
            Text = Loc.Get("EditTool.Subtitle"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        var hero = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        hero.Children.Add(heroIcon);
        hero.Children.Add(heroText);

        var save = new Button
        {
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            Width = 40,
            Height = 40,
            Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "", FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"], FontSize = 16 },
        };
        ToolTipService.SetToolTip(save, Loc.Get("Common.Save"));
        AutomationProperties.SetName(save, Loc.Get("Common.Save"));
        save.Click += (_, _) => Save();

        var header = new Grid { ColumnSpacing = 16, Padding = new Thickness(24, 20, 24, 16) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(hero, 0);
        Grid.SetColumn(save, 1);
        header.Children.Add(hero);
        header.Children.Add(save);

        // ---- Body: name field, icon field ----
        var body = new StackPanel { Spacing = 20, Padding = new Thickness(24, 0, 24, 24) };

        var nameLabel = new TextBlock { Text = Loc.Get("EditTool.Name"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        _nameBox = new TextBox
        {
            Text = item.DisplayName,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(_nameBox, Loc.Get("EditTool.Name"));
        var nameSection = new StackPanel { Spacing = 8 };
        nameSection.Children.Add(nameLabel);
        nameSection.Children.Add(_nameBox);
        body.Children.Add(nameSection);

        var iconLabel = new TextBlock { Text = Loc.Get("EditTool.Icon"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        var previewBorder = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
        };
        var previewContent = new Grid();
        _previewImage = new Image { Width = 24, Height = 24, Stretch = Stretch.Uniform, Visibility = item.IconImage is null ? Visibility.Collapsed : Visibility.Visible, Source = item.IconImage };
        _previewGlyphIcon = new FontIcon
        {
            Glyph = item.Glyph,
            FontFamily = item.GlyphFontFamily,
            FontSize = 18,
            Visibility = item.IconImage is null ? Visibility.Visible : Visibility.Collapsed,
        };
        previewContent.Children.Add(_previewGlyphIcon);
        previewContent.Children.Add(_previewImage);
        previewBorder.Child = previewContent;

        var editIconButton = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "", FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"], FontSize = 14 },
        };
        ToolTipService.SetToolTip(editIconButton, Loc.Get("Menu.ChangeIcon"));
        AutomationProperties.SetName(editIconButton, Loc.Get("Menu.ChangeIcon"));
        editIconButton.Click += (_, _) => ShowIconEditFlyout(editIconButton);

        var iconRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        iconRow.Children.Add(previewBorder);
        iconRow.Children.Add(editIconButton);

        var iconSection = new StackPanel { Spacing = 8 };
        iconSection.Children.Add(iconLabel);
        iconSection.Children.Add(iconRow);
        body.Children.Add(iconSection);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(header, 1);
        Grid.SetRow(body, 2);
        root.Children.Add(titleBar);
        root.Children.Add(header);
        root.Children.Add(body);
        RootGrid.Children.Add(root);
        SetTitleBar(titleBar);

        RootGrid.RequestedTheme = DockWindow.ResolveTheme(manager.Config.Theme);
        ApplyChromeTheme();
        RootGrid.ActualThemeChanged += (_, _) => ApplyChromeTheme();
        Activated += (_, _) => ApplyChromeTheme();

        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }
        _appWindow.IsShownInSwitchers = true;

        WindowChrome.SetClientSizeDip(_appWindow, _hwnd, 420, 320);
        WindowChrome.CenterOnCursor(_appWindow, windowId);

        _nameBox.Loaded += (_, _) =>
        {
            _nameBox.Focus(FocusState.Programmatic);
            _nameBox.SelectAll();
        };
    }

    private void ApplyChromeTheme()
    {
        bool dark = RootGrid.ActualTheme != ElementTheme.Light;
        WindowChrome.SetTitleBarTheme(_appWindow, dark);
        WindowChrome.HideWindowBorder(_hwnd, dark);
    }

    // ---- Icon picker: a full-window flyout, no scrolling — the swatch set is small enough that
    // the whole grid fits in one screen, so there is nothing to scroll to. ----

    private const int IconPickerColumns = 6;

    private void ShowIconEditFlyout(FrameworkElement anchor)
    {
        var flyout = new Flyout();
        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(4), MaxWidth = 260 };
        panel.Children.Add(FlyoutHeader(Loc.Get("IconPicker.Title")));
        panel.Children.Add(BuildSwatchGrid(flyout));

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
                ApplyPendingIconPath(path);
        };
        panel.Children.Add(browse);

        flyout.Content = panel;
        flyout.ShowAt(anchor);
    }

    private Grid BuildSwatchGrid(Flyout flyout)
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
            AutomationProperties.SetName(swatch, choice.Name);
            var captured = choice;
            swatch.Click += (_, _) =>
            {
                flyout.Hide();
                ApplyPendingGlyph(captured.Glyph);
            };
            Grid.SetColumn(swatch, i % columns);
            Grid.SetRow(swatch, i / columns);
            grid.Children.Add(swatch);
        }
        return grid;
    }

    /// <summary>A flyout section header using the Fluent "body strong" type-ramp style.</summary>
    private static TextBlock FlyoutHeader(string text)
    {
        var tb = new TextBlock { Text = text };
        if (Application.Current.Resources.TryGetValue("BodyStrongTextBlockStyle", out var s) &&
            s is Style style)
            tb.Style = style;
        else
            tb.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        return tb;
    }

    private void ApplyPendingGlyph(string glyph)
    {
        _iconChanged = true;
        _pendingGlyph = glyph;
        _pendingIconPath = null;
        _previewGlyphIcon.Glyph = glyph;
        _previewGlyphIcon.FontFamily = new FontFamily(GlyphFonts.IsTextGlyph(glyph) ? "Segoe UI" : "Segoe Fluent Icons");
        _previewGlyphIcon.Visibility = Visibility.Visible;
        _previewImage.Visibility = Visibility.Collapsed;
    }

    private void ApplyPendingIconPath(string path)
    {
        _iconChanged = true;
        _pendingIconPath = path;
        _pendingGlyph = null;
        _previewImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
        _previewImage.Visibility = Visibility.Visible;
        _previewGlyphIcon.Visibility = Visibility.Collapsed;
    }

    private async Task<string?> PickIconFileAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            foreach (var ext in new[] { ".png", ".ico", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" })
                picker.FileTypeFilter.Add(ext);

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            Diag.Log($"Edit tool: change icon failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ---- Commit ----

    private void Save()
    {
        var name = _nameBox.Text.Trim();
        if (name.Length > 0)
            _item.DisplayName = name; // observable -> tooltip updates

        if (_iconChanged)
        {
            if (_pendingGlyph is not null)
                _dock.SetCustomGlyph(_item, _pendingGlyph);
            else if (_pendingIconPath is not null)
                _dock.SetCustomIcon(_item, _pendingIconPath);
        }

        _manager.Save();
        _manager.NotifyItemsChanged();
        Close();
    }
}
