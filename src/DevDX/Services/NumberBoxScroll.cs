using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DevDX.Services;

/// <summary>
/// Mouse-wheel stepping for a <see cref="NumberBox"/> used as a plain editable field rather than a
/// spinner: the field carries no <c>SpinButtonPlacementMode</c> (so no up/down glyphs render at
/// all — the WinUI default already hides them), and this is what lets the wheel still nudge the
/// value the buttons used to.
/// </summary>
public static class NumberBoxScroll
{
    public static void EnableWheelStep(this NumberBox box, double step = 1)
    {
        box.PointerWheelChanged += (_, e) =>
        {
            if (double.IsNaN(box.Value))
                return;
            var delta = e.GetCurrentPoint(box).Properties.MouseWheelDelta;
            var next = box.Value + (delta > 0 ? step : -step);
            box.Value = Math.Clamp(next, box.Minimum, box.Maximum);
            e.Handled = true;
        };
    }
}
