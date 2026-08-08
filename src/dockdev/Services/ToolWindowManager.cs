using dockdev.Models;
using dockdev.ToolWindows;

namespace dockdev.Services;

/// <summary>
/// The in-process open-instance registry (design doc §17). Every state change here is an
/// in-process event, so the dock's open-window indicator dot needs no polling at all.
/// </summary>
public sealed class ToolWindowManager
{
    private readonly Dictionary<ToolKind, List<ToolWindowBase>> _open = new();

    /// <summary>Raised whenever a window opens or closes.</summary>
    public event Action? OpenSetChanged;

    public void Register(ToolWindowBase window)
    {
        if (!_open.TryGetValue(window.Kind, out var list))
            _open[window.Kind] = list = [];
        list.Add(window);
        OpenSetChanged?.Invoke();
    }

    public void Unregister(ToolWindowBase window)
    {
        if (_open.TryGetValue(window.Kind, out var list))
        {
            list.Remove(window);
            if (list.Count == 0)
                _open.Remove(window.Kind);
        }
        OpenSetChanged?.Invoke();
    }

    public int OpenCount(ToolKind kind) => _open.TryGetValue(kind, out var list) ? list.Count : 0;

    /// <summary>Focuses the most recently opened window of <paramref name="kind"/>, if any.</summary>
    public bool TryFocusMostRecent(ToolKind kind)
    {
        if (!_open.TryGetValue(kind, out var list) || list.Count == 0)
            return false;
        list[^1].BringToFront();
        return true;
    }

    /// <summary>Every open scratch window, across every tool.</summary>
    public IEnumerable<ToolWindowBase> AllWindows => _open.Values.SelectMany(w => w);

    /// <summary>Re-applies the app-wide theme to every open tool window (called by
    /// <c>dockdevManager.SetTheme</c>).</summary>
    public void ApplyThemeToAll()
    {
        foreach (var window in AllWindows)
            window.ApplyTheme();
    }
}
