using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Windows.System.Power;
using Windows.UI;
using WinRT;

namespace dockdev.Services;

/// <summary>
/// Applies a Windows 11 taskbar-style acrylic ("glass") backdrop to a window.
///
/// Two things make this look like the real taskbar rather than a generic WinUI window:
///  1. The backdrop is forced <b>active</b> (<c>IsInputActive = true</c>) whenever the OS isn't in
///     Energy Saver. A dock is never the foreground window, and by default WinUI collapses acrylic
///     to a flat fallback color when its window is deactivated; forcing active keeps the glass
///     alive the rest of the time. It is the one place a visual effect keeps DWM compositing
///     continuously regardless of focus, so it steps aside — falling back to the flat
///     <see cref="AcrylicRecipe.Fallback"/> color, the same look every window gets when
///     unfocused — the moment the user has told Windows to conserve power (see
///     <see cref="UpdateEnergySaverState"/>), rather than treating "always" as unconditional.
///  2. A hand-tuned tint/luminosity recipe per theme, layered on the system "Base" acrylic,
///     so it reads like the shell's own material and follows the Windows light/dark theme.
/// </summary>
public sealed class AcrylicBackdropManager : IDisposable
{
    private readonly Window _window;
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _config;
    private FrameworkElement? _themeRoot;
    private bool _disposed;

    public AcrylicBackdropManager(Window window) => _window = window;

    /// <summary>Recipe used in dark mode. Tunable to match the Win11 dark taskbar exactly.</summary>
    public AcrylicRecipe Dark { get; set; } = new(
        Tint: Rgb(0x1C, 0x1C, 0x1C),
        TintOpacity: 0.55,
        LuminosityOpacity: 0.90,
        Fallback: Rgb(0x2C, 0x2C, 0x2C));

    /// <summary>Recipe used in light mode.</summary>
    public AcrylicRecipe Light { get; set; } = new(
        Tint: Rgb(0xF2, 0xF2, 0xF2),
        TintOpacity: 0.55,
        LuminosityOpacity: 0.90,
        Fallback: Rgb(0xF3, 0xF3, 0xF3));

    /// <summary>
    /// The recipe currently in force — whichever of <see cref="Dark"/> / <see cref="Light"/> the
    /// window's effective theme selects, personalization already folded in. Exposed so anything
    /// that has to paint the same glass <em>outside</em> this window can read the one recipe
    /// rather than keep a second copy of it: a flyout opens in its own popup window,
    /// which a system backdrop cannot reach, so it mixes a XAML acrylic from these numbers
    /// instead (see <c>DockWindow.BarBackground</c>).
    /// </summary>
    public AcrylicRecipe Current =>
        (_themeRoot?.ActualTheme ?? ElementTheme.Dark) == ElementTheme.Light ? Light : Dark;

    /// <returns>true if acrylic was applied; false if the OS/GPU can't support it.</returns>
    public bool TryApply()
    {
        if (!DesktopAcrylicController.IsSupported())
            return false;

        _config = new SystemBackdropConfiguration();
        UpdateEnergySaverState(); // sets IsInputActive from the current Energy Saver state
        PowerManager.EnergySaverStatusChanged += OnEnergySaverStatusChanged;

        _themeRoot = _window.Content as FrameworkElement;
        if (_themeRoot is not null)
            _themeRoot.ActualThemeChanged += OnThemeChanged;

        _controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Base,
        };

        UpdateTheme(); // sets config.Theme + applies the matching recipe

        _controller.SetSystemBackdropConfiguration(_config);
        _controller.AddSystemBackdropTarget(_window.As<ICompositionSupportsSystemBackdrop>());
        return true;
    }

    private void OnThemeChanged(FrameworkElement sender, object args) => UpdateTheme();

    /// <summary>
    /// PowerManager raises this off the UI thread — a thread-pool thread — so the actual update is
    /// marshalled back to the window's own queue, where the composition objects it touches are
    /// safe to use.
    /// <para>
    /// Guarded, and that guard is load-bearing rather than defensive. An exception on a thread-pool
    /// thread is not something <c>Application.UnhandledException</c> ever sees: it ends the
    /// process, with no window left to say so. Everything needed for that is here — this fires
    /// whenever the user's battery saver switches on or off, which on a laptop means every unplug,
    /// and the window it reaches for can close (and this manager be disposed) in between, leaving
    /// a <c>Window.DispatcherQueue</c> whose native side has gone. That is a machine-dependent,
    /// once-in-a-while, nothing-in-the-log disappearance, which is the worst kind to chase.
    /// </para>
    /// </summary>
    private void OnEnergySaverStatusChanged(object? sender, object e)
    {
        if (_disposed)
            return;
        try
        {
            _window.DispatcherQueue.TryEnqueue(UpdateEnergySaverState);
            _window.DispatcherQueue?.TryEnqueue(UpdateEnergySaverState);
        }
        catch (Exception ex)
        {
            // The window is going or gone; the glass it would have re-tinted is going with it.
            Diag.Log("AcrylicBackdropManager: energy-saver update dropped: " + ex.Message);
        }
    }

    /// <summary>
    /// Ties <c>IsInputActive</c> to Energy Saver rather than leaving it permanently on: active
    /// keeps DWM compositing the glass continuously since the dock is never focused, which is the
    /// tradeoff this class exists to make, but it stops being worth making the moment the user has
    /// told Windows they want to conserve power. <see cref="UpdateTheme"/> still runs on top of
    /// whichever state this leaves the config in, so the fallback color is theme-correct too.
    /// </summary>
    private void UpdateEnergySaverState()
    {
        if (_config is null)
        if (_disposed || _config is null)
            return;
        _config.IsInputActive = PowerManager.EnergySaverStatus != EnergySaverStatus.On;
        try
        {
            _config.IsInputActive =
                PowerManager.EnergySaverStatus != EnergySaverStatus.On;
        }
        catch (Exception ex)
        {
            Diag.Log("AcrylicBackdropManager: energy-saver probe failed: " + ex.Message);
        }
    }

    /// <summary>Pushes <paramref name="r"/> onto the live controller.</summary>
    private void SetRecipe(AcrylicRecipe r)
    {
        if (_controller is null)
            return;
        _controller.TintColor = r.Tint;
        _controller.TintOpacity = (float)r.TintOpacity;
        _controller.LuminosityOpacity = (float)r.LuminosityOpacity;
        _controller.FallbackColor = r.Fallback;
    }

    /// <summary>
    /// Re-applies the current recipes. <see cref="Dark"/> and <see cref="Light"/> are plain
    /// properties, so assigning one changes what the <em>next</em> theme update would use but
    /// leaves the live controller alone; the personalization settings (glass opacity, accent
    /// tint) need it to take effect now.
    /// </summary>
    public void Refresh() => UpdateTheme();

    /// <summary>
    /// Re-tints both recipes for the given personalization settings, and applies them.
    /// </summary>
    /// <param name="luminosityOpacity">How frosted the glass is (0.3–1.0).</param>
    /// <param name="accentTint">Tint with the Windows accent color rather than the neutral grey
    /// the taskbar uses. The accent is darkened for the dark recipe and lightened for the light
    /// one, because the raw accent at full strength overwhelms a 40px strip of icons.</param>
    public void Personalize(double luminosityOpacity, bool accentTint)
    {
        double luminosity = Math.Clamp(luminosityOpacity, 0.3, 1.0);
        var darkTint = accentTint ? Blend(AccentColor(), Rgb(0x00, 0x00, 0x00), 0.55) : Rgb(0x1C, 0x1C, 0x1C);
        var lightTint = accentTint ? Blend(AccentColor(), Rgb(0xFF, 0xFF, 0xFF), 0.60) : Rgb(0xF2, 0xF2, 0xF2);

        Dark = Dark with { Tint = darkTint, LuminosityOpacity = luminosity };
        Light = Light with { Tint = lightTint, LuminosityOpacity = luminosity };
        Refresh();
    }

    /// <summary>The Windows accent color, or dockdev's fallback blue if it can't be read.</summary>
    private static Color AccentColor()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings()
                .GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        }
        catch
        {
            return Rgb(0x00, 0x78, 0xD4);
        }
    }

    /// <summary>Mixes <paramref name="color"/> toward <paramref name="toward"/> by
    /// <paramref name="amount"/> (0 = unchanged, 1 = fully the other color).</summary>
    private static Color Blend(Color color, Color toward, double amount)
    {
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromArgb(255, Mix(color.R, toward.R), Mix(color.G, toward.G), Mix(color.B, toward.B));
    }

    private void UpdateTheme()
    {
        if (_controller is null || _config is null)
            return;

        var theme = _themeRoot?.ActualTheme ?? ElementTheme.Dark;
        bool dark = theme != ElementTheme.Light;

        _config.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

        var r = dark ? Dark : Light;
        _controller.TintColor = r.Tint;
        _controller.TintOpacity = (float)r.TintOpacity;
        _controller.LuminosityOpacity = (float)r.LuminosityOpacity;
        _controller.FallbackColor = r.Fallback;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_themeRoot is not null)
            _themeRoot.ActualThemeChanged -= OnThemeChanged;
        PowerManager.EnergySaverStatusChanged -= OnEnergySaverStatusChanged;

        _controller?.Dispose();
        _controller = null;
        if (_controller is not null)
        {
            try
            {
                _controller.RemoveAllSystemBackdropTargets();
                _controller.ResetProperties();
                _controller.Dispose();
            }
            catch { /* ignore */ }
            _controller = null;
        }
        _config = null;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
}

/// <summary>A tunable acrylic material recipe.</summary>
/// <param name="Tint">Base tint color.</param>
/// <param name="TintOpacity">How strongly the tint color is applied (0..1).</param>
/// <param name="LuminosityOpacity">Frostiness — the taskbar leans high here (0..1).</param>
/// <param name="Fallback">Solid color used when composition acrylic is unavailable.</param>
public readonly record struct AcrylicRecipe(
    Color Tint,
    double TintOpacity,
    double LuminosityOpacity,
    Color Fallback);
