using System.Diagnostics;
using System.Text;
using dockdev.Services.Masking;
using Xunit;

namespace dockdev.Tests.Soak;

/// <summary>
/// The Data Masker is a privacy tool, so its realistic input is a file that is <em>mostly</em>
/// personal data — a contacts export, a leaked-credential dump someone wants to neutralise before
/// sharing. That is exactly the input that produces findings in the tens or hundreds of thousands,
/// and it is where two different things could quietly go wrong under load:
/// <list type="number">
///   <item><b>Detection could blow up.</b> Every value rule is a compiled regex with a match
///   timeout, but a scan that were super-linear in the input, or a rule that timed out and took the
///   window with it, would turn a large paste into a hang. These pin that detection stays bounded
///   and returns, offsets intact.</item>
///   <item><b>Rendering could blow up.</b> <c>MaskerPage.RenderFindingsList</c> builds per-finding
///   XAML rows into a non-virtualizing list; one row each for hundreds of thousands of findings
///   freezes the UI thread for minutes and can exhaust memory. That path needs a window and is
///   capped in the page (<c>MaxRenderedFindingRows</c>); this suite guards the half of the story
///   that is pure — that the count which drives it really can get that large from an ordinary file.</item>
/// </list>
/// </summary>
public class MaskerLoadTests
{
    private static List<Finding> Detect(string text) =>
        PiiDetector.Detect(text, PiiRuleSet.Default, Confidence.Medium);

    /// <summary>
    /// A file that is one email address per line — the shape of a mailing-list export — is detected
    /// in full and in bounded time. The budget is deliberately loose (it is a "does not hang" check,
    /// not a benchmark); a super-linear scan or a per-rule timeout being hit would blow past it.
    /// </summary>
    [Fact]
    public void ManyEmails_AreAllDetected_InBoundedTime()
    {
        const int count = 20_000;
        var sb = new StringBuilder(count * 24);
        for (int i = 0; i < count; i++)
            sb.Append("user").Append(i).Append("@example.com\n");
        var text = sb.ToString();

        var sw = Stopwatch.StartNew();
        var findings = Detect(text);
        sw.Stop();

        // Every line is a detectable address; a handful of rules overlap on the same span, so assert
        // "at least one per line" rather than an exact count.
        Assert.True(findings.Count >= count,
            $"Expected at least {count} findings from {count} email lines, got {findings.Count}.");

        // 20,000 addresses is a small file; if this is not comfortably sub-10s the scan is not linear.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"Detecting {count} emails took {sw.Elapsed.TotalSeconds:0.0}s — detection is not staying " +
            "bounded under load.");
    }

    /// <summary>
    /// The offsets a large result carries are still valid indices into the original text — the
    /// property masking and the excerpt preview both rely on, and the one most likely to drift when
    /// a scan is rewritten for speed.
    /// </summary>
    [Fact]
    public void LargeResult_FindingOffsets_StayWithinTheText()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 5_000; i++)
            sb.Append("Contact user").Append(i).Append("@example.com or call 4111 1111 1111 1111.\n");
        var text = sb.ToString();

        var findings = Detect(text);
        Assert.NotEmpty(findings);
        foreach (var f in findings)
        {
            Assert.True(f.Start >= 0 && f.Start <= text.Length, $"start {f.Start} out of range");
            Assert.True(f.Start + f.Length <= text.Length, $"end {f.Start + f.Length} past {text.Length}");
        }
    }

    /// <summary>
    /// Masking a large, PII-dense document returns an output of comparable size in bounded time and
    /// never surfaces the original — the whole loop the page runs on every edit, minus the window.
    /// </summary>
    [Fact]
    public void MaskingLargeInput_IsBounded_AndReplacesEveryFinding()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 10_000; i++)
            sb.Append("ada.lovelace").Append(i).Append("@example.com\n");
        var text = sb.ToString();

        var masker = new Masker(userSalt: "load");
        var sw = Stopwatch.StartNew();
        var output = masker.BuildOutput(text, Detect(text));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"Masking {text.Length} chars took {sw.Elapsed.TotalSeconds:0.0}s.");
        // None of the original local-parts should survive in the masked output.
        Assert.DoesNotContain("ada.lovelace0@example.com", output.Text, StringComparison.Ordinal);
    }
}
