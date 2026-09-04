using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// Lays canvas windows out in a fixed-column grid of screen-sized cells.
///
/// Each cell is exactly one monitor work area, so a window placed in a cell
/// fills the screen without ever being maximized — it stays an ordinary Normal
/// window that pans, clips and reprojects like any other. That is deliberate:
/// a real WS_MAXIMIZE window is excluded from the canvas entirely
/// (<see cref="IWindowApi.IsManageable"/>) and Windows re-snaps it if we try to
/// move it, so "fills the screen" and "participates in the canvas" are only
/// compatible if we never actually maximize.
///
/// Cell size also makes navigation trivial: stepping the camera by one cell
/// width or height lands exactly on the neighbouring window.
/// </summary>
internal static class GridArranger
{
    public const int DefaultColumns = 3;

    /// <summary>Fewest columns worth arranging — one gives a single stacked column.</summary>
    public const int MinColumns = 1;

    /// <summary>
    /// Upper bound for the configured column count. Not a technical limit; it
    /// just keeps a typo like 3000 from scattering every window a thousand
    /// screens apart with no obvious way back.
    /// </summary>
    public const int MaxColumns = 32;


    /// <summary>A window's assigned position in the grid.</summary>
    internal readonly record struct Cell(IntPtr HWnd, int Column, int Row);

    /// <summary>
    /// Decide which window goes in which cell, without touching the canvas.
    ///
    /// Ordering is by current world position — banded into rows of
    /// <paramref name="cellHeight"/>, then left-to-right — rather than by
    /// Z-order. That keeps the user's spatial mental map intact and makes a
    /// second Arrange a no-op instead of a reshuffle; Z-order ordering would
    /// move everything around again every time the focus changed.
    /// </summary>
    public static List<Cell> Plan(Canvas canvas, int columns, int cellHeight, Func<IntPtr, bool>? eligible = null)
    {
        if (columns < 1) columns = 1;
        if (cellHeight < 1) cellHeight = 1;

        var ordered = new List<(IntPtr HWnd, WorldRect Rect)>();
        foreach (var (hWnd, rect) in canvas.Windows)
        {
            if (!IsEligible(hWnd, rect, eligible)) continue;
            ordered.Add((hWnd, rect));
        }


        ordered.Sort((a, b) => Compare(a.Rect, b.Rect, cellHeight));

        var cells = new List<Cell>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
            cells.Add(new Cell(ordered[i].HWnd, i % columns, i / columns));
        return cells;
    }

    /// <summary>
    /// Minimized windows have no meaningful place in a spatial layout, pinned
    /// ones opted out of canvas positioning, and <paramref name="eligible"/>
    /// filters out anything the caller considers unfit for a cell (dialogs,
    /// fixed-size windows).
    /// </summary>
    private static bool IsEligible(IntPtr hWnd, WorldRect rect, Func<IntPtr, bool>? eligible)
    {
        if (rect.State != WindowState.Normal || rect.PinnedToScreen) return false;
        return eligible == null || eligible(hWnd);
    }

    private static int Compare(WorldRect a, WorldRect b, int cellHeight)

    {
        // Band by cell height so windows sitting roughly side by side land in
        // the same row instead of being split by a few pixels of Y drift.
        long bandA = (long)Math.Floor(a.Y / cellHeight);
        long bandB = (long)Math.Floor(b.Y / cellHeight);
        if (bandA != bandB) return bandA.CompareTo(bandB);

        int byX = a.X.CompareTo(b.X);
        if (byX != 0) return byX;

        // Deterministic tiebreak so the layout never depends on dictionary order.
        return b.ZOrder.CompareTo(a.ZOrder);
    }

    /// <summary>
    /// Place every eligible window into a <paramref name="columns"/>-wide grid of
    /// <paramref name="cellWidth"/> x <paramref name="cellHeight"/> cells.
    /// Returns the placements; empty if there was nothing to arrange.
    /// </summary>
    public static IReadOnlyList<Cell> Arrange(Canvas canvas, int columns, int cellWidth, int cellHeight,
        Func<IntPtr, bool>? eligible = null)
    {
        if (cellWidth < 1 || cellHeight < 1) return Array.Empty<Cell>();

        var cells = Plan(canvas, columns, cellHeight, eligible);

        foreach (var cell in cells)
        {
            var (x, y) = CameraForCell(cell, cellWidth, cellHeight);
            canvas.SetWindow(cell.HWnd, x, y, cellWidth, cellHeight);
        }
        return cells;
    }

    /// <summary>
    /// How far a window may sit from an exact cell and still count as being on
    /// it. Reconciliation rounds through integer screen coordinates, so a window
    /// that has been panned around for a while lands a pixel or two off.
    /// </summary>
    private const double CellTolerance = 4.0;

    private static bool TryCellOf(WorldRect rect, int columns, int cellWidth, int cellHeight,
        out int column, out int row)
    {
        column = 0;
        row = 0;
        if (Math.Abs(rect.W - cellWidth) > CellTolerance) return false;
        if (Math.Abs(rect.H - cellHeight) > CellTolerance) return false;

        double c = rect.X / cellWidth;
        double r = rect.Y / cellHeight;
        column = (int)Math.Round(c);
        row = (int)Math.Round(r);

        if (Math.Abs(c - column) * cellWidth > CellTolerance) return false;
        if (Math.Abs(r - row) * cellHeight > CellTolerance) return false;
        return column >= 0 && column < columns && row >= 0;
    }

    /// <summary>
    /// True when every eligible window already sits on a cell — i.e. the canvas
    /// is currently a grid that a newly opened window should join rather than
    /// landing wherever its app happened to put it.
    ///
    /// <paramref name="exclude"/> is the new window itself, which by definition
    /// is not on a cell yet. Returns false for an empty canvas: one window on
    /// its own is not evidence of an intended layout.
    /// </summary>
    public static bool IsArranged(Canvas canvas, int columns, int cellWidth, int cellHeight, IntPtr exclude,
        Func<IntPtr, bool>? eligible = null)
    {
        if (columns < 1 || cellWidth < 1 || cellHeight < 1) return false;

        bool any = false;
        foreach (var (hWnd, rect) in canvas.Windows)
        {
            if (hWnd == exclude) continue;
            if (!IsEligible(hWnd, rect, eligible)) continue;

            any = true;
            if (!TryCellOf(rect, columns, cellWidth, cellHeight, out _, out _))
                return false;
        }
        return any;
    }

    /// <summary>
    /// Lowest-index cell not already occupied, so a new window fills a gap left
    /// by a closed one before extending the grid downwards.
    /// </summary>
    public static Cell FirstFreeCell(Canvas canvas, int columns, int cellWidth, int cellHeight, IntPtr hWnd,
        Func<IntPtr, bool>? eligible = null)
    {
        if (columns < 1) columns = 1;

        var taken = new HashSet<(int Column, int Row)>();
        foreach (var (other, rect) in canvas.Windows)
        {
            if (other == hWnd) continue;
            if (!IsEligible(other, rect, eligible)) continue;
            if (TryCellOf(rect, columns, cellWidth, cellHeight, out int c, out int r))
                taken.Add((c, r));
        }

        // Terminates: taken is finite, so some index is always free.
        for (int i = 0; ; i++)
        {
            var candidate = (Column: i % columns, Row: i / columns);
            if (!taken.Contains(candidate))
                return new Cell(hWnd, candidate.Column, candidate.Row);
        }
    }

    /// <summary>Camera position that frames <paramref name="cell"/> exactly.</summary>

    public static (double x, double y) CameraForCell(Cell cell, int cellWidth, int cellHeight)
    {
        return (cell.Column * (double)cellWidth, cell.Row * (double)cellHeight);
    }

    /// <summary>
    /// The cell holding the most recently fronted window, so the caller can keep
    /// the user looking at what they were already looking at after a rearrange.
    /// </summary>
    public static Cell FrontMostCell(Canvas canvas, IReadOnlyList<Cell> cells)
    {
        Cell best = cells[0];
        long bestZ = long.MinValue;
        foreach (var cell in cells)
        {
            if (!canvas.Windows.TryGetValue(cell.HWnd, out var rect)) continue;
            if (rect.ZOrder <= bestZ) continue;
            bestZ = rect.ZOrder;
            best = cell;
        }
        return best;
    }
}
