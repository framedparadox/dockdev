using DevDX.Controls;
using DevDX.Interop;
using DevDX.Models;
using DevDX.Services;
using DevDX.ToolPages;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevDX.ToolWindows;

/// <summary>
/// Hosts exactly one <see cref="ToolPage"/> (design doc §9.1). Owns Mica, the custom title bar,
/// rounded corners, theme propagation, the dirty-check on close, and registry membership with
/// <see cref="ToolWindowManager"/>. Unlike the borderless dock, this is a normal resizable,
/// minimizable, maximizable window with a taskbar entry — a scratch JSON window is still something
/// a user genuinely wants to Alt-Tab to (§13.4).
/// </summary>
public sealed class ToolWindowBase : Window
{
    private readonly nint _hwnd;
    private readonly AppWindow _appWindow;
    private readonly DevDxManager _manager;
    private readonly Grid _root = new();
    private bool _forceClose;

    /// <summary>True while the discard-confirmation dialog is up. A <c>ContentDialog</c> is a XAML
    /// overlay, not a modal window: it covers the page but does nothing to Alt+F4, the system menu
    /// or a taskbar close, so a second close request reaches <see cref="OnClosing"/> while the
    /// first one's dialog is still showing. Asking WinUI to show a second dialog in the same
    /// XamlRoot throws, and that throw is on an <c>async void</c> path with no caller to catch
    /// it — pressing Alt+F4 twice on an unsaved tool window took the app down.</summary>
    private bool _askingToDiscard;

    public ToolPage Page { get; }
    public ToolKind Kind { get; }

    public ToolWindowBase(DevDxManager manager, ToolPage page, ToolDefinition definition)
    {
        _manager = manager;
        Page = page;
        Kind = definition.Kind;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        page.HostHwnd = _hwnd;

        Title = definition.DisplayName;
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        // After ExtendsContentIntoTitleBar, never before: the option applies to the
        // framework-managed title bar, which does not exist until that is set.
        WindowChrome.UseTallTitleBar(_appWindow);

        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = new AppTitleBar(definition.Glyph, definition.DisplayName);

        Grid.SetRow(titleBar, 0);
        Grid.SetRow(page, 1);
        _root.Children.Add(titleBar);
        _root.Children.Add(page);
        Content = _root;
        SetTitleBar(titleBar);

        ApplyTheme();

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }
        _appWindow.IsShownInSwitchers = true;

        WindowChrome.SetRoundedCorners(_hwnd);
        WindowChrome.SetClientSizeDip(_appWindow, _hwnd, 900, 640);
        WindowChrome.CenterOnCursor(_appWindow, windowId);

        Activated += (_, _) => ApplyChromeTheme();
        _root.ActualThemeChanged += (_, _) => ApplyChromeTheme();
        ApplyChromeTheme();

        _appWindow.Closing += OnClosing;
        Closed += (_, _) =>
        {
            Page.NotifyClosing();
            _manager.ToolWindows.Unregister(this);
        };

        manager.ToolWindows.Register(this);
    }

    /// <summary>Applies the app-wide Light/Dark/System/High-Contrast choice — called at
    /// construction and again by <see cref="DevDxManager.SetTheme"/> on every open tool window.</summary>
    public void ApplyTheme() => _root.RequestedTheme = DockWindow.ResolveTheme(_manager.Config.Theme);

    private void ApplyChromeTheme()
    {
        bool dark = _root.ActualTheme != ElementTheme.Light;
        WindowChrome.SetTitleBarTheme(_appWindow, dark);
        WindowChrome.HideWindowBorder(_hwnd, dark);
    }

    public void BringToFront()
    {
        try
        {
            if (_appWindow.IsVisible)
                _appWindow.Show(activateWindow: false);
            NativeMethods.SetForegroundWindow(_hwnd);
            Activate();
        }
        catch (Exception ex)
        {
            Diag.Log("ToolWindowBase.BringToFront failed: " + ex);
        }
    }

    /// <summary>
    /// The dirty-check on close (design doc §9.1 / §17): a scratch window with unsaved content
    /// asks before discarding it, driven entirely by <see cref="ToolPage.IsDirty"/>.
    /// </summary>
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose || !Page.IsDirty)
            return;

        args.Cancel = true;

        // A second close request while the first is still being answered: keep the window (the
        // cancel above already did that) and let the dialog already on screen decide.
        if (_askingToDiscard)
            return;

        _askingToDiscard = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = _root.XamlRoot,
                Title = Loc.Get("Tool.DiscardTitle"),
                Content = new TextBlock { Text = Loc.Get("Tool.DiscardBody"), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = Loc.Get("Tool.Discard"),
                CloseButtonText = Loc.Get("Common.Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                _forceClose = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            // Nothing here is worth losing the window over, and this path has no caller: an
            // exception escaping an async void handler is an unhandled exception. Fail closed —
            // the window stays open with the user's content in it.
            Diag.Log("ToolWindowBase.OnClosing: " + ex);
        }
        finally
        {
            _askingToDiscard = false;
        }
    }
}
