using Xunit;

namespace dockdev.Tests.Shell;

/// <summary>
/// The rules that keep dockdev running.
/// <para>
/// A WinUI desktop app's XAML runtime calls <c>PostQuitMessage</c> when the last XAML window on a
/// thread closes — <c>Application.Start</c> sets <c>DispatcherShutdownMode</c> to
/// <c>OnLastWindowClose</c> for exactly that. dockdev normally has one window, the dock, so under
/// that default anything that closed the dock ended the process: Alt+F4 while it had focus, a
/// fault in its content, or the close-and-recreate a language change and a backup import both
/// perform. The app disappeared, tray icon included, with nothing on screen to say why.
/// </para>
/// <para>
/// Both halves of the fix are a single line each in a file nobody reads twice, and both are the
/// kind of line a later edit removes without noticing — so they are asserted here, the same way
/// <c>NetworkPolicyArchitectureTests</c> asserts the <c>HttpClient</c> rule.
/// </para>
/// </summary>
public class ProcessLifetimeArchitectureTests
{
    [Fact]
    public void AppTakesOwnershipOfTheEventLoop()
    {
        var app = File.ReadAllText(Path.Combine(FindSourceRoot(), "App.xaml.cs"));

        Assert.True(
            app.Contains("DispatcherShutdownMode.OnExplicitShutdown", StringComparison.Ordinal),
            "App must set DispatcherShutdownMode to OnExplicitShutdown. Without it the XAML " +
            "runtime exits the process when the last window closes, and dockdev — a notification-" +
            "area utility whose only durable window is the dock — closes itself whenever the dock " +
            "goes.");
    }

    [Fact]
    public void OnlyTheManagerEndsTheProcess()
    {
        var srcRoot = FindSourceRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) is "dockdevManager.cs")
                continue;

            if (File.ReadAllText(file).Contains("Application.Current.Exit(", StringComparison.Ordinal))
                offenders.Add(Path.GetRelativePath(srcRoot, file));
        }

        Assert.True(offenders.Count == 0,
            "Quitting is dockdevManager.Quit's job — it releases the tray icon and the global " +
            "shortcuts first, and tells the dock it may close. These files exit the app behind " +
            "its back: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheDockRefusesACloseItDidNotAskFor()
    {
        var dock = File.ReadAllText(Path.Combine(FindSourceRoot(), "DockWindow.xaml.cs"));

        Assert.True(
            dock.Contains("_appWindow.Closing += OnAppWindowClosing", StringComparison.Ordinal),
            "DockWindow must handle AppWindow.Closing: the dock is borderless but still an " +
            "ordinary top-level window, so Alt+F4 reaches it.");

        Assert.True(
            dock.Contains("args.Cancel = true", StringComparison.Ordinal) &&
            dock.Contains("HideDockOnCloseRequest", StringComparison.Ordinal),
            "An unrequested close must be cancelled and turned into a hide, so the dock can be " +
            "summoned back from the tray icon or the shortcut instead of being destroyed.");
    }

    /// <summary>Walks up from the test binary's output directory to find <c>src/dockdev</c>, the same
    /// way <c>NetworkPolicyArchitectureTests</c> does.</summary>
    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "dockdev");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate src/dockdev from " + AppContext.BaseDirectory);
    }
}
