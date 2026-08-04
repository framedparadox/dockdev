using DevDX.Models;
using Microsoft.UI.Xaml.Controls;

namespace DevDX.ToolPages;

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

    /// <summary>Cancelled when the host window closes — every background parse ties to this.</summary>
    protected CancellationToken PageClosing => _pageClosing.Token;

    /// <summary>Called by <c>ToolWindowBase</c> when the host window closes.</summary>
    internal void NotifyClosing()
    {
        try { _pageClosing.Cancel(); } catch { /* already disposed */ }
    }
}
