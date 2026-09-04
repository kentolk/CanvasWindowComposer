using System;

namespace CanvasDesktop;

/// <summary>
/// Eases the canvas camera from where it is to a target position.
///
/// Deliberately passive: it owns no timer and starts no thread. The caller ticks
/// it — a UI timer in production, a <c>FakeClock</c> in tests — which keeps the
/// easing math verifiable and matches how <see cref="InertiaTracker"/> is driven.
///
/// Note that moving the camera does not move real windows on its own:
/// <see cref="WindowManager.OnCameraChanged"/> throttles reprojection to 200ms
/// because normal panning is covered by the overview's live DWM thumbnails. A
/// caller animating with no overlay up therefore has to schedule its own
/// per-frame reproject, or the camera will glide while the windows sit still.
/// </summary>
internal sealed class CameraAnimator
{
    /// <summary>Short enough to feel instant, long enough to read as motion.</summary>
    public const long DefaultDurationMs = 180;

    private readonly Canvas _canvas;
    private readonly IClock _clock;

    private double _startX, _startY;
    private double _targetX, _targetY;
    private long _startTick;
    private long _durationMs;
    private bool _animating;

    public CameraAnimator(Canvas canvas, IClock? clock = null)
    {
        _canvas = canvas;
        _clock = clock ?? SystemClock.Instance;
    }

    public bool IsAnimating
    {
        get { return _animating; }
    }

    /// <summary>
    /// Begin easing to (<paramref name="targetX"/>, <paramref name="targetY"/>).
    /// Retargets cleanly mid-flight: the new run starts from wherever the camera
    /// has actually reached, so holding an arrow key doesn't stutter.
    /// </summary>
    public void AnimateTo(double targetX, double targetY, long durationMs = DefaultDurationMs)
    {
        if (durationMs <= 0)
        {
            _animating = false;
            _canvas.SetCamera(targetX, targetY);
            return;
        }

        _startX = _canvas.CamX;
        _startY = _canvas.CamY;
        _targetX = targetX;
        _targetY = targetY;
        _startTick = _clock.TickCount64;
        _durationMs = durationMs;
        _animating = true;
    }

    /// <summary>Advance one frame. Returns true while the animation is still running.</summary>
    public bool Tick()
    {
        if (!_animating) return false;

        long elapsed = _clock.TickCount64 - _startTick;
        double t = Math.Clamp(elapsed / (double)_durationMs, 0.0, 1.0);
        double eased = EaseOutCubic(t);

        _canvas.SetCamera(
            _startX + (_targetX - _startX) * eased,
            _startY + (_targetY - _startY) * eased);

        if (t < 1.0) return true;

        _animating = false;
        return false;
    }

    public void Cancel()
    {
        _animating = false;
    }

    /// <summary>Fast start, gentle settle — the standard feel for a UI camera move.</summary>
    private static double EaseOutCubic(double t)
    {
        double inv = 1.0 - t;
        return 1.0 - inv * inv * inv;
    }
}
