using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class ForegroundCoordinatorTests
{
    private sealed class Harness
    {
        public Canvas Canvas = new();
        public FakeClock Clock = new();
        public FakeScreens Screens = new();
        public FakeInputRouter Input = new();
        public FakeOverviewController Overview = new();
        public ForegroundCoordinator Foreground = null!;

        public Harness()
        {
            Foreground = new ForegroundCoordinator(Canvas, Overview, Input, Clock, Screens);
        }
    }

    [Fact]
    public void WindowMinimized_StampsLastWindowLostTick()
    {
        var h = new Harness();

        // Tracked and holding the foreground before it is minimized, which is the
        // only order that occurs in practice — suppression is scoped to windows we
        // manage, and to the one the foreground is being taken from.
        h.Canvas.SetWindow((IntPtr)1, 100, 100, 400, 300);
        h.Clock.Now = 12000;
        h.Input.RaiseWindowFocused((IntPtr)1);

        h.Canvas.SetWindow((IntPtr)1, 5000, 5000, 400, 300);
        h.Clock.Now = 12345;
        h.Input.RaiseWindowMinimized((IntPtr)1);

        // Verify the stamp was applied: an immediate WindowFocused on an
        // off-screen tracked window should NOT recenter.
        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)1);
        Assert.Equal(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void WindowFocused_OffScreenWindow_RecentersCamera()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 5000, 5000, 400, 300);

        h.Clock.Now = 10000;
        h.Input.RaiseWindowFocused((IntPtr)1);

        // Camera should now be centered roughly on the window
        var (sx, sy) = h.Canvas.WorldToScreen(5200, 5150);
        Assert.InRange(sx, 1920 / 2 - 2, 1920 / 2 + 2);
        Assert.InRange(sy, 1040 / 2 - 2, 1040 / 2 + 2);
    }

    [Fact]
    public void WindowFocused_OnScreenWindow_DoesNotRecenter()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 100, 100, 400, 300);

        h.Clock.Now = 10000;
        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)1);

        Assert.Equal(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void WindowFocused_WithinSuppressionWindowAfterDestroy_DoesNotRecenter()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)2, 5000, 5000, 400, 300);

        h.Canvas.SetWindow((IntPtr)1, 0, 0, 400, 300);
        h.Input.RaiseWindowFocused((IntPtr)1); // the window about to close holds focus
        h.Clock.Now = 10000;
        h.Canvas.RemoveWindow((IntPtr)1);

        // Within 500ms — focused event should be suppressed

        h.Clock.Now = 10499;
        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)2);

        Assert.Equal(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void WindowFocused_AfterSuppressionWindowExpires_RecentersAgain()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)2, 5000, 5000, 400, 300);

        h.Canvas.SetWindow((IntPtr)1, 0, 0, 400, 300);
        h.Input.RaiseWindowFocused((IntPtr)1);
        h.Clock.Now = 10000;
        h.Canvas.RemoveWindow((IntPtr)1);

        // Past 500ms suppression window — focus should recenter

        h.Clock.Now = 10501;
        h.Input.RaiseWindowFocused((IntPtr)2);

        var (sx, _) = h.Canvas.WorldToScreen(5200, 5150);
        Assert.InRange(sx, 1920 / 2 - 2, 1920 / 2 + 2);
    }

    [Fact]
    public void OverviewClose_StampsOverlayClosedTick_SuppressesNextFocus()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 5000, 5000, 400, 300);

        h.Clock.Now = 10000;
        h.Overview.TransitionTo(OverviewMode.Zooming);
        h.Overview.TransitionTo(OverviewMode.Hidden);

        h.Clock.Now = 10100;
        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)1);

        Assert.Equal(camBefore, h.Canvas.CamX);
    }

    // ==================== PARTIAL VISIBILITY ====================

    [Fact]
    public void WindowFocused_OnlyASliverOnScreen_RecentersCamera()
    {
        var h = new Harness();
        // 20px of a 400px-wide window pokes onto the 1920x1080 monitor. The user
        // clicking this window's taskbar icon expects it to come to them.
        h.Canvas.SetWindow((IntPtr)1, 1900, 100, 400, 300);
        h.Clock.Now = 10000;

        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)1);

        Assert.NotEqual(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void WindowFocused_HalfOnScreen_RecentersCamera()
    {
        var h = new Harness();
        // 100 of 400 visible — a quarter. Still not usable where it is.
        h.Canvas.SetWindow((IntPtr)1, 1820, 100, 400, 300);
        h.Clock.Now = 10000;

        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)1);

        Assert.NotEqual(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void WindowFocused_MostlyOnScreen_DoesNotRecenter()
    {
        var h = new Harness();
        // 320 of 400 visible — comfortably usable, so moving the camera would
        // just be a jarring jump for no reason.
        h.Canvas.SetWindow((IntPtr)1, 1600, 100, 400, 300);
        h.Clock.Now = 10000;

        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)1);

        Assert.Equal(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void WindowFocused_FullyOnScreen_DoesNotRecenter()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 200, 200, 400, 300);
        h.Clock.Now = 10000;

        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)1);

        Assert.Equal(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void WindowRestored_ClearsMinimizeSuppressionSoTheRestoreCanRecenter()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 5000, 5000, 400, 300);

        // Clicking the taskbar icon of a foreground window minimises it; clicking
        // again restores it. Without clearing the stamp, the restore's focus event
        // lands inside the minimise suppression window and the window comes back
        // exactly where it was — off-screen.
        h.Clock.Now = 10000;
        h.Input.RaiseWindowMinimized((IntPtr)1);

        h.Clock.Now = 10100; // well inside ForegroundSuppressionMs
        h.Input.RaiseWindowRestored((IntPtr)1);

        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)1);

        Assert.NotEqual(camBefore, h.Canvas.CamX);
    }

    // ==================== SUPPRESSION SCOPE ====================

    [Fact]
    public void WindowFocused_AfterAnUntrackedWindowIsDestroyed_StillRecenters()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)2, 5000, 5000, 400, 300);
        h.Clock.Now = 10000;

        // Hovering a taskbar button destroys tooltips and the thumbnail flyout,
        // none of which we manage. Letting those refresh the suppression stamp
        // is why clicking a taskbar thumbnail activated the window but never
        // brought the camera to it.
        h.Input.RaiseWindowDestroyed((IntPtr)999);

        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)2);

        Assert.NotEqual(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void WindowFocused_AfterAnUntrackedWindowIsMinimized_StillRecenters()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)2, 5000, 5000, 400, 300);
        h.Clock.Now = 10000;

        h.Input.RaiseWindowMinimized((IntPtr)999);

        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)2);

        Assert.NotEqual(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void WindowFocused_RepeatedUntrackedChurnNeverBlocksTheRecenter()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)2, 5000, 5000, 400, 300);

        // A hover generates a stream of these, which is what kept the stamp
        // permanently fresh for as long as the pointer sat over the taskbar.
        for (int i = 0; i < 20; i++)
        {
            h.Clock.Now = 10000 + i * 20;
            h.Input.RaiseWindowDestroyed((IntPtr)(1000 + i));
        }

        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)2);

        Assert.NotEqual(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void WindowRemoved_ForTheFocusedWindow_StillSuppresses()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 0, 0, 400, 300);
        h.Canvas.SetWindow((IntPtr)2, 5000, 5000, 400, 300);

        h.Input.RaiseWindowFocused((IntPtr)1);
        h.Clock.Now = 10000;
        h.Canvas.RemoveWindow((IntPtr)1);

        // The original purpose of the suppression is intact: the foreground
        // window closing must not make the camera chase whatever Windows hands
        // focus to next.
        h.Clock.Now = 10200;
        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)2);

        Assert.Equal(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void WindowRemoved_ForABackgroundWindow_DoesNotSuppress()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 0, 0, 400, 300);
        h.Canvas.SetWindow((IntPtr)2, 5000, 5000, 400, 300);

        // Window 1 never had the foreground, so nothing is being reassigned when
        // it leaves — the stale-window tick and the shell's short-lived windows
        // both land here, and blacking out the next 500ms of recentring is what
        // made a taskbar click come to the front without the camera following.
        h.Input.RaiseWindowFocused((IntPtr)3);
        h.Clock.Now = 10000;
        h.Canvas.RemoveWindow((IntPtr)1);

        h.Clock.Now = 10050;
        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)2);

        Assert.NotEqual(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void WindowMinimized_ForABackgroundWindow_DoesNotSuppress()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 0, 0, 400, 300);
        h.Canvas.SetWindow((IntPtr)2, 5000, 5000, 400, 300);

        h.Input.RaiseWindowFocused((IntPtr)3);
        h.Clock.Now = 10000;
        h.Input.RaiseWindowMinimized((IntPtr)1);

        h.Clock.Now = 10050;
        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)2);

        Assert.NotEqual(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void RepeatedBackgroundRemovalChurn_NeverBlocksTheRecenter()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)2, 5000, 5000, 400, 300);
        h.Input.RaiseWindowFocused((IntPtr)3);

        // The stale sweep runs on the same 500ms cadence as the suppression
        // window, so a window churning in and out of tracking could hold the
        // blackout open indefinitely.
        for (int i = 0; i < 20; i++)
        {
            h.Clock.Now = 10000 + i * 20;
            h.Canvas.SetWindow((IntPtr)(1000 + i), 0, 0, 400, 300);
            h.Canvas.RemoveWindow((IntPtr)(1000 + i));
        }

        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)2);

        Assert.NotEqual(camBefore, h.Canvas.CamX);
    }
}
