namespace dockdev.Models;

/// <summary>Which screen edge the dock is snapped to.</summary>
public enum DockEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>
/// The app's colour theme. <see cref="System"/> follows the current Windows light/dark setting;
/// <see cref="Light"/> and <see cref="Dark"/> pin it regardless. A High Contrast accessibility
/// theme always overrides this so the shell's high-contrast colours come through.
/// </summary>
public enum DockTheme
{
    Light,
    Dark,
    System,
}

/// <summary>The two networked capabilities dockdev has, both off by default and both gated through
/// <see cref="Services.NetworkPolicy"/> (design doc §19/§21).</summary>
public sealed class NetworkSettings
{
    /// <summary>Check GitHub for a newer release on startup. Off by default and strictly opt-in —
    /// this is the <b>only</b> thing that makes dockdev talk to the network in v1 (Currency
    /// Converter's ECB fetch, design doc §16, was parked as a post-v1 enhancement — see
    /// <see cref="ToolKind"/> — so there is no second capability to gate yet).</summary>
    public bool UpdateCheck { get; set; }
}

/// <summary>
/// Everything that persists between runs: the dock and the app-wide settings. A fresh format with
/// no predecessor (design doc §19), so there is nothing legacy to migrate.
/// <para>
/// The dock's own state (items, position, edge, auto-hide) lives on <see cref="Dock"/>; everything
/// else here is app-wide.
/// </para>
/// </summary>
public sealed class DockConfig
{
    private DockProfile? _dock;

    /// <summary>
    /// The single dock strip dockdev shows. dockdev runs exactly one — there is no "add a second dock"
    /// flow — so this is a plain property rather than a list. Materialized on first read so it is
    /// never null, whether or not the file on disk carried one.
    /// </summary>
    public DockProfile Dock
    {
        get => _dock ??= new DockProfile();
        set => _dock = value;
    }

    /// <summary>
    /// Whether the deserialized file actually carried a dock, as opposed to <see cref="Dock"/>
    /// having manufactured one on first read. Import checks this — any JSON object deserializes
    /// happily into a <see cref="DockConfig"/> full of defaults, so without it a file that is not
    /// a dockdev config would import as a plausible-looking empty one.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasDock => _dock is not null;

    // ---- App-wide settings ------------------------------------------------

    /// <summary>Start dockdev automatically when the user signs in (per-user Run key).</summary>
    public bool LaunchAtStartup { get; set; }

    /// <summary>
    /// The app's colour theme. Defaults to <see cref="DockTheme.Dark"/> so it reads like the
    /// Windows 11 dark taskbar (and so existing configs without this field keep that look).
    /// </summary>
    public DockTheme Theme { get; set; } = DockTheme.Dark;

    /// <summary>
    /// Show a dot under a tool that has an open scratch window, and make clicking it focus that
    /// window instead of starting a second copy (Shift+click still forces a new instance) when
    /// <see cref="ReuseToolWindows"/> is on (design doc §19).
    /// </summary>
    public bool ShowOpenIndicators { get; set; } = true;

    /// <summary>
    /// The language dockdev's own UI uses, as a BCP-47 code from <c>Loc.Available</c> (e.g.
    /// <c>"de"</c>, <c>"zh-Hans"</c>). Empty — the default — follows the Windows display language.
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// The system-wide shortcut that brings the dock to the front, in the readable form
    /// <c>HotkeyGesture</c> parses (e.g. <c>"Ctrl+Alt+A"</c>). Empty means no shortcut. Kept as a
    /// string so a hand-edited config stays legible, and so an unparseable value degrades to
    /// "no shortcut" instead of failing to load the whole config.
    /// </summary>
    public string Hotkey { get; set; } = HotkeyGesture.Default.ToString();

    /// <summary>Whether <see cref="Hotkey"/> is registered with Windows. Lets the user switch the
    /// shortcut off without losing the combination they had chosen.</summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>
    /// True once the default items have been seeded (first run). Prevents re-seeding after the
    /// user has intentionally emptied the dock — an empty dock then persists and shows the
    /// "+ Add New" affordance instead of springing the defaults back.
    /// </summary>
    public bool Seeded { get; set; }

    /// <summary>
    /// How big the dock's icons and cells are. <see cref="DockDensity.Medium"/> matches the
    /// Windows 11 taskbar and is the default; the geometry every surface derives from it lives on
    /// <see cref="DockMetrics"/>.
    /// </summary>
    public DockDensity Density { get; set; } = DockDensity.Medium;

    /// <summary>
    /// How frosted the acrylic glass is (the backdrop's luminosity opacity), 0.3–1.0. Higher is
    /// more frosted; at the bottom of the range the desktop comes through almost unobstructed.
    /// </summary>
    public double GlassOpacity { get; set; } = 1.00;

    /// <summary>Tint the glass with the Windows accent color instead of the neutral grey the
    /// taskbar uses.</summary>
    public bool AccentTint { get; set; }

    /// <summary>
    /// Swell an icon as the cursor passes over it, macOS-dock style. The swell happens
    /// <em>inside</em> the cell — the dock window never resizes — because the acrylic backdrop
    /// paints the whole window and cannot be masked to a taller, mostly-empty one.
    /// </summary>
    public bool Magnify { get; set; }

    /// <summary>
    /// Per-item shortcuts (<c>ToolDockItem.Hotkey</c>) are only registered with Windows while this
    /// is on. Off by default: a handful of extra system-wide combinations is a decision the user
    /// should make deliberately, not one that arrives with an update.
    /// </summary>
    public bool ItemHotkeysEnabled { get; set; }

    /// <summary>
    /// The shortcut that opens quick-launch search, in <c>HotkeyGesture</c>'s readable form.
    /// Empty means none — which is the default, for the same reason as
    /// <see cref="ItemHotkeysEnabled"/>.
    /// </summary>
    public string SearchHotkey { get; set; } = string.Empty;

    /// <summary>A release the user chose to skip, so the same prompt doesn't reappear every
    /// launch. Empty means nothing is skipped.</summary>
    public string SkippedUpdate { get; set; } = string.Empty;

    /// <summary>
    /// Clicking a tool cell opens a brand-new temporary window by default (design doc §17). When
    /// this is on, click instead focuses the most recently opened window of that tool, and
    /// Shift+click always forces a new instance — taskbar semantics, offered as an opt-in for
    /// people who want it.
    /// </summary>
    public bool ReuseToolWindows { get; set; }

    /// <summary>Every networked capability, both off by default (design doc §21).</summary>
    public NetworkSettings Network { get; set; } = new();

    /// <summary>
    /// Per-tool preferences (indent width, word wrap, the mask threshold, …), keyed by the tool's
    /// catalog id in lower case. Loosely typed because each tool's preferences are its own shape;
    /// an unknown or malformed entry is simply not read back by that tool rather than failing the
    /// whole config load.
    /// </summary>
    public Dictionary<string, System.Text.Json.JsonElement> ToolSettings { get; set; } = new();

    /// <summary>Named Data Masker rule/strategy bundles a team has saved, exportable/importable as
    /// JSON (design doc §15.3). Empty until the user saves one — the three built-ins
    /// (<c>Services.Masking.MaskProfile.BuiltIn</c>) are code-defined and never stored here.</summary>
    public List<Services.Masking.MaskProfile> MaskProfiles { get; set; } = [];

    /// <summary>Guarantees the dock exists. Called once after loading; a config that is already
    /// valid is untouched.</summary>
    public DockConfig EnsureValid()
    {
        _ = Dock;
        return this;
    }

    /// <summary>
    /// Copies every app-wide setting off <paramref name="other"/>, leaving <see cref="Dock"/>
    /// alone. Used by import, which replaces the dock separately: the running
    /// <see cref="DockConfig"/> instance is shared by every window and the manager, so an import
    /// has to fill the existing object in rather than swap in a new one.
    /// <para>
    /// <see cref="Seeded"/> is deliberately not copied — it says whether <em>this installation</em>
    /// has been past its first run, which is a fact about this machine and not about the file.
    /// </para>
    /// </summary>
    public void CopyAppSettingsFrom(DockConfig other)
    {
        LaunchAtStartup = other.LaunchAtStartup;
        Theme = other.Theme;
        ShowOpenIndicators = other.ShowOpenIndicators;
        Language = other.Language;
        Hotkey = other.Hotkey;
        HotkeyEnabled = other.HotkeyEnabled;
        Density = other.Density;
        GlassOpacity = other.GlassOpacity;
        AccentTint = other.AccentTint;
        Magnify = other.Magnify;
        ItemHotkeysEnabled = other.ItemHotkeysEnabled;
        SearchHotkey = other.SearchHotkey;
        SkippedUpdate = other.SkippedUpdate;
        ReuseToolWindows = other.ReuseToolWindows;
        Network = other.Network;
        ToolSettings = other.ToolSettings;
        MaskProfiles = other.MaskProfiles;
    }
}
