using System.Runtime.InteropServices;
using dockdev.Interop;

namespace dockdev.Services;

/// <summary>
/// A hidden, never-shown HWND that exists only to receive window messages on the UI thread —
/// the tray icon's callbacks and <c>WM_HOTKEY</c> from <c>RegisterHotKey</c>, neither of which a
/// WinUI 3 <c>Window</c> can surface. Create it on the UI thread so its messages are pumped by
/// the app's own message loop, and dispose it before the app exits.
/// </summary>
public sealed class MessageWindow : IDisposable
{
<<<<<<< HEAD
    // Unique per instance (not just per process): a GUID means recreating the message window within
    // one process — which the manager does — cannot collide with a class the previous instance has
    // not finished unregistering, so RegisterClassEx never fails on a stale registration.
=======
    // Unique per process: two copies of dockdev in one session are already prevented by the instance mutex,
    // but a stale class registration from a previous AppDomain would make RegisterClassEx fail.
    private static readonly string ClassName = "dockdev.MessageWindow." + Environment.ProcessId;
    // Unique per instance: prevents RegisterClassEx failure when recreated within the same process.
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
    private readonly string _className = $"dockdev.MessageWindow.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly nint _hInstance;

    // The delegate is handed to Win32 as a raw function pointer, so it must be rooted for as long
    // as the window lives or the GC will collect it out from under the message pump.
    private readonly NativeMethods.WndProc _wndProc;
    private bool _disposed;

    /// <summary>The window handle. <see cref="nint.Zero"/> if creation failed.</summary>
    public nint Handle { get; }

    /// <summary>
    /// Raised for every message the window receives, on the UI thread. Handlers run before
    /// <c>DefWindowProc</c> and are expected to be quick and non-throwing.
    /// </summary>
    public event Action<uint, nint, nint>? MessageReceived;

    public MessageWindow()
    {
        _wndProc = OnMessage;
        _hInstance = NativeMethods.GetModuleHandle(null);

        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
<<<<<<< HEAD
=======
            hInstance = instance,
            lpszClassName = ClassName,
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
            hInstance = _hInstance,
            lpszClassName = _className,
        };

        if (NativeMethods.RegisterClassEx(ref wc) == 0)
        {
            Diag.Log($"MessageWindow: RegisterClassEx failed ({Marshal.GetLastWin32Error()})");
            return;
        }

        // WS_EX_TOOLWINDOW and no WS_VISIBLE: never painted, never in the taskbar or Alt-Tab, but
        // still a top-level window, so it can be made foreground for the tray menu.
        Handle = NativeMethods.CreateWindowEx(
<<<<<<< HEAD
=======
            (int)NativeMethods.WS_EX_TOOLWINDOW, ClassName, "dockdev", (uint)NativeMethods.WS_POPUP,
            0, 0, 0, 0, nint.Zero, nint.Zero, instance, nint.Zero);
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
            (int)NativeMethods.WS_EX_TOOLWINDOW, _className, "dockdev", (uint)NativeMethods.WS_POPUP,
            0, 0, 0, 0, nint.Zero, nint.Zero, _hInstance, nint.Zero);

        if (Handle == nint.Zero)
            Diag.Log($"MessageWindow: CreateWindowEx failed ({Marshal.GetLastWin32Error()})");
    }

    private nint OnMessage(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
<<<<<<< HEAD
            // Windows is logging off / shutting down: persist settings and release the dock's
            // close-guard so the process can exit cleanly instead of being force-killed (which is
            // one of the ways dock.json ends up truncated).
            if (msg is NativeMethods.WM_QUERYENDSESSION or NativeMethods.WM_ENDSESSION)
            {
                Diag.Log("MessageWindow: received OS shutdown/end-session message.");
=======
            if (msg is NativeMethods.WM_QUERYENDSESSION or NativeMethods.WM_ENDSESSION)
            {
                Diag.Log("MessageWindow: received OS shutdown/end-session query.");
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
                try
                {
                    App.Manager?.Save();
                    App.Manager?.Dock?.AllowClose();
                }
                catch (Exception ex)
                {
                    Diag.Log("MessageWindow: shutdown save failed: " + ex.Message);
                }
                return (nint)1;
            }

            MessageReceived?.Invoke(msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            // A throw here would unwind through native code and take the process down, so a
            // misbehaving handler is logged and the message is otherwise handled normally.
            Diag.Log($"MessageWindow handler failed (msg 0x{msg:X}): {ex}");
        }
        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (Handle != nint.Zero)
            NativeMethods.DestroyWindow(Handle);
<<<<<<< HEAD

        // The class is per-instance, so unlike a process-wide registration it is safe to release
        // here — this instance's window is already destroyed above.
=======
        // The window class is intentionally left registered: it is process-unique and unregisters
        // itself when the process exits, and UnregisterClass would race any in-flight messages.

>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
        NativeMethods.UnregisterClass(_className, _hInstance);
    }
}
