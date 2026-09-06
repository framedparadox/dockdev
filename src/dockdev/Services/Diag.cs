using System.Diagnostics;

namespace dockdev.Services;

/// <summary>Lightweight file logger for diagnosing runtime layout/window issues.</summary>
public static class Diag
{
    private static readonly object Gate = new();
    private static string? _logPath;
    private const long MaxLogBytes = 2L * 1024 * 1024; // 2 MB cap

    // Resolved lazily rather than in a static constructor: a throwing type initializer would take
    // down every caller of Diag.Log (including the crash handlers that depend on it), so path
    // resolution has to fail soft instead.
    private static string GetLogPath()
    {
        if (_logPath is not null)
            return _logPath;

        try
        {
            _logPath = Path.Combine(Path.GetTempPath(), "dockdev.log");
        }
        catch
        {
            _logPath = Path.Combine(Environment.CurrentDirectory, "dockdev.log");
        }
        return _logPath;
    }

    public static void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        Debug.WriteLine("[dockdev] " + line);
        try
        {
            lock (Gate)
            {
                var path = GetLogPath();
                try
                {
                    // Rotate rather than grow without bound: a soak run or a tight error loop would
                    // otherwise fill the disk over time.
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxLogBytes)
                        File.WriteAllText(path, $"=== dockdev log rotated {DateTime.Now:O} ===\n");
                }
                catch { /* ignore file-inspection failures */ }

                File.AppendAllText(path, line + "\n");
            }
        }
        catch { /* ignore all logging I/O failures */ }
    }
}
