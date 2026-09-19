using Xunit;

namespace dockdev.Tests.Shell;

/// <summary>
/// The rules that keep a closing window from taking the process with it.
/// <para>
/// A sibling WinUI dock crashed in <c>Microsoft.UI.Xaml.dll</c> at
/// <c>GetValueByKnownIndex_enum … FlowDirection_</c> — a <c>StowedException</c> with no
/// managed frames. dockdev itself has already died once from a highlight tick after a window
/// close, in a native rich-edit access violation that likewise never reached
/// <c>App.UnhandledException</c>. Both are the same class of fault: a timer, theme handler or
/// repeater still touching XAML after the content island has gone. These tests pin the
/// shutdown protocol that stops that, the same way
/// <c>ProcessLifetimeArchitectureTests</c> pins the event-loop rule.
/// </para>
/// </summary>
public class CrashLifetimeArchitectureTests
{
    [Fact]
    public void InheritedThemeReadsGoThroughXamlLifetime()
    {
        var lifetime = File.ReadAllText(Path.Combine(FindSourceRoot(), "Services", "XamlLifetime.cs"));

        Assert.True(
            lifetime.Contains("TryGetActualTheme", StringComparison.Ordinal) &&
            lifetime.Contains("FlowDirection", StringComparison.Ordinal),
            "XamlLifetime must be the one guarded ActualTheme/FlowDirection read. A raw " +
            "ActualTheme getter after the island is gone is the sibling-app StowedException.");
    }

    [Fact]
    public void TheDockTearsDownUiBeforeTheIslandDies()
    {
        var dock = File.ReadAllText(Path.Combine(FindSourceRoot(), "DockWindow.xaml.cs"));

        Assert.True(
            dock.Contains("private void TeardownUi()", StringComparison.Ordinal),
            "DockWindow must have a single TeardownUi that stops timers, dismisses tips, " +
            "disposes the backdrop and detaches the ItemsRepeater.");

        var closing = IndexOfOrFail(dock, "private void OnAppWindowClosing");
        var teardownFromClosing = dock.IndexOf("TeardownUi()", closing, StringComparison.Ordinal);
        var cancel = dock.IndexOf("args.Cancel = true", closing, StringComparison.Ordinal);

        Assert.True(
            teardownFromClosing >= 0 && teardownFromClosing < cancel,
            "OnAppWindowClosing must call TeardownUi on the AllowClose path, before the " +
            "island is gone. Doing the same work only from Closed is what made " +
            "ItemsRepeater / ActualTheme read FlowDirection off a dead peer.");
    }

    [Fact]
    public void CodeEditorStopsHighlightingFromShutdownNotOnlyUnloaded()
    {
        var editor = File.ReadAllText(Path.Combine(FindSourceRoot(), "Controls", "CodeEditor.cs"));

        Assert.True(
            editor.Contains("IWindowShutdown", StringComparison.Ordinal) &&
            editor.Contains("public void Shutdown()", StringComparison.Ordinal) &&
            editor.Contains("if (_shutdown)", StringComparison.Ordinal) &&
            editor.Contains("TryGetActualTheme", StringComparison.Ordinal),
            "CodeEditor must implement IWindowShutdown, no-op Highlight after Shutdown, and " +
            "not read ActualTheme bare. Unloaded alone is not a reliable close hook.");

        Assert.True(
            editor.Contains("Unloaded += (_, _) => Shutdown()", StringComparison.Ordinal),
            "Unloaded should still call Shutdown as a second belt, but it must not be the only one.");
    }

    [Fact]
    public void ToolPagesShutDownTheLiveTreeWhenTheHostStartsClosing()
    {
        var page = File.ReadAllText(Path.Combine(FindSourceRoot(), "ToolPages", "ToolPage.cs"));
        var host = File.ReadAllText(Path.Combine(FindSourceRoot(), "ToolWindows", "ToolWindowBase.cs"));

        Assert.True(
            page.Contains("IWindowShutdown", StringComparison.Ordinal) &&
            page.Contains("ShutdownSubtree", StringComparison.Ordinal) &&
            page.Contains("if (_closingNotified)", StringComparison.Ordinal),
            "ToolPage.NotifyClosing must walk the live tree for IWindowShutdown exactly once.");

        Assert.True(
            host.Contains("BeginUiClose()", StringComparison.Ordinal) &&
            host.Contains("Page.NotifyClosing()", StringComparison.Ordinal),
            "ToolWindowBase must begin UI close from AppWindow.Closing, not only from Closed.");

        var onClosing = IndexOfOrFail(host, "private async void OnClosing");
        Assert.True(
            host.IndexOf("BeginUiClose()", onClosing, StringComparison.Ordinal) >= 0,
            "OnClosing must call BeginUiClose on the path that actually lets the window go, " +
            "so editors stop before the rich-edit document is destroyed.");
    }

    [Fact]
    public void SettingsDoesNotRebuildAfterItHasClosed()
    {
        var settings = File.ReadAllText(Path.Combine(FindSourceRoot(), "SettingsWindow.xaml.cs"));

        Assert.True(
            settings.Contains("if (_closed)", StringComparison.Ordinal) &&
            settings.Contains("OnDockItemsChanged", StringComparison.Ordinal) &&
            settings.Contains("TryGetActualTheme", StringComparison.Ordinal),
            "SettingsWindow queues rebuilds from ItemsChanged/DockChanged. Those callbacks " +
            "must no-op after Closed, or they touch a torn-down tree.");
    }

    [Fact]
    public void ThemeHandlersInControlsRefuseATornDownPeer()
    {
        foreach (var relative in new[]
        {
            Path.Combine("Controls", "CodeView.cs"),
            Path.Combine("Controls", "ToolStatusBar.cs"),
            Path.Combine("Services", "AcrylicBackdropManager.cs"),
            Path.Combine("AddToolWindow.xaml.cs"),
        })
        {
            var text = File.ReadAllText(Path.Combine(FindSourceRoot(), relative));
            Assert.True(
                text.Contains("TryGetActualTheme", StringComparison.Ordinal) ||
                text.Contains("if (!_shutdown)", StringComparison.Ordinal) ||
                text.Contains("if (!_disposed)", StringComparison.Ordinal) ||
                text.Contains("if (!_closed)", StringComparison.Ordinal),
                relative + " still reads theme state without a lifetime guard.");
        }
    }

    private static int IndexOfOrFail(string text, string needle)
    {
        int i = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(i >= 0, "Expected to find '" + needle + "'");
        return i;
    }

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
