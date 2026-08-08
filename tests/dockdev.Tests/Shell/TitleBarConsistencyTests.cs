using System.Globalization;
using System.Xml.Linq;
using dockdev.Services;
using Xunit;

namespace dockdev.Tests.Shell;

/// <summary>
/// A custom title bar has to be exactly as tall as the caption buttons WinUI draws beside it, and
/// no layout pass can discover a mismatch — the two are sized by different things. Settings,
/// Add-Tool and every tool window each drew a 40px bar next to 32px buttons: three separate
/// authorings of the same element, which had also drifted to three different left insets, three
/// glyph sizes and two title styles.
/// <para>
/// <c>Controls/AppTitleBar</c> now builds the code-side ones from
/// <see cref="WindowChrome.TitleBarHeight"/>, so those cannot drift again. The Settings window's is
/// XAML, which cannot read a C# constant — these tests are what holds it to the same number, and
/// what catches a window that extends into its title bar without asking for the matching height.
/// </para>
/// </summary>
public class TitleBarConsistencyTests
{
    [Fact]
    public void TheTitleBarHeightIsOneWinUIWillDrawCaptionButtonsAt()
    {
        // PreferredHeightOption offers Standard (32) and Tall (48) and nothing between, so any
        // other value is a title bar that cannot line up with its own window's buttons.
        Assert.Contains(WindowChrome.TitleBarHeight, new double[] { 32, 48 });
    }

    [Fact]
    public void SettingsWindowsXamlTitleBarMatchesTheConstant()
    {
        var height = SettingsTitleBar().Attribute("Height")!.Value;

        Assert.Equal(WindowChrome.TitleBarHeight, double.Parse(height, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void SettingsWindowsXamlTitleBarUsesTheSharedInsetAndCaptionReserve()
    {
        // Left inset so the title lines up with the content below it; right inset so a long title
        // is not laid out underneath the close button.
        var margin = SettingsTitleBar()
            .Descendants()
            .Select(e => e.Attribute("Margin")?.Value)
            .First(m => m is not null)!
            .Split(',')
            .Select(p => double.Parse(p, CultureInfo.InvariantCulture))
            .ToArray();

        Assert.Equal(4, margin.Length);
        Assert.Equal(WindowChrome.TitleBarContentInset, margin[0]);
        Assert.Equal(WindowChrome.CaptionButtonReserve, margin[2]);
    }

    [Fact]
    public void EveryWindowWithCaptionButtonsAsksForTheMatchingHeight()
    {
        // Setting ExtendsContentIntoTitleBar and not UseTallTitleBar leaves the buttons at the
        // 32px default under a 48px bar — the bug this file exists for, in reverse.
        //
        // A borderless window is exempt and must be: the dock and the quick-launch card strip the
        // frame entirely, so they have no caption buttons for anything to line up with.
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(SourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var parts = file.Split(Path.DirectorySeparatorChar);
            if (parts.Contains("obj") || parts.Contains("bin"))
                continue;

            var text = File.ReadAllText(file);
            if (!text.Contains("ExtendsContentIntoTitleBar = true", StringComparison.Ordinal))
                continue;
            if (text.Contains("MakeBorderlessToolWindow", StringComparison.Ordinal))
                continue;
            if (!text.Contains("UseTallTitleBar", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0,
            "These windows extend content into the title bar without asking for the matching " +
            "caption-button height: " + string.Join(", ", offenders));
    }

    // ---- Helpers -----------------------------------------------------------

    private static XElement SettingsTitleBar()
    {
        var xaml = XDocument.Load(Path.Combine(SourceRoot, "SettingsWindow.xaml"));
        var name = XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml");
        return xaml.Descendants().Single(e => e.Attribute(name)?.Value == "AppTitleBar");
    }

    private static string SourceRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "src", "dockdev");
                if (Directory.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate src/dockdev from " + AppContext.BaseDirectory);
        }
    }
}
