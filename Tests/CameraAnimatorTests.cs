using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class CameraAnimatorTests
{
    [Fact]
    public void AnimateTo_ReachesTargetExactlyWhenDurationElapses()
    {
        var canvas = new Canvas();
        var clock = new FakeClock();
        var anim = new CameraAnimator(canvas, clock);

        anim.AnimateTo(1920, 1040, durationMs: 200);
        clock.Advance(200);

        Assert.False(anim.Tick());
        Assert.False(anim.IsAnimating);
        Assert.Equal(1920, canvas.CamX);
        Assert.Equal(1040, canvas.CamY);
    }

    [Fact]
    public void AnimateTo_OvershootingTheClockStillLandsExactly()
    {
        var canvas = new Canvas();
        var clock = new FakeClock();
        var anim = new CameraAnimator(canvas, clock);

        anim.AnimateTo(500, -300, durationMs: 100);
        clock.Advance(5000);

        anim.Tick();
        Assert.Equal(500, canvas.CamX);
        Assert.Equal(-300, canvas.CamY);
    }

    [Fact]
    public void Tick_MovesMonotonicallyTowardsTheTarget()
    {
        var canvas = new Canvas();
        var clock = new FakeClock();
        var anim = new CameraAnimator(canvas, clock);
        anim.AnimateTo(1000, 0, durationMs: 160);

        double previous = canvas.CamX;
        for (int i = 0; i < 10; i++)
        {
            clock.Advance(16);
            anim.Tick();
            Assert.True(canvas.CamX >= previous, "camera moved backwards");
            Assert.True(canvas.CamX <= 1000, "camera overshot the target");
            previous = canvas.CamX;
        }

        Assert.Equal(1000, canvas.CamX);
    }

    [Fact]
    public void Tick_EasesOutSoMostGroundIsCoveredEarly()
    {
        var canvas = new Canvas();
        var clock = new FakeClock();
        var anim = new CameraAnimator(canvas, clock);
        anim.AnimateTo(1000, 0, durationMs: 200);

        clock.Advance(100);
        anim.Tick();

        Assert.InRange(canvas.CamX, 860, 890);
    }

    [Fact]
    public void AnimateTo_MidFlight_RetargetsFromCurrentPosition()
    {
        var canvas = new Canvas();
        var clock = new FakeClock();
        var anim = new CameraAnimator(canvas, clock);

        anim.AnimateTo(1000, 0, durationMs: 200);
        clock.Advance(50);
        anim.Tick();
        double afterFirstLeg = canvas.CamX;
        Assert.True(afterFirstLeg > 0 && afterFirstLeg < 1000);

        anim.AnimateTo(2000, 0, durationMs: 200);
        clock.Advance(1);
        anim.Tick();

        Assert.True(canvas.CamX >= afterFirstLeg);
    }

    [Fact]
    public void Cancel_StopsAnimatingAndLeavesCameraWhereItIs()
    {
        var canvas = new Canvas();
        var clock = new FakeClock();
        var anim = new CameraAnimator(canvas, clock);

        anim.AnimateTo(1000, 0, durationMs: 200);
        clock.Advance(100);
        anim.Tick();
        double stopped = canvas.CamX;

        anim.Cancel();
        clock.Advance(500);

        Assert.False(anim.Tick());
        Assert.False(anim.IsAnimating);
        Assert.Equal(stopped, canvas.CamX);
    }

    [Fact]
    public void AnimateTo_ZeroDuration_JumpsImmediately()
    {
        var canvas = new Canvas();
        var anim = new CameraAnimator(canvas, new FakeClock());

        anim.AnimateTo(640, 480, durationMs: 0);

        Assert.False(anim.IsAnimating);
        Assert.Equal(640, canvas.CamX);
        Assert.Equal(480, canvas.CamY);
    }

    [Fact]
    public void Tick_WithoutAnimating_IsANoOp()
    {
        var canvas = new Canvas();
        canvas.SetCamera(12, 34);
        var anim = new CameraAnimator(canvas, new FakeClock());

        Assert.False(anim.Tick());
        Assert.Equal(12, canvas.CamX);
        Assert.Equal(34, canvas.CamY);
    }
}
