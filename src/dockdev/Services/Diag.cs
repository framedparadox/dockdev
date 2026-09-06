using System.Diagnostics;

namespace dockdev.Services;

/// <summary>Lightweight file logger for diagnosing runtime layout/window issues.</summary>
public static class Diag
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "dockdev.log");

    private static readonly object Gate = new();
    private static string? _logPath;
    private const long MaxLogBytes = 2L * 1024 * 1024; // 2 MB cap

    static Diag()
    private static string GetLogPath()
    {
        try { File.WriteAllText(LogPath, $"=== dockdev started {DateTime.Now:O} ===\n"); }
        catch { /* ignore */ }
        if (_logPath is not null)
            return _logPath;

        try
        {
            var temp = Path.GetTempPath();
            _logPath = Path.Combine(temp, "dockdev.log");
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
                File.AppendAllText(LogPath, line + "\n");
            {
                var path = GetLogPath();
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxLogBytes)
                    {
                        File.WriteAllText(path, $"=== dockdev log rotated {DateTime.Now:O} ===\n");
                    }
                }
                catch { /* ignore file inspection failures */ }

                File.AppendAllText(path, line + "\n");
            }
        }
        catch { /* ignore */ }
        catch { /* ignore all logging I/O failures */ }
    }
}
