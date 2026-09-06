using dockdev.Interop;
using dockdev.Models;
using dockdev.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace dockdev;

/// <summary>
/// Quick-launch search: a small acrylic card, summoned by its own shortcut, that filters the
/// whole nineteen-tool catalog and opens the one you pick. The dock stays where it is — this is
/// the way to reach a tool without going and finding the strip, and (at this catalog size) the
/// primary way to reach any of the nine tools that aren't pinned to a fresh dock (design doc §18).
/// </summary>
public sealed partial class SearchWindow : Window
{
    private const int MaxResults = ToolCatalogSearch.DefaultLimit;

    private const int CardWidth = 460;
    private const int CardHeight = 380;

    private readonly dockdevManager _manager;
    private readonly nint _hwnd;
    private readonly AppWindow _appWindow;
    private readonly AcrylicBackdropManager? _backdrop;
    private readonly List<ToolDefinition> _matches = new();
    private bool _wasActivated;

    public SearchWindow(dockdevManager manager)
    {
        _manager = manager;
        InitializeComponent();

        Title = Loc.Get("Search.Title");
        ExtendsContentIntoTitleBar = true;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        // Same chrome as a dock: borderless, rounded, off the taskbar and Alt-Tab. This is a
        // summoned overlay, not a window the user manages.
        WindowChrome.MakeBorderlessToolWindow(_appWindow, _hwnd);
        WindowChrome.StripFrame(_hwnd);
        WindowChrome.SetRoundedCorners(_hwnd, small: false);

        RootGrid.RequestedTheme = DockWindow.ResolveTheme(manager.Config.Theme);
        _backdrop = new AcrylicBackdropManager(this);
        if (_backdrop.TryApply())
            _backdrop.Personalize(manager.Config.GlassOpacity, manager.Config.AccentTint);
<<<<<<< HEAD
        else if (Application.Current.Resources.TryGetValue("SolidBackgroundFillColorBaseBrush", out var bg)
                 && bg is Microsoft.UI.Xaml.Media.Brush brush)
            // No acrylic (unsupported GPU / Remote Desktop): a transparent card would be unreadable,
            // so fall back to an opaque theme brush.
=======
        else if (Application.Current.Resources.TryGetValue("SolidBackgroundFillColorBaseBrush", out var bg) && bg is Brush brush)
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
            RootGrid.Background = brush;

        WindowChrome.SetClientSizeDip(_appWindow, _hwnd, CardWidth, CardHeight);
        CenterOnCursorDisplay();

        // Summoned by a keystroke and dismissed by losing focus: attention went elsewhere, and an
        // overlay that lingers after that is in the way. Guarded on having been activated first —
        // a window gets a Deactivated on its way to the foreground, and acting on that one would
        // close the card before it ever appeared.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
                _wasActivated = true;
            else if (_wasActivated)
<<<<<<< HEAD
                // Defer: closing the window synchronously from inside its own Activated callback can
                // re-enter the window machinery and fault; let the current message drain first.
=======
                Close();
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
                DispatcherQueue.TryEnqueue(Close);
        };
        Closed += (_, _) => _backdrop?.Dispose();

        Refresh(string.Empty);
    }

    /// <summary>Puts the card on the display the cursor is on, a third of the way down — where
    /// the shell's own search lands, rather than dead centre over whatever is being worked on.</summary>
    private void CenterOnCursorDisplay()
    {
        try
        {
            var size = _appWindow.Size;
            var display = NativeMethods.GetCursorPos(out var cursor)
                ? DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary)
                : DisplayArea.Primary;
            var work = display.WorkArea;
            _appWindow.Move(new PointInt32(
                work.X + (work.Width - size.Width) / 2,
                work.Y + Math.Max(0, (work.Height - size.Height) / 3)));
        }
        catch (Exception ex)
        {
            Diag.Log("Search: could not place the card: " + ex.Message);
        }
    }

    /// <summary>Takes focus and puts the caret in the box — the point of the shortcut is that you
    /// can start typing straight away.</summary>
    public void FocusQuery()
    {
        NativeMethods.SetForegroundWindow(_hwnd);
        Activate();
        QueryBox.Focus(FocusState.Programmatic);
        QueryBox.SelectAll();
    }

    // ---- Filtering ---------------------------------------------------------

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh(QueryBox.Text);

    /// <summary>Rebuilds the match list. The matching itself lives in <see cref="ToolCatalogSearch"/>.</summary>
    private void Refresh(string query)
    {
        _matches.Clear();
        _matches.AddRange(ToolCatalogSearch.Filter(query, MaxResults));

        Results.Items.Clear();
        foreach (var tool in _matches)
            Results.Items.Add(BuildRow(tool));

        bool any = _matches.Count > 0;
        Results.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        NoResults.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (any)
            Results.SelectedIndex = 0;
    }

    /// <summary>One result row: the tool's glyph, its name, and a one-line description.</summary>
    private Grid BuildRow(ToolDefinition tool)
    {
        var grid = new Grid { ColumnSpacing = 12, Height = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        bool isTextGlyph = GlyphFonts.IsTextGlyph(tool.Glyph);
        var icon = new FontIcon
        {
            Glyph = tool.Glyph,
            FontFamily = isTextGlyph
                ? new FontFamily("Segoe UI")
                : (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
            FontSize = isTextGlyph ? 18 * 0.8 : 18,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = tool.DisplayName,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = tool.Description,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, tool.DisplayName);
        return grid;
    }

    // ---- Keyboard ----------------------------------------------------------

    // PreviewKeyDown so Up/Down reach this before the TextBox treats them as caret movement:
    // typing happens in the box but the arrows drive the list, which is what makes the card
    // usable without ever leaving the keyboard.
    private void QueryBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                Close();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Down:
                Move(+1);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Up:
                Move(-1);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Enter:
                LaunchSelected();
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (_matches.Count == 0)
            return;
        int next = Results.SelectedIndex + delta;
        // Wrap, so Up from the first result reaches the last one without a long press of Down.
        Results.SelectedIndex = (next % _matches.Count + _matches.Count) % _matches.Count;
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private void Results_ItemClick(object sender, ItemClickEventArgs e)
    {
        int index = Results.Items.IndexOf(e.ClickedItem);
        if (index >= 0)
            Launch(index);
    }

    private void LaunchSelected() => Launch(Results.SelectedIndex);

    private void Launch(int index)
    {
        if (index < 0 || index >= _matches.Count)
            return;
        var tool = _matches[index];
        bool forceNewInstance = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
        // Close first: opening a tool brings another window forward, and a card still on screen
        // behind it would have to be dismissed by hand.
        Close();
        _manager.Launcher.Open(tool, forceNewInstance: forceNewInstance);
    }
}
