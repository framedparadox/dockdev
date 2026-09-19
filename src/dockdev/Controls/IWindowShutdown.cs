namespace dockdev.Controls;

/// <summary>
/// A control that must stop touching XAML the moment its host window starts closing.
/// <para>
/// WinUI does not dependably raise <c>Unloaded</c> on a window's content, and a
/// <c>DispatcherQueueTimer</c> or <c>ActualThemeChanged</c> handler that keeps running after
/// that is how a closed tool window still colours a dead rich-edit document or reads
/// <c>FlowDirection</c> off a torn-down peer. <c>ToolPage.NotifyClosing</c> walks the live
/// tree for this interface while the island is still up.
/// </para>
/// </summary>
internal interface IWindowShutdown
{
    void Shutdown();
}
