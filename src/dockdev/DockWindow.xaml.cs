using System.Collections.ObjectModel;
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
/// One dock strip: the always-on, borderless glass window that holds the tool icons plus the
/// settings gear. Owns window chrome and theming, the acrylic backdrop, the item layout
/// (horizontal or, when side-snapped, vertical), drag-to-move / drag-to-reorder gestures, the
/// per-item and background context menus, and its own placement. Snap + auto-hide behavior lives
/// in the <see cref="DockWindow"/> partial in <c>DockWindow.AutoHide.cs</c>.
/// <para>
/// There is exactly one of these. Everything app-wide — the config file, the tray icon, the
/// global shortcut, the open-tool-window registry, the Settings and Add windows — belongs to
/// <see cref="dockdevManager"/>; this class knows only about its own <see cref="DockProfile"/>.
/// </para>
/// </summary>
public sealed partial class DockWindow : Window
{
    private readonly nint _hwnd;
    private readonly WindowId _windowId;
    private readonly AppWindow _appWindow;
    private readonly AcrylicBackdropManager? _backdrop;
    private readonly dockdevManager _manager;
    private readonly DockProfile _profile;

    /// <summary>The visible items rendered on the dock (a projection of the master list that
    /// excludes hidden items). Reordering operates on this collection.</summary>
    public ObservableCollection<ToolDockItem> Items { get; } = new();

    /// <summary>The full, ordered item list (including hidden items) — the persisted source
    /// of truth, surfaced to the Settings window.</summary>
    public IReadOnlyList<ToolDockItem> AllItems => _profile.Items;

    /// <summary>This strip's persisted state: its items, edge, placement and hide behavior.</summary>
    public DockProfile Profile => _profile;

    /// <summary>App-wide settings, shared with every other window.</summary>
    public DockConfig Config => _manager.Config;

    public dockdevManager Manager => _manager;

    /// <summary>
    /// The dock window's title. It never appears in a caption (the dock is borderless) or in the
    /// taskbar, but it is the top-level window's UI Automation name — which is how the UI smoke
    /// tests find the dock, and how it shows up in Spy++ / Task Manager.
    /// </summary>
    internal const string WindowTitle = "dockdev";

    public DockWindow(dockdevManager manager, DockProfile profile, bool seedDefaults)
    {
        _manager = manager;
        _profile = profile;

        InitializeComponent();
        Title = WindowTitle;

        // Content fills the whole window (no reserved title bar). This also makes WinUI
        // size the content island's INPUT site to the full client area — without it, a
        // borderless window can end up with a 0x0 input site that silently swallows all
        // pointer input (no clicks / hover / drag reach the content).
        ExtendsContentIntoTitleBar = true;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(_windowId);

        // Dock-like chrome: borderless, topmost, off the taskbar & Alt-Tab, rounded corners.
        // The DWM border is suppressed (and the immersive-dark-mode flag tracked to the theme) in
        // ApplyWindowBorder, applied below once the theme is known and re-asserted on every
        // activation because DWM otherwise restores the default (contrasting) rim.
        WindowChrome.MakeBorderlessToolWindow(_appWindow, _hwnd);
        WindowChrome.StripFrame(_hwnd);
        WindowChrome.SetRoundedCorners(_hwnd, small: false);
        Activated += (_, _) => ApplyWindowBorder();

        // Seed the starter items only on the very first run — never after the user has
        // intentionally emptied the dock, and never for a dock they added themselves.
        if (seedDefaults)
            SeedDefaults();

        // Apply the chosen Light/Dark/System theme to the dock's root. A High Contrast theme
        // always wins (ApplyTheme resolves to ElementTheme.Default), in which case the acrylic
        // "glass" is also suppressed below so the shell's high-contrast system colors come
        // through and the dock stays legible.
        ApplyTheme();

        // Match the rounded DWM border to the effective theme now, and keep it in step when the
        // theme changes — either the user's choice, or the OS light/dark setting while in System
        // mode (ActualThemeChanged covers both).
        ApplyWindowBorder();
        RootGrid.ActualThemeChanged += (_, _) => ApplyWindowBorder();

        if (HighContrast.IsActive())
        {
            if (Application.Current.Resources.TryGetValue(
                    "SolidBackgroundFillColorBaseBrush", out var bg) && bg is Brush brush)
                RootGrid.Background = brush; // opaque, since there is no backdrop behind it
        }
        else
        {
            // The Windows 11 taskbar "glass". Follows RootGrid's theme via its own
            // ActualThemeChanged subscription, so a later SetTheme re-tints it automatically.
            _backdrop = new AcrylicBackdropManager(this);
            _backdrop.TryApply();
            ApplyGlass(); // the user's frostiness / accent-tint choice on top of the base recipe
        }

        ItemsHost.ItemsSource = Items;
        RebuildVisible();
        ApplyMetrics();
        ApplySettingsButtonPosition();
        // One subscription for the life of the window: it drives the cell highlight always, and
        // the magnify swell when that setting is on (see DockWindow.Magnify.cs).
        HookStripPointer();

        // ContextRequested (rather than RightTapped) so the dock menu is reachable by the
        // keyboard too (Menu key / Shift+F10), not only by right-click.
        DockStrip.ContextRequested += DockBackground_ContextRequested;
        // Items are Buttons now and mark PointerPressed handled for their own press visual;
        // subscribe with handledEventsToo so a press that starts on an icon still begins a
        // gesture (item reorder for icons, window drag for the background).
        DockStrip.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(Dock_PointerPressed), handledEventsToo: true);
        RootGrid.Loaded += (_, _) => QueueRelayout();

        // Alt+F4 reaches a borderless window like any other: StripFrame drops the caption, not the
        // system menu, so DefWindowProc still turns the accelerator into WM_CLOSE — and the dock
        // takes foreground whenever it is clicked or summoned, so it is a realistic thing to have
        // focus when someone means to close whatever is on top. Destroying it is never what that
        // means: there is exactly one dock and no way to ask for another. So an external close
        // request is refused and treated as the tray's "Hide dock", which both the tray icon and
        // the summon shortcut undo. dockdev's own teardown paths call AllowClose first.
        _appWindow.Closing += OnAppWindowClosing;

        Closed += (_, _) =>
        {
            _pollTimer?.Stop();
            _slideTimer?.Stop();
            _dragTimer?.Stop();
            _backdrop?.Dispose();
            ReleaseItems();
        };

        // Modest initial size so the first frame isn't full-screen before relayout.
        _appWindow.Resize(new SizeInt32(360, 96));

        _ = LoadIconsAsync();
    }

    // ---- Master / visible list sync ---------------------------------------

    /// <summary>Rebuilds the visible collection from the master list, honoring Hidden flags.</summary>
    private void RebuildVisible()
    {
        Items.Clear();
        foreach (var it in _profile.Items)
            if (!it.Hidden)
                Items.Add(it);
    }

    /// <summary>
    /// Pushes a visible reorder back into the master list. Hidden items stay anchored at their
    /// absolute master indices; each visible slot is refilled, in order, from the (reordered)
    /// visible collection. Deterministic and stable.
    /// </summary>
    private void SyncMasterFromVisible()
    {
        var q = new Queue<ToolDockItem>(Items);
        for (int i = 0; i < _profile.Items.Count && q.Count > 0; i++)
            if (!_profile.Items[i].Hidden)
                _profile.Items[i] = q.Dequeue();
    }

    // ---- Lifetime ----------------------------------------------------------

    /// <summary>True once dockdev itself has decided this window may go — quitting, or the
    /// close-and-recreate that a language change and a backup import both perform. Until then a
    /// close request is a hide, not a destroy.</summary>
    private bool _allowClose;

    /// <summary>Lets the next <c>Close</c> actually close this window. Called by
    /// <see cref="dockdevManager"/> on the paths that legitimately dispose of a dock.</summary>
    internal void AllowClose() => _allowClose = true;

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
            return;

        args.Cancel = true;
        // Queued rather than run from inside the notification: the cancel only takes effect once
        // this handler returns, and hiding a window part-way through its own close is the kind of
        // re-entrancy that is fine until the one build where it isn't.
        DispatcherQueue.TryEnqueue(_manager.HideDockOnCloseRequest);
    }

    /// <summary>
    /// Drops the strip's hold on the dock items as the window goes.
    /// <para>
    /// The items are <em>config</em>: they live as long as the process. The cells bound to them
    /// are this window's, and the item template's compiled bindings subscribe to each item's
    /// <c>PropertyChanged</c>. Clearing the source recycles every realized cell, and recycling is
    /// what detaches those subscriptions — so without this a dock replaced by a language change or
    /// a backup import stays reachable from the config for the rest of the session, with its whole
    /// visual tree behind it.
    /// </para>
    /// </summary>
    private void ReleaseItems()
    {
        try
        {
            ItemsHost.ItemsSource = null;
            Items.Clear();

            // Runtime-only visual state the strip set on process-lifetime objects. A replacement
            // dock reads these at construction, so an icon left swelled or highlighted under the
            // cursor when this window went would come back that way on the next one.
            foreach (var item in _profile.Items)
            {
                item.SetHovered(false);
                item.SetMagnification(1);
            }
        }
        catch (Exception ex)
        {
            // Closed runs after the content island has gone; the worst case here is the detach
            // this method exists for, and it must not throw out of a Closed handler.
            Diag.Log("DockWindow.ReleaseItems: " + ex.Message);
        }
    }

    // ---- Public API (used by the Settings / Add windows) ------------------

    public void AddDockItem(ToolDockItem item)
    {
        _profile.Items.Add(item);
        if (!item.Hidden)
            Items.Add(item);
        PersistAndRelayout();
        RaiseItemsChanged();
        _ = LoadOneIconAsync(item);
    }

    public void RemoveDockItem(ToolDockItem item)
    {
        _profile.Items.Remove(item);
        Items.Remove(item);
        PersistAndRelayout();
        RaiseItemsChanged();
    }

    public void SetItemHidden(ToolDockItem item, bool hidden)
    {
        if (item.Hidden == hidden)
            return;
        item.Hidden = hidden;
        RebuildVisible();
        PersistAndRelayout();
        RaiseItemsChanged();
    }

    /// <summary>
    /// Whether <paramref name="kind"/> is currently showing on the dock. "On the dock" means
    /// pinned <em>and</em> not hidden: switching a customized tool off in Settings ▸ Tools leaves a
    /// hidden item behind (see <see cref="SetToolActive"/>), and treating that as still-added is
    /// what let the Add-Tool gallery show a tile as checked while the icon was nowhere on the
    /// strip.
    /// </summary>
    public bool IsToolActive(ToolKind kind) =>
        _profile.Items.Any(i => i.Kind == kind && !i.Hidden);

    /// <summary>
    /// Puts a tool on the dock or takes it off — the one implementation behind the Settings ▸
    /// Tools switch and the Add-Tool gallery tile, so the two cannot drift apart. A tool that is
    /// merely hidden is un-hidden rather than pinned a second time, so switching one on never
    /// leaves two copies of the same icon on the strip.
    /// <para>
    /// Switching a tool <b>off</b> deletes its item only when there is nothing on it to lose. An
    /// item the user has given an icon, a new name or a system-wide shortcut is <em>hidden</em>
    /// instead (<see cref="ToolDockItem.HasUserCustomization"/>): all of that lives on the item,
    /// the dock has no undo, and deleting it threw the lot away on a switch flick — turn a tool
    /// off and back on and you had a factory-default item under a new id, with your shortcut
    /// silently unregistered. An untouched item carries nothing a fresh one would not, so removing
    /// it is invisible and keeps the config from accumulating entries for tools nobody uses.
    /// Deleting a customized item for good stays an explicit act: its own "Remove from dock".
    /// </para>
    /// </summary>
    public void SetToolActive(ToolDefinition tool, bool active)
    {
        var existing = _profile.Items.Where(i => i.Kind == tool.Kind).ToList();
        if (active)
        {
            if (existing.Count == 0)
            {
                AddDockItem(new ToolDockItem { Kind = tool.Kind, DisplayName = tool.DisplayName });
                return;
            }
            foreach (var hidden in existing.Where(i => i.Hidden))
                SetItemHidden(hidden, false);
            return;
        }

        foreach (var pinned in existing)
        {
            if (pinned.HasUserCustomization)
                SetItemHidden(pinned, true);
            else
                RemoveDockItem(pinned);
        }
    }

    // ---- Drag & drop onto the dock ---------------------------------------
    //
    // Dropped on empty dock space: route the file to the best-matching tool by extension (design
    // doc §12 point 3) and open it preloaded. There is no "add an arbitrary item" flow — the
    // catalog is closed and code-defined (§3 non-goals) — so a file with no match just isn't
    // opened, with a short inline explanation rather than silently doing the wrong thing.

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        var data = e.DataView;
        if (data.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            if (e.DragUIOverride is { } ui)
            {
                ui.Caption = Loc.Get("Dock.DropCaption");
                ui.IsCaptionVisible = true;
                ui.IsGlyphVisible = true;
            }
        }
        else
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var data = e.DataView;
            if (!data.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                return;

            foreach (var storageItem in await data.GetStorageItemsAsync())
            {
                var path = storageItem.Path;
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                OpenBestMatchOrExplain(path);
            }
        }
        catch (Exception ex)
        {
            Diag.Log("Drop failed: " + ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>Opens <paramref name="path"/> in the best-matching tool, or shows a short inline
    /// tooltip explaining that nothing in the catalog opens that extension (design doc §12).</summary>
    internal void OpenBestMatchOrExplain(string path)
    {
        var match = ToolCatalog.BestMatchFor(path);
        if (match is null)
        {
            ShowInlineTip(Loc.Get("Dock.NoToolForFile"), Path.GetFileName(path));
            return;
        }
        _manager.Launcher.Open(match, seedFilePath: path);
    }

    /// <summary>A short, self-dismissing explanation anchored to the dock — used when a drop
    /// can't be honored rather than silently doing the wrong thing (design doc §12).</summary>
    internal void ShowInlineTip(string title, string subtitle)
    {
        var tip = new TeachingTip
        {
            Title = title,
            Subtitle = subtitle,
            // Light dismiss is what makes it self-dismissing, which is what this is documented to
            // be: without it the tip waits for its close button, and one that is never pressed
            // stays in RootGrid.Children — a drop of an unopenable file adds another every time.
            IsLightDismissEnabled = true,
            IsOpen = true,
            XamlRoot = RootGrid.XamlRoot,
        };
        RootGrid.Children.Add(tip);
        tip.Closed += (_, _) => RootGrid.Children.Remove(tip);
    }

    /// <summary>Opens the Add window targeting <em>this</em> dock, whichever one it is.</summary>
    public void OpenAddNew() => _manager.OpenAddNew(this);

    public void OpenSettings() => _manager.OpenSettings();

    private void RaiseItemsChanged() => _manager.NotifyItemsChanged();

    private void PersistAndRelayout()
    {
        SaveConfig();
        QueueRelayout();
    }

    // ---- Seed content -----------------------------------------------------

    /// <summary>Seeds the six <see cref="ToolDefinition.VisibleByDefault"/> tools (design doc
    /// §18) — the rest of the nineteen-tool catalog is reachable from search, the Add-Tool
    /// gallery and Settings ▸ Tools from first launch, just not pinned to a fresh dock.</summary>
    private void SeedDefaults()
    {
        foreach (var tool in ToolCatalog.Seeded)
            _profile.Items.Add(new ToolDockItem { Kind = tool.Kind, DisplayName = tool.DisplayName });
    }

    /// <summary>Persists the whole configuration.</summary>
    private void SaveConfig() => _manager.Save();

    /// <summary>
    /// Resolves every item's <em>custom</em> icon. There is no shell-icon extraction or favicon
    /// fetch (design doc §26) — a tool's default look is its catalog glyph, rendered synchronously
    /// with no resolution step — so this only ever has work to do for items where the user picked
    /// a custom image via the icon picker.
    /// </summary>
    private Task LoadIconsAsync() =>
        Task.WhenAll(_profile.Items.ToArray().Select(LoadOneIconAsync));

    // ---- Size & position (bottom-center, above the taskbar) ---------------

    private bool _relayoutQueued;

    private void QueueRelayout()
    {
        if (_relayoutQueued)
            return;
        _relayoutQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _relayoutQueued = false;
            try
            {
                UpdateSizeAndPosition();
            }
            catch (Exception ex)
            {
                Diag.Log("UpdateSizeAndPosition failed: " + ex);
            }
        });
    }

    // Dock metrics — keep in sync with the item template, Strip and DockStrip in DockWindow.xaml.
    // Per-item cell sizes are NOT here: they vary by kind (a separator is a narrow slot), so they
    // live on ToolDockItem.CellExtent, which the sizing and reorder math below both read.
    //
    // The two that scale with the density setting are properties over DockMetrics rather than
    // constants, so a density change moves the gear cell and the divider with the items instead
    // of leaving the window sized for the old geometry.
    internal static double CellSize => DockMetrics.Cell;          // gear cell (taskbar-ish)
    internal static double DividerLength => DockMetrics.DividerLength; // Divider long side
    internal const double CellSpacing = 4;    // StackLayout + Strip Spacing
    internal const double DividerWidth = 1;   // Divider Rectangle thickness (short side)
    internal const double StripPadX = 8;      // DockStrip Padding (left/right)
    internal const double StripPadY = 6;      // DockStrip Padding (top/bottom)
    internal const double AddNewWidth = 116;  // "+ Add New" empty-state pill: minimum width

    /// <summary>
    /// Pushes the current <see cref="DockMetrics"/> geometry onto the parts of the strip that are
    /// plain XAML rather than item bindings — the gear cell and the empty-state pill — and asks
    /// every item to re-read its own. Called at construction and whenever the density changes.
    /// </summary>
    private void ApplyMetrics()
    {
        SettingsButton.Width = DockMetrics.Cell;
        SettingsButton.Height = DockMetrics.Cell;
        SettingsButton.CornerRadius = new CornerRadius(DockMetrics.CellCorner);
        SettingsGlyph.FontSize = DockMetrics.Glyph;
        AddNewButton.Height = DockMetrics.Cell;

        foreach (var item in _profile.Items)
            item.RefreshMetrics();
    }

    /// <summary>Re-applies the density (and re-sizes the window for it). Called by
    /// <see cref="dockdevManager.SetDensity"/>, since density is app-wide.</summary>
    public void ApplyDensity()
    {
        ApplyMetrics();
        QueueRelayout();
    }

    /// <summary>
    /// Moves the settings gear (and its divider) to the leading or trailing end of the strip per
    /// <see cref="DockProfile.SettingsButtonAtStart"/>. The pair always travels together — a
    /// divider with nothing on its far side would just be a stray line — and total strip length is
    /// unchanged either way, so this never needs a relayout of its own.
    /// </summary>
    private void ApplySettingsButtonPosition()
    {
        Strip.Children.Remove(Divider);
        Strip.Children.Remove(SettingsButton);
        if (_profile.SettingsButtonAtStart)
        {
            Strip.Children.Insert(0, Divider);
            Strip.Children.Insert(0, SettingsButton);
        }
        else
        {
            Strip.Children.Add(Divider);
            Strip.Children.Add(SettingsButton);
        }
    }

    /// <summary>Flips the gear's position and persists the choice. Used by the dock's own
    /// right-click menu and by Settings ▸ Dock.</summary>
    public void SetSettingsButtonAtStart(bool atStart)
    {
        if (_profile.SettingsButtonAtStart == atStart)
            return;
        _profile.SettingsButtonAtStart = atStart;
        ApplySettingsButtonPosition();
        SaveConfig();
    }

    // The pill's actual width. Measured rather than fixed at AddNewWidth because its caption is
    // translated, and "Hinzufügen" or "डॉक में जोड़ें" is wider than the English "Add New" that
    // constant was sized for — a fixed width would clip them.
    private double _addNewWidth = AddNewWidth;

    /// <summary>
    /// True when the dock should lay out vertically: the "vertical when side-snapped" option is
    /// on, the dock is snapped to the left or right edge, and it has at least one item (the
    /// empty-state "+ Add New" pill is always horizontal). Top/bottom and floating stay horizontal.
    /// </summary>
    private bool IsVertical =>
        _profile.VerticalWhenSideSnapped &&
        _profile.Snapped &&
        _profile.Edge is DockEdge.Left or DockEdge.Right &&
        Items.Count > 0;

    /// <summary>Flips the strip, the item layout and the divider between horizontal and vertical.</summary>
    private void ApplyOrientation(bool vertical)
    {
        Strip.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        if (ItemsHost.Layout is StackLayout stack)
            stack.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;

        // Cell sizes are per-item template bindings (a separator's slot is narrow along the flow
        // and full-width across it), so the orientation has to reach the items themselves.
        foreach (var item in Items)
            item.SetFlowVertical(vertical);

        // The divider is a thin line laid across the strip's flow, so its long/short sides swap
        // with the orientation (a vertical bar between horizontal items, a horizontal bar between
        // vertical items), and it's centered on the cross axis.
        Divider.Width = vertical ? DividerLength : DividerWidth;
        Divider.Height = vertical ? DividerWidth : DividerLength;
        Divider.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        Divider.VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Center;
    }

    /// <summary>Shows the "+ Add New" pill (and hides the item strip) when the dock is empty.</summary>
    private void UpdateEmptyState()
    {
        bool empty = Items.Count == 0;
        AddNewButton.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ItemsHost.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!empty)
            return;

        // Measure at natural size (a Collapsed element measures to zero, hence the early return
        // above), then pin the pill to that so the window sizing below has an exact number.
        AddNewButton.Width = double.NaN;
        AddNewButton.Measure(new Windows.Foundation.Size(
            double.PositiveInfinity, double.PositiveInfinity));
        _addNewWidth = Math.Max(AddNewWidth, Math.Ceiling(AddNewButton.DesiredSize.Width));
        AddNewButton.Width = _addNewWidth;
    }

    private void UpdateSizeAndPosition()
    {
        UpdateEmptyState();

        // Compute the strip size analytically from the (uniform) cell metrics. This is
        // deterministic and avoids the window-shrinks-then-clips-content feedback loop
        // that plagues "auto-size window to content" via ActualWidth.
        int n = Items.Count; // visible items
        bool empty = n == 0;
        bool vertical = IsVertical; // false when empty
        ApplyOrientation(vertical);

        // Along the strip's flow: [items OR add-new] [gap] [divider] [gap] [gear cell].
        // Across it: a single cell. Which of these is the window's width vs. height depends on
        // whether the dock is laid out vertically. Cells are summed rather than multiplied out:
        // they are not all the same size (a separator takes a narrow slot).
        double coreMain = empty ? _addNewWidth : ItemsExtent() + (n - 1) * CellSpacing;
        double contentMain = coreMain + CellSpacing + DividerWidth + CellSpacing + CellSize;
        double dipW = vertical ? CellSize + 2 * StripPadX : contentMain + 2 * StripPadX;
        double dipH = vertical ? contentMain + 2 * StripPadY : CellSize + 2 * StripPadY;

        double scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        int w = (int)Math.Ceiling(dipW * scale);
        int h = (int)Math.Ceiling(dipH * scale);

        // Which monitor is the dock on? Prefer the display under its stored position (so a dock
        // dropped on a secondary screen stays and hides on THAT screen); otherwise the display
        // nearest the window. This is the fix for snap/hide "jumping" across monitors.
        var display = ResolveDisplay(w, h);
        var work = display.WorkArea;      // excludes the taskbar — where the dock shows
        _outer = display.OuterBounds;     // full monitor — where the dock hides (behind the taskbar)
        int margin = (int)Math.Round(8 * scale);
        int x, y;

        if (_profile.Snapped)
        {
            // Flush to the snapped edge; along the edge, keep the position the user placed it at
            // (falling back to centered only if it has never been positioned).
            (x, y) = _profile.Edge switch
            {
                DockEdge.Top => (AlongX(work, w), work.Y),
                DockEdge.Left => (work.X, AlongY(work, h)),
                DockEdge.Right => (work.X + work.Width - w, AlongY(work, h)),
                _ => (AlongX(work, w), work.Y + work.Height - h), // Bottom
            };
        }
        else if (_profile.FreeX is int fx && _profile.FreeY is int fy)
        {
            x = Math.Clamp(fx, work.X, work.X + work.Width - w);
            y = Math.Clamp(fy, work.Y, work.Y + work.Height - h);
        }
        else
        {
            // Default free position: bottom-center with a small margin, fully visible.
            x = work.X + (work.Width - w) / 2;
            y = work.Y + work.Height - h - margin;
        }

        _shownRect = new RectInt32(x, y, w, h);
        _work = work;
        _appWindow.MoveAndResize(_shownRect);
        ApplyTopmost();
        OnRelayoutApplied();
    }

    /// <summary>Total DIPs the visible cells occupy along the strip's flow (gaps excluded).</summary>
    private double ItemsExtent()
    {
        double total = 0;
        foreach (var item in Items)
            total += item.CellExtent;
        return total;
    }

    // Along-edge coordinates derived from the stored placement, clamped to the work area.
    private int AlongX(RectInt32 work, int w) =>
        _profile.FreeX is int fx
            ? Math.Clamp(fx, work.X, work.X + Math.Max(0, work.Width - w))
            : work.X + (work.Width - w) / 2;

    private int AlongY(RectInt32 work, int h) =>
        _profile.FreeY is int fy
            ? Math.Clamp(fy, work.Y, work.Y + Math.Max(0, work.Height - h))
            : work.Y + (work.Height - h) / 2;

    /// <summary>Resolves the monitor the dock belongs to (multi-monitor safe).</summary>
    private DisplayArea ResolveDisplay(int w, int h)
    {
        DisplayArea? da = null;
        if (_profile.FreeX is int fx && _profile.FreeY is int fy)
        {
            var center = new PointInt32(fx + w / 2, fy + h / 2);
            da = DisplayArea.GetFromPoint(center, DisplayAreaFallback.Nearest);
        }
        da ??= DisplayArea.GetFromWindowId(_windowId, DisplayAreaFallback.Nearest);
        return da;
    }

    // Last computed "shown" rect, the current work area, and the full monitor bounds (physical
    // px), shared with the auto-hide/snap controller (see DockWindow.AutoHide.cs). The dock shows
    // within the work area but hides against the outer (screen) edge so a bottom-snapped dock
    // tucks behind the taskbar.
    private RectInt32 _shownRect;
    private RectInt32 _work;
    private RectInt32 _outer;

    partial void OnRelayoutApplied();

    // ---- Interaction ------------------------------------------------------
    //
    // Hover, pressed and keyboard-focus visuals come from Button itself (the Windows 11
    // subtle-fill control states), so there is no hand-rolled hover animation here.

    // NOTE: ItemsRepeater does NOT set FrameworkElement.DataContext on realized items
    // (x:Bind resolves via generated code, not DataContext). We stash the item in Tag via
    // Tag="{x:Bind}" on the template's cell Grid and read it back here — walking up from the
    // sender, because the element that raised the event (the launch Button) is a child of the
    // cell that carries the Tag.
    private static ToolDockItem? ItemOf(object sender) => FindItemFromSource(sender);

    // Button.Click fires for a pointer click AND a keyboard invoke (Space/Enter), so this one
    // handler covers mouse, touch and keyboard. Suppressed after a drag gesture.
    private void Item_Click(object sender, RoutedEventArgs e)
    {
        if (_dragOccurred)
        {
            Diag.Log("Item_Click suppressed: drag/reorder in progress");
            return; // the click that ends a drag/reorder, not a launch
        }
        var item = ItemOf(sender);
        Diag.Log($"Item_Click: tag={(item is null ? "NULL" : item.DisplayName)}");
        if (item is null)
            return;

        LaunchOrFocus(item);
    }

    /// <summary>
    /// Opens an item: brings an already-running app's window forward if there is one, otherwise
    /// launches it. Holding Shift forces a fresh instance the way the Windows 11 taskbar does.
    /// Shared by the dock strip and quick-launch search.
    /// </summary>
    private void LaunchOrFocus(ToolDockItem item)
    {
        // Read the modifier from the keyboard rather than the event args: Button.Click carries no
        // modifier state, and fires for keyboard invokes too.
        bool forceNewInstance = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
        _manager.LaunchOrFocus(item, forceNewInstance);
    }

    // ContextRequested fires for right-click and for the keyboard context-menu gesture
    // (Menu key / Shift+F10), so the per-item menu is reachable without a mouse.
    private void Item_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var target = (FrameworkElement)sender;
        if (target.Tag is not ToolDockItem item)
            return;

        var menu = new MenuFlyout();

        // A separator has no target to open, so its menu is just the Hide command below.
        if (!item.IsSeparator)
        {
            menu.Items.Add(Mi(Loc.Get("Menu.Open"), () => LaunchOrFocus(item)));
            if (item.HasCustomIcon)
                menu.Items.Add(Mi(Loc.Get("Menu.ResetIcon"), () => SetCustomIcon(item, null)));

            if (_manager.Config.ItemHotkeysEnabled)
                menu.Items.Add(BuildItemHotkeyMenu(target, item));
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        // Reordering is a drag, not a menu command — the menu entries duplicated the gesture the
        // dock already teaches. Removing lives in Settings ▸ Tools, next to the list of every tool
        // you could put back; Hide is the reversible version of it and is what belongs on a
        // right-click.
        menu.Items.Add(Mi(Loc.Get("Menu.Hide"), () => SetItemHidden(item, true)));

        if (e.TryGetPosition(target, out var pos))
            menu.ShowAt(target, pos);
        else
            menu.ShowAt(target); // keyboard-invoked: let the platform place it on the element
        e.Handled = true;

        static MenuFlyoutItem Mi(string text, Action onClick)
        {
            var mi = new MenuFlyoutItem { Text = text };
            mi.Click += (_, _) => onClick();
            return mi;
        }
    }

    private void DockBackground_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        if (e.TryGetPosition(fe, out var pos))
            ShowDockMenu(fe, pos);
        else
            ShowDockMenu(fe, new Windows.Foundation.Point(0, 0));
        e.Handled = true;
    }

    private void ShowDockMenu(FrameworkElement target, Windows.Foundation.Point at)
    {
        var menu = new MenuFlyout();

        // Adding a separator lives in the Add-tools window with the tools themselves, and search
        // in the tray menu — this menu is the two things you actually right-click the dock for.
        menu.Items.Add(MenuItem(Loc.Get("Menu.AddTool"), OpenAddNew));
        menu.Items.Add(MenuItem(Loc.Get("Menu.Settings"), OpenSettings));

        menu.Items.Add(new MenuFlyoutSeparator());

        // Which edge the dock is flush against — the four choices live directly under "Snap"
        // rather than in a further-nested "Snap to edge" submenu, since snapping is the only thing
        // this heading is about.
        var snap = new MenuFlyoutSubItem { Text = Loc.Get("Menu.PositionSection") };
        snap.Items.Add(SnapItem(Loc.Get("Edge.Bottom"), DockEdge.Bottom));
        snap.Items.Add(SnapItem(Loc.Get("Edge.Top"), DockEdge.Top));
        snap.Items.Add(SnapItem(Loc.Get("Edge.Left"), DockEdge.Left));
        snap.Items.Add(SnapItem(Loc.Get("Edge.Right"), DockEdge.Right));
        menu.Items.Add(snap);

        // Where the gear itself sits along the strip — a separate decision from where the whole
        // dock sits on screen. One toggling entry rather than a submenu: there are only two
        // positions, so the label just names the other one — "move it there" — instead of making
        // the user open a submenu to see which of two options is already checked.
        menu.Items.Add(MenuItem(
            _profile.SettingsButtonAtStart ? Loc.Get("Menu.SettingsButtonToEnd") : Loc.Get("Menu.SettingsButtonToStart"),
            () => SetSettingsButtonAtStart(!_profile.SettingsButtonAtStart)));

        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(MenuItem(Loc.Get("Menu.Quit"), _manager.Quit));

        menu.ShowAt(target, at);

        static MenuFlyoutItem MenuItem(string text, Action onClick)
        {
            var mi = new MenuFlyoutItem { Text = text };
            mi.Click += (_, _) => onClick();
            return mi;
        }

        MenuFlyoutItem SnapItem(string text, DockEdge edge)
        {
            var mi = new MenuFlyoutItem { Text = text };
            mi.Click += (_, _) => SetSnap(edge);
            return mi;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_dragOccurred)
            return; // the click that ends a drag, not a menu open
        OpenSettings();
    }

    private void AddNew_Click(object sender, RoutedEventArgs e)
    {
        if (_dragOccurred)
            return;
        OpenAddNew();
    }

    private static async Task LoadOneIconAsync(ToolDockItem item)
    {
        var icon = await ToolIconProvider.LoadCustomIconAsync(item);
        if (icon is not null)
            item.IconImage = icon;
    }

    // ---- Separators & custom icons ----------------------------------------

    /// <summary>Appends a divider to the end of the dock (dock menu, Settings, Add window).</summary>
    public void AddSeparator() => AddDockItem(new ToolDockItem
    {
        Kind = ToolKind.Separator,
        DisplayName = Loc.Get("Kind.Separator"),
    });

    /// <summary>
    /// Pins a user-supplied image onto an item, or clears it (null) so the shell/favicon icon
    /// comes back. The old bitmap is dropped first so the glyph shows while the new one loads.
    /// Clears any picked <see cref="ToolDockItem.CustomGlyph"/> too — the two are mutually exclusive.
    /// </summary>
    public void SetCustomIcon(ToolDockItem item, string? path)
    {
        item.CustomIconPath = string.IsNullOrWhiteSpace(path) ? null : path;
        item.CustomGlyph = null;
        item.IconImage = null;
        SaveConfig();
        RaiseItemsChanged();
        _ = LoadOneIconAsync(item);
    }

    /// <summary>
    /// Pins a built-in glyph (from the icon picker) onto an item, or clears it (null) so the
    /// kind's default glyph comes back. Clears any custom image path too — mutually exclusive
    /// with <see cref="ToolDockItem.CustomIconPath"/>. Needs no async resolution: the glyph renders
    /// the moment it's set.
    /// </summary>
    public void SetCustomGlyph(ToolDockItem item, string? glyph)
    {
        item.CustomGlyph = string.IsNullOrWhiteSpace(glyph) ? null : glyph;
        item.CustomIconPath = null;
        item.IconImage = null;
        SaveConfig();
        RaiseItemsChanged();
    }

    // ---- Snap / free positioning -----------------------------------------

    public void SetSnap(DockEdge? edge)
    {
        if (edge is DockEdge e)
        {
            _profile.Snapped = true;
            _profile.Edge = e;
            // Keep the current on-screen position as the placement anchor so it snaps flush
            // without jumping to the screen center.
            _profile.FreeX = _shownRect.X;
            _profile.FreeY = _shownRect.Y;
        }
        else
        {
            // Unsnap: leave it visible where it currently shows.
            _profile.Snapped = false;
            _profile.FreeX = _shownRect.X;
            _profile.FreeY = _shownRect.Y;
        }
        SaveConfig();
        // Reposition (snap flush or clamp free) FIRST so _shownRect/_outer reflect the new
        // edge, THEN start/stop hide-behind — otherwise ApplyAutoHide computes its slide
        // target from the stale (pre-snap) geometry and the relayout that follows hard-jumps
        // the window to correct it, which reads as a reset-then-instant-hide instead of one
        // smooth slide.
        UpdateSizeAndPosition();
        ApplyAutoHide();
    }

    public void SetAutoHide(bool on)
    {
        _profile.AutoHide = on;
        SaveConfig();
        UpdateSizeAndPosition();
        ApplyAutoHide();
    }

    public void SetAlwaysOnTop(bool on)
    {
        _profile.AlwaysOnTop = on;
        SaveConfig();
        ApplyTopmost();
    }

    public void SetVerticalWhenSideSnapped(bool on)
    {
        _profile.VerticalWhenSideSnapped = on;
        SaveConfig();
        QueueRelayout(); // re-orient (and re-size) if the dock is currently snapped to a side
    }

    // ---- Theme ------------------------------------------------------------

    /// <summary>
    /// Resolves the <see cref="ElementTheme"/> to request for any dockdev window given the chosen
    /// <see cref="DockTheme"/>. A High Contrast accessibility theme always wins (returns
    /// <see cref="ElementTheme.Default"/> so the window follows the system HC colors).
    /// </summary>
    internal static ElementTheme ResolveTheme(DockTheme theme)
    {
        if (HighContrast.IsActive())
            return ElementTheme.Default;
        return theme switch
        {
            DockTheme.Light => ElementTheme.Light,
            DockTheme.System => ElementTheme.Default,
            _ => ElementTheme.Dark,
        };
    }

    /// <summary>Applies the configured theme to the dock's root. The acrylic backdrop re-tints
    /// itself via its own <c>ActualThemeChanged</c> subscription. Called by
    /// <see cref="dockdevManager.SetTheme"/>, since the theme is app-wide.</summary>
    public void ApplyTheme() => RootGrid.RequestedTheme = ResolveTheme(_manager.Config.Theme);

    /// <summary>
    /// Re-colors the rounded DWM rim to disappear into the dock's glass. The color is the glass's
    /// own tint dimmed by how much of the desktop the frostiness setting lets through, so it
    /// tracks the theme, the accent-tint option and the frostiness slider together: at full
    /// frostiness the glass really is the tint and the rim matches it exactly, and as the glass
    /// clears the rim darkens with it instead of staying a bright ring around a translucent strip
    /// (which is what light mode's fixed surface color used to draw).
    /// <para>
    /// Erring dark is deliberate. The rim cannot be right for every wallpaper — the glass's
    /// rendered color depends on what is behind the window, which is unknowable from here — and a
    /// rim slightly darker than the glass reads as the shadow under a rounded edge, while one
    /// slightly brighter reads as an outline drawn around the dock.
    /// </para>
    /// </summary>
    private void ApplyWindowBorder()
    {
        bool dark = RootGrid.ActualTheme != ElementTheme.Light;
        var tint = _backdrop?.Current.Tint ?? (dark ? Rgb(0x20, 0x20, 0x20) : Rgb(0xF3, 0xF3, 0xF3));
        double lit = Math.Clamp(_manager.Config.GlassOpacity, 0.3, 1.0);

        WindowChrome.SetWindowBorderColor(_hwnd, dark, Rgb(
            (byte)Math.Round(tint.R * lit),
            (byte)Math.Round(tint.G * lit),
            (byte)Math.Round(tint.B * lit)));

        static Windows.UI.Color Rgb(byte r, byte g, byte b) =>
            Windows.UI.Color.FromArgb(255, r, g, b);
    }

    // The dock is topmost while snapped (so the auto-hide reveal shows over other windows), and
    // while floating only when the user has opted into "always on top".
    private bool ShouldBeTopmost => _profile.Snapped || _profile.AlwaysOnTop;

    private void ApplyTopmost()
    {
        bool top = ShouldBeTopmost;
        if (_appWindow.Presenter is OverlappedPresenter p)
            p.IsAlwaysOnTop = top;
        if (top)
            WindowChrome.EnsureTopmost(_hwnd);
        else
            WindowChrome.SetNotTopmost(_hwnd);
    }

    /// <summary>
    /// Re-places this dock after something outside the window moved it — a monitor change from
    /// Settings, say — so the new coordinates are honored and auto-hide re-computed for the edge
    /// it now sits on.
    /// </summary>
    public void RelayoutAfterExternalMove()
    {
        UpdateSizeAndPosition();
        ApplyAutoHide();
    }

    // ---- Visibility (driven by the tray menu, via dockdevManager) -------------

    /// <summary>Hides this dock until it is explicitly summoned back (tray "Hide dock").</summary>
    public void HideByUser()
    {
        // Stop the auto-hide controller first: it polls the cursor and would otherwise keep
        // moving (and re-showing) a window the user has asked to be rid of.
        PauseAutoHideForDrag();
        _appWindow.Hide();
    }

    /// <summary>Puts a user-hidden dock back on screen, without stealing focus.</summary>
    public void ShowAfterUserHide()
    {
        _appWindow.Show(activateWindow: false);
        UpdateSizeAndPosition();
        ApplyAutoHide();
    }

    /// <summary>
    /// Brings the dock into view and to the front: cancels an auto-hide slide (and holds it out
    /// for the usual settle period), re-asserts top-most Z-order, and takes foreground.
    /// </summary>
    public void BringToFront()
    {
        try
        {
            RevealNow();
            WindowChrome.EnsureTopmost(_hwnd);
            NativeMethods.SetForegroundWindow(_hwnd);
            Activate();
        }
        catch (Exception ex)
        {
            Diag.Log("BringToFront failed: " + ex);
        }
    }

    /// <summary>Pulls the dock fully back into view immediately. Implemented in the auto-hide
    /// partial, which owns the slide state.</summary>
    partial void RevealNow();

    /// <summary>Clears the dock, re-seeds the default items and returns to a floating position.</summary>
    public void ResetToDefaults()
    {
        _profile.Items.Clear();
        SeedDefaults();
        _profile.Snapped = false;
        _profile.FreeX = null;
        _profile.FreeY = null;
        RebuildVisible();
        SaveConfig();
        UpdateSizeAndPosition();
        ApplyAutoHide();
        RaiseItemsChanged();
        _ = LoadIconsAsync();
    }

    // ---- Dragging ---------------------------------------------------------
    //
    // Dragging a top-level window under the cursor is racy with WinUI pointer capture
    // (the cursor outruns the moving window and slips off it, dropping capture). So we
    // poll the global cursor and left-button state on a timer instead — rock solid
    // regardless of which window the cursor is currently over.
    //
    // A press that starts on an item reorders THAT item (never moves the dock); a press on
    // the background / divider / gear moves the whole dock window.

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dragTimer;
    private bool _dragging;
    private bool _dragOccurred;  // suppress the launch "tap" that may follow a drag/reorder
    private NativeMethods.POINT _dragStartCursor;
    private PointInt32 _dragStartWindow;
    private ToolDockItem? _reorderItem; // non-null while a press started on an item
    private double _reorderOriginPx; // screen px of the item host's leading edge along the flow axis
    private double _reorderScale;    // physical px per DIP, captured at gesture start
    private bool _reorderVertical;   // captured at gesture start so mid-drag stays consistent
    private const int DragThreshold = 12;

    private void Dock_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed)
            return;

        _dragOccurred = false; // reset on every press so a prior drag never eats this click
        NativeMethods.GetCursorPos(out _dragStartCursor);
        _dragging = false;

        // Did the press land on a dock item? If so this gesture is an item reorder, not a
        // window move — "don't allow dragging of the dock when moving the apps / items".
        _reorderItem = FindItemFromSource(e.OriginalSource);
        if (_reorderItem is null)
            _dragStartWindow = _appWindow.Position;

        _dragTimer ??= CreateDragTimer();
        if (!_dragTimer.IsRunning)
            _dragTimer.Start();
    }

    /// <summary>Walks up from the pressed element to find the dock item it belongs to (if any).</summary>
    private static ToolDockItem? FindItemFromSource(object source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is FrameworkElement fe && fe.Tag is ToolDockItem item)
                return item;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateDragTimer()
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(8);
        t.Tick += (_, _) => DragTick();
        return t;
    }

    private void DragTick()
    {
        // Button released -> end the gesture.
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) == 0)
        {
            _dragTimer?.Stop();
            bool wasDragging = _dragging;
            _dragging = false;

            if (_reorderItem is not null)
            {
                _reorderItem = null;
                if (wasDragging)
                    EndItemReorder();
                else
                    SetDropTarget(null);
                return;
            }

            if (wasDragging)
            {
                EndDragSnap();
                // Clear the drag flag once the trailing click (the pointer-release that ended
                // the drag) has been delivered and suppressed. Low priority runs after input
                // delivery, so a later keyboard invoke (Enter/Space) is not blocked.
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _dragOccurred = false);
            }
            return;
        }

        NativeMethods.GetCursorPos(out var cur);
        int dx = cur.X - _dragStartCursor.X;
        int dy = cur.Y - _dragStartCursor.Y;

        // ---- Item reorder path ----
        if (_reorderItem is not null)
        {
            if (!_dragging)
            {
                if (Math.Abs(dx) <= DragThreshold && Math.Abs(dy) <= DragThreshold)
                    return; // still a potential click/launch
                _dragging = true;
                _dragOccurred = true;
                PauseAutoHideForDrag();
                BeginItemReorder();
            }
            UpdateItemReorder(cur.X, cur.Y);
            return;
        }

        // ---- Window move path ----
        if (!_dragging)
        {
            if (Math.Abs(dx) <= DragThreshold && Math.Abs(dy) <= DragThreshold)
                return; // still a potential click
            _dragging = true;
            _dragOccurred = true;
            PauseAutoHideForDrag();
        }

        _appWindow.Move(new PointInt32(_dragStartWindow.X + dx, _dragStartWindow.Y + dy));
    }

    // ---- Item reorder mechanics ----

    private void BeginItemReorder()
    {
        // The window is stationary during a reorder, so the strip's screen geometry is fixed:
        // capture the item host's leading edge and the DPI scale once. When the dock is vertical
        // the items flow down the Y axis, so track Y instead of X.
        _reorderVertical = IsVertical;
        _reorderScale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        var origin = ItemsHost.TransformToVisual(RootGrid)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        _reorderOriginPx = _reorderVertical
            ? _appWindow.Position.Y + origin.Y * _reorderScale
            : _appWindow.Position.X + origin.X * _reorderScale;
    }

    /// <summary>
    /// Maps the cursor onto the slot the dragged item should occupy. Cells are not a uniform
    /// pitch (a separator is a narrow slot), so this walks the strip accumulating each cell's own
    /// extent. It measures against the layout of the <b>other</b> items — the dragged item
    /// excluded — and inserts where the cursor passes each one's midpoint: those midpoints don't
    /// move as the dragged item is re-inserted around them, so the result is stable instead of
    /// oscillating between two slots whenever a wide icon crosses a narrow separator.
    /// </summary>
    private void UpdateItemReorder(int cursorScreenX, int cursorScreenY)
    {
        int count = Items.Count;
        if (count < 2 || _reorderItem is null || _reorderScale <= 0)
            return;

        int from = Items.IndexOf(_reorderItem);
        if (from < 0)
            return;

        double coord = _reorderVertical ? cursorScreenY : cursorScreenX;
        double rel = (coord - _reorderOriginPx) / _reorderScale; // back into DIPs

        int target = 0;
        double edge = 0;
        foreach (var item in Items)
        {
            if (ReferenceEquals(item, _reorderItem))
                continue;

            if (rel > edge + item.CellExtent / 2)
                target++;
            edge += item.CellExtent + CellSpacing;
        }

        target = Math.Clamp(target, 0, count - 1);
        if (target != from)
            Items.Move(from, target);
    }

    private void EndItemReorder()
    {
        SetDropTarget(null);

        SyncMasterFromVisible();
        SaveConfig();
        RaiseItemsChanged();

        ResumeAutoHideAfterDrag();
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _dragOccurred = false);
    }

    /// <summary>On drop, snap to the nearest work-area edge if close enough, else float free.</summary>
    private void EndDragSnap()
    {
        var pos = _appWindow.Position;
        var size = _appWindow.Size;
        var work = DisplayArea.GetFromWindowId(_windowId, DisplayAreaFallback.Nearest).WorkArea;

        // Only snap when the dock is dropped essentially AT an edge (a small tolerance),
        // otherwise it floats freely wherever it was dropped.
        const int snapThreshold = 16;
        int dLeft = pos.X - work.X;
        int dTop = pos.Y - work.Y;
        int dRight = work.X + work.Width - (pos.X + size.Width);
        int dBottom = work.Y + work.Height - (pos.Y + size.Height);
        int min = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));

        // Remember exactly where it was dropped: this anchors the snapped position along the
        // edge (so it hides where you left it) and identifies the monitor it lives on.
        _profile.FreeX = pos.X;
        _profile.FreeY = pos.Y;

        if (min <= snapThreshold)
        {
            _profile.Snapped = true;
            _profile.Edge = min == dBottom ? DockEdge.Bottom
                         : min == dTop ? DockEdge.Top
                         : min == dLeft ? DockEdge.Left
                         : DockEdge.Right;
        }
        else
        {
            _profile.Snapped = false;
        }

        SaveConfig();
        UpdateSizeAndPosition();
        ApplyAutoHide();
    }

    // ---- Accessibility / small UI helpers ---------------------------------

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

    partial void ApplyAutoHide();
    partial void PauseAutoHideForDrag();
    partial void ResumeAutoHideAfterDrag();
}
