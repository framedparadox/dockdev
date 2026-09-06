using System.Runtime.InteropServices;

namespace dockdev.Interop;

/// <summary>
/// Thin Win32 / DWM interop surface. Kept small and focused on what the dock needs:
/// rounded window corners, always-on-top enforcement, tool-window styling, and
/// cursor probing for edge-reveal / auto-hide.
/// </summary>
internal static partial class NativeMethods
{
    // ---- DWM window attributes --------------------------------------------
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_BORDER_COLOR = 34;

    // DWM_WINDOW_CORNER_PREFERENCE
    public const int DWMWCP_DEFAULT = 0;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;       // full ~8px Win11 window radius
    public const int DWMWCP_ROUNDSMALL = 3;  // subtle ~4px radius (menus/tooltips)

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    // ---- Window Z-order / positioning -------------------------------------
    public static readonly nint HWND_TOPMOST = new(-1);
    public static readonly nint HWND_NOTOPMOST = new(-2);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint hwndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    // ---- Extended window styles (tool window: off taskbar & alt-tab) ------
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    // ---- Window styles (strip the non-client frame / resize border) -------
    public const int GWL_STYLE = -16;
    public const long WS_CAPTION = 0x00C00000;
    public const long WS_THICKFRAME = 0x00040000;
    public const long WS_BORDER = 0x00800000;
    public const long WS_DLGFRAME = 0x00400000;

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_FRAMECHANGED = 0x0020;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint newLong);

    // ---- Cursor / hit testing (auto-hide reveal) --------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT point);

    // ---- DPI (DIP -> physical pixel conversion for exact placement) -------
    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    // ---- Mouse button state (timer-polled dragging) -----------------------
    public const int VK_LBUTTON = 0x01;

    // Modifier keys, read the same way while capturing a hotkey: XAML's KeyDown reports the
    // pressed key but not which modifiers are held with it, and the Windows key never reaches
    // the app as a KeyDown at all.
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12; // Alt
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    // ---- Icon handle cleanup ----------------------------------------------
    //
    // dockdev extracts no shell icons: every tool's look is a bundled Segoe Fluent glyph resolved
    // from ToolCatalog (design doc §26), so there is no SHGetFileInfo, no SHGetImageList and no
    // IImageList here — a closed tool catalog needs none of the machinery a launcher does. What
    // remains is releasing the icon handle taken from dockdev's *own* executable for the tray icon.

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(nint hIcon);

    // ---- Hidden helper window (tray callbacks + global hotkeys) ------------
    //
    // Both the notification-area icon and RegisterHotKey deliver their events as window messages,
    // and a WinUI 3 Window exposes no WndProc to receive them. So dockdev owns one tiny hidden
    // HWND of its own (see Services/MessageWindow.cs) purely as a message sink. It is a real
    // (never-shown) popup rather than an HWND_MESSAGE child, because TrackPopupMenuEx needs an
    // owner that can be made the foreground window or the tray menu won't light-dismiss.

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    public const long WS_POPUP = 0x80000000L;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterClass(string className, nint hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(
        int exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string message);

    public const uint WM_APP = 0x8000;
    public const uint WM_NULL = 0x0000;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_QUERYENDSESSION = 0x0011;
    public const uint WM_ENDSESSION = 0x0016;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_NULL = 0x0000;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_APP = 0x8000;

    // ---- Notification-area (tray) icon -------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;

    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIF_SHOWTIP = 0x00000080;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    // ---- Icon loading (the exe's own icon, at the shell's small-icon size) --

    public const uint IMAGE_ICON = 1;
    public const uint LR_DEFAULTCOLOR = 0x0000;
    public const uint LR_SHARED = 0x8000;
    public const int SM_CXSMICON = 49;
    public const int SM_CYSMICON = 50;

    /// <summary>Resource id the .NET SDK gives an <c>&lt;ApplicationIcon&gt;</c> (IDI_APPLICATION).</summary>
    public static readonly nint IDI_APPLICATION = 32512;
    public static readonly nint IDI_APP_ICON = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint LoadImage(nint instance, nint name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconEx(string file, int iconIndex, out nint large, out nint small, uint icons);

    // ---- Native popup menu (the tray context menu) -------------------------
    //
    // A native menu rather than a XAML MenuFlyout: a flyout needs a XamlRoot to hang off, and the
    // tray icon's owner is a plain HWND with no XAML content at all.

    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;

    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_NONOTIFY = 0x0080;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(nint menu, uint flags, nuint itemId, string? item);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    // ---- Foreground / show (bring the dock to the front) -------------------

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(nint hwnd, int cmdShow);

    // ---- Deliberately absent: window / process enumeration -----------------
    //
    // A launcher dock answers "is this pinned app running?" by walking the visible top-level
    // windows with EnumWindows, mapping each back to its owner with GetWindowThreadProcessId, and
    // reading that process's image path through OpenProcess + QueryFullProcessImageName.
    //
    // dockdev opens no external process, so it has nothing to poll for (design doc §26): the
    // open-window indicator is driven by ToolWindowManager's in-process dictionary and its Closed
    // event, which is both exact and instant where a poll is neither. That makes the whole
    // enumeration surface dead code — and dead code that reads other processes' identities is
    // worth deleting rather than leaving for a Store reviewer, or a future contributor, to find
    // and wonder about. If a feature ever genuinely needs it, it comes back with a caller.

    // ---- Global hotkeys ----------------------------------------------------

    /// <summary>Ask Windows not to auto-repeat a held-down hotkey.</summary>
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(nint hwnd, int id);

    // ---- Package identity --------------------------------------------------
    //
    // The one reliable way to ask "am I running from an MSIX package?" without throwing: the
    // WinRT alternative (Package.Current) raises an exception when there is no package, and an
    // exception is a poor answer to a question this is asked at startup. Called with a zero
    // length and no buffer it does no work at all — it only reports which of the two error codes
    // below applies.

    /// <summary>The process has no package identity — i.e. it is a plain unpackaged exe.</summary>
    public const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    /// <summary>There is a package, and its name didn't fit the (deliberately empty) buffer.</summary>
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int GetCurrentPackageFullName(
        ref uint packageFullNameLength, [Out] char[]? packageFullName);
}
