using dockdev.Models;

namespace dockdev.Services.Formats;

/// <summary>
/// The one tested place a nested <see cref="DataNode"/> tree (from JSON or XML) is projected onto
/// CSV's flat row shape and back. This is where the design document's "lossiness is confined to
/// CSV" promise (§11.2) actually lives: <see cref="Flatten"/> emits dotted keys
/// (<c>address.city</c>, <c>tags.0</c>) for anything CSV cannot represent natively, and
/// <see cref="Rebuild"/> reverses it on a best-effort basis.
/// </summary>
public static class CsvProjection
{
    /// <summary>
    /// The deepest a single dotted header may expand into nested objects before <see cref="Rebuild"/>
    /// keeps it as one flat key instead. Matches <see cref="JsonFormat"/>/<see cref="XmlFormat"/>'s
    /// own 256-level cap (design doc §21) for the same reason those exist: <see cref="InsertPath"/>
    /// and <see cref="ToDataNode"/> both recurse once per path segment, and — unlike JSON and XML,
    /// whose depth comes from a document structure the format's own reader already bounds — a CSV
    /// header is just a pasted cell with no limit of its own on how many dots it contains. Without
    /// this, a header a few thousand dots deep (a sub-100 KB row, well inside the 50 MB text
    /// ceiling) recurses the same call stack into oblivion: an uncatchable
    /// <see cref="StackOverflowException"/> that ends the process, exactly the failure class XML's
    /// nesting cap was added to close off. A header with a legitimately deep path is not a shape any
    /// real CSV export produces; keeping it literal instead of expanding it is what keeps a
    /// hand-crafted one from taking the process down instead of just converting oddly.
    /// </summary>
    private const int MaxDepth = 256;

    /// <summary>
    /// Projects an arbitrary tree onto CSV's native shape: an <see cref="ArrayNode"/> of
    /// scalar-valued <see cref="ObjectNode"/> rows. A top-level object is treated as a single
    /// record; a top-level array is treated as one record per item.
    /// </summary>
    public static DataNode Flatten(DataNode root)
    {
        var records = root is ArrayNode arr ? arr.Items : [root];
        var flatRecords = new List<DataNode>();
        foreach (var record in records)
        {
            var members = new List<(string, DataNode)>();
            FlattenInto("", record, members);
            flatRecords.Add(new ObjectNode(members));
        }
        return new ArrayNode(flatRecords);
    }

    private static void FlattenInto(string prefix, DataNode node, List<(string Key, DataNode Value)> into)
    {
        switch (node)
        {
            case ObjectNode obj:
                foreach (var (key, value) in obj.Members)
                    FlattenInto(prefix.Length == 0 ? key : prefix + "." + key, value, into);
                break;
            case ArrayNode arr:
                for (int i = 0; i < arr.Items.Count; i++)
                    FlattenInto(prefix.Length == 0 ? i.ToString() : prefix + "." + i, arr.Items[i], into);
                break;
            case ScalarNode scalar:
                into.Add((prefix.Length == 0 ? "value" : prefix, scalar));
                break;
        }
    }

    /// <summary>
    /// Reverses <see cref="Flatten"/> on a best-effort basis: a dotted key becomes a nested
    /// object path, and an all-digit segment becomes an array index. Best-effort because a flat
    /// CSV export loses the distinction between "this was an object with a numeric key" and
    /// "this was an array" — the common case (round-tripping dockdev's own <see cref="Flatten"/>
    /// output) resolves correctly; a hand-authored CSV with unusual header names degrades to
    /// plain object nesting rather than throwing.
    /// </summary>
    public static DataNode Rebuild(DataNode flat)
    {
        var records = flat is ArrayNode arr ? arr.Items : [flat];
        var rebuilt = new List<DataNode>();
        foreach (var record in records)
        {
            if (record is not ObjectNode obj)
            {
                rebuilt.Add(record);
                continue;
            }

            var root = new Node();
            foreach (var (key, value) in obj.Members)
            {
                if (value is not ScalarNode scalar)
                    continue;
                var segments = key.Split('.');
                // See MaxDepth: a pathologically dotted header degrades to one flat key rather
                // than recursing arbitrarily deep.
                InsertPath(root, segments.Length > MaxDepth ? [key] : segments, 0, scalar);
            }
            rebuilt.Add(ToDataNode(root));
        }
        return new ArrayNode(rebuilt);
    }

    // A tiny mutable tree used only while rebuilding, so array-vs-object can be decided once
    // every path for a record is known instead of guessed segment by segment.
    private sealed class Node
    {
        public ScalarNode? Scalar;
        public readonly Dictionary<string, Node> Children = new();
        public readonly List<string> Order = [];

        public Node Child(string key)
        {
            if (Children.TryGetValue(key, out var child))
                return child;
            child = new Node();
            Children[key] = child;
            Order.Add(key);
            return child;
        }
    }

    private static void InsertPath(Node node, string[] segments, int index, ScalarNode value)
    {
        if (index == segments.Length)
        {
            node.Scalar = value;
            return;
        }
        InsertPath(node.Child(segments[index]), segments, index + 1, value);
    }

    private static DataNode ToDataNode(Node node)
    {
        if (node.Scalar is not null && node.Children.Count == 0)
            return node.Scalar;

        bool looksLikeArray = node.Order.Count > 0 && node.Order.All(k => k.Length > 0 && k.All(char.IsAsciiDigit));
        if (looksLikeArray)
        {
            var items = node.Order
                .Select(k => (Index: int.Parse(k), Node: node.Children[k]))
                .OrderBy(x => x.Index)
                .Select(x => ToDataNode(x.Node))
                .ToList();
            return new ArrayNode(items);
        }

        var members = node.Order.Select(k => (k, ToDataNode(node.Children[k]))).ToList();
        return new ObjectNode(members);
    }
}
