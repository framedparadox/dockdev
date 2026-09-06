using System.Text.Json;
using System.Text.Json.Serialization;
using dockdev.Models;

namespace dockdev.Services;

/// <summary>Loads and saves the dock configuration as JSON under %AppData%\dockdev.</summary>
public static class DockStore
{
    // Serializes Load against Save (and Save against Save): the config is saved from the UI thread
    // and from OS shutdown handling, so two writers could otherwise interleave on the same file.
    private static readonly object SaveLock = new();

    /// <summary>
    /// Where dockdev keeps everything it writes: <c>dock.json</c> and the icon cache.
    /// <c>%AppData%\dockdev</c> normally, or whatever <c>DOCKDEV_DATA_DIR</c> names.
    /// <para>
    /// The override exists so a UI test run (see <c>tests/dockdev.UITests</c>) drives a throwaway
    /// dock instead of the signed-in user's real one — reset-to-defaults and remove-item are
    /// exactly the paths worth covering, and exactly the ones you can't point at live data.
    /// It doubles as a way to run a genuinely portable copy from a removable drive.
    /// </para>
    /// </summary>
    public static string DataDirectory { get; } = ResolveDataDirectory();

    private static string ResolveDataDirectory()
    {
        var custom = Environment.GetEnvironmentVariable("DOCKDEV_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                return Path.GetFullPath(custom);
            }
            catch
            {
                // An unusable override must not cost the user their settings: fall through to
                // the default location rather than failing to start.
            }
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dockdev");
    }

    private static string Dir => DataDirectory;

    private static readonly string FilePath = Path.Combine(DataDirectory, "dock.json");
    private static readonly string BackupFilePath = Path.Combine(DataDirectory, "dock.json.bak");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        // The default encoder escapes anything outside a conservative ASCII set, which turns
        // "Ctrl+Alt+A" into "Ctrl+Alt+A" and a name like "書類" into a run of escapes.
        // dock.json is documented as hand-editable, so it is written to be read: this file is
        // never embedded in HTML or a script, which is the only context that escaping guards.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static bool Exists => File.Exists(FilePath) || File.Exists(BackupFilePath);

    /// <summary>
    /// Reads the config, always returning something usable: a file that is missing, unreadable or
    /// corrupt yields defaults rather than an exception, because the alternative is an app that
    /// won't start. The result is always validated, so callers can rely on
    /// <see cref="DockConfig.Dock"/> being populated. If the primary file is unreadable the backup
    /// is tried, and a genuinely corrupt file is preserved for recovery rather than silently lost.
    /// </summary>
    public static DockConfig Load()
    {
        lock (SaveLock)
        {
            if (File.Exists(FilePath))
            {
                var (ok, cfg) = TryReadAndDeserialize(FilePath);
                if (ok && cfg is not null)
                    return cfg.EnsureValid();

                Diag.Log("DockStore.Load: primary dock.json failed to load; attempting backup restore.");
            }

            if (File.Exists(BackupFilePath))
            {
                var (bakOk, bakCfg) = TryReadAndDeserialize(BackupFilePath);
                if (bakOk && bakCfg is not null)
                {
                    Diag.Log("DockStore.Load: recovered settings from dock.json.bak");
                    return bakCfg.EnsureValid();
                }
            }

            if (File.Exists(FilePath))
            {
                // Preserve the damaged file so the user (or a support request) can recover from it,
                // rather than letting the next Save overwrite it.
                try
                {
                    var corruptCopy = $"{FilePath}.corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}";
                    File.Copy(FilePath, corruptCopy, overwrite: true);
                    Diag.Log($"DockStore.Load: preserved corrupt configuration at '{corruptCopy}'");
                }
                catch { /* ignore */ }

                // Seeded=true so the manager does NOT treat this as a first run and seed defaults
                // (which would overwrite the corrupt file we just set aside).
                return new DockConfig { Seeded = true }.EnsureValid();
            }

            return new DockConfig().EnsureValid();
        }
    }

    /// <summary>Reads and deserializes one config file, retrying briefly on transient I/O errors
    /// (a virus scanner or search indexer holding the file open). Returns <c>(false, null)</c> for
    /// a missing file, a genuine parse error, or after the retries are exhausted.</summary>
    private static (bool Success, DockConfig? Config) TryReadAndDeserialize(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<DockConfig>(json, Options);
                if (cfg is not null)
                    return (true, cfg);
                return (false, null);
            }
            catch (JsonException ex)
            {
                Diag.Log($"DockStore: JSON parse error in '{path}': {ex.Message}");
                return (false, null);
            }
            catch (IOException ex)
            {
                Diag.Log($"DockStore: I/O error reading '{path}' (attempt {attempt + 1}): {ex.Message}");
                if (attempt < 2)
                    Thread.Sleep(25 * (attempt + 1));
            }
            catch (Exception ex)
            {
                Diag.Log($"DockStore: unexpected error reading '{path}': {ex.Message}");
                return (false, null);
            }
        }
        return (false, null);
    }

    public static void Save(DockConfig config)
    {
        lock (SaveLock)
        {
            var tmp = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(Dir);
                var json = JsonSerializer.Serialize(config, Options);

                // Write to a temp file with a forced disk flush, so a crash mid-write cannot leave
                // a half-written or zero-length dock.json.
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, System.Text.Encoding.UTF8))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                // Keep the last known-good file as a backup before replacing it, so Load has
                // something to fall back to if the new file is later found corrupt.
                if (File.Exists(FilePath))
                {
                    try { File.Copy(FilePath, BackupFilePath, overwrite: true); }
                    catch { /* ignore backup-creation failures */ }
                }

                // Replace via rename, retrying transient locks (antivirus, search indexer).
                bool moved = false;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    try
                    {
                        File.Move(tmp, FilePath, overwrite: true);
                        moved = true;
                        break;
                    }
                    catch (IOException) when (attempt < 3)
                    {
                        Thread.Sleep(25 * (attempt + 1));
                    }
                }

                if (!moved)
                    File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Diag.Log("DockStore.Save failed: " + ex.Message);
            }
            finally
            {
                try
                {
                    if (File.Exists(tmp))
                        File.Delete(tmp);
                }
                catch { /* ignore */ }
            }
        }
    }

    // ---- Import / export ---------------------------------------------------
    //
    // The exported file is exactly the same shape as dock.json — not a format of its own — so a
    // backup can also be dropped straight into %AppData%\dockdev by hand, and a config copied out
    // of there imports without conversion.

    /// <summary>Writes <paramref name="config"/> to an arbitrary path.</summary>
    /// <returns>True on success; a failure is logged and reported rather than thrown, since the
    /// caller is a button in the Settings window.</returns>
    public static bool ExportTo(DockConfig config, string path)
    {
        lock (SaveLock)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
                return true;
            }
            catch (Exception ex)
            {
                Diag.Log($"DockStore.ExportTo('{path}') failed: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Reads a configuration from an arbitrary path, migrated to the current shape.
    /// <para>
    /// Unlike <see cref="Load"/> this returns null on any problem instead of falling back to
    /// defaults. The difference is deliberate: at startup, defaults beat refusing to run, but
    /// importing a file that turned out not to be a dockdev config must not silently wipe the
    /// user's real dock and replace it with a seeded one.
    /// </para>
    /// </summary>
    public static DockConfig? ImportFrom(string path)
    {
        try
        {
            // Size-checked before it is read (§21): import accepts any path the user picks, and a
            // configuration file is kilobytes. Without this, pointing the picker at something that
            // is not a config at all is an allocation the size of that file before the shape check
            // below ever gets to reject it.
            var info = new FileInfo(path);
            if (!info.Exists || !InputLimits.IsWithin(info.Length, InputLimits.MaxConfigBytes))
            {
                Diag.Log($"DockStore.ImportFrom('{path}'): missing, or past the {InputLimits.MaxConfigBytes}-byte limit.");
                return null;
            }

            var config = JsonSerializer.Deserialize<DockConfig>(File.ReadAllText(path), Options);
            if (config is null)
                return null;

            // Checked before EnsureValid: any JSON object deserializes happily into a DockConfig
            // full of defaults, and EnsureValid would then manufacture a dock for it — so a file
            // that is not a dockdev config would import as a plausible-looking empty one.
            if (!config.HasDock)
                return null;

            return config.EnsureValid();
        }
        catch (Exception ex)
        {
            Diag.Log($"DockStore.ImportFrom('{path}') failed: {ex.Message}");
            return null;
        }
    }
}
