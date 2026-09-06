using dockdev.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace dockdev.Controls;

/// <summary>
/// The auxiliary panel for JSON/XML/Data Formatter: a <see cref="DataNode"/> tree with one-click
/// path copy (design doc §14.1 — clicking a node shows its path, e.g. <c>$.items[2].id</c>).
/// </summary>
public sealed class StructureTree : Grid
{
    private readonly TreeView _tree = new()
    {
        SelectionMode = TreeViewSelectionMode.Single,
        CanDragItems = false,
        CanReorderItems = false,
    };

    private readonly TextBlock _pathBox = new()
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 12,
        Margin = new Thickness(8, 4, 8, 4),
        TextTrimming = TextTrimming.CharacterEllipsis,
        IsTextSelectionEnabled = true,
    };

    public event EventHandler<string>? NodeSelected;

    /// <summary>This tree's accessible name, announced by Narrator when focus lands in it — see
    /// <see cref="Controls.CodeView.AccessibleName"/> for the equivalent on the sibling panes this
    /// control sits beside.</summary>
    public string AccessibleName
    {
        get => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(_tree);
        set => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_tree, value);
    }

    public StructureTree()
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_tree, 0);
        Grid.SetRow(_pathBox, 1);
        Children.Add(_tree);
        Children.Add(_pathBox);

        _tree.SelectionChanged += (_, _) =>
        {
            if (_tree.SelectedNode?.Content is PathTag tag)
            {
                _pathBox.Text = tag.Path;
                NodeSelected?.Invoke(this, tag.Path);
            }
        };
    }

    private const int MaxDepth = 64;
    private const int MaxChildrenPerNode = 200;

    public void SetRoot(DataNode? root)
    {
        _tree.RootNodes.Clear();
        _pathBox.Text = "";
        if (root is null)
            return;
        var node = new TreeViewNode { Content = new PathTag("$", Label("$", root)) };
        Populate(node, root, "$");
        int totalNodes = 0;
        Populate(node, root, "$", 0, ref totalNodes);
        _tree.RootNodes.Add(node);
        node.IsExpanded = true;
    }

    private static void Populate(TreeViewNode node, DataNode value, string path)
    private static void Populate(TreeViewNode node, DataNode value, string path, int depth, ref int totalNodes)
    {
        if (depth > MaxDepth || totalNodes > 1000)
            return;

        switch (value)
        {
            case ObjectNode obj:
                int objCount = 0;
                foreach (var (key, child) in obj.Members)
                {
                    if (objCount++ >= MaxChildrenPerNode || totalNodes++ > 1000)
                    {
                        node.Children.Add(new TreeViewNode { Content = new PathTag(path, $"... ({obj.Members.Count - MaxChildrenPerNode} more)") });
                        break;
                    }
                    var childPath = $"{path}.{key}";
                    var childNode = new TreeViewNode { Content = new PathTag(childPath, Label(key, child)) };
                    Populate(childNode, child, childPath);
                    Populate(childNode, child, childPath, depth + 1, ref totalNodes);
                    node.Children.Add(childNode);
                }
                break;
            case ArrayNode arr:
                int arrCount = 0;
                for (int i = 0; i < arr.Items.Count; i++)
                {
                    if (arrCount++ >= MaxChildrenPerNode || totalNodes++ > 1000)
                    {
                        node.Children.Add(new TreeViewNode { Content = new PathTag(path, $"... ({arr.Items.Count - MaxChildrenPerNode} more)") });
                        break;
                    }
                    var childPath = $"{path}[{i}]";
                    var childNode = new TreeViewNode { Content = new PathTag(childPath, Label($"[{i}]", arr.Items[i])) };
                    Populate(childNode, arr.Items[i], childPath);
                    Populate(childNode, arr.Items[i], childPath, depth + 1, ref totalNodes);
                    node.Children.Add(childNode);
                }
                break;
        }
    }

    private static string Label(string name, DataNode value) => value switch
    {
        ObjectNode obj => $"{name}  {{{obj.Members.Count}}}",
        ArrayNode arr => $"{name}  [{arr.Items.Count}]",
        ScalarNode { Kind: ScalarKind.String } s => $"{name}: \"{Truncate(s.Raw)}\"",
        ScalarNode s => $"{name}: {s.Raw ?? "null"}",
        _ => name,
    };

    private static string Truncate(string? s)
    {
        s ??= "";
        return s.Length > 40 ? s[..40] + "…" : s;
    }

    // TreeViewNode.Content needs something to render; TreeView's default item template calls
    // ToString(), so this doubles as both the display label and the path payload.
    private sealed record PathTag(string Path, string DisplayLabel)
    {
        public override string ToString() => DisplayLabel;
    }
}
