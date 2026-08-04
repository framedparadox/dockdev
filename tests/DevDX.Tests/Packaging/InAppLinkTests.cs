using System.Text.RegularExpressions;
using Xunit;

namespace DevDX.Tests.Packaging;

/// <summary>
/// Every link the app shows points at this repository, and a shipped build cannot be corrected once
/// one of them 404s. The privacy-policy link in Settings ▸ About pointed at
/// <c>docs/privacy-policy.md</c> for an entire release cycle while no such file existed — and it is
/// the one link a Store submission is *required* to have (policy 10.5.1), so it would have been
/// found by a reviewer rather than by us.
/// </summary>
public class InAppLinkTests
{
    /// <summary>
    /// <c>https://github.com/{owner}/{repo}/blob/{ref}/{path}</c> — the form that names a file in
    /// this repository, which is the only form that can be checked against the working tree.
    /// </summary>
    private static readonly Regex RepositoryFileLink = new(
        @"https://github\.com/framedparadox/dev-dx/blob/(?<ref>[^/""\s]+)/(?<path>[^""\s<>]+)",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

    [Fact]
    public void EveryInAppLinkToAFileInThisRepository_PointsAtAFileThatExists()
    {
        var missing = new List<string>();
        int found = 0;

        foreach (var link in RepositoryFileLinks())
        {
            found++;
            var onDisk = Path.Combine(RepositoryRoot, link.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(onDisk))
                missing.Add($"{Path.GetFileName(link.File)} → {link.Url}");
        }

        Assert.True(found > 0, "No in-app repository links were found — this test is checking nothing.");
        Assert.True(missing.Count == 0,
            "These links in the app point at files that are not in the repository: " +
            string.Join("; ", missing));
    }

    [Fact]
    public void RepositoryFileLinks_UseHeadRatherThanAHardCodedBranch()
    {
        // blob/HEAD resolves to whatever the default branch is called. A link pinned to a branch
        // name breaks the day that branch is renamed or retired, in builds already on users' PCs.
        var pinned = RepositoryFileLinks()
            .Where(l => !string.Equals(l.Ref, "HEAD", StringComparison.Ordinal))
            .Select(l => l.Url)
            .ToList();

        Assert.True(pinned.Count == 0,
            "Pinned to a branch name instead of HEAD: " + string.Join(", ", pinned));
    }

    [Fact]
    public void ThePrivacyPolicyIsLinkedFromInsideTheApp()
    {
        // Store policy 10.5.1 wants it reachable from the listing; having it in-app as well is what
        // makes the claim in About ("no telemetry, no accounts") checkable by the person reading it.
        var links = RepositoryFileLinks().Select(l => l.Path).ToList();
        Assert.Contains("docs/privacy-policy.md", links, StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "docs", "privacy-policy.md")));
    }

    private static List<(string File, string Url, string Ref, string Path)> RepositoryFileLinks()
    {
        var results = new List<(string File, string Url, string Ref, string Path)>();
        var source = Path.Combine(RepositoryRoot, "src", "DevDX");

        foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories))
        {
            if (Path.GetExtension(file) is not (".cs" or ".xaml"))
                continue;
            var parts = file.Split(Path.DirectorySeparatorChar);
            if (parts.Contains("obj") || parts.Contains("bin"))
                continue;

            foreach (Match match in RepositoryFileLink.Matches(File.ReadAllText(file)))
                results.Add((file, match.Value, match.Groups["ref"].Value, match.Groups["path"].Value));
        }

        return results;
    }

    /// <summary>Walks up from the test binary's output directory to the repository root — the
    /// directory that contains <c>src/DevDX</c>.</summary>
    private static string RepositoryRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "src", "DevDX")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate the repository root from " + AppContext.BaseDirectory);
        }
    }
}
