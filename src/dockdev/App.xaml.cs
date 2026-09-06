using System.Threading;
using dockdev.Services;
using Microsoft.UI.Xaml;

namespace dockdev;

/// <summary>
/// Application entry point. Creates the <see cref="dockdevManager"/> (which in turn creates a window
/// per configured dock), enforces one dockdev per config (a second launch exits quietly), owns the
/// process lifetime (see the constructor: the event loop outlives every window, and only
/// <see cref="dockdevManager.Quit"/> ends it), and routes otherwise-unhandled exceptions to the
/// diagnostic log so a fault leaves a trace instead of taking the app down.
/// </summary>
public partial class App : Application
{
    /// <summary>The running docks and everything app-wide that goes with them.</summary>
    public static dockdevManager? Manager { get; private set; }

    // Held for the whole process lifetime so the named mutex it represents stays alive; a second
    // launch (e.g. the Start-menu entry while the "start with Windows" copy is already running)
    // sees the mutex already exists and bows out. Kept in a static field so the GC/finalizer
    // never closes the handle out from under a running dock. "Local\" scopes it per user session.
    private static Mutex? _instanceMutex;

    public App()
    {
        InitializeComponent();

        // Last-resort net for faults that never reach the XAML UnhandledException hook below —
        // a fault on a background thread, or during shutdown — so the log still records why the
        // process died instead of it vanishing silently.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Diag.Log($"FATAL APPDOMAIN UNHANDLED: {e.ExceptionObject}");
        };

        UnhandledException += (_, e) =>
        {
            // A dock is always-on: a fault in one window — a flyout, a theme change, one tool page
            // — must not take the other windows, the tray icon and the shortcuts with it. This
            // used to leave Handled false, on the reasoning that a genuinely unhandled fault
            // should surface; what it actually surfaced as, in a windowless process with no
            // console and no crash dialog, was the app disappearing. So it is recorded (to
            // %Temp%\dockdev.log) and handled. The individual throw sites are guarded at source
            // too — this is the net under them, not the plan.
            Diag.Log($"UNHANDLED: {e.Message}\n{e.Exception}");
            e.Handled = true;
        };

        // A faulted fire-and-forget Task never reaches the handler above, and .NET does not
        // rethrow unobserved exceptions by default, so today they are silent. Logging them is what
        // makes a failed update check or a failed icon decode visible in the log at all.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Diag.Log($"UNOBSERVED TASK: {e.Exception}");
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!IsFirstInstance())
        {
            Diag.Log("Another dockdev instance is already running — exiting this one.");
            Exit();
            return;
        }

        // The one line that decides whether dockdev can be a notification-area utility at all, and
        // the fix for "the app closes by itself".
        //
        // Application.Start sets DispatcherShutdownMode to OnLastWindowClose for this thread, so
        // the XAML runtime calls PostQuitMessage the moment the last XAML window on it closes.
        // dockdev normally has exactly one — the dock — so ANYTHING that closed it ended the whole
        // process, tray icon and global shortcuts with it, and with no window left there was
        // nothing on screen to say why. Alt+F4 while the dock has focus did it (the dock is
        // borderless, but it is still an ordinary top-level window, and DefWindowProc turns Alt+F4
        // into WM_CLOSE whether or not a caption is drawn); so did a fault in the dock's content;
        // so did the close-then-recreate a language change and a backup import both perform.
        //
        // A tray app has to outlive its windows, so the event loop becomes ours to end:
        // dockdevManager.Quit is the only thing that ends it, via Application.Exit.
        //
        // Set here rather than in the constructor because Application.Start assigns the default
        // itself and this is unambiguously afterwards — and below the instance check, so the
        // second-launch path above still exits through the default it has always used.
        DispatcherShutdownMode = Microsoft.UI.Xaml.DispatcherShutdownMode.OnExplicitShutdown;

        // Load the string table before any window is constructed: XAML resolves its
        // {loc:Localize} bindings as it loads, so the language has to be settled first. The
        // config is read once here and handed to the manager — loading it twice would give the
        // dock windows a different object from the one that gets saved.
        var config = DockStore.Load();
        Loc.Initialize(config.Language);

        Manager = new dockdevManager(config);
        Manager.Start();
    }

    /// <summary>
    /// True if this is the only running dockdev in the current session. Uses a named mutex: the
    /// first process creates it (and keeps it alive via <see cref="_instanceMutex"/>); any later
    /// process finds it already present and returns false.
    /// <para>
    /// The name is scoped to the data directory, so two copies pointed at the same dock still
    /// collapse to one (the case this exists for) while a copy running against its own data —
    /// the UI test harness, or a portable install on a removable drive — is a separate instance
    /// rather than one that silently refuses to start.
    /// </para>
    /// </summary>
    private static bool IsFirstInstance()
    {
        string name = @"Local\dockdev.SingleInstance.v1." + DataDirectoryKey();
        _instanceMutex = new Mutex(initiallyOwned: false, name, out bool createdNew);
        return createdNew;
    }

    /// <summary>A short, stable, path-character-free token identifying the data directory.</summary>
    private static string DataDirectoryKey()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            DockStore.DataDirectory.ToLowerInvariant());
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes), 0, 8);
    }
}
