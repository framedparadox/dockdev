using Xunit;

namespace DevDX.Tests.Services;

/// <summary>
/// Design doc §21: "an architecture test that scans the source tree for <c>new HttpClient(</c>
/// outside <c>NetworkPolicy.cs</c> and fails the build if it finds one — the rule is only real if
/// it cannot be bypassed by forgetting it."
/// </summary>
public class NetworkPolicyArchitectureTests
{
    [Fact]
    public void NoComponentConstructsHttpClientDirectly_ExceptNetworkPolicy()
    {
        var srcRoot = FindSourceRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name is "NetworkPolicy.cs")
                continue;

            var text = File.ReadAllText(file);
            if (text.Contains("new HttpClient(", StringComparison.Ordinal))
                offenders.Add(file);
        }

        Assert.True(offenders.Count == 0,
            "These files construct HttpClient directly, bypassing NetworkPolicy: " + string.Join(", ", offenders));
    }

    /// <summary>Walks up from the test binary's output directory to find <c>src/DevDX</c>.</summary>
    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "DevDX");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate src/DevDX from " + AppContext.BaseDirectory);
    }
}
