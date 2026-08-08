using dockdev.Models;
using Xunit;

namespace dockdev.Tests.Models;

/// <summary>
/// <see cref="ToolDockItem.HasUserCustomization"/> is what decides whether switching a tool off in
/// Settings ▸ Tools may delete the item or has to hide it (<c>DockWindow.SetToolActive</c>). It is
/// therefore the guard on unrecoverable data loss — there is no undo on the dock — so it gets tests
/// of its own even though the switch that consults it needs a running window and cannot.
/// </summary>
public class ToolDockItemTests
{
    /// <summary>An item exactly as <c>SetToolActive</c> creates one: catalog kind, catalog caption,
    /// nothing else. Delete-on-switch-off is invisible for these, because turning the tool back on
    /// builds one indistinguishable from it.</summary>
    private static ToolDockItem Pristine(ToolKind kind = ToolKind.Json) =>
        new() { Kind = kind, DisplayName = ToolCatalog.Get(kind)?.DisplayName ?? "" };

    [Fact]
    public void FreshlyPinnedItem_IsNotCustomized()
    {
        Assert.False(Pristine().HasUserCustomization);
    }

    [Fact]
    public void DefaultConstructedItem_IsNotCustomized()
    {
        // A hand-edited or older config can leave the caption empty; empty is not "renamed".
        Assert.False(new ToolDockItem { Kind = ToolKind.Json, DisplayName = "" }.HasUserCustomization);
    }

    [Fact]
    public void RenamedItem_IsCustomized()
    {
        var item = Pristine();
        item.DisplayName = "My JSON thing";
        Assert.True(item.HasUserCustomization);
    }

    [Fact]
    public void CustomGlyph_IsCustomized()
    {
        var item = Pristine();
        item.CustomGlyph = "\uE734";
        Assert.True(item.HasUserCustomization);
    }

    [Fact]
    public void CustomIconPath_IsCustomized()
    {
        var item = Pristine();
        item.CustomIconPath = @"C:\icons\mine.png";
        Assert.True(item.HasUserCustomization);
    }

    [Fact]
    public void AssignedHotkey_IsCustomized()
    {
        var item = Pristine();
        item.Hotkey = "Ctrl+Alt+1";
        Assert.True(item.HasUserCustomization);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankHotkey_IsNotCustomization(string? hotkey)
    {
        var item = Pristine();
        item.Hotkey = hotkey;
        Assert.False(item.HasUserCustomization);
    }

    [Fact]
    public void PlainSeparator_IsNotCustomized()
    {
        // A separator has no catalog entry, so its caption must not be read as a rename.
        Assert.False(new ToolDockItem { Kind = ToolKind.Separator }.HasUserCustomization);
        Assert.False(new ToolDockItem { Kind = ToolKind.Separator, DisplayName = "—" }.HasUserCustomization);
    }

    [Fact]
    public void SeparatorWithHotkey_IsCustomized()
    {
        var item = new ToolDockItem { Kind = ToolKind.Separator, Hotkey = "Ctrl+Alt+9" };
        Assert.True(item.HasUserCustomization);
    }

    /// <summary>
    /// Every catalog tool has to answer "not customized" for a freshly pinned item, or switching
    /// that tool off would leave a hidden entry behind for no reason.
    /// </summary>
    [Fact]
    public void EveryCatalogTool_PinsAsUncustomized()
    {
        foreach (var tool in ToolCatalog.All)
            Assert.False(Pristine(tool.Kind).HasUserCustomization, tool.Kind.ToString());
    }
}
