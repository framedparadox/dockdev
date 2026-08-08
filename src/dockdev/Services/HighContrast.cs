namespace dockdev.Services;

/// <summary>
/// Whether Windows is currently running a High Contrast theme (design doc §13.3: "High Contrast
/// always wins"). Centralised because more than one control needs to suppress a custom
/// theme-dependent brush when the OS is providing its own guaranteed-contrast palette instead —
/// <see cref="Controls.CodeView"/> and <see cref="dockdev.DockWindow"/> each carried their own private
/// copy of this exact check, and <see cref="Controls.ToolStatusBar"/> carried none at all, which is
/// exactly the kind of drift a duplicated private helper invites.
/// </summary>
public static class HighContrast
{
    /// <summary>
    /// Guarded: if the setting is unavailable on this host, this assumes false and keeps the normal
    /// theme styling rather than throwing out of a render path.
    /// </summary>
    public static bool IsActive()
    {
        try { return new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast; }
        catch { return false; }
    }
}
