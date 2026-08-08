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
}
