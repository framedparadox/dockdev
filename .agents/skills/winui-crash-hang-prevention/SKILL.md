---
name: winui-crash-hang-prevention
description: >-
  Comprehensive checklist and guide for identifying, preventing, and fixing application crashes,
  hangs, deadlocks, and memory leaks in .NET and WinUI 3 (Windows App SDK) desktop applications.
---

# WinUI 3 & Modern .NET Crash and Hang Prevention Guide

This skill provides an authoritative checklist, architectural patterns, and defensive coding techniques for eliminating crashes, hangs, deadlocks, and performance degradations in Windows App SDK / WinUI 3 and modern .NET applications.

---

## 1. Asynchronous Execution & `async void` Safety

### Rules & Checklist
1. **Never use `async void` except for top-level event handlers.**
   - All other async methods MUST return `Task` or `ValueTask`.
2. **Every `async void` event handler MUST have an outer `try/catch (Exception ex)` block.**
   - In .NET, an unhandled exception thrown past the first `await` in an `async void` method cannot be caught by any caller on the call stack and immediately triggers process termination via `AppDomain.UnhandledException`.
3. **Guard against UI interaction after Window closure.**
   - When resuming execution after an `await` in a window or page event handler, verify `XamlRoot is not null` or `!_isClosed` before touching any UI elements or properties.
4. **Attach all three global exception traps in `App.xaml.cs`:**
   - `UnhandledException += (_, e) => { Diag.Log(...); e.Handled = true; };` (WinUI XAML UI thread)
   - `TaskScheduler.UnobservedTaskException += (_, e) => { Diag.Log(...); e.SetObserved(); };` (Fire-and-forget Tasks)
   - `AppDomain.CurrentDomain.UnhandledException += (_, e) => { Diag.Log(...); };` (ThreadPool / background / native callbacks)

---

## 2. Win32 Interop, P/Invoke, and Window Subclassing

### Rules & Checklist
1. **Always declare `[UnmanagedFunctionPointer(CallingConvention.Winapi)]` on native delegate types.**
   - Missing calling convention causes register corruption or stack unbalance when native message pumps dispatch messages.
2. **Root managed delegates passed to native code as function pointers.**
   - Store the delegate in an instance field (`private readonly NativeMethods.WndProc _wndProc;`) for the lifetime of the native window / hook to prevent the Garbage Collector from freeing the underlying native stub (which triggers fatal `0xC0000005` Access Violations).
3. **Register unique window class names per instance and unregister on dispose.**
   - Generate unique class names (`$"{Prefix}.{ProcessId}.{Guid.NewGuid():N}"`) and call `UnregisterClass` in `Dispose()`.
4. **Never block Windows OS shutdown / restart.**
   - In WinUI 3, canceling `AppWindow.Closing` (`args.Cancel = true;`) blocks Windows shutdown.
   - Listen for `WM_QUERYENDSESSION` (0x0011) / `WM_ENDSESSION` (0x0016) on a message window, save state, and allow the window to close.

---

## 3. UI Thread Responsiveness & Hang Prevention

### Rules & Checklist
1. **Never block the UI thread synchronously.**
   - Strictly prohibit `.Result`, `.Wait()`, `GetAwaiter().GetResult()`, `Thread.Sleep()`, and `Task.WaitAll()` on the UI thread.
2. **Offload compute-heavy workloads to `Task.Run`.**
   - Syntax highlighting, large file diffing (Myers diff), multi-hash calculations, and format conversions over large buffers must execute on background threads and marshal results back via `DispatcherQueue`.
3. **Bound XAML text and control allocations.**
   - Never generate unbounded strings (e.g. 100k line numbers) for a single `TextBlock` (`DirectWrite` layout stall).
   - Never instantiate thousands of unvirtualized XAML controls (e.g., findings lists) in one frame. Cap previews to reasonable bounds (e.g., top 200 items).
4. **Enforce Regular Expression timeouts (`RegexMatchTimeoutException`).**
   - All `Regex` instances and `[GeneratedRegex]` attributes MUST define an explicit timeout (e.g., `TimeSpan.FromMilliseconds(500)` or `matchTimeoutMilliseconds: 500`) to prevent Catastrophic Backtracking (ReDoS).

---

## 4. Storage, State Persistence & Concurrency

### Rules & Checklist
1. **Always serialize shared state under thread synchronization.**
   - Synchronize `JsonSerializer.Serialize` and collection modifications under a shared lock (`SaveLock`) to prevent `InvalidOperationException: Collection was modified`.
2. **Perform atomic file writes with unique temporary files and forced disk flushes.**
   - Write to `$"{FilePath}.{Guid.NewGuid():N}.tmp"`.
   - Use `stream.Flush(flushToDisk: true)` and `FileOptions.WriteThrough`.
   - Use `File.Replace` (or move with retry) to replace the target atomically.
3. **Implement exponential backoff retry loops on file I/O.**
   - Handle transient `IOException` (error 32 `ERROR_SHARING_VIOLATION`) caused by Windows Defender, search indexers, or cloud sync engines.
4. **Preserve damaged configuration files and provide backup fallback.**
   - Never overwrite damaged config files with defaults automatically. Maintain `.bak` copies and preserve corrupted files to `.corrupt.<timestamp>` for disaster recovery.

---

## 5. Clipboard & Shared System Resource Interop

### Rules & Checklist
1. **Always wrap `Clipboard.SetContent` and `Clipboard.GetContentAsync` in retry loops.**
   - Win32 clipboard can be locked by another process (RDP, clipboard managers), throwing `COMException` (`CLIPBRD_E_CANT_OPEN` 0x800401D0).
   - Implement a reusable `ClipboardService.TrySetText(text, retries: 3, delayMs: 50)`.

---

## 6. Arithmetic, Parsing & Boundary Safety

### Rules & Checklist
1. **Guard `Math.Abs(MinValue)`.**
   - `Math.Abs(int.MinValue)` and `Math.Abs(long.MinValue)` throw `OverflowException`. Use unsigned bit casting: `(uint)val` or string length checks.
2. **Validate Unix Epoch timestamp ranges before conversion.**
   - `DateTimeOffset.FromUnixTimeSeconds(seconds)` throws `ArgumentOutOfRangeException` if `seconds < -62135596800` or `seconds > 253402300799`.
3. **Sanitize XML element tag names.**
   - Convert arbitrary strings/keys to XML-safe tag names using `XmlConvert.EncodeLocalName(name)` to prevent `XmlException`.
4. **Enforce recursion depth limits.**
   - Deeply nested JSON/XML/AST trees must have an explicit depth limit (e.g., 64–128 levels) to prevent `StackOverflowException`.
5. **Guard ComboBox `SelectedIndex == -1`.**
   - Always validate or clamp `SelectedIndex` before indexing arrays or collections.

