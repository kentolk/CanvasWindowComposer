using System;
using System.Collections.Generic;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class GridArrangerTests
{
    private const int CellW = 1920;
    private const int CellH = 1040;

    private static Canvas WithWindows(params (int id, double x, double y)[] windows)
    {
        var canvas = new Canvas();
        foreach (var (id, x, y) in windows)
            canvas.SetWindow((IntPtr)id, x, y, 800, 600);
        return canvas;
    }

    [Fact]
    public void Arrange_FillsRowsOfThreeBeforeWrapping()
    {
        var canvas = WithWindows((1, 0, 0), (2, 100, 0), (3, 200, 0), (4, 300, 0));

        GridArranger.Arrange(canvas, 3, CellW, CellH);

        Assert.Equal(0, canvas.Windows[(IntPtr)1].X);
        Assert.Equal(CellW, canvas.Windows[(IntPtr)2].X);
        Assert.Equal(CellW * 2, canvas.Windows[(IntPtr)3].X);

        // Fourth wraps to the start of row two.
        Assert.Equal(0, canvas.Windows[(IntPtr)4].X);
        Assert.Equal(CellH, canvas.Windows[(IntPtr)4].Y);
    }

    [Fact]
    public void Arrange_SizesEveryWindowToOneFullCell()
    {
        var canvas = WithWindows((1, 0, 0), (2, 5000, 5000));

        GridArranger.Arrange(canvas, 3, CellW, CellH);

        foreach (var w in canvas.Windows.Values)
        {
            // This is what replaces maximize: the window fills a screen while
            // staying an ordinary pannable canvas window.
            Assert.Equal(CellW, w.W);
            Assert.Equal(CellH, w.H);
        }
    }

    [Fact]
    public void Arrange_IsIdempotent()
    {
        var canvas = WithWindows((1, 40, 900), (2, 10, 10), (3, 3000, 60), (4, 77, 4000));

        GridArranger.Arrange(canvas, 3, CellW, CellH);
        var first = new Dictionary<IntPtr, WorldRect>(canvas.Windows);

        GridArranger.Arrange(canvas, 3, CellW, CellH);

        // Running it twice must not reshuffle — that's why ordering is by
        // position rather than Z-order.
        foreach (var (hWnd, rect) in first)
        {
            Assert.Equal(rect.X, canvas.Windows[hWnd].X);
            Assert.Equal(rect.Y, canvas.Windows[hWnd].Y);
        }
    }

    [Fact]
    public void Arrange_OrdersByPositionNotZOrder()
    {
        var canvas = WithWindows((1, 2000, 0), (2, 0, 0));
        // Window 1 is the most recently fronted, but sits to the right.
        canvas.BringToForeground((IntPtr)2);
        canvas.BringToForeground((IntPtr)1);

        GridArranger.Arrange(canvas, 3, CellW, CellH);

        // Left-most window still takes the left-most cell.
        Assert.Equal(0, canvas.Windows[(IntPtr)2].X);
        Assert.Equal(CellW, canvas.Windows[(IntPtr)1].X);
    }

    [Fact]
    public void Arrange_BandsNearlyAlignedWindowsIntoTheSameRow()
    {
        // A few pixels of Y drift must not split a visual row across grid rows.
        var canvas = WithWindows((1, 0, 0), (2, 900, 7), (3, 1800, 3));

        GridArranger.Arrange(canvas, 3, CellW, CellH);

        foreach (var w in canvas.Windows.Values)
            Assert.Equal(0, w.Y);
    }

    [Fact]
    public void Arrange_SkipsMinimizedAndPinnedWindows()
    {
        var canvas = WithWindows((1, 0, 0), (2, 100, 0), (3, 200, 0));
        canvas.CollapseWindow((IntPtr)2);
        canvas.SetPinnedToScreen((IntPtr)3, true);

        var cells = GridArranger.Arrange(canvas, 3, CellW, CellH);

        Assert.Single(cells);
        Assert.Equal(800, canvas.Windows[(IntPtr)2].W); // untouched
        Assert.Equal(800, canvas.Windows[(IntPtr)3].W);
    }

    [Fact]
    public void Arrange_EmptyCanvas_ReturnsNoCells()
    {
        Assert.Empty(GridArranger.Arrange(new Canvas(), 3, CellW, CellH));
    }

    [Fact]
    public void Arrange_RejectsNonPositiveCellSize()
    {
        var canvas = WithWindows((1, 0, 0));
        Assert.Empty(GridArranger.Arrange(canvas, 3, 0, CellH));
        Assert.Empty(GridArranger.Arrange(canvas, 3, CellW, -5));
        Assert.Equal(800, canvas.Windows[(IntPtr)1].W); // canvas untouched
    }

    [Fact]
    public void Arrange_SingleColumnWhenColumnsIsInvalid()
    {
        var canvas = WithWindows((1, 0, 0), (2, 100, 0));

        GridArranger.Arrange(canvas, 0, CellW, CellH);

        Assert.Equal(0, canvas.Windows[(IntPtr)1].X);
        Assert.Equal(0, canvas.Windows[(IntPtr)2].X);
        Assert.Equal(CellH, canvas.Windows[(IntPtr)2].Y);
    }

    [Fact]
    public void CameraForCell_FramesTheCellExactly()
    {
        var canvas = WithWindows((1, 0, 0), (2, 100, 0), (3, 200, 0), (4, 300, 0));
        var cells = GridArranger.Arrange(canvas, 3, CellW, CellH);

        foreach (var cell in cells)
        {
            var (camX, camY) = GridArranger.CameraForCell(cell, CellW, CellH);
            canvas.SetCamera(camX, camY);

            // With the camera on a cell, that cell's window sits at screen 0,0.
            var screen = canvas.WorldToScreen(canvas.Windows[cell.HWnd]);
            Assert.Equal(0, screen.X);
            Assert.Equal(0, screen.Y);
        }
    }

    [Fact]
    public void FrontMostCell_PicksTheMostRecentlyFrontedWindow()
    {
        var canvas = WithWindows((1, 0, 0), (2, 2000, 0), (3, 4000, 0));
        canvas.BringToForeground((IntPtr)3);
        canvas.BringToForeground((IntPtr)2);

        var cells = GridArranger.Arrange(canvas, 3, CellW, CellH);
        var front = GridArranger.FrontMostCell(canvas, cells);

        Assert.Equal((IntPtr)2, front.HWnd);
    }

    [Fact]
    public void Arrange_HonoursTheConfiguredColumnCount()
    {
        foreach (int columns in new[] { 1, 2, 4, 6 })
        {
            var canvas = new Canvas();
            for (int i = 1; i <= 12; i++)
                canvas.SetWindow((IntPtr)i, i * 10, 0, 800, 600);

            var cells = GridArranger.Arrange(canvas, columns, CellW, CellH);

            Assert.Equal(12, cells.Count);
            foreach (var cell in cells)
                Assert.InRange(cell.Column, 0, columns - 1);
            Assert.Equal((12 - 1) / columns, cells[^1].Row);
        }
    }

    // ==================== JOINING AN EXISTING GRID ====================

    [Fact]
    public void IsArranged_TrueWhenEveryWindowSitsOnACell()
    {
        var canvas = WithWindows((1, 0, 0), (2, 100, 0), (3, 200, 0));
        GridArranger.Arrange(canvas, 3, CellW, CellH);

        Assert.True(GridArranger.IsArranged(canvas, 3, CellW, CellH, exclude: IntPtr.Zero));
    }

    [Fact]
    public void IsArranged_FalseWhenAnyWindowIsOffGrid()
    {
        var canvas = WithWindows((1, 0, 0), (2, 100, 0));
        GridArranger.Arrange(canvas, 3, CellW, CellH);
        canvas.SetWindow((IntPtr)2, 137, 42, 640, 480); // dragged out by hand

        Assert.False(GridArranger.IsArranged(canvas, 3, CellW, CellH, exclude: IntPtr.Zero));
    }

    [Fact]
    public void IsArranged_IgnoresTheNewcomer()
    {
        var canvas = WithWindows((1, 0, 0), (2, 100, 0));
        GridArranger.Arrange(canvas, 3, CellW, CellH);
        canvas.SetWindow((IntPtr)9, 512, 384, 800, 600); // just opened, not placed yet

        Assert.True(GridArranger.IsArranged(canvas, 3, CellW, CellH, exclude: (IntPtr)9));
    }

    [Fact]
    public void IsArranged_ToleratesAFewPixelsOfDrift()
    {
        var canvas = WithWindows((1, 0, 0));
        GridArranger.Arrange(canvas, 3, CellW, CellH);
        // Reconcile rounds through integer screen coords; a window panned around
        // for a while lands a pixel or two off its cell.
        canvas.SetWindow((IntPtr)1, 2, -1, CellW + 1, CellH - 2);

        Assert.True(GridArranger.IsArranged(canvas, 3, CellW, CellH, exclude: IntPtr.Zero));
    }

    [Fact]
    public void IsArranged_FalseForAnEmptyCanvas()
    {
        // One window on its own is not evidence of an intended layout.
        Assert.False(GridArranger.IsArranged(new Canvas(), 3, CellW, CellH, exclude: IntPtr.Zero));
    }

    [Fact]
    public void FirstFreeCell_AppendsAfterAFullRow()
    {
        var canvas = WithWindows((1, 0, 0), (2, 100, 0), (3, 200, 0));
        GridArranger.Arrange(canvas, 3, CellW, CellH);

        var cell = GridArranger.FirstFreeCell(canvas, 3, CellW, CellH, (IntPtr)9);

        Assert.Equal(0, cell.Column);
        Assert.Equal(1, cell.Row);
    }

    [Fact]
    public void FirstFreeCell_FillsAGapLeftByAClosedWindow()
    {
        var canvas = WithWindows((1, 0, 0), (2, 100, 0), (3, 200, 0), (4, 300, 0));
        GridArranger.Arrange(canvas, 3, CellW, CellH);
        canvas.RemoveWindow((IntPtr)2); // middle of the first row closes

        var cell = GridArranger.FirstFreeCell(canvas, 3, CellW, CellH, (IntPtr)9);

        Assert.Equal(1, cell.Column);
        Assert.Equal(0, cell.Row);
    }

    [Fact]
    public void FirstFreeCell_OnAnEmptyCanvasIsTheOrigin()
    {
        var cell = GridArranger.FirstFreeCell(new Canvas(), 3, CellW, CellH, (IntPtr)9);
        Assert.Equal(0, cell.Column);
        Assert.Equal(0, cell.Row);
    }

    [Fact]
    public void FirstFreeCell_SkipsMinimizedAndPinnedOccupants()
    {
        var canvas = WithWindows((1, 0, 0), (2, 100, 0));
        GridArranger.Arrange(canvas, 3, CellW, CellH);
        canvas.CollapseWindow((IntPtr)2); // frees cell (1,0)

        var cell = GridArranger.FirstFreeCell(canvas, 3, CellW, CellH, (IntPtr)9);

        Assert.Equal(1, cell.Column);
        Assert.Equal(0, cell.Row);
    }

    // ==================== ELIGIBILITY (dialogs, fixed-size windows) ====================

    private static bool NotWindow(IntPtr excluded, IntPtr hWnd) => hWnd != excluded;

    [Fact]
    public void Arrange_SkipsIneligibleWindows()
    {
        var canvas = WithWindows((1, 0, 0), (2, 100, 0), (3, 200, 0));

        // Window 2 is a dialog: it stays on the canvas and pans, but must not be
        // handed a screen-sized cell away from the window that opened it.
        var cells = GridArranger.Arrange(canvas, 3, CellW, CellH, h => h != (IntPtr)2);

        Assert.Equal(2, cells.Count);
        Assert.DoesNotContain(cells, c => c.HWnd == (IntPtr)2);
        Assert.Equal(800, canvas.Windows[(IntPtr)2].W); // untouched
    }

    [Fact]
    public void IsArranged_IgnoresIneligibleWindowsSittingOffGrid()
    {
        var canvas = WithWindows((1, 0, 0), (2, 100, 0));
        GridArranger.Arrange(canvas, 3, CellW, CellH, h => h != (IntPtr)2);

        // The dialog is deliberately off-grid. It must not make the canvas look
        // un-arranged, or auto-placement would silently stop working the moment
        // any dialog was open.
        Assert.True(GridArranger.IsArranged(canvas, 3, CellW, CellH,
            exclude: IntPtr.Zero, eligible: h => h != (IntPtr)2));
    }

    [Fact]
    public void FirstFreeCell_DoesNotLetAnIneligibleWindowReserveACell()
    {
        var canvas = WithWindows((1, 0, 0));
        GridArranger.Arrange(canvas, 3, CellW, CellH);
        // A dialog that happens to sit on cell (1,0) shouldn't block it.
        canvas.SetWindow((IntPtr)2, CellW, 0, CellW, CellH);

        var cell = GridArranger.FirstFreeCell(canvas, 3, CellW, CellH, (IntPtr)9,
            eligible: h => h != (IntPtr)2);

        Assert.Equal(1, cell.Column);
        Assert.Equal(0, cell.Row);
    }

    [Fact]
    public void Arrange_WithNoPredicate_IncludesEverything()
    {
        var canvas = WithWindows((1, 0, 0), (2, 100, 0));
        Assert.Equal(2, GridArranger.Arrange(canvas, 3, CellW, CellH).Count);
    }
}
