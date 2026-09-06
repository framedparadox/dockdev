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

    /// <summary>
    /// The nesting depth past which a document is refused. Matches <c>System.Text.Json</c>'s
    /// <c>MaxDepth</c> in <see cref="JsonFormat"/> (256), deliberately: the two formats convert into
    /// one another, so a cap that let XML nest deeper than JSON can parse would be asymmetric — and
    /// far more importantly, XML is the one format here with no built-in depth limit of its own.
    /// <para>
    /// Without this, deep nesting has three separate failure modes, none of them an
    /// <see cref="XmlException"/> the callers catch: <see cref="ConvertElement"/> and
    /// <c>StructureTree.Populate</c> recurse per level and blow the call stack — an uncatchable
    /// <c>StackOverflowException</c> that ends the process outright, exactly the "closes by itself"
    /// class this app was hardened against — and an indented <see cref="XDocument.Save"/> is
    /// O(depth²) in output size and reaches <see cref="OutOfMemoryException"/> first. A document
    /// several thousand elements deep is a sub-100 KB string, well inside the 50 MB text ceiling, so
    /// this is reachable by a paste, not a theoretical bound. 256 leaves an ample margin below the
    /// ~thousands-deep stack limit while admitting any realistic hand- or machine-authored XML.
    /// </para>
    /// </summary>
    private const int MaxDepth = 256;

    /// <summary>The one place an <see cref="XmlReader"/> is constructed for untrusted text —
    /// hardened against XXE and billion-laughs by refusing DTDs entirely, and against deep-nesting
    /// stack overflow / quadratic-Save OOM by capping depth (see <see cref="MaxDepth"/>).</summary>
    private static XDocument LoadSecure(string text)
    {
        using var stringReader = new StringReader(text);
        using var reader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
        });
        var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        GuardDepth(doc);
        return doc;
    }

    /// <summary>
    /// Rejects a document nested deeper than <see cref="MaxDepth"/> before any recursive consumer of
    /// the tree runs. Deliberately iterative, with an explicit heap stack rather than recursion:
    /// a depth check that recursed could overflow on the very input it exists to reject.
    /// <see cref="XDocument.Load"/> itself builds the tree without recursion, so it is safe to
    /// measure the tree after the fact — the recursion this guards against is entirely in what reads
    /// the tree next (<see cref="ConvertElement"/>, the writer's indented save, the structure view).
    /// </summary>
    private static void GuardDepth(XDocument doc)
    {
        if (doc.Root is not { } root)
            return;

        var stack = new Stack<(XElement Element, int Depth)>();
        stack.Push((root, 1));
        while (stack.Count > 0)
        {
            var (element, depth) = stack.Pop();
            if (depth > MaxDepth)
                throw new XmlException(
                    $"XML nesting is deeper than the supported maximum of {MaxDepth} levels.");
            foreach (var child in element.Elements())
                stack.Push((child, depth + 1));
        }
    }

    private static XmlWriterSettings WriterSettings(bool indent, int indentWidth) => new()
    {
        Indent = indent,
        IndentChars = new string(' ', Math.Clamp(indentWidth <= 0 ? 2 : indentWidth, 1, 8)),
        OmitXmlDeclaration = true,
        NewLineOnAttributes = false,
    };

    private static Diagnostic ToDiagnostic(XmlException ex) => new(ex.LineNumber, ex.LinePosition, ex.Message);

    /// <summary>
    /// Repeated sibling elements collapse into an <see cref="ArrayNode"/> (the conventional
    /// XML→JSON shape); a leaf with no attributes becomes a bare <see cref="ScalarNode"/> so a
    /// simple <c>&lt;name&gt;Ada&lt;/name&gt;</c> round-trips as the string <c>"Ada"</c> rather
    /// than an object wrapper.
    /// </summary>
    private static DataNode ConvertElement(XElement element)
    {
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
        }
        foreach (var name in order)
        {
            var list = groups[name];
            members.Add((name, list.Count > 1 ? new ArrayNode(list) : list[0]));
        }
        return new ObjectNode(members);
    }

    private static XElement BuildElement(string name, DataNode node)
    {
        var el = new XElement(name);
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
                break;
            case ObjectNode obj:
                foreach (var (key, value) in obj.Members)
                {
                    if (key.StartsWith('@'))
                        el.SetAttributeValue(key[1..], (value as ScalarNode)?.Raw ?? "");
                    else if (key == "#text")
                        el.Value = (value as ScalarNode)?.Raw ?? "";
                    else if (value is ArrayNode valueArray)
                        foreach (var item in valueArray.Items)
                            el.Add(BuildElement(key, item));
                    else
                        el.Add(BuildElement(key, value));
                }
                break;
        }
        return el;
    }
}
