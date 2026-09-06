using dockdev.Models;
using dockdev.ToolWindows;

namespace dockdev.Services;

/// <summary>
/// Opens a tool: a brand-new temporary window by default, or focuses the most recent one of that
/// tool when "Reuse the open window for a tool" is on (design doc §17). Opening means
/// constructing an in-process page — nothing is ever handed to <c>ShellExecute</c>.
/// </summary>
public sealed class ToolWindowLauncher(dockdevManager manager)
{
    /// <summary>Opens <paramref name="definition"/>, optionally preloaded from a dropped/picked
    /// file. Returns the window it opened, or null when an existing window was focused instead.</summary>
    public ToolWindowBase? Open(ToolDefinition definition, string? seedFilePath = null, bool forceNewInstance = false)
    {
        try
        {
            if (!forceNewInstance && manager.Config.ReuseToolWindows &&
                manager.ToolWindows.TryFocusMostRecent(definition.Kind))
                return null;

            var page = definition.Factory();
            var window = new ToolWindowBase(manager, page, definition);
            window.Activate();
            window.BringToFront();

            if (!string.IsNullOrEmpty(seedFilePath))
                _ = SeedAsync(page, seedFilePath);

            return window;
        }
        catch (Exception ex)
        {
            // Every caller is an event handler with no caller of its own — a dock icon's Click, a
            // quick-launch Enter, a global shortcut arriving as a window message, a file drop — so
            // a page that fails to construct (a missing font, a theme resource this build's
            // dictionary lacks) took the whole app with it rather than one tool. One tool that
            // will not open is a bug in that tool; a dock that disappears is a bug in the dock.
            Diag.Log($"ToolWindowLauncher: could not open '{definition.Kind}': {ex}");
            return null;
        }
    }

    /// <summary>
    /// Loads the seed file into the freshly opened page. The launcher cannot await this — the
    /// window has to come up now, not when the disk gets round to it — so the failure has to be
    /// caught here.
    /// <para>
    /// Two of the pages that implement <c>LoadFileAsync</c> guard their own read and two do not,
    /// and an exception from an unawaited Task is discarded by the runtime without a trace: a file
    /// that was locked, deleted between the drop and the read, or refused by permissions opened an
    /// empty window and left nothing anywhere to say why. Catching here covers every page and
    /// every future one, and leaves a line in the log.
    /// </para>
    /// </summary>
    private static async Task SeedAsync(ToolPages.ToolPage page, string path)
    {
        try
        {
            await page.LoadFileAsync(path);
        }
        catch (Exception ex)
        {
            Diag.Log($"ToolWindowLauncher: could not seed '{path}': {ex.GetType().Name}: {ex.Message}");
        }
    }
}
