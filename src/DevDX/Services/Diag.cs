using System.Diagnostics;

namespace DevDX.Services;

/// <summary>Lightweight file logger for diagnosing runtime layout/window issues.</summary>
public static class Diag
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "devdx.log");

    private static readonly object Gate = new();

    static Diag()
    {
        try { File.WriteAllText(LogPath, $"=== DevDX started {DateTime.Now:O} ===\n"); }
        catch { /* ignore */ }
    }

    public static void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        Debug.WriteLine("[DevDX] " + line);
        try
        {
            lock (Gate)
                File.AppendAllText(LogPath, line + "\n");
        }
        catch { /* ignore */ }
    }
}
