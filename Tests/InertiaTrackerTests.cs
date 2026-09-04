using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class InertiaTrackerTests
{
    [Fact]
    public void Release_NoSamples_ReturnsFalse()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);
        Assert.False(tracker.Release());
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void Release_SingleSample_ReturnsFalse()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);
        tracker.RecordDelta(10, 0);
        Assert.False(tracker.Release());
    }

    [Fact]
    public void Release_FastSamples_StartsInertia()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        // 10px every 16ms -> ~0.625 px/ms, well above 0.02 stop threshold
        for (int i = 0; i < 5; i++)
        {
            tracker.RecordDelta(10, 5);
            clock.Advance(16);
        }

        Assert.True(tracker.Release());
        Assert.True(tracker.IsActive);
    }

    [Fact]
    public void Release_StaleSamplesOutsideWindow_ReturnsFalse()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        tracker.RecordDelta(10, 10);
        tracker.RecordDelta(10, 10);
        // Velocity window is 100ms; jumping forward 500ms drops both samples.
        clock.Advance(500);

        Assert.False(tracker.Release());
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void Release_SlowSamples_BelowThreshold_ReturnsFalse()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        // 1px every 100ms -> 0.01 px/ms, below 0.02 stop threshold
        tracker.RecordDelta(1, 0);
        clock.Advance(100);
        tracker.RecordDelta(1, 0);
        clock.Advance(100);

        Assert.False(tracker.Release());
    }

    [Fact]
    public void Tick_BeforeRelease_ReturnsZeroAndInactive()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);
        var (dx, dy, stopped) = tracker.Tick();
        Assert.Equal(0, dx);
        Assert.Equal(0, dy);
        Assert.False(stopped);
    }

    [Fact]
    public void Tick_DecaysVelocityOverTime()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        // Build up strong velocity: 30px/16ms ~= 1.875 px/ms
        for (int i = 0; i < 5; i++)
        {
            tracker.RecordDelta(30, 0);
            clock.Advance(16);
        }
        Assert.True(tracker.Release());

        clock.Advance(16);
        var first = tracker.Tick();

        clock.Advance(16);
        var second = tracker.Tick();

        // Decay is multiplicative — second tick must move less than the first.
        Assert.True(Math.Abs(second.dx) < Math.Abs(first.dx),
            $"expected decay: first={first.dx} second={second.dx}");
    }

    [Fact]
    public void Tick_EventuallyStops()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        for (int i = 0; i < 5; i++)
        {
            tracker.RecordDelta(20, 0);
            clock.Advance(16);
        }
        Assert.True(tracker.Release());

        bool stopped = false;
        for (int i = 0; i < 1000 && !stopped; i++)
        {
            clock.Advance(16);
            stopped = tracker.Tick().stopped;
        }

        Assert.True(stopped, "inertia never decayed below stop threshold");
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void Cancel_StopsInertiaImmediately()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        for (int i = 0; i < 5; i++)
        {
            tracker.RecordDelta(20, 0);
            clock.Advance(16);
        }
        Assert.True(tracker.Release());
        Assert.True(tracker.IsActive);

        tracker.Cancel();

        Assert.False(tracker.IsActive);
        var (dx, dy, _) = tracker.Tick();
        Assert.Equal(0, dx);
        Assert.Equal(0, dy);
    }

    [Fact]
    public void RecordDelta_BeyondSampleWindow_TrimsOldest()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        // Push 30 samples — old ones beyond SampleWindow=15 get dropped, but the
        // logic still releases on the recent ones.
        for (int i = 0; i < 30; i++)
        {
            tracker.RecordDelta(15, 0);
            clock.Advance(8);
        }

        Assert.True(tracker.Release());
    }

    [Fact]
    public void Tick_AccumulatesSubPixelMotionInsteadOfDroppingIt()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        // ~0.0625 px/ms: above the 0.02 stop threshold, but a 1ms tick yields
        // 0.0625px, which rounds to zero. Without a carried residual the tracker
        // reports "still moving" while emitting nothing but zeros.
        tracker.RecordDelta(1, 1);
        clock.Advance(16);
        tracker.RecordDelta(1, 1);
        Assert.True(tracker.Release());

        int totalX = 0, totalY = 0;
        for (int i = 0; i < 40; i++)
        {
            clock.Advance(1);
            var (dx, dy, stopped) = tracker.Tick();
            totalX += dx;
            totalY += dy;
            if (stopped) break;
        }

        Assert.True(totalX > 0, $"expected accumulated x motion, got {totalX}");
        Assert.True(totalY > 0, $"expected accumulated y motion, got {totalY}");
    }

    [Fact]
    public void Tick_ConservesTotalDisplacementAcrossFrameRates()
    {
        // The same fling sampled at 1ms and at 16ms should travel comparable
        // distance — displacement is the integral of velocity, so it must not
        // depend on how finely the caller ticks.
        static int RunAt(long stepMs)
        {
            var clock = new FakeClock();
            var tracker = new InertiaTracker(clock);
            for (int i = 0; i < 5; i++)
            {
                tracker.RecordDelta(10, 0);
                clock.Advance(16);
            }
            tracker.Release();

            int total = 0;
            for (int i = 0; i < 4000; i++)
            {
                clock.Advance(stepMs);
                var (dx, _, stopped) = tracker.Tick();
                total += dx;
                if (stopped) break;
            }
            return total;
        }

        int fine = RunAt(1);
        int coarse = RunAt(16);

        Assert.InRange(fine, coarse - 12, coarse + 12);
    }
}
