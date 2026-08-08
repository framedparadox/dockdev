using DevDX.Interop;
using DevDX.Models;
using DevDX.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace DevDX;

/// <summary>
/// Snap + auto-hide behavior. When the dock is snapped to an edge (and auto-hide is on) it
/// slides off that edge leaving a thin peek plus a rounded "notch" handle, and reveals when the
/// cursor reaches the edge within the dock's span. Works for all four edges. Partial of
/// <see cref="DockWindow"/>.
/// </summary>
public sealed partial class DockWindow
{
    private DispatcherQueueTimer? _pollTimer;
    private DispatcherQueueTimer? _slideTimer;
    private bool _autoHideStarted;
    private bool _revealed = true;
    private double _currentCoord;
    private int _targetCoord;
    private DateTime _lastInside = DateTime.MinValue;

    // While the clock is before this instant the dock refuses to hide. Set when it lands on an
    // edge so it can settle in full view first (see ArmSettleDelay).
    private DateTime _suppressHideUntil = DateTime.MinValue;

    private const int Peek = 6;      // px of the dock left visible when hidden (the notch band)
    private const int HotZone = 6;   // px band at the edge that triggers a reveal
    private const int EdgePad = 24;  // slack around the dock's span
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(600);

    // The poll runs at FastPollInterval while revealed (so the HideDelay countdown lands within
    // a tick of its real deadline) or while the cursor is in the hot zone (so a reveal feels
    // immediate). Once hidden with the cursor away from the edge — the state auto-hide spends
    // almost all of its time in — nothing changes between ticks, so SlowPollInterval backs off to
    // roughly a third the tick rate rather than polling GetCursorPos ten times a second forever.
    private static readonly TimeSpan FastPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SlowPollInterval = TimeSpan.FromMilliseconds(300);
    private bool _pollingFast = true;

    // Grace period after the dock is placed on an edge before auto-hide may take it. Longer than
    // HideDelay so that dropping the dock at an edge reads as "it settles, then tucks away".
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(1200);

    // ===== Hidden-state "notch" handle size (DIPs) — change these to resize the notch. =====
    // NotchLength runs ALONG the snapped edge (its long side); NotchThickness is the short side.
    private const double NotchLength = 62;         // was 64 — the notch "width" on a top/bottom edge
    private const double NotchLengthVertical = 40; // long side when snapped to the left/right edge
    private const double NotchThickness = 10;      // the tab's short side (into the screen)

    // Whether snapped edges actually hide (vs. staying pinned flush and visible).
    private bool AutoHideEnabled => _profile.Snapped && _profile.AutoHide;

    // True when another monitor sits immediately beyond the snapped edge (an interior / shared
    // edge). Recomputed on each relayout so it costs nothing per poll tick.
    private bool _edgeHasNeighbor;

    // The dock only auto-hides against a TRUE outer screen edge. On an edge shared with a
    // neighboring monitor it would slide into that monitor instead of off-screen, so we keep it
    // pinned flush & visible there rather than "hiding into the next screen".
    private bool CanHide => AutoHideEnabled && !_edgeHasNeighbor;

    private bool ComputeEdgeHasNeighbor()
    {
        if (_outer.Width == 0 || _outer.Height == 0)
            return false;

        int midX = _shownRect.X + _shownRect.Width / 2;
        int midY = _shownRect.Y + _shownRect.Height / 2;
        var probe = _profile.Edge switch
        {
            DockEdge.Bottom => new PointInt32(midX, _outer.Y + _outer.Height + 2),
            DockEdge.Top => new PointInt32(midX, _outer.Y - 2),
            DockEdge.Left => new PointInt32(_outer.X - 2, midY),
            DockEdge.Right => new PointInt32(_outer.X + _outer.Width + 2, midY),
            _ => new PointInt32(midX, midY),
        };

        try
        {
            // Fallback.None → null when the probe point is on no display (a true outer edge).
            return DisplayArea.GetFromPoint(probe, DisplayAreaFallback.None) is not null;
        }
        catch
        {
            return false;
        }
    }

    // The dock hides along Y for top/bottom, along X for left/right.
    private bool HideIsVertical => _profile.Edge is DockEdge.Bottom or DockEdge.Top;

    private int ShownCoord => HideIsVertical ? _shownRect.Y : _shownRect.X;

    // The dock hides against the OUTER (physical screen) edge, not the work-area edge, so a
    // bottom-snapped dock slides all the way down behind the taskbar and the notch peeks out
    // over it. (For edges with no taskbar the outer and work edges coincide.)
    private int HiddenCoord => _profile.Edge switch
    {
        DockEdge.Bottom => _outer.Y + _outer.Height - Peek,
        DockEdge.Top => _outer.Y - _shownRect.Height + Peek,
        DockEdge.Left => _outer.X - _shownRect.Width + Peek,
        DockEdge.Right => _outer.X + _outer.Width - Peek,
        _ => _shownRect.Y,
    };

    private void MoveWindowCoord(int coord) => _appWindow.Move(
        HideIsVertical ? new PointInt32(_shownRect.X, coord) : new PointInt32(coord, _shownRect.Y));

    // Called after every reposition (from UpdateSizeAndPosition).
    partial void OnRelayoutApplied()
    {
        _edgeHasNeighbor = _profile.Snapped && ComputeEdgeHasNeighbor();
        _currentCoord = ShownCoord;
        if (CanHide)
        {
            EnsureStarted();
            if (!_revealed)
                MoveWindowCoord(HiddenCoord); // keep it tucked away after a size/edge change
        }
        else if (_autoHideStarted)
        {
            // Auto-hide off, or a shared/interior edge: pin the dock flush & visible instead of
            // sliding it into the neighboring monitor.
            Stop();
        }
        UpdateNotch();
    }

    // Called when snap state changes (drag-drop, menu, settings).
    partial void ApplyAutoHide()
    {
        _edgeHasNeighbor = _profile.Snapped && ComputeEdgeHasNeighbor();
        if (CanHide)
        {
            EnsureStarted();
            // Let the dock settle at its new edge in full view rather than slamming it shut.
            // Hiding immediately here would fight the cursor that is still parked at the edge
            // from the drag that placed it: the very next poll tick sees the hot zone and
            // re-reveals, giving a hide/show/hide flicker. Arming the grace period instead lets
            // the dock land, then tuck away once — smoothly — after the cursor leaves.
            ArmSettleDelay();
        }
        else
        {
            // Not hiding: fully stop the controller and pin the dock flush/visible.
            Stop();
        }
    }

    /// <summary>
    /// Holds the dock revealed and blocks auto-hide for <see cref="SettleDelay"/>. After that the
    /// normal poll rules resume, so it hides once the cursor has also been away for
    /// <see cref="HideDelay"/>.
    /// </summary>
    private void ArmSettleDelay()
    {
        var now = DateTime.UtcNow;
        _lastInside = now;
        _suppressHideUntil = now + SettleDelay;
        if (!_revealed)
            SetRevealed(true); // slides back out if a hide was already under way
        else
            UpdateNotch();
    }

    /// <summary>
    /// Pulls the dock fully into view right now — used when it is summoned from the tray or by
    /// the global shortcut. If it can auto-hide it slides back out and gets the usual settle
    /// grace period (so it doesn't tuck away again the instant the cursor is elsewhere);
    /// otherwise it is simply snapped back to its shown position.
    /// </summary>
    partial void RevealNow()
    {
        if (CanHide)
        {
            EnsureStarted();
            ArmSettleDelay();
        }
        else
        {
            _currentCoord = ShownCoord;
            MoveWindowCoord(ShownCoord);
        }
    }

    partial void PauseAutoHideForDrag()
    {
        _pollTimer?.Stop();
        _slideTimer?.Stop();
        _revealed = true; // don't fight the drag
        UpdateNotch();
    }

    // Re-arm auto-hide after an in-place interaction (e.g. an item reorder) without slamming
    // the dock shut immediately: keep it revealed for the usual grace period, then it hides.
    partial void ResumeAutoHideAfterDrag()
    {
        if (!CanHide)
            return;
        EnsureStarted();
        ArmSettleDelay();
    }

    private void EnsureStarted()
    {
        if (_autoHideStarted && _pollTimer is not null)
        {
            _pollTimer.Start();
            return;
        }
        _autoHideStarted = true;

        _pollTimer ??= DispatcherQueue.CreateTimer();
        // Fast to start: EnsureStarted only runs while settling at an edge or reacting to a
        // reveal, both of which want the responsive rate. PollCursor backs it off once it
        // observes the hidden-and-away state that makes the slow rate safe.
        _pollingFast = true;
        _pollTimer.Interval = FastPollInterval;
        _pollTimer.Tick -= OnPollTick;
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
    }

    private void OnPollTick(DispatcherQueueTimer sender, object args) => PollCursor();

    private void Stop()
    {
        _autoHideStarted = false;
        _pollTimer?.Stop();
        _pollTimer = null;
        _slideTimer?.Stop();
        _slideTimer = null;
        _revealed = true;
        MoveWindowCoord(ShownCoord); // snap fully back into view
        UpdateNotch();
    }

    private void PollCursor()
    {
        if (!CanHide)
            return;
        if (!NativeMethods.GetCursorPos(out var p))
            return;

        int l = _shownRect.X - EdgePad;
        int r = _shownRect.X + _shownRect.Width + EdgePad;
        int t = _shownRect.Y - EdgePad;
        int b = _shownRect.Y + _shownRect.Height + EdgePad;
        bool inX = p.X >= l && p.X <= r;
        bool inY = p.Y >= t && p.Y <= b;

        // Reveal from the physical screen edge (so moving the cursor onto the notch over the
        // taskbar reveals the dock), matching where it hides.
        bool atHotZone = _profile.Edge switch
        {
            DockEdge.Bottom => inX && p.Y >= _outer.Y + _outer.Height - HotZone,
            DockEdge.Top => inX && p.Y <= _outer.Y + HotZone,
            DockEdge.Left => inY && p.X <= _outer.X + HotZone,
            DockEdge.Right => inY && p.X >= _outer.X + _outer.Width - HotZone,
            _ => false,
        };

        bool overDock = inX && inY;

        var now = DateTime.UtcNow;
        if (atHotZone || (overDock && _revealed))
        {
            _lastInside = now;
            if (!_revealed)
                SetRevealed(true);
        }
        else if (_revealed && now - _lastInside > HideDelay && now >= _suppressHideUntil)
        {
            SetRevealed(false);
        }

        // Only the hidden-and-away state can tolerate the slower rate: revealed needs it to catch
        // HideDelay elapsing promptly, and the hot zone needs it to catch the cursor leaving it
        // (or arriving) without a sluggish reveal.
        bool wantFast = _revealed || atHotZone;
        if (wantFast != _pollingFast && _pollTimer is not null)
        {
            _pollingFast = wantFast;
            _pollTimer.Interval = wantFast ? FastPollInterval : SlowPollInterval;
        }
    }

    private void SetRevealed(bool reveal)
    {
        _revealed = reveal;
        _targetCoord = reveal ? ShownCoord : HiddenCoord;
        // Re-assert top-most in both directions: when hidden at the bottom the dock sits behind
        // the taskbar, and its notch must stay above the (also top-most) taskbar to be visible.
        WindowChrome.EnsureTopmost(_hwnd);
        UpdateNotch();

        // Honor the system "show animations" accessibility setting: when animations are off
        // (reduced motion), jump straight to the target instead of the slide.
        if (ReducedMotion)
        {
            _currentCoord = _targetCoord;
            MoveWindowCoord(_targetCoord);
            return;
        }
        StartSlide();
    }

    /// <summary>
    /// Positions and shows the hidden-state "notch" handle. It only appears when the dock is
    /// hidden, and is anchored to the edge of the window that stays on-screen (the side facing
    /// into the desktop) so it pokes out of the thin peek band.
    /// </summary>
    private void UpdateNotch()
    {
        if (Notch is null)
            return;

        bool show = CanHide && !_revealed;
        Notch.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
            return;

        switch (_profile.Edge)
        {
            case DockEdge.Bottom: // window pushed down; visible band is at the TOP of the window
                Notch.HorizontalAlignment = HorizontalAlignment.Center;
                Notch.VerticalAlignment = VerticalAlignment.Top;
                Notch.Width = NotchLength; Notch.Height = NotchThickness;
                break;
            case DockEdge.Top:    // visible band at the BOTTOM of the window
                Notch.HorizontalAlignment = HorizontalAlignment.Center;
                Notch.VerticalAlignment = VerticalAlignment.Bottom;
                Notch.Width = NotchLength; Notch.Height = NotchThickness;
                break;
            case DockEdge.Left:   // visible band at the RIGHT of the window
                Notch.HorizontalAlignment = HorizontalAlignment.Right;
                Notch.VerticalAlignment = VerticalAlignment.Center;
                Notch.Width = NotchThickness; Notch.Height = NotchLengthVertical;
                break;
            case DockEdge.Right:  // visible band at the LEFT of the window
                Notch.HorizontalAlignment = HorizontalAlignment.Left;
                Notch.VerticalAlignment = VerticalAlignment.Center;
                Notch.Width = NotchThickness; Notch.Height = NotchLengthVertical;
                break;
        }
    }

    // Cached once: the reduced-motion preference rarely changes within a session. Guarded so
    // an unavailable setting simply leaves animations on.
    private bool? _reducedMotion;
    private bool ReducedMotion => _reducedMotion ??= ComputeReducedMotion();

    private static bool ComputeReducedMotion()
    {
        try { return !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
        catch { return false; }
    }

    // ---- The slide --------------------------------------------------------
    //
    // Driven by a timer rather than by a Composition animation, and that is a constraint rather
    // than a choice: hiding means moving the WINDOW off the screen edge, and Composition animates
    // content inside a window — it has no way to animate an HWND's position. Keeping the window
    // still and sliding its content instead would leave a full-size, invisible window sitting over
    // the screen edge swallowing every click aimed at what is behind it.
    //
    // What the timer does is time-based rather than per-frame proportional, which is the part that
    // actually shows: the old "move 28% of the remaining distance each tick" is a different
    // duration for every travel distance (a tall dock took visibly longer to hide than a short
    // one) and never quite arrives, so it ended on a snap. This runs a fixed duration through a
    // cubic ease-out and lands exactly on the target.

    private const double SlideDurationMs = 220;
    private DateTime _slideStart;
    private double _slideFrom;

    private void StartSlide()
    {
        _slideStart = DateTime.UtcNow;
        _slideFrom = _currentCoord;

        if (_slideTimer is null)
        {
            _slideTimer = DispatcherQueue.CreateTimer();
            // ~120Hz: fine enough that a high-refresh display doesn't show steps, and cheap —
            // each tick is one MoveWindow on a window a few hundred pixels across.
            _slideTimer.Interval = TimeSpan.FromMilliseconds(8);
            _slideTimer.Tick += (_, _) => SlideTick();
        }
        if (!_slideTimer.IsRunning)
            _slideTimer.Start();
    }

    private void SlideTick()
    {
        double progress = (DateTime.UtcNow - _slideStart).TotalMilliseconds / SlideDurationMs;
        if (progress >= 1)
        {
            _currentCoord = _targetCoord;
            MoveWindowCoord(_targetCoord);
            _slideTimer?.Stop();
            return;
        }

        // Cubic ease-out: quick off the mark, settling into the edge — the curve the shell's own
        // fly-outs use, and the one that reads as "it slid" rather than "it moved".
        double eased = 1 - Math.Pow(1 - progress, 3);
        _currentCoord = _slideFrom + (_targetCoord - _slideFrom) * eased;
        MoveWindowCoord((int)Math.Round(_currentCoord));
    }
}
