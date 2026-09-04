using System;

namespace CanvasDesktop;

/// <summary>
/// Foreground-suppression policy: ignore <see cref="IInputRouter.WindowFocused"/>
/// events that fire shortly after a tracked window vanishes (minimize/destroy)
/// or an overview overlay closes, so the camera doesn't recenter on transient
/// focus blips. When a focused window is genuinely off-screen, recenter the
/// canvas on it.
/// </summary>
internal sealed class ForegroundCoordinator
{
    private const long ForegroundSuppressionMs = 500;

    /// <summary>
    /// How much of a focused window must already be on a monitor before we leave
    /// the camera alone. Below this we treat the window as "not reachable where
    /// it is" and recentre on it.
    ///
    /// This used to be a bare any-overlap test, which meant a window with a
    /// single column of pixels poking onto the screen counted as visible and was
    /// never recentred. Clicking its taskbar icon then focused it without
    /// bringing it into view — and if it happened to be the foreground window
    /// already, Windows' own taskbar behaviour minimised and restored it
    /// instead, which reads as the click doing nothing useful.
    /// </summary>
    private const double MinVisibleFraction = 0.8;

    private readonly Canvas _canvas;
    private readonly IOverviewController _overview;
    private readonly IClock _clock;
    private readonly IScreens _screens;

    private long _lastWindowLostTick;
    private long _lastOverlayClosedTick;

    /// <summary>
    /// The last window the system handed the foreground to, tracked or not.
    ///
    /// Suppression exists for exactly one situation: the window that *had* the
    /// foreground vanishes and Windows reassigns focus to whatever is next in
    /// line. A background window leaving reassigns nothing, so stamping for it
    /// only blocks the next legitimate recentre.
    /// </summary>
    private IntPtr _lastFocusedHWnd;

    public ForegroundCoordinator(
        Canvas canvas,
        IOverviewController overview,
        IInputRouter input,
        IClock clock,
        IScreens screens)
    {
        _canvas = canvas;
        _overview = overview;
        _clock = clock;
        _screens = screens;

        overview.BeforeModeChanged += OnOverviewModeChanged;

        // Not input.WindowDestroyed: EVENT_OBJECT_DESTROY fires for every
        // top-level window on the system, and hovering the taskbar destroys
        // tooltips and the thumbnail flyout continuously. Each one refreshed the
        // suppression stamp, so clicking a taskbar thumbnail activated the
        // window but never recentred on it. Canvas.WindowRemoved fires only for
        // windows we actually tracked.
        canvas.WindowRemoved += OnTrackedWindowRemoved;
        input.WindowFocused   += OnWindowFocused;
        input.WindowMinimized += OnWindowMinimized;
        input.WindowRestored  += OnWindowRestored;
    }

    private void OnOverviewModeChanged(OverviewMode from, OverviewMode to)
    {
        if (to == OverviewMode.Hidden)
            _lastOverlayClosedTick = _clock.TickCount64;
    }

    private void OnWindowMinimized(IntPtr hWnd)
    {
        // Same reasoning: only a window we manage vanishing is worth suppressing
        // a camera move for.
        if (!_canvas.HasWindow(hWnd)) return;
        if (!WasHoldingFocus(hWnd)) return;
        _lastWindowLostTick = _clock.TickCount64;
    }

    private void OnTrackedWindowRemoved(IntPtr hWnd)
    {
        if (!WasHoldingFocus(hWnd)) return;
        _lastWindowLostTick = _clock.TickCount64;
    }

    /// <summary>
    /// Whether <paramref name="hWnd"/> is the window the foreground is being
    /// taken from — the only case where the focus event that follows is a
    /// consequence of the vanish rather than something the user asked for.
    ///
    /// Without this, *any* tracked window leaving armed a 500ms blackout on
    /// recentring, and plenty of them leave without the user doing anything:
    /// <see cref="WindowManager.RemoveStale"/> drops windows on a 500ms tick,
    /// and the Win11 shell churns short-lived top-level windows that discovery
    /// can pick up. A taskbar-icon click landing inside a blackout it had
    /// nothing to do with is exactly the "it came to the front and lit up on the
    /// minimap, but the camera never moved" case.
    /// </summary>
    private bool WasHoldingFocus(IntPtr hWnd)
    {
        return hWnd == _lastFocusedHWnd;
    }

    private void OnWindowRestored(IntPtr hWnd)
    {
        // Restoring is the user explicitly asking for this window, not the kind
        // of transient focus blip the suppression window exists to absorb. Clear
        // the stamp so the focus event that follows a restore can recentre —
        // otherwise a taskbar-icon minimise/restore round trip lands inside the
        // suppression window and the window comes back still off-screen.
        _lastWindowLostTick = _clock.TickCount64 - ForegroundSuppressionMs;
    }

    private void OnWindowFocused(IntPtr hwnd)
    {
        // Recorded ahead of every gate: this has to follow the real system
        // foreground even on the events we decline to act on, or the next
        // vanish can't tell whether it is losing focus or merely leaving.
        _lastFocusedHWnd = hwnd;

        if (_overview.CurrentMode != OverviewMode.Hidden)
            return;

        long now = _clock.TickCount64;
        if (now - _lastWindowLostTick    < ForegroundSuppressionMs ||
            now - _lastOverlayClosedTick < ForegroundSuppressionMs)
            return;

        if (_canvas.HasWindow(hwnd))
        {
            var world = _canvas.Windows[hwnd];
            if (world.PinnedToScreen)
                return;

            var r = _canvas.WorldToScreen(world);
            if (!IsSufficientlyVisible(r))
            {
                CanvasNavigation.CenterOnWindow(_canvas, _screens, world);
                _canvas.Commit();
            }
        }
    }

    /// <summary>
    /// True when at least <see cref="MinVisibleFraction"/> of <paramref name="r"/>
    /// falls inside some monitor — i.e. the window is usable where it currently
    /// sits and moving the camera would be a gratuitous jump.
    /// </summary>
    private bool IsSufficientlyVisible(WindowRect r)
    {
        long area = (long)Math.Max(0, r.W) * Math.Max(0, r.H);
        if (area <= 0) return false;

        long visible = 0;
        foreach (var bounds in _screens.AllBounds)
        {
            int overlapW = Math.Min(r.X + r.W, bounds.Right) - Math.Max(r.X, bounds.X);
            int overlapH = Math.Min(r.Y + r.H, bounds.Bottom) - Math.Max(r.Y, bounds.Y);
            if (overlapW <= 0 || overlapH <= 0) continue;
            visible += (long)overlapW * overlapH;
        }

        return visible >= area * MinVisibleFraction;
    }
}
