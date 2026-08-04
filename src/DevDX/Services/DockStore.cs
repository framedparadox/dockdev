using System.Text.Json;
using System.Text.Json.Serialization;
using DevDX.Models;

namespace DevDX.Services;

/// <summary>Loads and saves the dock configuration as JSON under %AppData%\DevDX.</summary>
public static class DockStore
{
    /// <summary>
    /// Where DevDX keeps everything it writes: <c>dock.json</c> and the icon cache.
    /// <c>%AppData%\DevDX</c> normally, or whatever <c>DEVDX_DATA_DIR</c> names.
    /// <para>
    /// The override exists so a UI test run (see <c>tests/DevDX.UITests</c>) drives a throwaway
    /// dock instead of the signed-in user's real one — reset-to-defaults and remove-item are
    /// exactly the paths worth covering, and exactly the ones you can't point at live data.
    /// It doubles as a way to run a genuinely portable copy from a removable drive.
    /// </para>
    /// </summary>
    public static string DataDirectory { get; } = ResolveDataDirectory();

    private static string ResolveDataDirectory()
    {
        var custom = Environment.GetEnvironmentVariable("DEVDX_DATA_DIR");
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
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevDX");
    }

    private static string Dir => DataDirectory;

    private static readonly string FilePath = Path.Combine(DataDirectory, "dock.json");

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

    public static bool Exists => File.Exists(FilePath);

    /// <summary>
    /// Reads the config, always returning something usable: a file that is missing, unreadable or
    /// corrupt yields defaults rather than an exception, because the alternative is an app that
    /// won't start. The result is always validated, so callers can rely on
    /// <see cref="DockConfig.Dock"/> being populated.
    /// </summary>
    public static DockConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<DockConfig>(json, Options);
                if (cfg is not null)
                    return cfg.EnsureValid();
            }
        }
        catch (Exception ex)
        {
            Diag.Log("DockStore.Load failed: " + ex.Message);
        }
        return new DockConfig().EnsureValid();
    }

    public static void Save(DockConfig config)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(config, Options);
            // Write-then-rename for crash safety.
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Diag.Log("DockStore.Save failed: " + ex.Message);
        }
    }

    // ---- Import / export ---------------------------------------------------
    //
    // The exported file is exactly the same shape as dock.json — not a format of its own — so a
    // backup can also be dropped straight into %AppData%\DevDX by hand, and a config copied out
    // of there imports without conversion.

    /// <summary>Writes <paramref name="config"/> to an arbitrary path.</summary>
    /// <returns>True on success; a failure is logged and reported rather than thrown, since the
    /// caller is a button in the Settings window.</returns>
    public static bool ExportTo(DockConfig config, string path)
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

    /// <summary>
    /// Reads a configuration from an arbitrary path, migrated to the current shape.
    /// <para>
    /// Unlike <see cref="Load"/> this returns null on any problem instead of falling back to
    /// defaults. The difference is deliberate: at startup, defaults beat refusing to run, but
    /// importing a file that turned out not to be a DevDX config must not silently wipe the
    /// user's real dock and replace it with a seeded one.
    /// </para>
    /// </summary>
    public static DockConfig? ImportFrom(string path)
    {
        try
        {
            var config = JsonSerializer.Deserialize<DockConfig>(File.ReadAllText(path), Options);
            if (config is null)
                return null;

            // Checked before EnsureValid: any JSON object deserializes happily into a DockConfig
            // full of defaults, and EnsureValid would then manufacture a dock for it — so a file
            // that is not a DevDX config would import as a plausible-looking empty one.
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
