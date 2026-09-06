using System.Text;
using System.Xml;
using dockdev.Services.Formats;
using Xunit;

namespace dockdev.Tests.Formats;

/// <summary>
/// Enforces design doc §21's XXE/entity-expansion hardening as a test, not a convention: exactly
/// the kind of setting that silently rots if only "checked by code review".
/// </summary>
public class XmlFormatSecurityTests
{
    [Fact]
    public void ExternalEntity_IsRejected_NotResolved()
    {
        const string xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE root [ <!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini"> ]>
            <root>&xxe;</root>
            """;

        var diagnostics = XmlFormat.Instance.Validate(xxe);
        Assert.NotEmpty(diagnostics);

        var result = XmlFormat.Instance.Format(xxe, FormatOptions.Default);
        Assert.False(result.Success);
    }

    [Fact]
    public void BillionLaughs_IsRefused_NotExpanded()
    {
        const string billionLaughs = """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [
              <!ENTITY lol "lol">
              <!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
            ]>
            <lolz>&lol2;</lolz>
            """;

        var result = XmlFormat.Instance.Format(billionLaughs, FormatOptions.Default);
        Assert.False(result.Success);
    }

    [Fact]
    public void AnyDoctype_IsProhibited()
    {
        const string withDoctype = "<!DOCTYPE root [ <!ELEMENT root (#PCDATA)> ]><root>hi</root>";
        var diagnostics = XmlFormat.Instance.Validate(withDoctype);
        Assert.NotEmpty(diagnostics);
    }

    [Fact]
    public void WellFormedXml_WithoutDoctype_Validates()
    {
        var diagnostics = XmlFormat.Instance.Validate("<root><child>text</child></root>");
        Assert.Empty(diagnostics);
    }

    // ---- Deep nesting: a parse bomb the DTD guards do not cover ------------------------------
    //
    // JSON is capped by System.Text.Json's MaxDepth; XML had no equivalent, so a document a few
    // thousand elements deep — a sub-100 KB string, well within the 50 MB text ceiling — reached
    // three distinct faults, none of them an XmlException the pages catch: ConvertElement (the Data
    // Converter's ToCanonical) and StructureTree.Populate recurse per level and overflow the call
    // stack (an *uncatchable* StackOverflowException that kills the process outright), and an
    // indented Save is O(depth²) and hits OutOfMemoryException first. XmlFormat now refuses anything
    // past its depth cap up front, which turns all three into an ordinary reported error.

    /// <summary><paramref name="depth"/> nested &lt;a&gt; elements around a text core.</summary>
    private static string NestedXml(int depth)
    {
        var sb = new StringBuilder(depth * 8);
        for (int i = 0; i < depth; i++) sb.Append("<a>");
        sb.Append("core");
        for (int i = 0; i < depth; i++) sb.Append("</a>");
        return sb.ToString();
    }

    [Fact]
    public void DeeplyNestedXml_IsRefused_NotCrashed_OnFormat()
    {
        // 20,000 deep: past the point where ConvertElement overflows the stack and an indented Save
        // exhausts memory, so a naive implementation never returns from this call at all — the test
        // process would die. Reaching the assertion is itself most of the point.
        var result = XmlFormat.Instance.Format(NestedXml(20_000), FormatOptions.Default);
        Assert.False(result.Success);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void DeeplyNestedXml_IsRefused_NotCrashed_OnMinify()
    {
        var result = XmlFormat.Instance.Minify(NestedXml(20_000));
        Assert.False(result.Success);
    }

    [Fact]
    public void DeeplyNestedXml_IsRefused_NotCrashed_OnValidate()
    {
        Assert.NotEmpty(XmlFormat.Instance.Validate(NestedXml(20_000)));
    }

    [Fact]
    public void DeeplyNestedXml_Converting_ThrowsCatchably_NotStackOverflow()
    {
        // ToCanonical is the Data Converter's entry point and does not swallow exceptions of its own
        // (ConverterPage catches broadly). The contract this pins is that it throws an ordinary,
        // catchable XmlException rather than recursing into a StackOverflowException — the whole
        // reason the depth guard has to reject the document *before* ConvertElement is reached.
        Assert.Throws<XmlException>(() => XmlFormat.Instance.ToCanonical(NestedXml(20_000)));
    }

    [Fact]
    public void ModeratelyNestedXml_WithinTheCap_StillWorks()
    {
        // The cap must reject parse bombs without rejecting any realistic document. 200 deep is far
        // past anything hand- or machine-authored and still well under the limit, so it round-trips.
        var xml = NestedXml(200);
        Assert.Empty(XmlFormat.Instance.Validate(xml));

        var formatted = XmlFormat.Instance.Format(xml, FormatOptions.Default);
        Assert.True(formatted.Success);

        var canonical = XmlFormat.Instance.ToCanonical(xml); // must not throw
        Assert.NotNull(canonical);
    }
}
