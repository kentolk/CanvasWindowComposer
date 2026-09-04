using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// Tracks panning velocity from recorded deltas and decays it over time.
/// Thread-safe: RecordDelta/Release/Cancel can be called from UI thread,
/// Tick from a render thread.
/// </summary>
internal sealed class InertiaTracker
{
    private const int SampleWindow = 15;
    private const double FrictionPerFrame = 0.92;
    private const double TargetFrameMs = 16.667;
    private const double StopThresholdPxPerMs = 0.02;
    private const long VelocityWindowMs = 100;
    private const double MaxDeltaMs = 60;

    private readonly object _lock = new();
    private readonly List<(double dx, double dy, long ticks)> _samples = new();
    private readonly IClock _clock;
    private double _vx, _vy;
    // Sub-pixel carry. Tick emits whole pixels and keeps the fraction for the
    // next frame, so the slow tail of a fling still advances instead of
    // rounding to zero every frame until the stop threshold is crossed.
    private double _residualX, _residualY;
    private volatile bool _active;
    private long _lastTick;

    public InertiaTracker(IClock? clock = null)
    {
        _clock = clock ?? SystemClock.Instance;
    }

    public bool IsActive
    {
        get { return _active; }
    }

    public void RecordDelta(int dx, int dy)
    {
        lock (_lock)
        {
            _samples.Add((dx, dy, _clock.TickCount64));
            if (_samples.Count > SampleWindow)
                _samples.RemoveRange(0, _samples.Count - SampleWindow);
        }
    }

    /// <summary>
    /// Compute velocity from recent samples and start decaying.
    /// Returns true if inertia is active (velocity above stop threshold).
    /// </summary>
    public bool Release()
    {
        bool hasVelocity = false;
        lock (_lock)
        {
            if (_samples.Count >= 2)
            {
                double sumDx = 0, sumDy = 0;
                long now = _clock.TickCount64;
                long oldest = now;

                for (int i = _samples.Count - 1; i >= 0; i--)
                {
                    var s = _samples[i];
                    if (now - s.ticks > VelocityWindowMs) break;
                    sumDx += s.dx;
                    sumDy += s.dy;
                    oldest = s.ticks;
                }

                long elapsed = now - oldest;
                if (elapsed >= 1)
                {
                    _vx = sumDx / elapsed;
                    _vy = sumDy / elapsed;
                    _residualX = 0;
                    _residualY = 0;
                    hasVelocity = Math.Abs(_vx) >= StopThresholdPxPerMs ||
                                  Math.Abs(_vy) >= StopThresholdPxPerMs;
                }
            }
            _samples.Clear();
        }

        if (hasVelocity)
        {
            _lastTick = _clock.TickCount64;
            _active = true;
        }
        return hasVelocity;
    }

    public void Cancel()
    {
        _active = false;
        lock (_lock)
        {
            _vx = _vy = 0;
            _residualX = _residualY = 0;
            _samples.Clear();
        }
    }

    /// <summary>
    /// Advance inertia by one frame. Returns the deltas to apply and whether
    /// inertia just stopped (caller should clean up).
    /// </summary>
    public (int dx, int dy, bool stopped) Tick()
    {
        if (!_active) return (0, 0, false);

        long now = _clock.TickCount64;
        double dt = Math.Clamp(now - _lastTick, 1, MaxDeltaMs);
        _lastTick = now;

        double vx, vy;
        lock (_lock)
        {
            double decay = Math.Pow(FrictionPerFrame, dt / TargetFrameMs);
            _vx *= decay;
            _vy *= decay;
            vx = _vx;
            vy = _vy;
        }

        if (Math.Abs(vx) < StopThresholdPxPerMs && Math.Abs(vy) < StopThresholdPxPerMs)
        {
            _active = false;
            return (0, 0, true);
        }

        // Integrate into the carry and emit only whole pixels. Rounding each
        // frame independently discards up to half a pixel per axis per frame,
        // which at the stop threshold (~0.33px/frame at 60Hz) is most of the
        // remaining motion.
        _residualX += vx * dt;
        _residualY += vy * dt;
        int dx = (int)_residualX;
        int dy = (int)_residualY;
        _residualX -= dx;
        _residualY -= dy;
        return (dx, dy, false);
    }
}
