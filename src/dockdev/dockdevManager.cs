using dockdev.Interop;
using dockdev.Models;
using dockdev.Services;
using Microsoft.UI.Xaml;

namespace dockdev;

/// <summary>
/// Owns everything app-wide: the configuration file, the notification-area icon, the global
/// shortcut, the open-tool-window registry, and the Settings, Add-Tool and Search windows. It
/// creates the single <see cref="DockWindow"/> for <see cref="DockConfig.Dock"/> and keeps the
/// app-wide state in step with it.
/// <para>
/// Open-window state is driven by <see cref="ToolWindowManager"/>'s in-process events rather than
/// by any poll, and launching means opening a <see cref="ToolWindows.ToolWindowBase"/> via
/// <see cref="ToolWindowLauncher"/> (design doc §17/§26).
/// </para>
/// </summary>
public sealed class dockdevManager
{
    private DockWindow? _dock;

    private MessageWindow? _messageWindow;
    private TrayIconService? _tray;
    private HotkeyService? _hotkeys;
    private SettingsWindow? _settingsWindow;
    private AddToolWindow? _addToolWindow;
    private SearchWindow? _searchWindow;

    /// <summary>True while the user has hidden the docks from the tray menu. Distinct from
    /// auto-hide: they stay gone until explicitly summoned back.</summary>
    private bool _hiddenByUser;

    private bool _shuttingDown;

    public dockdevManager(DockConfig config)
    {
        Config = config;
        ToolWindows = new ToolWindowManager();
        ToolWindows.OpenSetChanged += ApplyOpenState;
        Launcher = new ToolWindowLauncher(this);
    }

    public DockConfig Config { get; }

    /// <summary>The in-process registry of open scratch windows, shared by every dock.</summary>
    public ToolWindowManager ToolWindows { get; }

    /// <summary>Opens tools into new or reused windows (design doc §17).</summary>
    public ToolWindowLauncher Launcher { get; }

    /// <summary>Raised when the dock window is replaced, so open windows can rebuild their lists.</summary>
    public event Action? DockChanged;

    /// <summary>Raised whenever the dock's item set changes (add / remove / hide / reorder).</summary>
    public event Action? ItemsChanged;

    // ---- Startup / shutdown ------------------------------------------------

    /// <summary>Creates the dock window, then wires up the tray and shortcut.</summary>
    public void Start()
    {
        bool firstRun = !Config.Seeded;
        Config.Seeded = true;

        // Settle the geometry before the window is built: the item template binds to it, and a
        // dock that lays out at the default density and then re-sizes reads as a flicker.
        DockMetrics.SetDensity(Config.Density);

        _dock = CreateWindow(Config.Dock, seedDefaults: firstRun && Config.Dock.Items.Count == 0);

        // A dock is a tool window with no taskbar button, so the tray icon is the only always-
        // available handle on a running dockdev — and, with the global shortcut, the way back to a
        // dock that is tucked behind a screen edge.
        SetUpTrayAndHotkey();

        _dock.Activate();

        if (firstRun)
            Save();

        // Strictly opt-in, and deliberately last: nothing above it touches the network, and a
        // slow or unreachable GitHub must never delay the dock appearing. A packaged copy skips it
        // outright — the Store updates itself.
        if (NetworkPolicy.UpdateCheckAllowed(Config) && UpdateChecksSupported)
            _ = CheckForUpdatesAsync(promptOnly: true);
    }

    private DockWindow CreateWindow(DockProfile profile, bool seedDefaults)
    {
        var window = new DockWindow(this, profile, seedDefaults);
        // A dock closed by any means (a crash in its content) must not leave a stale reference
        // behind that the tray and Settings still think is live.
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_dock, window))
                return; // already replaced (RebuildWindows clears the field before it closes one)
            _dock = null;
            RecreateAfterUnexpectedClose();
        };
        return window;
    }

    /// <summary>How many times a dock that went down on its own has been put back. Bounded so a
    /// window that cannot survive its own construction fails visibly rather than in a loop.</summary>
    private int _unexpectedRebuilds;

    private const int MaxUnexpectedRebuilds = 3;

    /// <summary>
    /// Puts the dock back after it closed without dockdev asking it to.
    /// <para>
    /// The process no longer ends with its last window (see <c>App.OnLaunched</c>, which is what
    /// stopped a closed dock taking the whole app with it) — so the failure mode on this path
    /// changed from "dockdev disappears" to "dockdev is running with a tray icon and nothing to
    /// summon", which is not much better. The dock is the app's only durable surface; if one goes,
    /// another takes its place.
    /// </para>
    /// </summary>
    private void RecreateAfterUnexpectedClose()
    {
        if (_shuttingDown || _unexpectedRebuilds >= MaxUnexpectedRebuilds)
            return;
        _unexpectedRebuilds++;

        try
        {
            Diag.Log("dockdevManager: the dock closed unexpectedly — putting it back.");
            _dock = CreateWindow(Config.Dock, seedDefaults: false);
            _dock.Activate();
            // It was hidden when it went, and the tray menu still says so — come back the same way.
            if (_hiddenByUser)
                _dock.HideByUser();
            DockChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // Leaves _dock null: the tray menu still works, so Quit and Settings remain reachable.
            Diag.Log("dockdevManager: could not put the dock back: " + ex);
        }
    }

    public void Save() => DockStore.Save(Config);

    /// <summary>
    /// Removes the tray icon and releases the global shortcut. Idempotent, and called both when
    /// quitting and before the process is replaced on restart.
    /// </summary>
    public void ReleaseShellIntegration()
    {
        _tray?.Dispose();
        _hotkeys?.Dispose();
        _messageWindow?.Dispose();
        _tray = null;
        _hotkeys = null;
        _messageWindow = null;
    }

    /// <summary>Shuts dockdev down cleanly: shell integration first, then the app.</summary>
    public void Quit()
    {
        _shuttingDown = true;
        try { Save(); }
        catch { /* ignore */ }
        ReleaseShellIntegration();
        // A dock refuses any close request that did not come from dockdev (see
        // DockWindow.AllowClose), and would otherwise cancel Application.Exit's teardown.
        _dock?.AllowClose();
        Application.Current.Exit();
    }

    private void SetUpTrayAndHotkey()
    {
        try
        {
            _messageWindow = new MessageWindow();
            _messageWindow.MessageReceived += (msg, _, _) =>
            {
                if (msg is NativeMethods.WM_DISPLAYCHANGE or NativeMethods.WM_SETTINGCHANGE)
                {
                    _dock?.DispatcherQueue.TryEnqueue(() => _dock?.RelayoutAfterExternalMove());
                }
            };

            _tray = new TrayIconService(_messageWindow)
            {
                IsDockVisible = () => !_hiddenByUser,
            };
            _tray.Activated += BringToFront;
            _tray.ShowHideRequested += ToggleVisibility;
            _tray.AddNewRequested += () => OpenAddNew();
            _tray.SettingsRequested += OpenSettings;
            _tray.SearchRequested += OpenSearch;
            _tray.QuitRequested += Quit;
            _tray.HasUpdate = () => PendingUpdate is not null;
            _tray.UpdateRequested += () =>
            {
                OpenSettings();
                if (PendingUpdate is { } release)
                    UpdateAvailable?.Invoke(release);
            };

            _hotkeys = new HotkeyService(_messageWindow);
            _hotkeys.Pressed += OnHotkeyPressed;
            ApplyHotkey();
            ApplySearchHotkey();
            ApplyItemHotkeys();
        }
        catch (Exception ex)
        {
            // dockdev running without its tray icon is degraded, not broken — never let this take
            // the app down at startup.
            Diag.Log("Tray/hotkey setup failed: " + ex);
        }
    }

    /// <summary>
    /// Back to a first-run dock: the strip is emptied and re-seeded. App-wide settings (theme,
    /// language, shortcut) are deliberately left alone.
    /// </summary>
    public void ResetToDefaults()
    {
        _dock?.ResetToDefaults();
        Save();
    }

    /// <summary>The dock's window, or null while it is being rebuilt.</summary>
    public DockWindow? Dock => _dock;

    // ---- Monitors ----------------------------------------------------------

    public static IReadOnlyList<Microsoft.UI.Windowing.DisplayArea> Displays
    {
        get
        {
            try
            {
                var found = Microsoft.UI.Windowing.DisplayArea.FindAll();
                var list = new List<Microsoft.UI.Windowing.DisplayArea>(found.Count);
                for (int i = 0; i < found.Count; i++)
                    list.Add(found[i]);
                return list;
            }
            catch (Exception ex)
            {
                Diag.Log("dockdevManager: could not enumerate displays: " + ex.Message);
                return Array.Empty<Microsoft.UI.Windowing.DisplayArea>();
            }
        }
    }

    public static void PlaceOnDisplay(DockProfile profile, int displayIndex)
    {
        var all = Displays;
        if (all.Count == 0)
            return;
        var work = all[Math.Clamp(displayIndex, 0, all.Count - 1)].WorkArea;
        profile.FreeX = work.X + work.Width / 2 - 180;
        profile.FreeY = work.Y + work.Height / 2 - 48;
    }

    public static int DisplayIndexOf(DockProfile profile)
    {
        var all = Displays;
        if (profile.FreeX is not int x || profile.FreeY is not int y || all.Count == 0)
            return 0;
        try
        {
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromPoint(
                new Windows.Graphics.PointInt32(x, y),
                Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            for (int i = 0; i < all.Count; i++)
                if (all[i].DisplayId.Value == area.DisplayId.Value)
                    return i;
        }
        catch (Exception ex)
        {
            Diag.Log("dockdevManager: could not resolve a dock's display: " + ex.Message);
        }
        return 0;
    }

    public void MoveDockToDisplay(int displayIndex)
    {
        PlaceOnDisplay(Config.Dock, displayIndex);
        Save();
        _dock?.RelayoutAfterExternalMove();
    }

    // ---- Summoning ---------------------------------------------------------

    public void BringToFront()
    {
        if (_hiddenByUser)
        {
            _hiddenByUser = false;
            _dock?.ShowAfterUserHide();
        }

        _dock?.BringToFront();
    }

    public void ToggleVisibility()
    {
        if (_hiddenByUser)
        {
            BringToFront();
            return;
        }

        _hiddenByUser = true;
        _dock?.HideByUser();
    }

    /// <summary>
    /// What a close request the dock refused means instead: hide it, exactly as the tray menu's
    /// "Hide dock" does, so the tray entry and the summon shortcut both describe and undo it
    /// correctly. Alt+F4 reaches the dock like any other top-level window, and destroying the
    /// app's only durable surface — or, before <c>App</c> set an explicit shutdown mode, the whole
    /// process — was never what it meant. Quitting stays the tray menu's Quit, which says so.
    /// </summary>
    public void HideDockOnCloseRequest()
    {
        if (_shuttingDown || _hiddenByUser)
            return;
        _hiddenByUser = true;
        _dock?.HideByUser();
    }

    // ---- Child windows -----------------------------------------------------

    public void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Activate();
    }

    public void OpenAddNew(DockWindow? target = null)
    {
        target ??= _dock;
        if (target is null)
            return;

        if (_addToolWindow is not null)
        {
            _addToolWindow.Activate();
            return;
        }
        _addToolWindow = new AddToolWindow(this, target);
        _addToolWindow.Closed += (_, _) => _addToolWindow = null;
        _addToolWindow.Activate();
    }

    public void OpenSearch()
    {
        if (_searchWindow is not null)
        {
            _searchWindow.Close();
            return;
        }

        _searchWindow = new SearchWindow(this);
        _searchWindow.Closed += (_, _) => _searchWindow = null;
        _searchWindow.Activate();
        _searchWindow.FocusQuery();
    }

    public void NotifyItemsChanged()
    {
        if (_shuttingDown)
            return;
        ItemsChanged?.Invoke();
        ApplyOpenState();
        ApplyItemHotkeys();
    }

    /// <summary>
    /// Opens a tool: brings an already-open window forward if <see cref="DockConfig.ReuseToolWindows"/>
    /// is on and one exists, otherwise opens a brand-new temporary window (design doc §17). Shared
    /// by the dock strip, the fly-out bars, quick-launch search and the per-item shortcuts.
    /// </summary>
    public void LaunchOrFocus(ToolDockItem item, bool forceNewInstance)
    {
        var definition = ToolCatalog.Get(item.Kind);
        if (definition is null)
        {
            Diag.Log($"LaunchOrFocus: '{item.Kind}' is not in the catalog — skipped.");
            return;
        }
        Launcher.Open(definition, seedFilePath: null, forceNewInstance);
    }

    // ---- App-wide settings -------------------------------------------------

    public void SetTheme(DockTheme theme)
    {
        Config.Theme = theme;
        Save();
        _dock?.ApplyTheme();
        _settingsWindow?.ApplyTheme(theme);
        _addToolWindow?.ApplyTheme(theme);
        ToolWindows.ApplyThemeToAll();
    }

    public async Task<StartupService.StartupState> SetLaunchAtStartupAsync(bool on)
    {
        var state = await StartupService.SetEnabledAsync(on);
        Config.LaunchAtStartup = state == StartupService.StartupState.Enabled;
        Save();
        return state;
    }

    public void SetShowOpenIndicators(bool on)
    {
        Config.ShowOpenIndicators = on;
        Save();
        ApplyOpenState();
    }

    public void SetDensity(DockDensity density)
    {
        Config.Density = density;
        DockMetrics.SetDensity(density);
        Save();
        _dock?.ApplyDensity();
    }

    public void SetGlassOpacity(double opacity)
    {
        Config.GlassOpacity = Math.Clamp(opacity, 0.3, 1.0);
        Save();
        _dock?.ApplyGlass();
    }

    public void SetAccentTint(bool on)
    {
        Config.AccentTint = on;
        Save();
        _dock?.ApplyGlass();
    }

    public void SetMagnify(bool on)
    {
        Config.Magnify = on;
        Save();
        _dock?.ApplyMagnifySetting();
    }

    public void SetReuseToolWindows(bool on)
    {
        Config.ReuseToolWindows = on;
        Save();
    }

    public void SetUpdateCheckEnabled(bool on)
    {
        Config.Network.UpdateCheck = on;
        Save();
    }

    // ---- Update check ------------------------------------------------------

    public static bool UpdateChecksSupported => !PackagedRuntime.IsPackaged;

    public event Action<ReleaseInfo>? UpdateAvailable;

    public ReleaseInfo? PendingUpdate { get; private set; }

    public async Task<ReleaseInfo?> CheckForUpdatesAsync(bool promptOnly)
    {
        var release = await UpdateService.CheckAsync(Config, UpdateService.CurrentVersion);
        if (release is null)
            return null;

        if (promptOnly &&
            string.Equals(Config.SkippedUpdate, release.Version.ToString(), StringComparison.Ordinal))
            return null;

        PendingUpdate = release;
        UpdateAvailable?.Invoke(release);
        return release;
    }

    public void SkipUpdate(ReleaseInfo release)
    {
        Config.SkippedUpdate = release.Version.ToString();
        PendingUpdate = null;
        Save();
    }

    // ---- Import / export ---------------------------------------------------

    public bool Export(string path) => DockStore.ExportTo(Config, path);

    public bool Import(string path)
    {
        var imported = DockStore.ImportFrom(path);
        if (imported is null)
            return false;

        Config.Dock = imported.Dock;
        Config.CopyAppSettingsFrom(imported);
        Save();

        DockMetrics.SetDensity(Config.Density);
        Loc.Initialize(Config.Language);
        _ = StartupService.SetEnabledAsync(Config.LaunchAtStartup);

        RebuildWindows();

        ApplyHotkey();
        ApplySearchHotkey();
        ApplyItemHotkeys();
        ItemsChanged?.Invoke();
        return true;
    }

    public void SetLanguage(string code)
    {
        Config.Language = code ?? string.Empty;
        Save();
        Loc.Initialize(Config.Language);
        _tray?.RefreshTooltip();
        RebuildWindows();
    }

    private void RebuildWindows()
    {
        var old = _dock;
        _dock = null;
        // A deliberate teardown, so this one really may close (a dock refuses otherwise).
        old?.AllowClose();
        old?.Close();

        _dock = CreateWindow(Config.Dock, seedDefaults: false);
        _dock.Activate();

        _addToolWindow?.Close();

        DockChanged?.Invoke();
    }

    public void ReopenSettings(string page)
    {
        var window = _settingsWindow;
        if (window is null)
            return;
        _settingsWindow = null;
        window?.Close();

        window.DispatcherQueue.TryEnqueue(() =>
        var dq = _dock?.DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        dq?.TryEnqueue(() =>
        {
            _settingsWindow = null;
            window.Close();

            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Activate();
            _settingsWindow.Navigate(page);
        });
    }

    // ---- Global shortcuts --------------------------------------------------

    private const int SummonHotkeyId = 0x4143; // 'A','C'
    private const int SearchHotkeyId = 0x4144;
    private const int FirstItemHotkeyId = 0x4200;

    private readonly Dictionary<int, ToolDockItem> _itemHotkeys = new();

    private void OnHotkeyPressed(int id)
    {
        if (id == SummonHotkeyId)
            BringToFront();
        else if (id == SearchHotkeyId)
            OpenSearch();
        else if (_itemHotkeys.TryGetValue(id, out var item))
            LaunchOrFocus(item, forceNewInstance: false);
    }

    public HotkeyGesture? ConfiguredHotkey =>
        HotkeyGesture.TryParse(Config.Hotkey, out var gesture) ? gesture : null;

    public HotkeyGesture? ConfiguredSearchHotkey =>
        HotkeyGesture.TryParse(Config.SearchHotkey, out var gesture) ? gesture : null;

    public bool ApplyHotkey()
    {
        if (_hotkeys is null)
            return true;
        if (!Config.HotkeyEnabled || ConfiguredHotkey is not { } gesture)
        {
            _hotkeys.Unregister(SummonHotkeyId);
            return true;
        }
        return _hotkeys.Register(SummonHotkeyId, gesture);
    }

    public bool ApplySearchHotkey()
    {
        if (_hotkeys is null)
            return true;
        if (ConfiguredSearchHotkey is not { } gesture)
        {
            _hotkeys.Unregister(SearchHotkeyId);
            return true;
        }
        return _hotkeys.Register(SearchHotkeyId, gesture);
    }

    public IReadOnlyList<ToolDockItem> ApplyItemHotkeys()
    {
        var refused = new List<ToolDockItem>();
        if (_hotkeys is null)
            return refused;

        foreach (var id in _itemHotkeys.Keys.ToList())
            _hotkeys.Unregister(id);
        _itemHotkeys.Clear();

        if (!Config.ItemHotkeysEnabled)
            return refused;

        int nextId = FirstItemHotkeyId;
        foreach (var item in AllItems())
        {
            if (!HotkeyGesture.TryParse(item.Hotkey ?? string.Empty, out var gesture))
                continue;

            int id = nextId++;
            if (_hotkeys.Register(id, gesture))
                _itemHotkeys[id] = item;
            else
                refused.Add(item);
        }
        return refused;
    }

    public IEnumerable<ToolDockItem> AllItems() => Config.Dock.Items;

    public bool SetHotkey(HotkeyGesture? gesture)
    {
        var previous = Config.Hotkey;
        Config.Hotkey = gesture?.ToString() ?? string.Empty;
        Save();
        return ApplyHotkey();
        if (ApplyHotkey())
        {
            Save();
            return true;
        }
        Config.Hotkey = previous;
        ApplyHotkey();
        return false;
    }

    public bool SetHotkeyEnabled(bool on)
    {
        Config.HotkeyEnabled = on;
        Save();
        return ApplyHotkey();
    }

    public bool SetSearchHotkey(HotkeyGesture? gesture)
    {
        var previous = Config.SearchHotkey;
        Config.SearchHotkey = gesture?.ToString() ?? string.Empty;
        Save();
        return ApplySearchHotkey();
        if (ApplySearchHotkey())
        {
            Save();
            return true;
        }
        Config.SearchHotkey = previous;
        ApplySearchHotkey();
        return false;
    }

    public void SetItemHotkeysEnabled(bool on)
    {
        Config.ItemHotkeysEnabled = on;
        Save();
        ApplyItemHotkeys();
    }

    public bool SetItemHotkey(ToolDockItem item, HotkeyGesture? gesture)
    {
        var previous = item.Hotkey;
        item.Hotkey = gesture?.ToString();
        var refused = ApplyItemHotkeys();
        if (refused.Contains(item))
        {
            item.Hotkey = previous;
            ApplyItemHotkeys();
            return false;
        }
        Save();
        var refused = ApplyItemHotkeys();
        ItemsChanged?.Invoke();
        return !refused.Contains(item);
        return true;
    }

    // ---- Open-window state --------------------------------------------------

    /// <summary>Pushes the current open-window state onto every item on every dock, children
    /// included. Driven by <see cref="ToolWindowManager.OpenSetChanged"/> — an in-process event,
    /// never a poll (design doc §17).</summary>
    private void ApplyOpenState()
    {
        if (!Config.ShowOpenIndicators)
        {
            foreach (var item in AllItems())
                item.SetOpen(false, "");
            return;
        }

        string status = Loc.Get("Dock.Open");
        foreach (var item in AllItems())
            item.SetOpen(ToolWindows.OpenCount(item.Kind) > 0, status);
    }
}
