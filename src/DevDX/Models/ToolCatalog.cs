using DevDX.Services;
using DevDX.Services.Formats;
using DevDX.ToolPages;

namespace DevDX.Models;

/// <summary>
/// What a dock cell represents. Every launchable value maps to exactly one built-in tool page —
/// there is no "arbitrary target" concept: the catalog is closed and code-defined (§3 non-goals).
/// <para>
/// <b>Currency is deliberately absent from v1.</b> The design document specs a Currency Converter
/// (§16) behind an ECB rate fetch, but it is the only tool that needs the network, and it was
/// parked as a post-v1 enhancement so the "offline by default, zero tools deferred for it" story
/// stays simple. <see cref="Services.NetworkPolicy"/> already exists and is ready to gate it
/// (opt-in, revocable) the day it is added — see <c>docs/devdx-design-document.md</c> §28.
/// </para>
/// </summary>
public enum ToolKind
{
    Json, DataFormatter, Xml, DataConverter, Base64, DataMasker, Jwt,
    TextToolkit, TextDiff, RegexTester, UrlEncoding,
    Hash, Uuid, Timestamp, NumberBase,
    Color, Lorem, Password, Cron,
    Separator,   // a visual divider, not launchable
}

public enum ToolCategory { FormatConvert, EncodeDecode, Privacy, Text, Generate, NumbersTime }

/// <summary>Static, code-defined metadata for one catalog entry — the "manifest" of a built-in
/// tool. Adding a tool means adding one of these plus one <see cref="ToolPage"/>.</summary>
public sealed record ToolDefinition(
    ToolKind Kind,
    ToolCategory Category,
    string NameKey,
    string DescriptionKey,
    string Glyph,
    Func<ToolPage> Factory,
    string[] FileExtensions,
    string[] SearchAliases,
    bool VisibleByDefault)
{
    public string DisplayName => Loc.Get(NameKey);
    public string Description => Loc.Get(DescriptionKey);
}

/// <summary>
/// The fixed, ordered, code-defined list of every built-in tool. Closed and code-defined by
/// design (§3 non-goals: no third-party plugin SDK) — nineteen tools (the design document's
/// sixteen, minus Currency, plus Colour Converter, Lorem Ipsum, Password Generator and Cron
/// Parser), sharing a handful of page classes (see each Factory).
/// </summary>
public static class ToolCatalog
{
    public static IReadOnlyList<ToolDefinition> All { get; } = Build();

    public static ToolDefinition? Get(ToolKind kind) =>
        All.FirstOrDefault(t => t.Kind == kind);

    public static IEnumerable<IGrouping<ToolCategory, ToolDefinition>> ByCategory() =>
        All.GroupBy(t => t.Category);

    /// <summary>The tools shown on a freshly seeded dock (§18): seven of nineteen, chosen for
    /// breadth across categories. The rest are absent from the dock but fully present in the
    /// catalog, so search, the Add-Tool gallery and Settings ▸ Tools list all nineteen from
    /// first launch.</summary>
    public static IEnumerable<ToolDefinition> Seeded => All.Where(t => t.VisibleByDefault);

    /// <summary>
    /// Routes a dropped file to the tool best suited to open it (§12): the first catalog entry
    /// (in declaration order) whose <see cref="ToolDefinition.FileExtensions"/> contains the
    /// file's extension, or null if nothing claims it.
    /// </summary>
    public static ToolDefinition? BestMatchFor(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
            return null;
        return All.FirstOrDefault(t => t.FileExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<ToolDefinition> Build() =>
    [
        // ---- Format & Convert --------------------------------------------------------------
        new ToolDefinition(ToolKind.Json, ToolCategory.FormatConvert,
            "Tool.Json.Name", "Tool.Json.Desc", "\uE943",
            () => new FormatterPage(FormatRegistry.Json),
            [".json"], ["json"], VisibleByDefault: true),

        new ToolDefinition(ToolKind.DataFormatter, ToolCategory.FormatConvert,
            "Tool.Formatter.Name", "Tool.Formatter.Desc", "\uE8E4",
            () => new FormatterPage(FormatRegistry.Auto),
            [".json", ".xml", ".csv"], ["beautify", "pretty", "format"], VisibleByDefault: true),

        new ToolDefinition(ToolKind.Xml, ToolCategory.FormatConvert,
            "Tool.Xml.Name", "Tool.Xml.Desc", "</>",
            () => new FormatterPage(FormatRegistry.Xml),
            [".xml"], ["xml"], VisibleByDefault: false),

        new ToolDefinition(ToolKind.DataConverter, ToolCategory.FormatConvert,
            "Tool.Converter.Name", "Tool.Converter.Desc", "\uE895",
            () => new ConverterPage(),
            [], ["convert", "csv"], VisibleByDefault: false),

        // ---- Encode & Decode ----------------------------------------------------------------
        new ToolDefinition(ToolKind.Base64, ToolCategory.EncodeDecode,
            "Tool.Base64.Name", "Tool.Base64.Desc", "01",
            () => new Base64Page(),
            [], ["base64", "encode", "decode"], VisibleByDefault: true),

        new ToolDefinition(ToolKind.UrlEncoding, ToolCategory.EncodeDecode,
            "Tool.Url.Name", "Tool.Url.Desc", "\uE71B",
            () => new UrlPage(),
            [], ["url", "urlencode", "percent", "html"], VisibleByDefault: false),

        new ToolDefinition(ToolKind.Jwt, ToolCategory.EncodeDecode,
            "Tool.Jwt.Name", "Tool.Jwt.Desc", "\uE8D7",
            () => new JwtPage(),
            [], ["jwt", "token", "bearer", "claims"], VisibleByDefault: true),

        new ToolDefinition(ToolKind.Hash, ToolCategory.EncodeDecode,
            "Tool.Hash.Name", "Tool.Hash.Desc", "\uE928",
            () => new HashPage(),
            [], ["md5", "sha", "sha256", "checksum", "hmac", "crc32"], VisibleByDefault: false),

        // ---- Privacy --------------------------------------------------------------------------
        new ToolDefinition(ToolKind.DataMasker, ToolCategory.Privacy,
            "Tool.Masker.Name", "Tool.Masker.Desc", "\uE83D",
            () => new MaskerPage(),
            [".json", ".xml", ".csv", ".txt", ".log"], ["pii", "redact", "anonymise", "anonymize", "mask"],
            VisibleByDefault: true),

        // ---- Text -------------------------------------------------------------------------
        new ToolDefinition(ToolKind.TextToolkit, ToolCategory.Text,
            "Tool.TextToolkit.Name", "Tool.TextToolkit.Desc", "\uE70F",
            () => new TextToolkitPage(),
            [".txt"], ["case", "slugify", "dedupe", "sort"], VisibleByDefault: false),

        new ToolDefinition(ToolKind.TextDiff, ToolCategory.Text,
            "Tool.Diff.Name", "Tool.Diff.Desc", "\uE8AB",
            () => new DiffPage(),
            [".txt"], ["diff", "compare"], VisibleByDefault: true),

        new ToolDefinition(ToolKind.RegexTester, ToolCategory.Text,
            "Tool.Regex.Name", "Tool.Regex.Desc", "\uE721",
            () => new RegexPage(),
            [], ["regex", "pattern", "regexp"], VisibleByDefault: false),

        // ---- Generate ---------------------------------------------------------------------
        new ToolDefinition(ToolKind.Uuid, ToolCategory.Generate,
            "Tool.Uuid.Name", "Tool.Uuid.Desc", "\uE8EC",
            () => new UuidPage(),
            [], ["guid", "uuid"], VisibleByDefault: false),

        new ToolDefinition(ToolKind.Password, ToolCategory.Generate,
            "Tool.Password.Name", "Tool.Password.Desc", "\uE72E",
            () => new PasswordPage(),
            [], ["password", "secret", "random", "passphrase", "entropy"], VisibleByDefault: false),

        new ToolDefinition(ToolKind.Lorem, ToolCategory.Generate,
            "Tool.Lorem.Name", "Tool.Lorem.Desc", "\uE8A5",
            () => new LoremPage(),
            [], ["lorem", "ipsum", "placeholder", "dummy", "filler"], VisibleByDefault: false),

        new ToolDefinition(ToolKind.Color, ToolCategory.Generate,
            "Tool.Color.Name", "Tool.Color.Desc", "\uE790",
            () => new ColorPage(),
            [], ["color", "colour", "hex", "rgb", "hsl", "contrast"], VisibleByDefault: false),

        // ---- Numbers & Time -----------------------------------------------------------------
        new ToolDefinition(ToolKind.Timestamp, ToolCategory.NumbersTime,
            "Tool.Timestamp.Name", "Tool.Timestamp.Desc", "\uE917",
            () => new TimestampPage(),
            [], ["epoch", "unix", "timestamp"], VisibleByDefault: true),

        new ToolDefinition(ToolKind.Cron, ToolCategory.NumbersTime,
            "Tool.Cron.Name", "Tool.Cron.Desc", "\uE8EE",
            () => new CronPage(),
            [], ["cron", "crontab", "schedule", "quartz"], VisibleByDefault: false),

        new ToolDefinition(ToolKind.NumberBase, ToolCategory.NumbersTime,
            "Tool.NumberBase.Name", "Tool.NumberBase.Desc", "\uE8EF",
            () => new NumberBasePage(),
            [], ["hex", "binary", "octal", "bitwise"], VisibleByDefault: false),
    ];
}
