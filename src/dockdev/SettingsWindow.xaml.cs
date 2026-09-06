using System.ComponentModel;
using dockdev.Controls;
using dockdev.Models;
using dockdev.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace dockdev;

/// <summary>
/// A Windows-Settings-style window: a left NavigationView with a <b>General</b> page (language,
/// open-window indicators, start-with-Windows, backup, reset), an <b>Appearance</b> page (theme,
/// icon size, glass, accent tint, magnification), a <b>Shortcuts</b> page (the two system-wide
/// shortcuts and the per-item master switch), a <b>Dock</b> page (monitor, position, transpose,
/// auto-hide, always-on-top), a <b>Tools</b> page listing every pinned tool with a show/hide
/// switch and a remove button, and an <b>About</b> page. Mica-backed, and matches the dock's own
/// topmost state so it stays above it when it is topmost (snapped, or floating with "always on
/// top" on) without needlessly outranking every other app otherwise.
/// <para>
/// It talks to the <see cref="dockdevManager"/> rather than to the dock directly: most of what it
/// changes is app-wide.
/// </para>
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly dockdevManager _manager;
    private readonly nint _hwnd;
    private readonly AppWindow _appWindow;

    /// <summary>
    /// Every <see cref="ToolDockItem.PropertyChanged"/> handler this window has attached, so they
    /// can be detached at a point that is guaranteed to happen.
    /// <para>
    /// The rows in Tools subscribe to their item because a custom icon resolves asynchronously and
    /// can land after the row is built. The item is <em>config</em> — it lives as long as the
    /// process — so the subscription has to come off, and it used to come off in the row's
    /// <c>Unloaded</c>. That hook cannot do the job. <see cref="RebuildTools"/> runs once from this
    /// constructor, before the content has a <see cref="XamlRoot"/>: those rows are never loaded,
    /// so replacing them never unloads them, and their handlers stay on a process-lifetime object
    /// holding this whole window with them. Open Settings, change a tool, close it, repeat — every
    /// Settings window ever opened stays alive.
    /// </para>
    /// <para>
    /// Tracked here and torn down on <c>Closed</c>, which always fires, and again before each
    /// rebuild so handlers cannot pile up within one window either.
    /// </para>
    /// </summary>
    private readonly List<(ToolDockItem Item, PropertyChangedEventHandler Handler)> _itemSubscriptions = new();

    /// <summary>Detaches every tracked item subscription. Idempotent.</summary>
    private void ReleaseItemSubscriptions()
    {
        foreach (var (item, handler) in _itemSubscriptions)
            item.PropertyChanged -= handler;
        _itemSubscriptions.Clear();
    }

    /// <summary>
    /// Suppresses the "user changed this" handlers while controls are being populated from the
    /// config, so filling the page in cannot be mistaken for editing it.
    /// <para>
    /// Starts <b>true</b>, before any control exists, and <see cref="LoadSettings"/> clears it when
    /// the pages are loaded. That is not belt-and-braces: <c>InitializeComponent</c> itself
    /// provokes a change. The frostiness <c>Slider</c> is declared <c>Minimum="30"</c> while its
    /// Value is still the property default of 0, so the parser's own assignment coerces Value up to
    /// 30 and raises ValueChanged — with the flag defaulting to false, that would run the real
    /// handler and write 30% frostiness to the config on every open.
    /// </para>
    /// </summary>
    private bool _initializing = true;

    // Set once the window has closed. Every deferred (DispatcherQueue) and awaited continuation
    // checks this before touching a control, so a callback that lands after the window is gone is a
    // no-op instead of an access to a torn-down XAML tree.
    private bool _isClosed;

    public SettingsWindow(dockdevManager manager)
    {
        _manager = manager;
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        Title = Loc.Get("Settings.Title");
        SystemBackdrop = new MicaBackdrop();

        // Extend the Mica backdrop under the caption so the title bar matches a native Windows 11
        // window instead of showing an opaque strip. Must happen before the chrome theming below:
        // ExtendsContentIntoTitleBar re-extends the DWM frame into the client area, which resets
        // any border color already applied — set it any later and HideWindowBorder's effect gets
        // silently clobbered, leaving the default rim visible along the top edge.
        ExtendsContentIntoTitleBar = true;
        // After ExtendsContentIntoTitleBar, never before — see WindowChrome.UseTallTitleBar. This
        // is what makes the caption buttons 48px, matching AppTitleBar's height in the XAML.
        WindowChrome.UseTallTitleBar(_appWindow);
        SetTitleBar(AppTitleBar);

        // Match the dock's chosen Light/Dark/System theme so the Settings window reads the same,
        // and keep the caption buttons and window frame in step if the OS theme changes while
        // System mode is on.
        ApplyTheme(manager.Config.Theme);
        RootGrid.ActualThemeChanged += (_, _) => ApplyChromeTheme();
        // Re-assert on activation: DWM otherwise restores its default rim on some state changes.
        Activated += (_, _) => ApplyChromeTheme();

        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsMaximizable = false;
            // Only outrank other apps when the dock itself currently does — otherwise this window
            // would needlessly float above everything (full-screen apps, video calls) even though
            // a floating, non-topmost dock doesn't need that.
            p.IsAlwaysOnTop = manager.Config.Dock.Snapped || manager.Config.Dock.AlwaysOnTop;
        }
        _appWindow.IsShownInSwitchers = true;

        // Wide enough for the two-column Tools grid: at 880 the nav pane and the page padding
        // leave ~300px a card, which is where the tool names start being trimmed.
        WindowChrome.SetClientSizeDip(_appWindow, _hwnd, 1000, 680);
        WindowChrome.CenterOnCursor(_appWindow, windowId);

        // Each of these InfoBars starts closed and stays Collapsed in XAML rather than Visible, so
        // the Spacing on its parent StackPanel has no rendered sibling to insert a gap around
        // (StackPanel.Spacing applies even to Collapsed children — WinUI issue #916 — and IsOpen
        // alone doesn't collapse an InfoBar either — issue #9507 — so left alone this leaves a
        // permanent empty gap under every card whether or not the bar has ever opened). Opening it
        // programmatically also has to flip Visibility back, since setting IsOpen=true no longer
        // implies Visible once XAML starts it Collapsed; Closed covers every way it closes again,
        // including the user's own close button.
        LinkInfoBarVisibility(StartupBlockedBar);
        LinkInfoBarVisibility(BackupBar);
        LinkInfoBarVisibility(HotkeyBar);
        LinkInfoBarVisibility(SearchHotkeyBar);
        LinkInfoBarVisibility(UpdateBar);

        BuildLanguageList();
        BuildShortcutCaptures();
        LoadSettings();
        RebuildDock();
        RebuildTools();
        RebuildItemHotkeys();
        VersionText.Text = Loc.Format("About.Version", UpdateService.CurrentVersion.ToString());
        VersionPillText.Text = $"v{UpdateService.CurrentVersion}";
        LoadUpdateCard();

        // Keep the lists in step if something changes elsewhere (a drag reorder, a per-item menu).
        // Deferred so a change we initiate from here doesn't rebuild the tree mid-handler.
        _manager.ItemsChanged += OnDockItemsChanged;
        _manager.DockChanged += OnDockChanged;
        Closed += (_, _) =>
        {
            _isClosed = true;
            _manager.ItemsChanged -= OnDockItemsChanged;
            _manager.DockChanged -= OnDockChanged;
            // The config items outlive this window by the life of the process, so anything still
            // attached to one is this window kept alive after it closed.
            ReleaseItemSubscriptions();
        };
    }

    /// <summary>Keeps an InfoBar's own Visibility in step with IsOpen so a closed bar takes no
    /// space in its parent StackPanel. See the comment where this is called from the constructor.</summary>
    private static void LinkInfoBarVisibility(InfoBar bar) =>
        bar.Closed += (_, _) => bar.Visibility = Visibility.Collapsed;

    // Flipping a switch raises ItemsChanged, which lands back here and rebuilds the Tools list —
    // including the switch the user is standing on. Focus is captured before the rebuild and
    // restored after, on whichever card the same tool ended up on.
    private void OnDockItemsChanged() => DispatcherQueue?.TryEnqueue(() =>
    {
        if (_isClosed || RootGrid?.XamlRoot is null) return;
        var focusedKind = FocusedToolKind();
        RebuildTools();
        RebuildItemHotkeys();
        if (focusedKind is { } kind)
            RestoreToolFocus(kind);
    });

    private void OnDockChanged() => DispatcherQueue?.TryEnqueue(() =>
    {
        if (_isClosed || RootGrid?.XamlRoot is null) return;
        RebuildDock();
        RebuildTools();
        // Lives on the Appearance page rather than the (rebuilt-from-scratch) Dock page, so it
        // needs its own refresh to stay in step with a change made elsewhere — the dock's own
        // right-click menu, in particular.
        SettingsButtonPositionSwitch.IsOn = _manager.Config.Dock.SettingsButtonAtStart;
    });

    /// <summary>Applies the given app theme to this window's root (called on open and whenever the
    /// choice changes elsewhere), and re-themes the window chrome to match.</summary>
    internal void ApplyTheme(DockTheme theme)
    {
        RootGrid.RequestedTheme = DockWindow.ResolveTheme(theme);
        ApplyChromeTheme();
    }

    /// <summary>
    /// Tracks the caption buttons to the effective theme, and hides the DWM window rim by painting
    /// it this theme's surface color. This window extends its content into the title bar, leaving
    /// only the rim's top edge visible — where anything that doesn't match the surface reads as a
    /// stray line above the title bar (see <see cref="WindowChrome.HideWindowBorder"/>).
    /// </summary>
    private void ApplyChromeTheme()
    {
        bool dark = RootGrid.ActualTheme != ElementTheme.Light;
        WindowChrome.SetTitleBarTheme(_appWindow, dark);
        WindowChrome.HideWindowBorder(_hwnd, dark);
    }

    // The dimmed-text style the code-built cards use, taken from this window's own resources
    // rather than the application's. See the comment on it in SettingsWindow.xaml: the brush it
    // carries has to be resolved against THIS window's theme, which is the user's Light/Dark
    // choice and not necessarily the application's.
    private Style SecondaryCaptionStyle => (Style)RootGrid.Resources["SecondaryCaption"];

    // ---- Navigation --------------------------------------------------------

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (GeneralPanel is null || AppearancePanel is null ||
            ShortcutsPanel is null || DockPanel is null || ToolsPanel is null ||
            DocumentationPanel is null || AboutPanel is null)
            return;
        GeneralPanel.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPanel.Visibility = tag == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        DockPanel.Visibility = tag == "dock" ? Visibility.Visible : Visibility.Collapsed;
        ToolsPanel.Visibility = tag == "tools" ? Visibility.Visible : Visibility.Collapsed;
        DocumentationPanel.Visibility = tag == "documentation" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Selects a page by its nav tag. Used when the window is re-opened after a language
    /// change, so the user lands back where they were rather than on General.</summary>
    public void Navigate(string tag)
    {
        foreach (var candidate in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if ((candidate.Tag as string) == tag)
            {
                Nav.SelectedItem = candidate;
                return;
            }
        }
    }

    /// <summary>The page currently showing, as its nav tag.</summary>
    internal string CurrentPage => (Nav.SelectedItem as NavigationViewItem)?.Tag as string ?? "general";

    // ---- Loading the pages from the config ---------------------------------

    private void LoadSettings()
    {
        _initializing = true;

        var cfg = _manager.Config;
        ThemeChoice.SelectedIndex = cfg.Theme switch
        {
            DockTheme.Light => 0,
            DockTheme.Dark => 1,
            DockTheme.System => 2,
            _ => 1,
        };
        // Index 0 is "Match Windows"; the rest follow Loc.Available in order.
        int languageIndex = string.IsNullOrEmpty(cfg.Language)
            ? 0
            : Loc.Available.ToList().FindIndex(
                l => string.Equals(l.Code, cfg.Language, StringComparison.OrdinalIgnoreCase)) + 1;
        LanguageChoice.SelectedIndex = languageIndex > 0 ? languageIndex : 0;

        HotkeySwitch.IsOn = cfg.HotkeyEnabled;

        OpenIndicatorsSwitch.IsOn = cfg.ShowOpenIndicators;
        ReuseWindowsSwitch.IsOn = cfg.ReuseToolWindows;

        // Asked of Windows rather than read from the config, because the user can change it
        // outside dockdev (Task Manager ▸ Startup apps) — and on the packaged build that answer is
        // an async WinRT call, so the switch settles just after the page rather than with it.
        _ = RefreshStartupSwitchAsync();

        DensityChoice.SelectedIndex = cfg.Density switch
        {
            DockDensity.Small => 0,
            DockDensity.Large => 2,
            _ => 1,
        };
        GlassSlider.Value = Math.Round(cfg.GlassOpacity * 100);
        AccentTintSwitch.IsOn = cfg.AccentTint;
        MagnifySwitch.IsOn = cfg.Magnify;
        SettingsButtonPositionSwitch.IsOn = cfg.Dock.SettingsButtonAtStart;
        ItemHotkeysSwitch.IsOn = cfg.ItemHotkeysEnabled;

        _initializing = false;
    }

    // ---- Appearance page ---------------------------------------------------

    private void DensityChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        _manager.SetDensity(DensityChoice.SelectedIndex switch
        {
            0 => DockDensity.Small,
            2 => DockDensity.Large,
            _ => DockDensity.Medium,
        });
    }

    private void GlassSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_initializing)
            return;
        _manager.SetGlassOpacity(e.NewValue / 100.0);
    }

    private void AccentTintSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _manager.SetAccentTint(AccentTintSwitch.IsOn);
    }

    private void MagnifySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _manager.SetMagnify(MagnifySwitch.IsOn);
    }

    private void SettingsButtonPositionSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _manager.Dock?.SetSettingsButtonAtStart(SettingsButtonPositionSwitch.IsOn);
    }

    // ---- Language ----------------------------------------------------------

    private void BuildLanguageList()
    {
        // "Match Windows" first, then every shipped language under its own name — so someone who
        // can't read the language currently in use can still recognize theirs in the list.
        LanguageChoice.Items.Add(new ComboBoxItem { Content = Loc.Get("Language.System") });
        foreach (var language in Loc.Available)
            LanguageChoice.Items.Add(new ComboBoxItem { Content = language.NativeName });
    }

    private void LanguageChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;

        int index = LanguageChoice.SelectedIndex;
        string code = index <= 0 ? string.Empty : Loc.Available[index - 1].Code;

        // SetLanguage rebuilds the dock so the new table takes effect immediately; this window has
        // to be rebuilt too — it is the one the user is looking at — and it re-opens on the page
        // they were on. Deferred so the rebuild doesn't run inside this handler, which would be
        // tearing down the very ComboBox that raised it.
        _manager.SetLanguage(code);
        _manager.ReopenSettings(CurrentPage);
    }

    private void ThemeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        var theme = ThemeChoice.SelectedIndex switch
        {
            0 => DockTheme.Light,
            2 => DockTheme.System,
            _ => DockTheme.Dark,
        };
        _manager.SetTheme(theme);
    }

    private void OpenIndicatorsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _manager.SetShowOpenIndicators(OpenIndicatorsSwitch.IsOn);
    }

    private void ReuseWindowsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _manager.SetReuseToolWindows(ReuseWindowsSwitch.IsOn);
    }

    private async void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _isClosed)
            return;

        try
        {
            var state = await _manager.SetLaunchAtStartupAsync(StartupSwitch.IsOn);
            if (_isClosed || RootGrid?.XamlRoot is null)
                return;

            // Windows refuses to let an app re-enable a startup entry its user turned off, so a switch
            // left showing "on" would be a lie. Put it back and name the place they can undo it.
            bool blocked = state is StartupService.StartupState.BlockedByUser
                                or StartupService.StartupState.BlockedByPolicy;
            if (blocked)
                StartupBlockedBar.Visibility = Visibility.Visible;
            StartupBlockedBar.IsOpen = blocked;
            if (blocked)
                SetStartupSwitchSilently(false);
        }
        catch (Exception ex)
        {
            Diag.Log("StartupSwitch_Toggled failed: " + ex.Message);
        }
    }

    /// <summary>Reads the real startup state back from Windows and shows it, without the write-back
    /// that setting the switch would otherwise trigger.</summary>
    private async Task RefreshStartupSwitchAsync()
    {
        try
        {
            bool enabled = await StartupService.IsEnabledAsync();
            if (_isClosed || RootGrid?.XamlRoot is null)
                return;
            SetStartupSwitchSilently(enabled);
        }
        catch (Exception ex)
        {
            Diag.Log("RefreshStartupSwitchAsync failed: " + ex.Message);
        }
    }

    /// <summary>Moves the startup switch to match reality. <see cref="_initializing"/> is saved and
    /// restored rather than simply cleared: this also runs from inside <see cref="LoadSettings"/>'s
    /// initializing block, which is not finished with it.</summary>
    private void SetStartupSwitchSilently(bool on)
    {
        bool wasInitializing = _initializing;
        _initializing = true;
        StartupSwitch.IsOn = on;
        _initializing = wasInitializing;
    }

    // ---- Shortcuts page ----------------------------------------------------
    //
    // The two capture buttons are built here rather than in XAML because HotkeyCaptureButton is a
    // code-only control (see Controls/HotkeyCaptureButton.cs) — it carries the whole "arm capture,
    // read the next combination, reject the ones Windows would refuse" behavior that both of these
    // need.

    private HotkeyCaptureButton? _summonCapture;
    private HotkeyCaptureButton? _searchCapture;

    private void BuildShortcutCaptures()
    {
        _summonCapture = new HotkeyCaptureButton
        {
            MinWidth = 150,
            HorizontalAlignment = HorizontalAlignment.Right,
            Label = Loc.Get("Settings.Hotkey"),
            Gesture = _manager.ConfiguredHotkey,
        };
        _summonCapture.NeedsModifier += () => ShowBar(HotkeyBar, "Hotkey.NeedModifier");
        _summonCapture.Assigned += gesture =>
        {
            bool registered = _manager.SetHotkey(gesture);
            // A cleared shortcut can't clash, so only a real assignment can fail here — and only
            // when the switch is on, since nothing is registered while it is off.
            if (gesture is not null && !registered && HotkeySwitch.IsOn)
                ShowBar(HotkeyBar, "Hotkey.Taken");
            else
                HotkeyBar.IsOpen = false;
        };
        HotkeyColumn.Children.Add(_summonCapture);

        _searchCapture = new HotkeyCaptureButton
        {
            MinWidth = 150,
            HorizontalAlignment = HorizontalAlignment.Right,
            Label = Loc.Get("Settings.SearchHotkey"),
            Gesture = _manager.ConfiguredSearchHotkey,
        };
        _searchCapture.NeedsModifier += () => ShowBar(SearchHotkeyBar, "Hotkey.NeedModifier");
        _searchCapture.Assigned += gesture =>
        {
            // Same shape as the summon shortcut above: clearing can't clash, so only a real
            // assignment can come back refused.
            bool registered = _manager.SetSearchHotkey(gesture);
            if (gesture is not null && !registered)
                ShowBar(SearchHotkeyBar, "Hotkey.Taken");
            else
                SearchHotkeyBar.IsOpen = false;
        };
        SearchHotkeyColumn.Children.Add(_searchCapture);
    }

    private void HotkeySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        if (!_manager.SetHotkeyEnabled(HotkeySwitch.IsOn))
            ShowBar(HotkeyBar, "Hotkey.Taken");
        else
            HotkeyBar.IsOpen = false;
    }

    private void ItemHotkeysSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _manager.SetItemHotkeysEnabled(ItemHotkeysSwitch.IsOn);
        RebuildItemHotkeys();
    }

    /// <summary>
    /// Lists the per-item shortcuts that are currently assigned, each with a way to drop it.
    /// Assignment itself happens on an icon's own right-click menu — that is where you know which
    /// item you mean — but a shortcut you assigned three weeks ago is otherwise invisible until you
    /// happen to press it, so they are gathered here.
    /// </summary>
    private void RebuildItemHotkeys()
    {
        if (ItemHotkeyList is null)
            return;
        ItemHotkeyList.Children.Clear();

        var assigned = _manager.AllItems()
            .Where(i => HotkeyGesture.TryParse(i.Hotkey, out _))
            .ToList();

        if (assigned.Count == 0)
        {
            ItemHotkeyList.Children.Add(new TextBlock
            {
                Text = Loc.Get("Shortcuts.NoneAssigned"),
                Style = SecondaryCaptionStyle,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var item in assigned)
        {
            var row = new Grid { ColumnSpacing = 12, VerticalAlignment = VerticalAlignment.Center };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(item.DisplayName) ? Loc.Get("Tools.Unnamed") : item.DisplayName,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 0);
            row.Children.Add(name);

            var gesture = new TextBlock
            {
                Text = item.Hotkey ?? string.Empty,
                Style = SecondaryCaptionStyle,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(gesture, 1);
            row.Children.Add(gesture);

            var clear = new Button
            {
                Content = new FontIcon
                {
                    Glyph = "\uE711", // Cancel
                    FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                    FontSize = 12,
                },
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(clear, Loc.Get("Menu.ShortcutClear"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(clear, Loc.Get("Menu.ShortcutClear"));
            var captured = item;
            clear.Click += (_, _) =>
            {
                _manager.SetItemHotkey(captured, null);
                RebuildItemHotkeys();
            };
            Grid.SetColumn(clear, 2);
            row.Children.Add(clear);

            ItemHotkeyList.Children.Add(row);
        }
    }

    private static void ShowBar(InfoBar bar, string key)
    {
        bar.Message = Loc.Get(key);
        bar.Visibility = Visibility.Visible;
        bar.IsOpen = true;
    }

    // ---- About page: updates -----------------------------------------------
    //
    // The one networked capability (design doc §21), off by default. The card itself is hidden on
    // a packaged build (see LoadUpdateCard) — see docs/store-submission.md's sideload checklist,
    // which asserts Settings ▸ About shows no update-check card there, since the Store delivers
    // updates on that build instead.

    /// <summary>The release <see cref="CheckNowButton_Click"/> or a startup check already found,
    /// kept so Get/Skip act on the same one that is on screen.</summary>
    private ReleaseInfo? _foundRelease;

    private void LoadUpdateCard()
    {
        UpdateCard.Visibility = dockdevManager.UpdateChecksSupported ? Visibility.Visible : Visibility.Collapsed;
        if (!dockdevManager.UpdateChecksSupported)
            return;

        bool wasInitializing = _initializing;
        _initializing = true;
        UpdateCheckSwitch.IsOn = _manager.Config.Network.UpdateCheck;
        _initializing = wasInitializing;

        CheckNowButton.IsEnabled = UpdateCheckSwitch.IsOn;

        // A startup check may already have found something before this window ever opened —
        // reflect it rather than making the user press Check now again to see what the tray icon
        // is already showing.
        if (_manager.PendingUpdate is { } pending)
            ShowUpdateResult(pending);
    }

    private void UpdateCheckSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _manager.SetUpdateCheckEnabled(UpdateCheckSwitch.IsOn);
        CheckNowButton.IsEnabled = UpdateCheckSwitch.IsOn;
        if (!UpdateCheckSwitch.IsOn)
        {
            // Consent just came off: an in-flight or previously shown result would otherwise sit
            // there implying network access that is no longer permitted.
            _foundRelease = null;
            UpdateActionsPanel.Visibility = Visibility.Collapsed;
            UpdateBar.IsOpen = false;
        }
    }

    private async void CheckNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed || RootGrid?.XamlRoot is null)
            return;

        CheckNowButton.IsEnabled = false;
        string originalContent = (string)CheckNowButton.Content;
        CheckNowButton.Content = Loc.Get("Update.Checking");
        UpdateActionsPanel.Visibility = Visibility.Collapsed;
        UpdateBar.IsOpen = false;

        try
        {
            var release = await _manager.CheckForUpdatesAsync(promptOnly: false);
            if (_isClosed || RootGrid?.XamlRoot is null)
                return;

            if (release is not null)
                ShowUpdateResult(release);
            else
                ShowUpToDate();
        }
        catch (Exception ex)
        {
            Diag.Log("CheckNowButton_Click failed: " + ex.Message);
        }
        finally
        {
            if (!_isClosed && RootGrid?.XamlRoot is not null)
            {
                CheckNowButton.Content = originalContent;
                CheckNowButton.IsEnabled = UpdateCheckSwitch.IsOn;
            }
        }
    }

    private void SkipUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_foundRelease is not { } release)
            return;
        _manager.SkipUpdate(release);
        _foundRelease = null;
        UpdateActionsPanel.Visibility = Visibility.Collapsed;
        UpdateBar.IsOpen = false;
    }

    private void ShowUpdateResult(ReleaseInfo release)
    {
        _foundRelease = release;
        GetUpdateLink.NavigateUri = new Uri(release.Url);
        UpdateActionsPanel.Visibility = Visibility.Visible;

        UpdateBar.Severity = InfoBarSeverity.Informational;
        UpdateBar.Message = Loc.Format("Update.Available", release.Version.ToString());
        UpdateBar.Visibility = Visibility.Visible;
        UpdateBar.IsOpen = true;
    }

    private void ShowUpToDate()
    {
        _foundRelease = null;
        UpdateActionsPanel.Visibility = Visibility.Collapsed;

        UpdateBar.Severity = InfoBarSeverity.Success;
        UpdateBar.Message = Loc.Get("Update.UpToDate");
        UpdateBar.Visibility = Visibility.Visible;
        UpdateBar.IsOpen = true;
    }

    // ---- Import / export ---------------------------------------------------

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed || RootGrid?.XamlRoot is null)
            return;

        try
        {
            var path = await FilePickers.PickSaveFileAsync(_hwnd, "dockdev", ".json", "JSON");
            if (path is null || _isClosed || RootGrid?.XamlRoot is null)
                return;

            if (_manager.Export(path))
                ShowBackupResult(Loc.Format("Backup.Exported", path), InfoBarSeverity.Success);
            else
                ShowBackupResult(Loc.Get("Backup.ExportFailed"), InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            Diag.Log("Export failed: " + ex.Message);
            if (!_isClosed && RootGrid?.XamlRoot is not null)
                ShowBackupResult(Loc.Get("Backup.ExportFailed"), InfoBarSeverity.Error);
        }
    }

    // Import replaces the dock and every pinned item with no undo, so — like Reset — it gets a
    // confirmation, with the safe choice as the default button.
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed || RootGrid?.XamlRoot is null || Nav?.XamlRoot is null)
            return;

        try
        {
            var path = await FilePickers.PickOpenFileAsync(_hwnd, [".json"]);
            if (path is null || _isClosed || RootGrid?.XamlRoot is null || Nav?.XamlRoot is null)
                return;

            var confirm = new ContentDialog
            {
                XamlRoot = Nav.XamlRoot,
                Title = Loc.Get("Backup.ConfirmTitle"),
                Content = Loc.Get("Backup.ConfirmBody"),
                PrimaryButtonText = Loc.Get("Backup.ConfirmButton"),
                CloseButtonText = Loc.Get("Common.Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;

            if (_isClosed)
                return;

            if (!_manager.Import(path))
            {
                if (!_isClosed && RootGrid?.XamlRoot is not null)
                    ShowBackupResult(Loc.Get("Backup.ImportFailed"), InfoBarSeverity.Error);
                return;
            }

            // An import can change the language and the theme, so this window has to be rebuilt
            // rather than merely refreshed — same path a language change takes.
            _manager.ReopenSettings(CurrentPage);
        }
        catch (Exception ex)
        {
            Diag.Log("Import failed: " + ex.Message);
            if (!_isClosed && RootGrid?.XamlRoot is not null)
                ShowBackupResult(Loc.Get("Backup.ImportFailed"), InfoBarSeverity.Error);
        }
    }

    private void ShowBackupResult(string message, InfoBarSeverity severity)
    {
        BackupBar.Message = message;
        BackupBar.Severity = severity;
        BackupBar.Visibility = Visibility.Visible;
        BackupBar.IsOpen = true;
    }

    // Resetting wipes every pinned tool with no undo, so — unlike the low-stakes, easily-re-added
    // per-item "Remove" — it gets a confirmation dialog, per the Fluent guidance to confirm
    // destructive, hard-to-recover actions. The safe choice (Cancel) is the default button so an
    // accidental Enter doesn't wipe the dock.
    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed || RootGrid?.XamlRoot is null || Nav?.XamlRoot is null)
            return;

        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Nav.XamlRoot,
                Title = Loc.Get("Reset.Title"),
                Content = Loc.Get("Reset.Body"),
                PrimaryButtonText = Loc.Get("Reset.Confirm"),
                CloseButtonText = Loc.Get("Common.Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !_isClosed)
                _manager.ResetToDefaults();
        }
        catch (Exception ex)
        {
            Diag.Log("Reset failed: " + ex.Message);
        }
    }

    // ---- Dock page ---------------------------------------------------------
    //
    // One card, built imperatively: the number of monitors to offer is only known at runtime.

    private void RebuildDock()
    {
        DockList.Children.Clear();
        DockList.Children.Add(BuildDockCard());
    }

    private Border BuildDockCard()
    {
        var profile = _manager.Config.Dock;
        var dock = _manager.Dock;
        var panel = new StackPanel { Spacing = 12 };

        // Monitor. Rebuilt on every RebuildDock, so unplugging a display is reflected the next
        // time the page is rebuilt rather than being cached for the session.
        var displays = dockdevManager.Displays;
        if (displays.Count > 1)
        {
            var monitors = new ComboBox
            {
                MinWidth = 190,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(monitors, Loc.Get("Dock.MonitorLabel"));
            for (int i = 0; i < displays.Count; i++)
            {
                var bounds = displays[i].OuterBounds;
                monitors.Items.Add(new ComboBoxItem
                {
                    Content = Loc.Format("Dock.Monitor", i + 1, bounds.Width, bounds.Height),
                });
            }
            monitors.SelectedIndex = dockdevManager.DisplayIndexOf(profile);
            monitors.SelectionChanged += (_, _) =>
            {
                if (_initializing || monitors.SelectedIndex < 0)
                    return;
                _manager.MoveDockToDisplay(monitors.SelectedIndex);
            };
            panel.Children.Add(Row(Loc.Get("Dock.MonitorLabel"), Loc.Get("Dock.MonitorDesc"), monitors));
        }

        // Position.
        var edges = new ComboBox
        {
            MinWidth = 190,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(edges, Loc.Get("Settings.Position"));
        foreach (var key in new[]
                 {
                     "Position.Floating", "Position.Bottom", "Position.Top",
                     "Position.Left", "Position.Right",
                 })
            edges.Items.Add(new ComboBoxItem { Content = Loc.Get(key) });
        edges.SelectedIndex = !profile.Snapped ? 0 : profile.Edge switch
        {
            DockEdge.Bottom => 1,
            DockEdge.Top => 2,
            DockEdge.Left => 3,
            DockEdge.Right => 4,
            _ => 0,
        };
        edges.SelectionChanged += (_, _) =>
        {
            if (_initializing)
                return;
            dock?.SetSnap(edges.SelectedIndex switch
            {
                1 => DockEdge.Bottom,
                2 => DockEdge.Top,
                3 => DockEdge.Left,
                4 => DockEdge.Right,
                _ => (DockEdge?)null, // Floating
            });
        };
        panel.Children.Add(Row(Loc.Get("Settings.Position"), Loc.Get("Settings.PositionDesc"), edges));

        panel.Children.Add(Row(
            Loc.Get("Settings.Transpose"), Loc.Get("Settings.TransposeDesc"),
            Switch(profile.VerticalWhenSideSnapped, Loc.Get("Settings.Transpose"),
                on => dock?.SetVerticalWhenSideSnapped(on))));

        panel.Children.Add(Row(
            Loc.Get("Settings.AutoHide"), Loc.Get("Settings.AutoHideDesc"),
            Switch(profile.AutoHide, Loc.Get("Settings.AutoHide"), on => dock?.SetAutoHide(on))));

        panel.Children.Add(Row(
            Loc.Get("Settings.AlwaysOnTop"), Loc.Get("Settings.AlwaysOnTopDesc"),
            Switch(profile.AlwaysOnTop, Loc.Get("Settings.AlwaysOnTop"), on => dock?.SetAlwaysOnTop(on))));

        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 14, 14),
            Child = panel,
        };

        ToggleSwitch Switch(bool isOn, string name, Action<bool> apply)
        {
            var toggle = new ToggleSwitch
            {
                IsOn = isOn,
                OnContent = null,
                OffContent = null,
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, name);
            toggle.Toggled += (_, _) =>
            {
                if (!_initializing)
                    apply(toggle.IsOn);
            };
            return toggle;
        }
    }

    /// <summary>A title/description pair with a control on the right — the Settings row shape. An
    /// instance method because the description's style has to come from this window's own
    /// resources, so that it picks up this window's theme.</summary>
    private Grid Row(string title, string description, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            Style = SecondaryCaptionStyle,
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    // ---- Card grids --------------------------------------------------------

    /// <summary>
    /// Lays a built list of cards into a two-column grid, filling left-to-right. Two to a row
    /// rather than one: at nineteen tools a single column is a page and a half of scrolling for a
    /// list whose rows are mostly empty on the right, and the eye can take in a pair at a glance.
    /// The columns are declared in XAML; the rows can only be added once the cards are known.
    /// </summary>
    private static void FillTwoColumns(Grid grid, IReadOnlyList<FrameworkElement> cards)
    {
        grid.Children.Clear();
        grid.RowDefinitions.Clear();

        for (int i = 0; i < cards.Count; i++)
        {
            if (i % 2 == 0)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var card = cards[i];
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
            // Stretch vertically too, so the two cards on a row are the same height whichever of
            // them has the taller description.
            card.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetRow(card, i / 2);
            Grid.SetColumn(card, i % 2);
            grid.Children.Add(card);
        }
    }

    /// <summary>
    /// The accent bar that marks a card as active — the same 3px pill NavigationView draws beside
    /// the selected item, so "this one is on your dock" reads the way selection reads everywhere
    /// else in Windows 11. Always present so the two columns stay aligned; only the opacity moves,
    /// which also means colour is not the only signal (the row's own dimming carries it too).
    /// </summary>
    private static Border ActiveIndicator(bool active) => new()
    {
        Width = 3,
        Height = 20,
        CornerRadius = new CornerRadius(1.5),
        VerticalAlignment = VerticalAlignment.Center,
        Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
        Opacity = active ? 1 : 0,
        IsHitTestVisible = false,
    };

    // ---- Tools page --------------------------------------------------------

    /// <summary>
    /// Fills the Tools page: every tool in the catalog, always, with a switch saying whether it is
    /// on the dock. This previously listed only what was already pinned, which made it a view of
    /// the dock rather than of the tools and needed an "Add tools" button to reach the rest - so
    /// the catalog lived in two windows that disagreed about what a tool list is. Any separators
    /// the user has added are listed after the tools, since they are dock furniture with no
    /// catalog entry and would otherwise become unreachable from here.
    /// </summary>
    private void RebuildTools()
    {
        // Every row about to be discarded took a handler on a process-lifetime config item. Drop
        // them before building the replacements, or one window accumulates a set per rebuild.
        ReleaseItemSubscriptions();

        var dock = _manager.Dock;
        if (dock is null)
        {
            FillTwoColumns(ToolsList, []);
            return;
        }

        var cards = new List<FrameworkElement>();
        foreach (var tool in ToolCatalog.All)
            cards.Add(BuildToolCard(dock, tool));

        foreach (var separator in dock.AllItems.Where(i => i.IsSeparator).ToList())
            cards.Add(BuildRow(dock, separator));

        FillTwoColumns(ToolsList, cards);

        // Reflects the catalog's current state rather than driving it: on, only once every tool
        // already is. Set through _initializing so writing it back here doesn't loop back into
        // EnableAllToolsSwitch_Toggled and re-run the very enable/disable sweep that got us here.
        bool wasInitializing = _initializing;
        _initializing = true;
        EnableAllToolsSwitch.IsOn = ToolCatalog.All.All(t => dock.IsToolActive(t.Kind));
        _initializing = wasInitializing;
    }

    private void EnableAllToolsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        if (_manager.Dock is not { } dock)
            return;
        bool on = EnableAllToolsSwitch.IsOn;
        foreach (var tool in ToolCatalog.All)
            dock.SetToolActive(tool, on);
    }

    /// <summary>
    /// The tool whose on-dock switch currently has focus, or null if focus is elsewhere. Read from
    /// the switch's Tag, which is set to the tool's kind for exactly this.
    /// <para>
    /// The <see cref="XamlRoot"/> guard is load-bearing, not defensive.
    /// <see cref="RebuildTools"/> runs once from this window's constructor, and at that point the
    /// content has not been attached to a root yet, so <c>RootGrid.XamlRoot</c> is still null —
    /// and <c>FocusManager.GetFocusedElement(null)</c> does not return null, it throws
    /// <c>ArgumentException: The parameter is incorrect</c>. Thrown from a constructor on the UI
    /// thread that meant the whole app went down the moment anyone opened Settings.
    /// </para>
    /// <para>
    /// Null is the right answer here anyway: a window that has no root has no focused element, and
    /// nothing to restore focus to.
    /// </para>
    /// </summary>
    private ToolKind? FocusedToolKind() =>
        RootGrid.XamlRoot is { } root &&
        Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is ToggleSwitch
        {
            Tag: ToolKind kind,
        }
            ? kind
            : null;

    private void RestoreToolFocus(ToolKind kind)
    {
        foreach (var toggle in ToolsList.Children.OfType<Border>()
                     .Select(card => card.Child).OfType<Grid>()
                     .SelectMany(grid => grid.Children.OfType<ToggleSwitch>()))
        {
            if (toggle.Tag is ToolKind tagged && tagged == kind)
            {
                toggle.Focus(FocusState.Programmatic);
                return;
            }
        }
    }

    /// <summary>One catalog tool: icon, name, description and the on-dock switch. An active tool
    /// is marked the way a selected NavigationView item is — an accent bar down its leading edge
    /// — while a tool that is off reads as disabled: dimmed, but still legible and still
    /// switchable.</summary>
    private Border BuildToolCard(DockWindow dock, ToolDefinition tool)
    {
        var item = dock.AllItems.FirstOrDefault(i => i.Kind == tool.Kind);
        bool active = dock.IsToolActive(tool.Kind);

        var grid = new Grid { ColumnSpacing = 12, VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var indicator = ActiveIndicator(active);
        Grid.SetColumn(indicator, 0);
        grid.Children.Add(indicator);

        var iconHost = new Grid { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        if (item?.IconImage is not null)
        {
            iconHost.Children.Add(new Image { Source = item.IconImage, Width = 28, Height = 28, Stretch = Stretch.Uniform });
        }
        else
        {
            var glyph = item is { Glyph.Length: > 0 } ? item.Glyph : tool.Glyph;
            bool isTextGlyph = GlyphFonts.IsTextGlyph(glyph);
            iconHost.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontFamily = isTextGlyph
                    ? new FontFamily("Segoe UI")
                    : (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                FontSize = isTextGlyph ? 18 * 0.8 : 18,
            });
        }
        Grid.SetColumn(iconHost, 1);
        grid.Children.Add(iconHost);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = item is null || string.IsNullOrWhiteSpace(item.DisplayName) ? tool.DisplayName : item.DisplayName,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = item is null ? tool.Description : SubtitleFor(item),
            Style = SecondaryCaptionStyle,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        var toggle = new ToggleSwitch
        {
            IsOn = active,
            OnContent = null,
            OffContent = null,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            // How RebuildTools finds this switch again after it has replaced every card.
            Tag = tool.Kind,
        };
        ToolTipService.SetToolTip(toggle, Loc.Get("Tools.ShowOnDock"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, tool.DisplayName);
        toggle.Toggled += (_, _) => dock.SetToolActive(tool, toggle.IsOn);
        Grid.SetColumn(toggle, 3);
        grid.Children.Add(toggle);

        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            // Active state reads from the toggle and the leading accent bar alone — the card's own
            // rim stays neutral regardless of active state.
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 10, 12, 10),
            // Dimming the whole card, not just the switch, is what makes a page of nineteen tools
            // scannable: the active ones stand out without any of them being hidden.
            Opacity = active ? 1.0 : 0.55,
            Child = grid,
        };
    }

    private Border BuildRow(DockWindow dock, ToolDockItem item)
    {
        bool active = !item.Hidden;

        var grid = new Grid { ColumnSpacing = 12, VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Same leading accent bar the tool cards carry, so a separator's icon lines up with theirs
        // and "on the dock" looks the same whatever the item is.
        var indicator = ActiveIndicator(active);
        Grid.SetColumn(indicator, 0);
        grid.Children.Add(indicator);

        // Icon (a user-picked bitmap if one resolved, else the catalog glyph). Built imperatively
        // rather than via a binding, so it needs its own refresh: a custom image resolves
        // asynchronously and can land after this row is already on screen.
        var iconHost = new Grid { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        void RenderIcon()
        {
            iconHost.Children.Clear();
            if (item.IconImage is not null)
            {
                iconHost.Children.Add(new Image
                {
                    Source = item.IconImage,
                    Width = 28,
                    Height = 28,
                    Stretch = Stretch.Uniform,
                });
            }
            else
            {
                bool isTextGlyph = GlyphFonts.IsTextGlyph(item.Glyph);
                iconHost.Children.Add(new FontIcon
                {
                    Glyph = item.Glyph,
                    FontFamily = isTextGlyph
                        ? new FontFamily("Segoe UI")
                        : (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                    FontSize = isTextGlyph ? 18 * 0.8 : 18,
                });
            }
        }
        RenderIcon();

        void OnItemPropertyChanged(object? s, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ToolDockItem.IconImage))
                RenderIcon();
        }
        item.PropertyChanged += OnItemPropertyChanged;
        _itemSubscriptions.Add((item, OnItemPropertyChanged));

        Grid.SetColumn(iconHost, 1);
        grid.Children.Add(iconHost);

        // Name + subtitle (what it is, plus any assigned shortcut).
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.DisplayName)
                ? Loc.Get("Tools.Unnamed")
                : item.DisplayName,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = SubtitleFor(item),
            Style = SecondaryCaptionStyle,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        // Actions: the show/hide switch sits directly to the left of the delete button.
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Show/hide switch (On = shown on the dock). No on/off caption — the switch state alone
        // conveys it; the accessible name/tooltip carry the meaning for AT users.
        var toggle = new ToggleSwitch
        {
            IsOn = !item.Hidden,
            OnContent = null,
            OffContent = null,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(toggle, Loc.Get("Tools.ShowOnDock"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, Loc.Get("Tools.ShowOnDock"));
        toggle.Toggled += (s, _) =>
        {
            if (s is ToggleSwitch ts)
                dock.SetItemHidden(item, !ts.IsOn);
        };
        actions.Children.Add(toggle);

        var remove = new Button
        {
            Content = new FontIcon
            {
                Glyph = "\uE74D", // Delete
                FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                FontSize = 14,
            },
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(remove, Loc.Get("Tools.RemoveFromDock"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, Loc.Get("Tools.RemoveFromDock"));
        remove.Click += (_, _) => dock.RemoveDockItem(item);
        actions.Children.Add(remove);

        Grid.SetColumn(actions, 3);
        grid.Children.Add(actions);

        var row = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 10, 12, 10),
            Opacity = active ? 1.0 : 0.55,
            Child = grid,
        };
        // Teardown is _itemSubscriptions' job, not this row's: see the field's remarks for why an
        // Unloaded handler here could never run for the rows this window builds in its constructor.
        return row;
    }

    /// <summary>
    /// The second line of a Tools row: what the item is, then the details that are otherwise
    /// invisible on this page — the tool's own one-line description, and any assigned shortcut.
    /// <para>
    /// The shortcut matters most here. It is assigned from an icon's own right-click menu on the
    /// dock, so without this the only place it appears is Settings ▸ Shortcuts, and a row here
    /// would say nothing about an item that quietly owns a system-wide combination.
    /// </para>
    /// </summary>
    private static string SubtitleFor(ToolDockItem item)
    {
        var parts = new List<string>();

        if (item.IsSeparator)
            parts.Add(Loc.Get("Kind.Separator"));
        else if (ToolCatalog.Get(item.Kind) is { } definition)
            parts.Add(definition.Description);

        if (HotkeyGesture.TryParse(item.Hotkey, out var gesture))
            parts.Add(gesture.ToString());

        return string.Join(" • ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
