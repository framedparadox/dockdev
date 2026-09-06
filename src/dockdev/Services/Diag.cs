using System.Diagnostics;

namespace dockdev.Services;

/// <summary>Lightweight file logger for diagnosing runtime layout/window issues.</summary>
public static class Diag
{
    private static readonly object Gate = new();
    private static string? _logPath;
    private const long MaxLogBytes = 2L * 1024 * 1024; // 2 MB cap

<<<<<<< HEAD
    // Resolved lazily rather than in a static constructor: a throwing type initializer would take
    // down every caller of Diag.Log (including the crash handlers that depend on it), so path
    // resolution has to fail soft instead.
    private static string GetLogPath()
    {
=======
    static Diag()
    private static string GetLogPath()
    {
        try { File.WriteAllText(LogPath, $"=== dockdev started {DateTime.Now:O} ===\n"); }
        catch { /* ignore */ }
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
        if (_logPath is not null)
            return _logPath;

        try
        {
<<<<<<< HEAD
            _logPath = Path.Combine(Path.GetTempPath(), "dockdev.log");
=======
            var temp = Path.GetTempPath();
            _logPath = Path.Combine(temp, "dockdev.log");
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
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
<<<<<<< HEAD
=======
                File.AppendAllText(LogPath, line + "\n");
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
            {
                var path = GetLogPath();
                try
                {
<<<<<<< HEAD
                    // Rotate rather than grow without bound: a soak run or a tight error loop would
                    // otherwise fill the disk over time.
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxLogBytes)
                        File.WriteAllText(path, $"=== dockdev log rotated {DateTime.Now:O} ===\n");
                }
                catch { /* ignore file-inspection failures */ }
=======
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxLogBytes)
                    {
                        File.WriteAllText(path, $"=== dockdev log rotated {DateTime.Now:O} ===\n");
                    }
                }
                catch { /* ignore file inspection failures */ }
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177

                File.AppendAllText(path, line + "\n");
            }
        }
<<<<<<< HEAD
=======
        catch { /* ignore */ }
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
        catch { /* ignore all logging I/O failures */ }
    }
}
