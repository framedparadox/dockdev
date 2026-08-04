using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Xunit;

namespace DevDX.UITests;

/// <summary>
/// One running DevDX, shared by every test in the collection.
/// <para>
/// Launching the app is the expensive part of a UI test — process start, WinUI type activation and
/// the Mica controller together cost seconds — so it happens once and the tests take turns. What
/// makes that safe is that each test opens its own tool window and closes it again; nothing shares
/// state but the dock itself.
/// </para>
/// <para>
/// The session is pointed at a throwaway data directory via <c>DEVDX_DATA_DIR</c> and seeds a
/// config into it before launch. That does three jobs at once: it keeps the tests off the
/// signed-in user's real dock, it pins the language to English so assertions can be written
/// against known strings, and it puts every tool on the strip so the table below has something to
/// click for each row. It also means the single-instance mutex, which is scoped to the data
/// directory, does not collide with a DevDX the developer already has running.
/// </para>
/// </summary>
public sealed class DevDxSession : IDisposable
{
    /// <summary>How long to wait for a window or element to turn up. Generous: a cold first tool
    /// window pays for XAML type activation, and a machine under load pays more.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public UIA3Automation Automation { get; }
    public Application Application { get; }
    public string DataDirectory { get; }

    /// <summary>
    /// The dock, re-resolved on every access rather than captured once.
    /// <para>
    /// Caching it looks obviously right and is obviously wrong: DevDX rebuilds windows in response
    /// to settings changes — a theme switch, a language change — and the moment it does, a held
    /// element reference points at a destroyed window. UI Automation does not throw for that. It
    /// returns an <em>empty subtree</em>, so every later lookup simply finds nothing and every test
    /// after the one that changed a setting fails with "no dock icon", twenty seconds at a time,
    /// pointing at the wrong culprit. Re-resolving costs a few milliseconds and makes the whole
    /// class of failure impossible.
    /// </para>
    /// </summary>
    public Window Dock =>
        Retry(FindDock) ?? throw new InvalidOperationException(
            "The dock is not there any more. " + Describe());

    public DevDxSession()
    {
        var executable = ResolveExecutable();

        DataDirectory = Path.Combine(
            Path.GetTempPath(), "devdx-uitests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(DataDirectory);
        SeedConfig(DataDirectory);

        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        info.EnvironmentVariables["DEVDX_DATA_DIR"] = DataDirectory;

        Automation = new UIA3Automation();
        Application = Application.Launch(info);

        // Not stored — this is the "did it start at all?" check, and it fails here with a message
        // about startup rather than leaving every later test to fail with a message about the dock.
        _ = Retry(FindDock) ?? throw new InvalidOperationException(
            "The dock window never appeared. " + Describe() + " Check %TEMP%\\devdx.log.");
    }

    /// <summary>
    /// Every top-level window this DevDX owns.
    /// <para>
    /// Deliberately not FlaUI's <c>Application.GetAllTopLevelWindows</c>, which filters desktop
    /// children by <c>ControlType.Window</c>. DevDX's windows are borderless and extend their
    /// content into the title bar, and UI Automation reports such a window as a <b>Pane</b> — so
    /// that helper returns an empty array for an app that is plainly on screen. Filtering on the
    /// process id alone is what actually finds them.
    /// </para>
    /// </summary>
    public Window[] TopLevelWindows()
    {
        AutomationElement[] children;
        try
        {
            children = Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(Application.ProcessId));
        }
        catch (Exception)
        {
            // The desktop's child list changed while it was being walked. Nothing to report — the
            // caller is inside a Retry and will ask again.
            return [];
        }

        // Materialized one at a time and skipping the ones that throw: the automation tree is live,
        // so a window closing at this instant — which is every moment right after a test closes its
        // tool window — makes reads on it fail. Losing a dying window from the list is correct;
        // letting its exception escape and fail the caller is not.
        var windows = new List<Window>(children.Length);
        foreach (var child in children)
        {
            try
            {
                var window = child.AsWindow();
                _ = window.Properties.NativeWindowHandle.Value; // forces the read that would throw
                windows.Add(window);
            }
            catch (Exception)
            {
                // Gone between the enumeration and now.
            }
        }
        return [.. windows];
    }

    /// <summary>
    /// Closes a window by posting <c>WM_CLOSE</c> to it.
    /// <para>
    /// Not FlaUI's <c>Window.Close()</c>, which needs the <c>WindowPattern</c> and throws "Close is
    /// not supported" on DevDX's borderless windows — the same reason those windows report as Panes
    /// rather than Windows. <c>WM_CLOSE</c> is what the title-bar X sends anyway, so this drives
    /// exactly the path a user does, including the dirty-close confirmation.
    /// </para>
    /// <para>
    /// Posted rather than sent: a window that answers with a modal dialog would block a synchronous
    /// send until the dialog closed, and the whole point of the dirty-close test is to still be
    /// running while that dialog is up.
    /// </para>
    /// </summary>
    public static void CloseWindow(Window window)
    {
        const uint WM_CLOSE = 0x0010;
        try
        {
            PostMessage(window.Properties.NativeWindowHandle.Value, WM_CLOSE, nint.Zero, nint.Zero);
        }
        catch (Exception)
        {
            // Already gone. Closing something that has closed is the outcome we wanted.
        }
    }

    /// <summary>
    /// Closes a window and waits for it to actually be gone.
    /// <para>
    /// <c>WM_CLOSE</c> is posted, not sent, so <see cref="CloseWindow"/> returns while the window is
    /// still up. That is fine when the test is finished with it and wrong when the next test is
    /// about to enumerate windows: it snapshots a set that still contains the closing window, and
    /// then has to guess which of the ones that appear afterwards is the one it asked for. Tests
    /// that leave a window behind have to settle before handing the app on.
    /// </para>
    /// </summary>
    public void CloseAndWait(Window window)
    {
        nint handle;
        try { handle = window.Properties.NativeWindowHandle.Value; }
        catch (Exception) { return; } // already gone

        CloseWindow(window);
        RetryUntil(() => !TopLevelWindowHandles().Contains(handle), TimeSpan.FromSeconds(5));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>
    /// Presses a button, tolerating the transient failures UI Automation hands back under load —
    /// a stale element reference surfaces as <c>ArgumentException</c> ("value does not fall within
    /// the expected range") or a <c>COMException</c>, neither of which means the button is broken.
    /// Falls back to a real click, which goes through a different path in the automation stack and
    /// succeeds when <c>Invoke</c> will not.
    /// </summary>
    public static void Press(AutomationElement element)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                element.AsButton().Invoke();
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
            catch (Exception)
            {
                element.Click();
                return;
            }
        }
    }

    /// <summary>
    /// The dock, identified by the settings button inside it rather than by the window's own title
    /// or automation id. A WinUI 3 desktop window exposes neither usefully — the id belongs to the
    /// root Grid, not the window, and every window in the app answers to the title "DevDX".
    /// <para>
    /// The button, specifically, and not the <c>ItemsRepeater</c> that holds the icons: a repeater
    /// is a layout container, and UI Automation's control view leaves those out. A <c>Button</c> is
    /// in the control view by construction, which is the same reason the tool icons themselves are
    /// findable — they are Buttons too.
    /// </para>
    /// </summary>
    private Window? FindDock() =>
        TopLevelWindows()
            .FirstOrDefault(w => w.FindFirstDescendant(cf => cf.ByAutomationId("DevDXSettingsButton")) is not null);

    /// <summary>What was actually on screen, for a failure message that can be acted on rather than
    /// re-run under a debugger.</summary>
    private string Describe()
    {
        try
        {
            var mine = TopLevelWindows();
            var everything = Automation.GetDesktop().FindAllChildren()
                .Select(e =>
                {
                    try { return $"'{e.Name}' pid={e.Properties.ProcessId.ValueOrDefault}"; }
                    catch { return "<unreadable>"; }
                })
                .ToArray();

            var contents = mine.Select(w =>
            {
                var ids = w.FindAllDescendants()
                    .Select(e => e.AutomationId)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct()
                    .Take(40);
                return $"'{w.Name}' contains [{string.Join(", ", ids)}]";
            });

            return $"pid={Application.ProcessId} exited={Application.HasExited} " +
                   $"ownWindows={mine.Length} -> {string.Join(" ;; ", contents)}. " +
                   $"Desktop children: {string.Join(" | ", everything)}";
        }
        catch (Exception ex)
        {
            return $"Could not enumerate windows: {ex.GetType().Name}: {ex.Message}.";
        }
    }

    /// <summary>
    /// The DevDX to drive. <c>DEVDX_EXE</c> wins; otherwise the Release build in the source tree,
    /// then the Debug one. Explicit rather than searched-for beyond that: silently testing a
    /// months-old binary because it happened to be on disk is worse than not running.
    /// </summary>
    private static string ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("DEVDX_EXE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!File.Exists(configured))
                throw new FileNotFoundException($"DEVDX_EXE points at a file that isn't there: {configured}");
            return configured;
        }

        var root = RepositoryRoot();
        const string Tfm = "net10.0-windows10.0.26100.0";
        string[] candidates =
        [
            Path.Combine(root, "src", "DevDX", "bin", "x64", "Release", Tfm, "win-x64", "DevDX.exe"),
            Path.Combine(root, "src", "DevDX", "bin", "Release", Tfm, "win-x64", "DevDX.exe"),
            Path.Combine(root, "src", "DevDX", "bin", "Debug", Tfm, "win-x64", "DevDX.exe"),
        ];

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "No DevDX.exe found. Build the app first, or set DEVDX_EXE. Looked in:" +
                Environment.NewLine + string.Join(Environment.NewLine, candidates));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "DevDX")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Writes the dock.json the session runs against: English, every tool pinned, auto-hide off.
    /// <para>
    /// Auto-hide matters most. The dock slides behind the screen edge on a timer, and an icon that
    /// has slid off is an icon UI automation can find but not click — the click lands on whatever
    /// is underneath. Turning it off is the difference between a suite that passes and one that
    /// fails on whichever test happened to run after the pointer left the strip.
    /// </para>
    /// <para>
    /// Built as JSON text rather than by referencing the app's model types, because this project
    /// deliberately has no reference to DevDX. The shape is small and the app's loader tolerates
    /// anything it does not recognise, so writing it out by hand costs less than the coupling would.
    /// <b>PascalCase throughout</b>: <c>DockStore</c> serializes with no naming policy and
    /// <c>System.Text.Json</c> matches case-sensitively by default, so a camelCase key here is not
    /// a near miss — it is silently ignored, and the app starts on its seeded defaults with
    /// auto-hide back on. That failure looks exactly like a flaky test.
    /// </para>
    /// </summary>
    private static void SeedConfig(string dataDirectory)
    {
        var items = new JsonArray();
        foreach (var kind in ToolKinds)
        {
            items.Add(new JsonObject
            {
                ["Id"] = Guid.NewGuid().ToString("N"),
                ["Kind"] = kind,
                ["DisplayName"] = kind,
                ["Hidden"] = false,
            });
        }

        var config = new JsonObject
        {
            ["Language"] = "en",
            ["Theme"] = "Dark",
            ["LaunchAtStartup"] = false,
            ["HotkeyEnabled"] = false,
            ["ItemHotkeysEnabled"] = false,
            ["ReuseToolWindows"] = false,
            // Seeded, so the app does not decide this is a first run and replace the item list with
            // its own default dock.
            ["Seeded"] = true,
            ["Dock"] = new JsonObject
            {
                ["Id"] = Guid.NewGuid().ToString("N"),
                ["Items"] = items,
                ["Snapped"] = false,
                ["AutoHide"] = false,
                ["AlwaysOnTop"] = true,
                ["FreeX"] = 80,
                ["FreeY"] = 80,
            },
        };

        File.WriteAllText(
            Path.Combine(dataDirectory, "dock.json"),
            config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The launchable kinds, spelled as the enum serializes them. Separator is not one —
    /// it has nothing to open.</summary>
    public static readonly string[] ToolKinds =
    [
        "Json", "DataFormatter", "Xml", "DataConverter", "Base64", "DataMasker", "Jwt",
        "TextToolkit", "TextDiff", "RegexTester", "UrlEncoding",
        "Hash", "Uuid", "Timestamp", "NumberBase",
        "Color", "Lorem", "Password", "Cron",
    ];

    // ---- Driving ------------------------------------------------------------

    /// <summary>
    /// Clicks the dock icon for <paramref name="kind"/> and returns the tool window it opened.
    /// <para>
    /// The window is found by waiting for a <em>new</em> top-level window rather than by name: the
    /// title is the tool's translated display name, and matching on it would tie every row of the
    /// table to the English string table.
    /// </para>
    /// </summary>
    public Window OpenTool(string kind) =>
        WaitForNewWindow(
            () =>
            {
                var icon = Retry(() => Dock.FindFirstDescendant(
                    cf => cf.ByAutomationId("DockItem." + kind)))
                    ?? throw new InvalidOperationException(
                        $"No dock icon for '{kind}'. The dock offers: {DockContents()}");
                Press(icon);
            },
            $"opening '{kind}'");

    /// <summary>
    /// Runs <paramref name="trigger"/> and returns the top-level window that appeared because of it.
    /// <para>
    /// Identifying a new window by its handle not being in the before-set, rather than by something
    /// inside it, is what makes this work for windows whose contents are still loading — and it
    /// avoids keying on a translated title or on an inner automation id that may not be in the
    /// automation tree yet, which is what made the settings window "never appear" when it was
    /// plainly on screen.
    /// </para>
    /// </summary>
    public Window WaitForNewWindow(Action trigger, string what)
    {
        var before = TopLevelWindowHandles();
        trigger();

        return Retry(() => TopLevelWindows()
                .FirstOrDefault(w => !before.Contains(w.Properties.NativeWindowHandle.Value)))
            ?? throw new InvalidOperationException($"No new window appeared while {what}.");
    }

    private HashSet<nint> TopLevelWindowHandles() =>
        TopLevelWindows().Select(w => w.Properties.NativeWindowHandle.Value).ToHashSet();

    /// <summary>Every automation id and button name currently on the dock, so "no dock icon for X"
    /// says what there was instead rather than only what was wanted.</summary>
    private string DockContents()
    {
        try
        {
            var descendants = Dock.FindAllDescendants().Select(e =>
            {
                try
                {
                    var id = e.AutomationId;
                    return string.IsNullOrEmpty(id) ? $"<{e.ControlType}:{e.Name}>" : id;
                }
                catch { return "<unreadable>"; }
            });
            return string.Join(", ", descendants.Take(60));
        }
        catch (Exception ex)
        {
            return $"<could not read the dock: {ex.Message}>";
        }
    }

    /// <summary>
    /// Polls <paramref name="probe"/> until it returns something, or the timeout runs out.
    /// <para>
    /// Every wait in this suite goes through here rather than through a sleep. A fixed sleep is
    /// either too short on a loaded machine — which is a flake, the thing that kills a UI suite's
    /// credibility — or too long on a fast one, paid on every single case.
    /// </para>
    /// </summary>
    public static T? Retry<T>(Func<T?> probe, TimeSpan? timeout = null) where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? Timeout);
        while (true)
        {
            try
            {
                if (probe() is { } value)
                    return value;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                // An element that is mid-teardown throws rather than returning null. Only
                // interesting if it is still throwing when the clock runs out.
            }

            if (DateTime.UtcNow >= deadline)
                return null;
            Thread.Sleep(100);
        }
    }

    /// <summary>Waits for a condition to become true.</summary>
    public static bool RetryUntil(Func<bool> condition, TimeSpan? timeout = null) =>
        Retry(() => condition() ? "" : null, timeout) is not null;

    public void Dispose()
    {
        try
        {
            Application.Close();
            if (!Application.WaitWhileMainHandleIsMissing(TimeSpan.FromSeconds(2)))
                Application.Kill();
        }
        catch
        {
            try { Application.Kill(); } catch { /* already gone */ }
        }

        Automation.Dispose();
        try { Directory.Delete(DataDirectory, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>
/// One app launch for the whole suite. xUnit gives every collection its own fixture instance, so
/// putting all the UI tests in one collection is what keeps it to a single DevDX rather than one
/// per test class — and, just as importantly, stops two of them driving the desktop at once.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DevDxCollection : ICollectionFixture<DevDxSession>
{
    public const string Name = "DevDX UI";
}

/// <summary>
/// Skips the whole suite unless <c>DEVDX_UITESTS</c> is set. These need a real, visible, logged-in
/// desktop; on a headless agent they would fail for reasons that say nothing about the code, and a
/// suite that cries wolf gets ignored. <c>scripts/run-ui-tests.ps1</c> sets it.
/// </summary>
public sealed class UIFactAttribute : FactAttribute
{
    public UIFactAttribute()
    {
        if (!Enabled)
            Skip = "Set DEVDX_UITESTS=1 to run the UI tests (they need an interactive desktop).";
    }

    public static bool Enabled =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DEVDX_UITESTS"));
}

/// <inheritdoc cref="UIFactAttribute"/>
public sealed class UITheoryAttribute : TheoryAttribute
{
    public UITheoryAttribute()
    {
        if (!UIFactAttribute.Enabled)
            Skip = "Set DEVDX_UITESTS=1 to run the UI tests (they need an interactive desktop).";
    }
}
