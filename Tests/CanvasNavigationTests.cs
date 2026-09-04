using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class CanvasNavigationTests
{
    [Fact]
    public void CenterOnWindow_CentersUsingPrimaryWorkingArea()
    {
        var canvas = new Canvas();
        var screens = new FakeScreens
        {
            // Deliberately different from the primary working area, so a
            // regression back to VirtualScreen shows up as a failure.
            Virtual = new ScreenRect(-1920, 0, 3840, 1080),
            PrimaryWa = new ScreenRect(0, 0, 1600, 900)
        };
        var world = new WorldRect { X = 4000, Y = 3000, W = 400, H = 300 };

        CanvasNavigation.CenterOnWindow(canvas, screens, world);

        // Camera lands so the window's centre sits at the middle of a 1600x900
        // viewport: 4000 + 200 - 800 = 3400, 3000 + 150 - 450 = 2700.
        Assert.Equal(3400, canvas.CamX);
        Assert.Equal(2700, canvas.CamY);
    }

    [Fact]
    public void CenterOnWindow_PutsWindowCentreAtViewportCentre()
    {
        var canvas = new Canvas();
        var screens = new FakeScreens { PrimaryWa = new ScreenRect(0, 0, 1920, 1040) };
        var world = new WorldRect { X = 5000, Y = -2000, W = 800, H = 600 };

        CanvasNavigation.CenterOnWindow(canvas, screens, world);

        var (sx, sy) = canvas.WorldToScreen(world.X + world.W / 2, world.Y + world.H / 2);
        Assert.Equal(960, sx);
        Assert.Equal(520, sy);
    }

    [Fact]
    public void CenterOnWindow_IsIndependentOfMonitorCount()
    {
        // Attaching a second monitor must not change where "jump to window"
        // lands — that was the VirtualScreen bug.
        static double CamFor(FakeScreens s)
        {
            var canvas = new Canvas();
            CanvasNavigation.CenterOnWindow(canvas, s, new WorldRect { X = 1000, Y = 0, W = 400, H = 300 });
            return canvas.CamX;
        }

        var single = new FakeScreens { Virtual = new ScreenRect(0, 0, 1920, 1080) };
        var dual = new FakeScreens { Virtual = new ScreenRect(-1920, 0, 3840, 1080) };

        Assert.Equal(CamFor(single), CamFor(dual));
    }
}
