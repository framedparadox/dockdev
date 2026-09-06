using System.Diagnostics;
using System.Runtime.InteropServices;

namespace dockdev.UITests;

/// <summary>
/// A point-in-time reading of what the running dockdev is holding.
/// <para>
/// Four counters rather than one, because the leaks this suite is looking for do not all show up
/// in the same place. A retained visual tree shows as managed bytes; an undetached
/// <c>DispatcherQueueTimer</c> or an unclosed clipboard <c>DataPackage</c> shows as handles; a
/// brush, font or bitmap that outlives the window it was made for shows as GDI objects — and a
/// process can sit on ten thousand of those with a flat working set right up until it hits the
/// per-process quota and the next allocation fails.
/// </para>
/// </summary>
public readonly record struct ResourceSample(
    long PrivateBytes,
    int Handles,
    int GdiObjects,
    int UserObjects,
    int Threads)
{
    public override string ToString() =>
        $"private={PrivateBytes / (1024 * 1024)}MB handles={Handles} gdi={GdiObjects} " +
        $"user={UserObjects} threads={Threads}";

    /// <summary>This sample minus <paramref name="baseline"/>, field by field.</summary>
    public ResourceSample Since(ResourceSample baseline) => new(
        PrivateBytes - baseline.PrivateBytes,
        Handles - baseline.Handles,
        GdiObjects - baseline.GdiObjects,
        UserObjects - baseline.UserObjects,
        Threads - baseline.Threads);
}

/// <summary>
/// The crash oracle for a soak run.
/// <para>
/// <b>Why a log reader is the centre of this.</b> Since <c>App.UnhandledException</c> began setting
/// <c>Handled = true</c> — which is what stopped a fault taking the whole tray app down — a crash
/// no longer looks like a crash. The process survives, the window stays on screen, and the only
/// trace of a fault on a timer tick or in an <c>async void</c> handler is a line in
/// <c>%TEMP%\dockdev.log</c>. A soak test that only asserted "the process is still running" would
/// therefore pass through exactly the faults this app was most recently fixed for. So the assertion
/// is on the log, sliced to the window of one test by remembering the file's length before the
/// loop starts.
/// </para>
/// </summary>
public sealed class AppHealth(int processId)
{
    /// <summary>Where <c>Services.Diag</c> writes. Fixed path, one per user — the app resolves it
    /// with <c>Path.GetTempPath()</c>, and the test process runs as the same user with the same
    /// environment, so both agree on it without being told.</summary>
    public static string LogPath { get; } = Path.Combine(Path.GetTempPath(), "dockdev.log");

    /// <summary>Log lines that mean "something threw where nothing could catch it". Matched as
    /// substrings of a line, in the spelling <c>App</c> and the guarded call sites actually use.</summary>
    private static readonly string[] FaultMarkers =
    [
        "UNHANDLED:",
        "UNOBSERVED TASK:",
        "failed:",          // ToolCommand.Invoke, ToolWindowBase.BringToFront, CodeEditor.Highlight
        "document unavailable:",
        "could not restore the document:",
    ];

    private readonly Process _process = Process.GetProcessById(processId);

    /// <summary>Length of the log right now — the mark a later <see cref="FaultsSince"/> reads from.
    /// Taken as a length rather than a timestamp because the log's clock is only to the millisecond
    /// and a soak loop writes faster than that.</summary>
    public long LogMark()
    {
        try { return new FileInfo(LogPath).Exists ? new FileInfo(LogPath).Length : 0; }
        catch (IOException) { return 0; }
    }

    /// <summary>Everything the app logged after <paramref name="mark"/>.</summary>
    public string LogSince(long mark)
    {
        try
        {
            // Shared read/write/delete: Diag holds the file only for the instant of an
            // AppendAllText, but "only for an instant" is still a window a soak loop will hit.
            using var stream = new FileStream(
                LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (mark > stream.Length)
                mark = 0; // truncated under us — a restart; report the lot rather than nothing
            stream.Seek(mark, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>The lines logged since <paramref name="mark"/> that indicate a swallowed fault.</summary>
    public IReadOnlyList<string> FaultsSince(long mark) =>
        [.. LogSince(mark)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => FaultMarkers.Any(marker => line.Contains(marker, StringComparison.Ordinal)))];

    public bool HasExited
    {
        get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } }
    }

    /// <summary>The process's exit code, or null while it is still running.</summary>
    public int? ExitCode
    {
        get { try { return _process.HasExited ? _process.ExitCode : null; } catch (Exception) { return null; } }
    }

    public ResourceSample Sample()
    {
        _process.Refresh();
        return new ResourceSample(
            _process.PrivateMemorySize64,
            _process.HandleCount,
            (int)GuiResources(GdiObjectsFlag),
            (int)GuiResources(UserObjectsFlag),
            _process.Threads.Count);
    }

    private uint GuiResources(uint flags)
    {
        try { return GetGuiResources(_process.Handle, flags); }
        catch (Exception) { return 0; }
    }

    private const uint GdiObjectsFlag = 0;
    private const uint UserObjectsFlag = 1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(nint hProcess, uint uiFlags);
}
