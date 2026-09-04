using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class CanvasNavigatorTests
{
    private const int CellW = 1920;
    private const int CellH = 1040;

    private static (Canvas canvas, FakeScreens screens) Grid(int count)
    {
        var canvas = new Canvas();
        for (int i = 1; i <= count; i++)
            canvas.SetWindow((IntPtr)i, 0, 0, 800, 600);
        var screens = new FakeScreens { PrimaryWa = new ScreenRect(0, 0, CellW, CellH) };
        GridArranger.Arrange(canvas, 3, CellW, CellH);
        return (canvas, screens);
    }

    private static void LookAt(Canvas canvas, int column, int row)
    {
        canvas.SetCamera(column * (double)CellW, row * (double)CellH);
    }

    [Fact]
    public void FindNeighbour_RightFromFirstCell_LandsOnTheNextColumn()
    {
        var (canvas, screens) = Grid(3);
        LookAt(canvas, 0, 0);

        var hit = CanvasNavigation.FindNeighbour(canvas, screens, NavDirection.Right);

        Assert.NotNull(hit);
        Assert.Equal(CellW, canvas.Windows[hit!.Value].X);
    }

    [Fact]
    public void FindNeighbour_DownFromFirstRow_LandsDirectlyBelowNotDiagonally()
    {
        var (canvas, screens) = Grid(6);
        LookAt(canvas, 0, 0);

        var hit = CanvasNavigation.FindNeighbour(canvas, screens, NavDirection.Down);

        Assert.NotNull(hit);
        var w = canvas.Windows[hit!.Value];
        Assert.Equal(0, w.X);
        Assert.Equal(CellH, w.Y);
    }

    [Fact]
    public void FindNeighbour_AtTheEdge_ReturnsNull()
    {
        var (canvas, screens) = Grid(3);
        LookAt(canvas, 2, 0);

        Assert.Null(CanvasNavigation.FindNeighbour(canvas, screens, NavDirection.Right));
        Assert.Null(CanvasNavigation.FindNeighbour(canvas, screens, NavDirection.Up));
    }

    [Fact]
    public void FindNeighbour_LeftAndRightAreInverses()
    {
        var (canvas, screens) = Grid(3);
        LookAt(canvas, 1, 0);

        var right = CanvasNavigation.FindNeighbour(canvas, screens, NavDirection.Right);
        var left = CanvasNavigation.FindNeighbour(canvas, screens, NavDirection.Left);

        Assert.NotNull(right);
        Assert.NotNull(left);
        Assert.Equal(CellW * 2, canvas.Windows[right!.Value].X);
        Assert.Equal(0, canvas.Windows[left!.Value].X);
    }

    [Fact]
    public void FindNeighbour_WalksTheWholeGridAndStops()
    {
        var (canvas, screens) = Grid(3);
        LookAt(canvas, 0, 0);

        int steps = 0;
        while (CanvasNavigation.FindNeighbour(canvas, screens, NavDirection.Right) is IntPtr next)
        {
            CanvasNavigation.CenterOnWindow(canvas, screens, canvas.Windows[next]);
            if (++steps > 10) break;
        }

        Assert.Equal(2, steps);
    }

    [Fact]
    public void FindNeighbour_PrefersStraightAheadOverACloserDiagonal()
    {
        var canvas = new Canvas();
        var screens = new FakeScreens { PrimaryWa = new ScreenRect(0, 0, CellW, CellH) };
        canvas.SetWindow((IntPtr)1, 1500, 3000, 800, 600);
        canvas.SetWindow((IntPtr)2, 4000, 220, 800, 600);
        canvas.SetCamera(0, 0);

        var hit = CanvasNavigation.FindNeighbour(canvas, screens, NavDirection.Right);

        Assert.Equal((IntPtr)2, hit);
    }

    [Fact]
    public void FindNeighbour_IgnoresMinimizedAndPinnedWindows()
    {
        var canvas = new Canvas();
        var screens = new FakeScreens { PrimaryWa = new ScreenRect(0, 0, CellW, CellH) };
        canvas.SetWindow((IntPtr)1, 3000, 220, 800, 600);
        canvas.SetWindow((IntPtr)2, 6000, 220, 800, 600);
        canvas.CollapseWindow((IntPtr)1);
        canvas.SetPinnedToScreen((IntPtr)2, true);
        canvas.SetCamera(0, 0);

        Assert.Null(CanvasNavigation.FindNeighbour(canvas, screens, NavDirection.Right));
    }

    [Fact]
    public void FindNeighbour_EmptyCanvas_ReturnsNull()
    {
        var screens = new FakeScreens();
        Assert.Null(CanvasNavigation.FindNeighbour(new Canvas(), screens, NavDirection.Down));
    }

    // ============ Ctrl+Alt+middle-click: centre the window under the cursor ============

    private sealed class GestureHarness : IDisposable
    {
        public Canvas Canvas = new();
        public FakeWindowApi Api = new();
        public FakeInputRouter Input = new();
        public FakeScreens Screens = new();
        public FakeAppConfig Config = new();
        public WindowManager Wm;
        public CanvasNavigator Navigator;

        public GestureHarness()
        {
            Wm = new WindowManager(Canvas, Api, Config, Input, new FakeClock());
            Navigator = new CanvasNavigator(Canvas, Wm, Input, Screens, Config, new FakeClock());
        }

        /// <summary>Track a window and put it under the cursor for the hit test.</summary>
        public void WindowUnderCursor(IntPtr hWnd, int x, int y, int w, int h)
        {
            Canvas.SetWindow(hWnd, x, y, w, h);
            Api.HitTest = (_, _) => hWnd;
        }

        public void Dispose()
        {
            Navigator.Dispose();
        }
    }

    [Fact]
    public void CenterRequest_OnAWindowFillingTheScreen_CentersIt()
    {
        using var h = new GestureHarness();
        // 2000x1000 hanging 100px off the left edge: ~92% of the 1920x1080
        // monitor. This is the case the gesture exists for.
        h.WindowUnderCursor((IntPtr)7, -100, 40, 2000, 1000);

        h.Input.RaiseCenterRequested(500, 500);

        // world.X + W/2 - viewWidth/2  ==  -100 + 1000 - 960
        Assert.Equal(-60, h.Canvas.CamX);
        Assert.Equal(20, h.Canvas.CamY);
    }

    [Fact]
    public void CenterRequest_OnASmallWindowAtTheSide_DoesNothing()
    {
        using var h = new GestureHarness();
        // 100x300 visible of a 400x300 window: 1.4% of the screen. Clicking the
        // sliver you can see must not haul the view across to it — that is the
        // behaviour this gesture replaced.
        h.WindowUnderCursor((IntPtr)7, 1820, 100, 400, 300);

        h.Input.RaiseCenterRequested(1850, 200);

        Assert.Equal(0, h.Canvas.CamX);
        Assert.Equal(0, h.Canvas.CamY);
    }

    [Fact]
    public void CenterRequest_OnAWindowCoveringHalfTheScreen_DoesNothing()
    {
        using var h = new GestureHarness();
        // 1100x1000 visible is ~53% — cut off, but not filling the view.
        h.WindowUnderCursor((IntPtr)7, -100, 40, 1200, 1000);

        h.Input.RaiseCenterRequested(500, 500);

        Assert.Equal(0, h.Canvas.CamX);
    }

    [Fact]
    public void CenterRequest_WithNothingUnderTheCursor_DoesNothing()
    {
        using var h = new GestureHarness();
        h.Canvas.SetWindow((IntPtr)7, -100, 40, 2000, 1000);
        h.Api.HitTest = (_, _) => IntPtr.Zero;

        h.Input.RaiseCenterRequested(500, 500);

        Assert.Equal(0, h.Canvas.CamX);
    }

    [Fact]
    public void CenterRequest_OnAWindowTheCanvasDoesNotTrack_DoesNothing()
    {
        using var h = new GestureHarness();
        h.Canvas.SetWindow((IntPtr)7, -100, 40, 2000, 1000);
        // Cursor is over something we do not manage — the taskbar, a dialog we
        // filtered out. There is no world rect to centre on.
        h.Api.HitTest = (_, _) => (IntPtr)99;

        h.Input.RaiseCenterRequested(500, 500);

        Assert.Equal(0, h.Canvas.CamX);
    }

    [Fact]
    public void CenterRequest_OnAPinnedWindow_DoesNothing()
    {
        using var h = new GestureHarness();
        h.WindowUnderCursor((IntPtr)7, -100, 40, 2000, 1000);
        h.Canvas.SetPinnedToScreen((IntPtr)7, true);

        // A pinned window does not move with the camera, so centring on it would
        // drag every other window across while it sat still.
        h.Input.RaiseCenterRequested(500, 500);

        Assert.Equal(0, h.Canvas.CamX);
    }

    [Fact]
    public void CenterRequest_OnAFullyVisibleWindowFillingTheScreen_StillCentersIt()
    {
        using var h = new GestureHarness();
        // Nothing is cut off, but it fills the view and sits low. The gesture is
        // explicit, so it does what it says rather than second-guessing.
        h.WindowUnderCursor((IntPtr)7, 0, 0, 1920, 800);

        h.Input.RaiseCenterRequested(500, 500);

        Assert.Equal(-120, h.Canvas.CamY);
    }
}
