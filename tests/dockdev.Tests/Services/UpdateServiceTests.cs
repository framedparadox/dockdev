using dockdev.Services;
using Xunit;

namespace dockdev.Tests.Services;

/// <summary>
/// The update check is the only part of dockdev that reads data it did not produce, and the only
/// value it takes from that data and gives back to the user is a URL — which
/// <c>SettingsWindow</c> puts on a <c>HyperlinkButton</c>, i.e. hands to the shell. So the vetting
/// of that URL is the security boundary of the whole feature, and it is tested as one.
/// </summary>
public class UpdateServiceTests
{
    [Theory]
    // The shapes that matter: another protocol entirely, a look-alike host, a host that merely
    // ends in the real one, credentials smuggled in the authority, and an odd port.
    [InlineData("file://server/share/payload.exe")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://github.com/framedparadox/dockdev/releases/tag/v9")]
    [InlineData("https://github.com.evil.example/framedparadox/dockdev")]
    [InlineData("https://evil.example/framedparadox/dockdev")]
    [InlineData("https://notgithub.com/x")]
    [InlineData("https://user:pass@github.com/x")]
    [InlineData("https://github.com:8443/x")]
    [InlineData("not a url at all")]
    [InlineData("")]
    [InlineData(null)]
    public void HostileReleaseUrl_FallsBackToTheKnownReleasesPage(string? candidate)
    {
        Assert.Equal(UpdateService.ReleasesPage, UpdateService.SafeReleaseUrl(candidate));
    }

    [Theory]
    [InlineData("https://github.com/framedparadox/dockdev/releases/tag/v1.2.0")]
    [InlineData("https://GitHub.com/framedparadox/dockdev/releases/latest")]
    public void GenuineGitHubReleaseUrl_IsKept(string candidate)
    {
        var result = UpdateService.SafeReleaseUrl(candidate);
        Assert.StartsWith("https://github.com/", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dockdev", result, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryVettedUrl_IsSomethingUriCanConstruct()
    {
        // The point of vetting at the boundary is that the UI can build a Uri from the result
        // without a try/catch — a UriFormatException inside a click handler is a crash.
        foreach (var candidate in new[] { "https://github.com/a/b", "gibberish", "", "file:///c:/x" })
        {
            var vetted = UpdateService.SafeReleaseUrl(candidate);
            Assert.True(Uri.TryCreate(vetted, UriKind.Absolute, out var uri));
            Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
        }
    }

    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("V2.0.1", "2.0.1")]
    public void ReleaseTag_ParsesWithOrWithoutTheVPrefix(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateService.ParseVersion(tag));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nightly")]
    [InlineData(null)]
    public void UnparseableTag_IsNullRatherThanAThrow(string? tag)
    {
        Assert.Null(UpdateService.ParseVersion(tag));
    }
}
