using System.Xml;
using System.Xml.Linq;
using dockdev.Models;
using dockdev.Services.Syntax;

namespace dockdev.Services.Formats;

/// <summary>
/// The XML format. Every reader is XXE- and entity-expansion-hardened (design doc §21):
/// <see cref="XmlReaderSettings.DtdProcessing"/> is <see cref="DtdProcessing.Prohibit"/> and
/// <see cref="XmlReaderSettings.XmlResolver"/> is null, which blocks both classic XXE (reading
/// local files via an external entity) and billion-laughs expansion by refusing any DOCTYPE at
/// all. This is enforced by <c>dockdev.Tests.Formats.XmlFormatSecurityTests</c>, not by convention.
/// </summary>
public sealed class XmlFormat : IDataFormat
{
    public static readonly XmlFormat Instance = new();

    public string Id => "xml";
    public string DisplayNameKey => "Format.Xml";
    public string[] Extensions => [".xml"];
    public ITokenizer Tokenizer => XmlTokenizer.Instance;

    public int DetectConfidence(ReadOnlySpan<char> text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 0 && trimmed[0] == '<' ? 80 : 0;
    }

    public FormatResult Format(string text, FormatOptions options)
    {
        try
        {
            var doc = LoadSecure(text);
            using var sw = new StringWriter();
            using (var writer = XmlWriter.Create(sw, WriterSettings(indent: true, options.IndentWidth)))
                doc.Save(writer);
            return FormatResult.Ok(sw.ToString());
        }
        catch (XmlException ex)
        {
            return FormatResult.Fail(ToDiagnostic(ex));
        }
    }

    public FormatResult Minify(string text)
    {
        try
        {
            var doc = LoadSecure(text);
            using var sw = new StringWriter();
            using (var writer = XmlWriter.Create(sw, WriterSettings(indent: false, 0)))
                doc.Save(writer);
            return FormatResult.Ok(sw.ToString());
        }
        catch (XmlException ex)
        {
            return FormatResult.Fail(ToDiagnostic(ex));
        }
    }

    public IReadOnlyList<Diagnostic> Validate(string text)
    {
        try
        {
            LoadSecure(text);
            return [];
        }
        catch (XmlException ex)
        {
            return [ToDiagnostic(ex)];
        }
    }

    public DataNode ToCanonical(string text)
    {
        var doc = LoadSecure(text);
        var root = doc.Root ?? throw new XmlException("Document has no root element.");
        return new ObjectNode([(root.Name.LocalName, ConvertElement(root))]);
    }

    public string FromCanonical(DataNode node, FormatOptions options)
    {
        string rootName = "root";
        DataNode content = node;
        if (node is ObjectNode { Members.Count: 1 } obj)
        {
            rootName = obj.Members[0].Key;
            content = obj.Members[0].Value;
        }

        var root = BuildElement(rootName, content);
        using var sw = new StringWriter();
        using (var writer = XmlWriter.Create(sw, WriterSettings(indent: true, options.IndentWidth)))
            new XDocument(root).Save(writer);
        return sw.ToString();
    }

    // ---- Internals ----------------------------------------------------------------------

    /// <summary>The one place an <see cref="XmlReader"/> is constructed for untrusted text —
    /// hardened against XXE and billion-laughs by refusing DTDs entirely.</summary>
    private static XDocument LoadSecure(string text)
    {
        using var stringReader = new StringReader(text);
        using var reader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
        });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static XmlWriterSettings WriterSettings(bool indent, int indentWidth) => new()
    {
        Indent = indent,
        IndentChars = new string(' ', Math.Clamp(indentWidth <= 0 ? 2 : indentWidth, 1, 8)),
        OmitXmlDeclaration = true,
        NewLineOnAttributes = false,
    };

    private static Diagnostic ToDiagnostic(XmlException ex) => new(ex.LineNumber, ex.LinePosition, ex.Message);

    private static string SafeXmlName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "item";
        try
        {
            var encoded = XmlConvert.EncodeLocalName(name);
            return string.IsNullOrEmpty(encoded) ? "item" : encoded;
        }
        catch
        {
            return "item";
        }
    }

    /// <summary>
    /// Repeated sibling elements collapse into an <see cref="ArrayNode"/> (the conventional
    /// XML→JSON shape); a leaf with no attributes becomes a bare <see cref="ScalarNode"/> so a
    /// simple <c>&lt;name&gt;Ada&lt;/name&gt;</c> round-trips as the string <c>"Ada"</c> rather
    /// than an object wrapper.
    /// </summary>
    private static DataNode ConvertElement(XElement element)
    private static DataNode ConvertElement(XElement element, int depth = 0)
    {
        if (depth > 128)
            return new ScalarNode(element.Value, ScalarKind.String);

        var members = new List<(string Key, DataNode Value)>();
        foreach (var attr in element.Attributes())
        {
            if (!attr.IsNamespaceDeclaration)
                members.Add(("@" + attr.Name.LocalName, new ScalarNode(attr.Value, ScalarKind.String)));
        }

        var childElements = element.Elements().ToList();
        if (childElements.Count == 0)
        {
            if (members.Count == 0)
                return new ScalarNode(element.Value, ScalarKind.String);
            members.Add(("#text", new ScalarNode(element.Value, ScalarKind.String)));
            return new ObjectNode(members);
        }

        var order = new List<string>();
        var groups = new Dictionary<string, List<DataNode>>();
        foreach (var child in childElements)
        {
            var name = child.Name.LocalName;
            if (!groups.TryGetValue(name, out var list))
            {
                list = [];
                groups[name] = list;
                order.Add(name);
            }
            list.Add(ConvertElement(child));
            list.Add(ConvertElement(child, depth + 1));
        }
        foreach (var name in order)
        {
            var list = groups[name];
            members.Add((name, list.Count > 1 ? new ArrayNode(list) : list[0]));
        }
        return new ObjectNode(members);
    }

    private static XElement BuildElement(string name, DataNode node)
    private static XElement BuildElement(string name, DataNode node, int depth = 0)
    {
        var el = new XElement(name);
        var safeName = SafeXmlName(name);
        var el = new XElement(safeName);
        if (depth > 128)
        {
            el.Value = (node as ScalarNode)?.Raw ?? "";
            return el;
        }

        switch (node)
        {
            case ScalarNode s:
                el.Value = s.Kind == ScalarKind.Null ? "" : s.Raw ?? "";
                break;
            case ArrayNode arr:
                // Reached only for an array with no member name of its own (e.g. the canonical
                // root is itself an array); "item" is the least-surprising synthetic tag.
                foreach (var item in arr.Items)
                    el.Add(BuildElement("item", item));
                    el.Add(BuildElement("item", item, depth + 1));
                break;
            case ObjectNode obj:
                foreach (var (key, value) in obj.Members)
                {
                    if (key.StartsWith('@'))
                        el.SetAttributeValue(key[1..], (value as ScalarNode)?.Raw ?? "");
                    {
                        var attrName = SafeXmlName(key[1..]);
                        try
                        {
                            el.SetAttributeValue(attrName, (value as ScalarNode)?.Raw ?? "");
                        }
                        catch (XmlException) { }
                    }
                    else if (key == "#text")
                        el.Value = (value as ScalarNode)?.Raw ?? "";
                    else if (value is ArrayNode valueArray)
                        foreach (var item in valueArray.Items)
                            el.Add(BuildElement(key, item));
                            el.Add(BuildElement(key, item, depth + 1));
                    else
                        el.Add(BuildElement(key, value));
                        el.Add(BuildElement(key, value, depth + 1));
                }
                break;
        }
        return el;
    }
}
