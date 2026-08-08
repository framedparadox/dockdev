using Xunit;

namespace dockdev.Tests.Shell;

/// <summary>
/// Design doc §13.3: "High Contrast always wins." <see cref="Controls.CodeView"/> and
/// <see cref="dockdev.DockWindow"/> each carried their own private copy of the High Contrast check, and
/// <see cref="Controls.ToolStatusBar"/> carried none at all — it hard-coded a red foreground with no
/// guard, and <see cref="Controls.CodeEditor"/> hard-coded a Black/White default with no guard
/// either, both fighting whatever palette the user actually chose. <c>Services/HighContrast.cs</c>
/// is the one sanctioned place that check lives now; these tests are what keep a future control from
/// quietly reinventing (or, worse, simply omitting) it, the same way
/// <c>NetworkPolicyArchitectureTests</c> keeps <c>HttpClient</c> construction inside
/// <c>NetworkPolicy.cs</c>.
/// </summary>
public class HighContrastArchitectureTests
{
    [Fact]
    public void NoFileOutsideHighContrastConstructsAccessibilitySettingsDirectly()
    {
        var srcRoot = FindSourceRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) is "HighContrast.cs")
                continue;

            var text = File.ReadAllText(file);
            if (text.Contains("new Windows.UI.ViewManagement.AccessibilitySettings(", StringComparison.Ordinal))
                offenders.Add(Path.GetRelativePath(srcRoot, file));
        }

        Assert.True(offenders.Count == 0,
            "These files construct AccessibilitySettings directly instead of going through " +
            "Services.HighContrast: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// A control that branches on <c>ActualTheme</c> to pick a literal foreground colour is exactly
    /// the shape of the bug this file exists for: a colour tuned for Light/Dark that was never
    /// checked against the third theme the OS actually offers. This does not require every control
    /// to be High-Contrast-aware — most never hard-code a colour at all, and are unaffected — only
    /// that the ones that do also reference the shared helper somewhere in the same file.
    /// </summary>
    [Fact]
    public void EveryThemeConditionalHardcodedColourInControlsChecksHighContrast()
    {
        var controlsDir = Path.Combine(FindSourceRoot(), "Controls");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(controlsDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var text = File.ReadAllText(file);
            bool branchesOnTheme = text.Contains("ActualTheme", StringComparison.Ordinal);
            bool picksALiteralColour =
                text.Contains("Color.FromArgb(", StringComparison.Ordinal) ||
                text.Contains("Colors.Black", StringComparison.Ordinal) ||
                text.Contains("Colors.White", StringComparison.Ordinal);

            if (branchesOnTheme && picksALiteralColour && !text.Contains("HighContrast", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0,
            "These controls pick a hard-coded colour based on ActualTheme but never check " +
            "HighContrast, so a High Contrast theme gets a colour tuned for Light/Dark instead of " +
            "its own palette: " + string.Join(", ", offenders));
    }

    /// <summary>Walks up from the test binary's output directory to find <c>src/dockdev</c>, the same
    /// way <c>NetworkPolicyArchitectureTests</c> does.</summary>
    private static string FindSourceRoot()
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
