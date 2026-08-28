namespace dockdev.Models;

/// <summary>
/// The dock strip: its items and everything about where and how it sits on screen.
/// <para>
/// dockdev shows exactly one of these. Settings that belong to the <em>app</em> rather than to the
/// strip (theme, language, the global shortcut, start-with-Windows) stay on
/// <see cref="DockConfig"/>.
/// </para>
/// <para>
/// There is no monitor identifier here on purpose. The dock's monitor is implied by
/// <see cref="FreeX"/>/<see cref="FreeY"/> — the display those coordinates fall on — which
/// survives a reboot, a resolution change and a monitor being unplugged and plugged back in far
/// better than any display id or device name does. Moving the dock to another monitor is therefore
/// just writing coordinates on that monitor.
/// </para>
/// </summary>
public sealed class DockProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public List<ToolDockItem> Items { get; set; } = new();

    /// <summary>
    /// When true the dock is snapped flush to <see cref="Edge"/>. If <see cref="AutoHide"/> is
    /// also on it slides behind that edge and reveals on cursor approach; otherwise it stays
    /// pinned flush and fully visible. When false the dock floats freely at (<see cref="FreeX"/>,
    /// <see cref="FreeY"/>).
    /// </summary>
    public bool Snapped { get; set; }

    public DockEdge Edge { get; set; } = DockEdge.Bottom;

    /// <summary>
    /// The dock's last top-left position in physical pixels. Kept up to date whether snapped or
    /// floating: when floating it is the free position; when snapped it records where the user
    /// dropped the dock so it re-appears at that spot (and on that monitor) rather than snapping
    /// back to the screen centre. Null only before the dock has ever been placed.
    /// </summary>
    public int? FreeX { get; set; }
    public int? FreeY { get; set; }

    /// <summary>When snapped, hide the dock behind the edge and reveal on hover.</summary>
    public bool AutoHide { get; set; } = true;

    /// <summary>
    /// Keep the dock above other windows while it is <b>floating</b> (not snapped). When snapped
    /// the dock is always topmost regardless, so the auto-hide reveal works over other windows.
    /// </summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>
    /// Puts the settings (gear) button and its divider at the leading edge of the strip instead of
    /// the trailing one. Off by default — the gear trails the tools, matching where the Windows 11
    /// taskbar's own overflow/system tray sits.
    /// </summary>
    public bool SettingsButtonAtStart { get; set; }
}
