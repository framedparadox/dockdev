using System.Globalization;
using System.Text;

namespace DevDX.Services;

/// <summary>
/// The size ceilings from design doc §22, and the only sanctioned way to read a user-chosen file
/// into a tool.
/// <para>
/// §21 calls these "parse-bomb and allocation guards" and §22 states the contract they exist to
/// keep: an input past the ceiling is "refused with a clear, specific message — never an OOM, never
/// a hang". Every tool that opens a file previously called <c>File.ReadAllTextAsync</c> straight on
/// whatever the picker returned, so a multi-gigabyte file — a VM disk image picked by mistake, a
/// log that grew overnight — was read whole into a string on the way to a tokenizer. The failure
/// mode is not a slow window: it is an <see cref="OutOfMemoryException"/> from an unawaited task
/// that the runtime discards, leaving an empty window and no explanation.
/// </para>
/// <para>
/// The size is taken from the file's metadata <em>before</em> a byte is read, which is the whole
/// point — a check made after the read has already lost. Text is additionally capped by the .NET
/// string limit long before the disk is exhausted, so the ceiling here is the smaller, deliberate
/// one.
/// </para>
/// </summary>
public static class InputLimits
{
    /// <summary>
    /// §22's ceiling: past 50 MB an input is refused rather than degraded. The band below it is a
    /// performance concern (tokenize visible lines only, no live highlighting); this is the point
    /// at which the answer is "no".
    /// </summary>
    public const long MaxTextBytes = 50L * 1024 * 1024;

    /// <summary>
    /// The ceiling for a file read as bytes rather than text — Hash &amp; HMAC's file mode and
    /// Base64's encode-a-file mode. Higher than <see cref="MaxTextBytes"/> because nothing
    /// tokenizes, colourises or lays out these bytes: they are hashed or encoded once. Still
    /// bounded, because both paths hold the whole array in memory and Base64 turns it into a
    /// string a third larger again.
    /// </summary>
    public const long MaxBinaryBytes = 256L * 1024 * 1024;

    /// <summary>
    /// The ceiling for a custom dock icon. An icon is decoded to a bitmap and drawn at 44 px;
    /// anything past this is not an icon, and image decoders are a classic place to hand a
    /// deliberately expensive file.
    /// </summary>
    public const long MaxIconBytes = 16L * 1024 * 1024;

    /// <summary>
    /// The ceiling for an imported <c>dock.json</c>. A configuration file is kilobytes; the guard
    /// is against a file that is not one at all, since import accepts any path the user picks.
    /// </summary>
    public const long MaxConfigBytes = 8L * 1024 * 1024;

    /// <summary>What a bounded read produced: the content, or the reason there isn't any.</summary>
    /// <param name="Ok">True when <paramref name="Text"/> / <paramref name="Bytes"/> is usable.</param>
    /// <param name="Text">The file's text, or empty.</param>
    /// <param name="Bytes">The file's bytes, or empty.</param>
    /// <param name="Error">A message fit to show a user, or empty when <paramref name="Ok"/>.</param>
    public readonly record struct ReadResult(bool Ok, string Text, byte[] Bytes, string Error);

    /// <summary>True when a file of <paramref name="length"/> bytes is within <paramref name="limit"/>.</summary>
    public static bool IsWithin(long length, long limit) => length >= 0 && length <= limit;

    /// <summary>
    /// "50 MB" / "256 MB" — the ceiling as it appears in the message, so the user is told the
    /// actual number rather than "too large".
    /// </summary>
    public static string Describe(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.#} GB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB");

    /// <summary>
    /// Reads a file as UTF-8 text if it is within <paramref name="limit"/>, otherwise refuses it
    /// with a message naming the ceiling. Never throws: a locked, deleted or unreadable file is
    /// reported the same way an oversized one is, because the caller — a drop handler or a picker
    /// callback — has the same one thing to do with either.
    /// </summary>
    public static async Task<ReadResult> ReadTextAsync(string path, long limit = MaxTextBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return Failed(Loc.Get("Tool.FileUnreadable"));
            if (!IsWithin(info.Length, limit))
                return Failed(Loc.Format("Tool.FileTooLarge", Describe(limit)));

            // Encoding spelled out rather than left to detection: every tool in DevDX works in
            // UTF-8, and a byte-order mark is consumed rather than becoming a stray leading
            // character in the editor.
            var text = await File.ReadAllTextAsync(path, Encoding.UTF8);
            return new ReadResult(true, text, [], "");
        }
        catch (Exception ex)
        {
            Diag.Log($"InputLimits.ReadTextAsync failed: {ex.GetType().Name}: {ex.Message}");
            return Failed(Loc.Get("Tool.FileUnreadable"));
        }
    }

    /// <summary>
    /// Reads a file as bytes if it is within <paramref name="limit"/>. Same contract as
    /// <see cref="ReadTextAsync"/>.
    /// </summary>
    public static async Task<ReadResult> ReadBytesAsync(string path, long limit = MaxBinaryBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return Failed(Loc.Get("Tool.FileUnreadable"));
            if (!IsWithin(info.Length, limit))
                return Failed(Loc.Format("Tool.FileTooLarge", Describe(limit)));

            var bytes = await File.ReadAllBytesAsync(path);
            return new ReadResult(true, "", bytes, "");
        }
        catch (Exception ex)
        {
            Diag.Log($"InputLimits.ReadBytesAsync failed: {ex.GetType().Name}: {ex.Message}");
            return Failed(Loc.Get("Tool.FileUnreadable"));
        }
    }

    private static ReadResult Failed(string error) => new(false, "", [], error);
}
