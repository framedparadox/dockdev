namespace DevDX.Models;

/// <summary>
/// The canonical intermediate every format converts through (design doc §11.2). A recursive node
/// rather than a flat table, so JSON⇄XML is lossless and lossiness is confined to the CSV leg,
/// where it is inherent.
/// </summary>
public abstract record DataNode;

/// <summary>Members are an ordered list, not a dictionary — JSON permits duplicate keys and XML
/// routinely has repeated sibling elements; a dictionary would silently drop data.</summary>
public sealed record ObjectNode(IReadOnlyList<(string Key, DataNode Value)> Members) : DataNode;

public sealed record ArrayNode(IReadOnlyList<DataNode> Items) : DataNode;

/// <summary>Keeps the raw lexeme rather than a parsed <see cref="double"/>, so a 20-digit id or a
/// high-precision decimal round-trips exactly instead of being mangled by float conversion.</summary>
public sealed record ScalarNode(string? Raw, ScalarKind Kind) : DataNode;

public enum ScalarKind { String, Number, Boolean, Null }
