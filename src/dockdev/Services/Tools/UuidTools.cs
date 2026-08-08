namespace dockdev.Services.Tools;

public enum UuidVersion { V4, V7, Nil }

public enum UuidFormat { Hyphenated, Braced, Parenthesised, Compact, Uppercase, Base64 }

/// <summary>UUID Generator's engine (design doc §14.13): v4, v7 (time-ordered — increasingly the
/// right default for database keys) and NIL, in six textual formats.</summary>
public static class UuidTools
{
    public static Guid Generate(UuidVersion version) => version switch
    {
        UuidVersion.V4 => Guid.NewGuid(),
        UuidVersion.V7 => Guid.CreateVersion7(),
        _ => Guid.Empty,
    };

    public static string Format(Guid guid, UuidFormat format) => format switch
    {
        UuidFormat.Hyphenated => guid.ToString("D"),
        UuidFormat.Braced => guid.ToString("B"),
        UuidFormat.Parenthesised => guid.ToString("P"),
        UuidFormat.Compact => guid.ToString("N"),
        UuidFormat.Uppercase => guid.ToString("D").ToUpperInvariant(),
        UuidFormat.Base64 => Convert.ToBase64String(guid.ToByteArray()),
        _ => guid.ToString(),
    };

    public static List<string> GenerateMany(UuidVersion version, UuidFormat format, int count) =>
        Enumerable.Range(0, Math.Clamp(count, 1, 10_000)).Select(_ => Format(Generate(version), format)).ToList();
}
