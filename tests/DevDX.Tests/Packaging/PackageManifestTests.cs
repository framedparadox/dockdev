using System.Xml.Linq;
using Xunit;

namespace DevDX.Tests.Packaging;

/// <summary>
/// The Microsoft Store rejects a submission for manifest problems <em>after</em> the upload, and
/// several of them are invisible until then — a version whose fourth component is not zero, a logo
/// the manifest names but the repository does not contain, a startup task whose id does not match
/// the one the code asks Windows for.
/// <para>
/// Design doc §25 says the Store package is built on demand with <c>-p:StorePackage=true</c>, which
/// means the manifest is never compiled by an ordinary build or by CI — nothing was checking it at
/// all. These are the checks that do not need the packaging tooling installed: pure statements
/// about the manifest, the project file, and the files on disk beside them.
/// </para>
/// </summary>
public class PackageManifestTests
{
    private static readonly XNamespace Foundation =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Desktop =
        "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
    private static readonly XNamespace Rescap =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    private static XElement Package => XDocument.Load(ManifestPath).Root!;

    private static string ManifestPath => Path.Combine(SourceRoot, "Package.appxmanifest");
    private static string ProjectPath => Path.Combine(SourceRoot, "DevDX.csproj");
    private static string AppManifestPath => Path.Combine(SourceRoot, "app.manifest");

    // ---- Identity ----------------------------------------------------------

    [Fact]
    public void Identity_IsNotAToolingPlaceholder()
    {
        var identity = Package.Element(Foundation + "Identity")!;
        var name = identity.Attribute("Name")!.Value;
        var publisher = identity.Attribute("Publisher")!.Value;

        Assert.DoesNotContain("Placeholder", name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MyApp", name, StringComparison.OrdinalIgnoreCase);
        // The publisher has to be the account's publisher ID, spelled as a distinguished name; a
        // mismatch is rejected at upload with an error that names neither field.
        Assert.StartsWith("CN=", publisher, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityVersion_LeavesTheRevisionComponentToTheStore()
    {
        var version = Version.Parse(Package.Element(Foundation + "Identity")!.Attribute("Version")!.Value);

        // The Store reserves the fourth component and refuses any submission that sets it.
        Assert.Equal(0, version.Revision);
        Assert.True(version.Major >= 0 && version.Minor >= 0 && version.Build >= 0);
    }

    [Fact]
    public void EveryVersionInTheRepositoryAgrees()
    {
        // Three files carry a version: the one in the Store listing, the one the .exe reports, and
        // the one in the side-by-side assembly identity. A user reporting a bug cites whichever one
        // they can see, so they have to be the same number.
        var manifestVersion = Version.Parse(
            Package.Element(Foundation + "Identity")!.Attribute("Version")!.Value);

        var project = XDocument.Load(ProjectPath).Root!;
        var projectVersion = Version.Parse(
            project.Descendants("Version").First().Value);
        var assemblyVersion = Version.Parse(
            project.Descendants("AssemblyVersion").First().Value);

        var appManifestVersion = Version.Parse(
            XDocument.Load(AppManifestPath).Root!
                .Elements().First(e => e.Name.LocalName == "assemblyIdentity")
                .Attribute("version")!.Value);

        Assert.Equal(manifestVersion, projectVersion);
        Assert.Equal(manifestVersion, assemblyVersion);
        Assert.Equal(manifestVersion, appManifestVersion);
    }

    [Fact]
    public void DisplayNamesAgreeBetweenThePropertiesAndTheApplication()
    {
        // The Start menu entry, the installed-apps list and the Store listing all read from these,
        // and a mismatch reads as two different products.
        var properties = Package.Element(Foundation + "Properties")!;
        var visual = Package
            .Element(Foundation + "Applications")!
            .Element(Foundation + "Application")!
            .Element(Uap + "VisualElements")!;

        Assert.Equal(
            properties.Element(Foundation + "DisplayName")!.Value,
            visual.Attribute("DisplayName")!.Value);

        Assert.False(string.IsNullOrWhiteSpace(
            properties.Element(Foundation + "PublisherDisplayName")!.Value));
    }

    // ---- Assets ------------------------------------------------------------

    [Fact]
    public void EveryImageTheManifestNamesExists()
    {
        var visual = Package
            .Element(Foundation + "Applications")!
            .Element(Foundation + "Application")!
            .Element(Uap + "VisualElements")!;

        var referenced = new List<string?>
        {
            Package.Element(Foundation + "Properties")!.Element(Foundation + "Logo")!.Value,
            visual.Attribute("Square150x150Logo")?.Value,
            visual.Attribute("Square44x44Logo")?.Value,
            visual.Element(Uap + "DefaultTile")?.Attribute("Wide310x150Logo")?.Value,
            visual.Element(Uap + "DefaultTile")?.Attribute("Square71x71Logo")?.Value,
            visual.Element(Uap + "DefaultTile")?.Attribute("Square310x310Logo")?.Value,
            visual.Element(Uap + "SplashScreen")?.Attribute("Image")?.Value,
        };

        var missing = referenced
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!.Replace('\\', Path.DirectorySeparatorChar))
            .Where(r => !File.Exists(Path.Combine(SourceRoot, r)))
            .ToList();

        Assert.True(missing.Count == 0,
            "The manifest names images that are not in the repository: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheStoreLogoAndTheTileLogosAreDistinctFiles()
    {
        // A single image reused for every size is the classic reason a listing looks wrong at one
        // of them. This only asserts they are separate files, not that they are well drawn.
        var visual = Package
            .Element(Foundation + "Applications")!
            .Element(Foundation + "Application")!
            .Element(Uap + "VisualElements")!;

        var storeLogo = Package.Element(Foundation + "Properties")!.Element(Foundation + "Logo")!.Value;
        var tileLogo = visual.Attribute("Square150x150Logo")!.Value;

        Assert.NotEqual(storeLogo, tileLogo);
    }

    // ---- Capabilities and extensions ---------------------------------------

    [Fact]
    public void RunFullTrust_IsDeclared_BecauseTheApplicationIsAFullTrustWin32Executable()
    {
        // Design doc §29 risk 9 wonders whether DevDX needs runFullTrust at all. It does, and not
        // as a convenience: an Application with EntryPoint="Windows.FullTrustApplication" is a
        // packaged desktop app by definition, and Windows refuses to deploy one that has not
        // declared the capability. This test is the answer to that open question, written down.
        var application = Package
            .Element(Foundation + "Applications")!
            .Element(Foundation + "Application")!;

        Assert.Equal("Windows.FullTrustApplication", application.Attribute("EntryPoint")!.Value);

        var capabilities = Package.Element(Foundation + "Capabilities")!;
        Assert.Contains(capabilities.Elements(Rescap + "Capability"),
            c => c.Attribute("Name")!.Value == "runFullTrust");
    }

    [Fact]
    public void NoBroadOrSensitiveCapabilityIsDeclared()
    {
        // Every capability past runFullTrust lengthens Store review and has to be justified in the
        // listing. DevDX reads no location, no camera, no contacts, and — §21 — no arbitrary path.
        var forbidden = new[]
        {
            "broadFileSystemAccess", "location", "webcam", "microphone", "contacts",
            "appointments", "documentsLibrary", "picturesLibrary", "videosLibrary",
            "musicLibrary", "allowElevation", "packageQuery",
        };

        var declared = Package
            .Element(Foundation + "Capabilities")
            ?.Elements()
            .Select(e => e.Attribute("Name")?.Value ?? string.Empty)
            .Where(n => n.Length > 0)
            .ToList() ?? [];

        var offenders = declared
            .Where(n => forbidden.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Capabilities that need justifying at Store review: " + string.Join(", ", offenders));
    }

    [Fact]
    public void StartupTaskId_MatchesTheOneTheCodeAsksWindowsFor()
    {
        // StartupService documents that a mismatch makes StartupTask.GetAsync throw and the
        // "start with Windows" toggle a silent no-op. Documented, and until now unenforced — a
        // rename in either file alone would ship a setting that does nothing.
        var declared = Package
            .Element(Foundation + "Applications")!
            .Element(Foundation + "Application")!
            .Element(Foundation + "Extensions")!
            .Elements(Desktop + "Extension")
            .Where(e => e.Attribute("Category")?.Value == "windows.startupTask")
            .Select(e => e.Element(Desktop + "StartupTask")!.Attribute("TaskId")!.Value)
            .Single();

        var source = File.ReadAllText(Path.Combine(SourceRoot, "Services", "StartupService.cs"));
        Assert.Contains($"TaskId = \"{declared}\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupTask_ShipsDisabled_SoAutostartStaysOptIn()
    {
        var startupTask = Package
            .Element(Foundation + "Applications")!
            .Element(Foundation + "Application")!
            .Element(Foundation + "Extensions")!
            .Elements(Desktop + "Extension")
            .Select(e => e.Element(Desktop + "StartupTask"))
            .Single(e => e is not null)!;

        Assert.Equal("false", startupTask.Attribute("Enabled")!.Value, ignoreCase: true);
    }

    [Fact]
    public void ExecutableNameMatchesTheAssemblyTheProjectProduces()
    {
        var executable = Package
            .Element(Foundation + "Applications")!
            .Element(Foundation + "Application")!
            .Attribute("Executable")!.Value;

        var assemblyName = XDocument.Load(ProjectPath).Root!
            .Descendants("AssemblyName").First().Value;

        Assert.Equal(assemblyName + ".exe", executable);
    }

    [Fact]
    public void MinimumOsVersionMatchesTheProjectsTargetPlatformMinVersion()
    {
        var declared = Package
            .Element(Foundation + "Dependencies")!
            .Element(Foundation + "TargetDeviceFamily")!;

        var projectMin = XDocument.Load(ProjectPath).Root!
            .Descendants("TargetPlatformMinVersion").First().Value;

        Assert.Equal("Windows.Desktop", declared.Attribute("Name")!.Value);
        Assert.Equal(Version.Parse(projectMin), Version.Parse(declared.Attribute("MinVersion")!.Value));
    }

    // ---- The side-by-side manifest -----------------------------------------

    [Fact]
    public void TheExecutableRunsAsInvoker()
    {
        // A packaged app cannot request elevation — the Store rejects it — and an executable with
        // no explicit level is eligible for UAC installer detection and file/registry
        // virtualization. Both are decided by app.manifest, not by any code.
        var manifest = XDocument.Load(AppManifestPath);
        var level = manifest.Descendants()
            .Where(e => e.Name.LocalName == "requestedExecutionLevel")
            .Select(e => e.Attribute("level")?.Value)
            .SingleOrDefault();

        Assert.Equal("asInvoker", level);

        var uiAccess = manifest.Descendants()
            .Where(e => e.Name.LocalName == "requestedExecutionLevel")
            .Select(e => e.Attribute("uiAccess")?.Value)
            .Single();
        Assert.Equal("false", uiAccess);
    }

    [Fact]
    public void TheExecutableIsPerMonitorDpiAware()
    {
        // Windows' own baseline for a desktop app: without it, the dock is bitmap-stretched the
        // moment it crosses to a monitor at a different scale.
        var manifest = XDocument.Load(AppManifestPath);
        var awareness = manifest.Descendants()
            .Where(e => e.Name.LocalName == "dpiAwareness")
            .Select(e => e.Value)
            .Single();

        Assert.Contains("PerMonitorV2", awareness, StringComparison.Ordinal);
    }

    // ---- Helpers -----------------------------------------------------------

    /// <summary>Walks up from the test binary's output directory to find <c>src/DevDX</c>, the same
    /// way the other architecture tests do.</summary>
    private static string SourceRoot
    {
        get
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
}
