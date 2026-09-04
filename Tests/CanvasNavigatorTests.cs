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
}
