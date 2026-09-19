using dockdev.Controls;
using dockdev.Models;
using dockdev.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace dockdev.ToolPages;

/// <summary>
/// The base of every tool. Splitting the page from its host window (<c>ToolWindowBase</c>) costs
/// one indirection today and means a future tabbed host is a host change, not a rewrite of every
/// tool (design doc §9.1).
/// </summary>
public abstract class ToolPage : UserControl
{
    public abstract ToolKind Kind { get; }

    /// <summary>True while the page holds content the user has not saved or copied out. Drives
    /// the close-confirmation.</summary>
    public abstract bool IsDirty { get; }

    /// <summary>Commands surfaced in the host's command bar and bound to accelerators.</summary>
    public abstract IReadOnlyList<ToolCommand> Commands { get; }

    /// <summary>The hosting <c>ToolWindowBase</c>'s window handle, set once at construction — the
    /// one thing a page needs to show a WinRT file picker (an unpackaged app must tell WinRT which
    /// window owns it; see <c>Services.FilePickers</c>).</summary>
    public nint HostHwnd { get; internal set; }

    /// <summary>Seed the page from a dropped file or a picker.</summary>
    public virtual Task LoadFileAsync(string path) => Task.CompletedTask;

    /// <summary>Text the page would contribute to the clipboard-aware launch chip check.</summary>
    public virtual bool AcceptsClipboardText(string text) => false;

    /// <summary>Loads clipboard text into the page in response to the "Paste clipboard" chip.</summary>
    public virtual void PasteClipboardText(string text) { }

    private readonly CancellationTokenSource _pageClosing = new();
    private readonly List<Action> _shutdownActions = [];
    private bool _closingNotified;

    /// <summary>Cancelled when the host window closes — every background parse ties to this.</summary>
    protected CancellationToken PageClosing => _pageClosing.Token;

    /// <summary>
    /// Registers work that must run when the host starts closing, even if this page was never
    /// loaded (so <c>Unloaded</c> will never fire). Used by flyouts and other objects that are
    /// not in the visual tree as <see cref="IWindowShutdown"/> controls.
    /// </summary>
    protected internal void RegisterShutdown(Action action)
    {
        if (_closingNotified)
        {
            try { action(); } catch { /* the window is already going */ }
            return;
        }
        _shutdownActions.Add(action);
    }

    /// <summary>
    /// Called by <c>ToolWindowBase</c> when the host window starts closing — from
    /// <c>AppWindow.Closing</c> while the content island is still up, and again from
    /// <c>Closed</c> as a no-op if that already ran.
    /// <para>
    /// First walks the live tree for <see cref="IWindowShutdown"/> so a <c>CodeEditor</c> timer
    /// is stopped before the rich-edit document it colours is destroyed (WinUI does not
    /// dependably raise <c>Unloaded</c> on a closing window's content). Then runs the registered
    /// callbacks and cancels <see cref="PageClosing"/> — the pages that own a
    /// <c>DispatcherQueueTimer</c> stop it there (see <c>DiffPage</c>, <c>TimestampPage</c>).
    /// </para>
    /// <para>
    /// <b>Not disposed, deliberately.</b> A <see cref="CancellationTokenSource"/> only holds an
    /// unmanaged resource once something arms its timer (<c>CancelAfter</c>) or asks for its
    /// <c>WaitHandle</c>; this one does neither, so it is plain managed memory collected with the
    /// page that owns it — there is nothing for a Dispose to release. Calling one anyway would be
    /// actively harmful: <c>RegexPage</c> reads <see cref="PageClosing"/> after awaiting its match,
    /// which is precisely the "the window closed underneath us" case, and reading <c>.Token</c> on
    /// a disposed source throws <see cref="ObjectDisposedException"/> — from an <c>async void</c>
    /// handler, where an exception is an unhandled one.
    /// </para>
    /// </summary>
    internal void NotifyClosing()
    {
        if (_closingNotified)
            return;
        _closingNotified = true;

        ShutdownSubtree(this);
        foreach (var action in _shutdownActions)
        {
            try { action(); }
            catch (Exception ex) { Diag.Log("ToolPage.NotifyClosing: " + ex.Message); }
        }
        _shutdownActions.Clear();
        try { _pageClosing.Cancel(); } catch { /* a registered callback threw; the window is going anyway */ }
    }

    private static void ShutdownSubtree(DependencyObject root)
    {
        try
        {
            if (root is IWindowShutdown shutdown)
                shutdown.Shutdown();

            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
                ShutdownSubtree(VisualTreeHelper.GetChild(root, i));
        }
        catch (Exception ex)
        {
            Diag.Log("ToolPage.ShutdownSubtree: " + ex.Message);
        }
    }
}
