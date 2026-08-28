using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace dockdev.Controls;

/// <summary>
/// Arranges an <see cref="ItemsRepeater"/>'s realized children at caller-supplied points instead
/// of in a line. This class knows nothing about circles or docks — it just places whatever
/// <see cref="SetGeometry"/> was last told to, at the panel size it was given. The ring math (the
/// centre, the radius, which angle each item's index maps to) lives entirely in
/// <c>DockWindow</c>, which already owns every other number the dock's geometry depends on; this
/// is only the seam that lets <c>ItemsRepeater</c> honor it instead of laying its children out in
/// its own built-in line.
/// <para>
/// Non-virtualizing on purpose: the dock holds at most a couple of dozen items, so there is
/// nothing to gain from virtualizing them, and a virtualizing layout would have to re-derive which
/// items are "on screen" on a ring where every item always is.
/// </para>
/// </summary>
public sealed class RadialLayout : NonVirtualizingLayout
{
    private IReadOnlyList<Point> _positions = Array.Empty<Point>();
    private Size _extent;

    /// <summary>
    /// Sets the top-left arrange position for each realized child, by index (matching
    /// <c>ItemsRepeater.ItemsSource</c> order, which is what <see cref="NonVirtualizingLayoutContext"/>
    /// exposes since nothing here is virtualized), and the panel's own reported size. Called by
    /// <c>DockWindow</c> whenever the ring's geometry changes — an item added or removed, the
    /// density changed, the window resized. A child beyond the end of <paramref name="positions"/>
    /// (realization can lag the source collection by a frame) is left at the ring's centre rather
    /// than thrown on, so a stale extra frame reads as "hasn't moved out yet" instead of a crash.
    /// </summary>
    public void SetGeometry(IReadOnlyList<Point> positions, Size extent)
    {
        _positions = positions;
        _extent = extent;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        var unconstrained = new Size(double.PositiveInfinity, double.PositiveInfinity);
        foreach (var child in context.Children)
            child.Measure(unconstrained);
        return _extent;
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        var children = context.Children;
        var fallback = new Point(finalSize.Width / 2, finalSize.Height / 2);
        for (int i = 0; i < children.Count; i++)
        {
            var pos = i < _positions.Count ? _positions[i] : fallback;
            var desired = children[i].DesiredSize;
            children[i].Arrange(new Rect(pos.X, pos.Y, desired.Width, desired.Height));
        }
        return finalSize;
    }
}
