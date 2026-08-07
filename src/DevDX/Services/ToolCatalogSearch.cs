using DevDX.Models;

namespace DevDX.Services;

/// <summary>
/// The matching behind quick-launch search (design doc §18): which tools a query finds, and in
/// what order. Unlike a search over only what is pinned, this
/// searches the whole nineteen-tool catalog, because at this catalog size search is the primary
/// discovery path for the tools that never made it onto the dock, not just a shortcut for ones
/// that did.
/// <para>
/// Separate from <c>SearchWindow</c> so the ranking is testable: a WinUI <c>Window</c> can't be
/// constructed in a unit test.
/// </para>
/// </summary>
public static class ToolCatalogSearch
{
    /// <summary>How many matches to show.</summary>
    public const int DefaultLimit = 8;

    public const int RankNameStartsWith = 0;
    public const int RankNameContains = 1;
    public const int RankAliasMatch = 2;
    public const int RankDescriptionContains = 3;
    public const int NoMatch = int.MaxValue;

    /// <summary>
    /// The best <paramref name="limit"/> matches for <paramref name="query"/>, best first. An
    /// empty query returns the catalog in declaration order, so the card is useful before a
    /// single key is typed.
    /// </summary>
    public static IReadOnlyList<ToolDefinition> Filter(string? query, int limit = DefaultLimit)
    {
        query = query?.Trim() ?? string.Empty;
        var candidates = ToolCatalog.All;

        if (query.Length == 0)
            return candidates.Take(limit).ToList();

        return candidates
            .Select(tool => (tool, rank: Rank(tool, query)))
            .Where(x => x.rank != NoMatch)
            .OrderBy(x => x.rank)
            .ThenBy(x => x.tool.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(x => x.tool)
            .ToList();
    }

    public static int Rank(ToolDefinition tool, string query)
    {
        if (string.IsNullOrEmpty(query))
            return RankNameStartsWith;

        var name = tool.DisplayName;
        if (name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
            return RankNameStartsWith;
        if (name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            return RankNameContains;
        if (tool.SearchAliases.Any(a => a.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            return RankAliasMatch;
        if (tool.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            return RankDescriptionContains;
        return NoMatch;
    }
}
