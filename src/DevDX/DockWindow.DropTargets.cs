using DevDX.Models;
using DevDX.Services;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace DevDX;

/// <summary>
/// What happens when a file is dropped on a particular <em>icon</em> rather than on the strip in
/// general (design doc §12). Partial of <see cref="DockWindow"/>.
/// <para>
/// Dropped on a tool cell: open a new instance preloaded with that file if the tool's
/// <see cref="ToolDefinition.FileExtensions"/> accepts it, otherwise a short inline tooltip rather
/// than silently doing the wrong thing. Everything else on the strip keeps the existing behavior —
/// the drop opens the best-matching tool in the whole catalog (see <c>DockWindow.Root_Drop</c>) —
/// which is why these handlers mark the event handled only when they actually take it.
/// </para>
/// </summary>
public sealed partial class DockWindow
{
    /// <summary>The icon a drag is currently hovering, swelled as the drop cue.</summary>
    private ToolDockItem? _dropTarget;

    /// <summary>How much a cell grows to say "drop it here".</summary>
    private const double DropTargetSwell = 1.3;

    private void Item_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ToolDockItem item } || item.IsSeparator)
            return;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        string caption = Loc.Format("Dock.DropOnTool", item.DisplayName);

        SetDropTarget(item);
        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride is { } ui)
        {
            ui.Caption = caption;
            ui.IsCaptionVisible = true;
            ui.IsGlyphVisible = true;
        }
        // Stop here: the strip's handler would otherwise replace the caption with its own.
        e.Handled = true;
    }

    /// <summary>
    /// Drops the cue when the drag leaves this cell — but only if this cell is still the one
    /// showing it. Moving from one icon straight onto the next raises the new cell's DragOver
    /// before the old cell's DragLeave, so an unconditional clear here would wipe the cue off the
    /// cell the cursor had just arrived at.
    /// </summary>
    private void Item_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ToolDockItem item } && ReferenceEquals(_dropTarget, item))
            SetDropTarget(null);
    }

    private async void Item_Drop(object sender, DragEventArgs e)
    {
        SetDropTarget(null);

        if (sender is not FrameworkElement { Tag: ToolDockItem item } || item.IsSeparator ||
            !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        // Handled before the first await: the strip's Drop handler runs as this one bubbles, and
        // by the time an await has resumed it would otherwise also route the same file.
        e.Handled = true;

        var deferral = e.GetDeferral();
        try
        {
            var paths = (await e.DataView.GetStorageItemsAsync())
                .Select(s => s.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
            if (paths.Count == 0)
                return;

            foreach (var path in paths)
                OpenOnItemOrExplain(item, path);
        }
        catch (Exception ex)
        {
            Diag.Log("Drop onto an icon failed: " + ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>Opens <paramref name="path"/> on <paramref name="item"/>'s own tool if its
    /// extension is accepted, otherwise explains why not rather than silently ignoring the drop.</summary>
    private void OpenOnItemOrExplain(ToolDockItem item, string path)
    {
        var definition = ToolCatalog.Get(item.Kind);
        if (definition is null)
            return;

        var ext = Path.GetExtension(path);
        if (definition.FileExtensions.Length > 0 &&
            definition.FileExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            Manager.Launcher.Open(definition, seedFilePath: path);
        }
        else
        {
            ShowInlineTip(Loc.Format("Dock.CantOpenExtension", definition.DisplayName, ext), Path.GetFileName(path));
        }
    }

    /// <summary>
    /// Swells the icon a drag is over, and un-swells whatever it was over before. Reuses the
    /// magnification channel rather than adding a second visual state: it is already the property
    /// the item template watches for "this cell is bigger than the others", and two mechanisms
    /// fighting over one icon's size is a bug waiting to be written.
    /// </summary>
    private void SetDropTarget(ToolDockItem? item)
    {
        if (ReferenceEquals(_dropTarget, item))
            return;
        _dropTarget?.SetMagnification(1);
        _dropTarget = item;
        _dropTarget?.SetMagnification(DropTargetSwell);
    }
}
