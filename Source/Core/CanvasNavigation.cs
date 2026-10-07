using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>Direction for <see cref="CanvasNavigation.FindNeighbour"/>.</summary>
internal enum NavDirection { Left, Right, Up, Down }

/// <summary>
/// The one place that decides which viewport "jump to this window" centres on,
/// and which window a directional jump lands on.
///
/// Three features navigate the canvas to a window — Alt+S search, the overview's
/// click/Enter, and <see cref="ForegroundCoordinator"/>'s off-screen recentre.
/// They used to pick their own viewport rectangle: the overview measured against
/// <see cref="IScreens.VirtualScreen"/> while the other two used
/// <see cref="IScreens.PrimaryWorkingArea"/>. On a single monitor those agree, so
/// the split went unnoticed; on a multi-monitor layout they land the same window
/// in visibly different places, and on a layout with a monitor left of or above
/// primary (negative virtual-screen origin) the overview's result can sit partly
/// off-screen.
///
/// Primary working area is the right answer: the canvas camera projects to screen
/// coordinates whose origin is the primary monitor's top-left, and the working
/// area excludes the taskbar so a centred window isn't tucked under it.
/// </summary>
internal static class CanvasNavigation
{
    /// <summary>
    /// How much a candidate's sideways offset counts against it relative to its
    /// distance along the direction of travel. Above 1 so a window straight ahead
    /// beats a nearer one off to the side — without it, pressing Right in a grid
    /// can land on a diagonal neighbour.
    /// </summary>
    private const double PerpendicularPenalty = 2.0;

    /// <summary>Ignore candidates this close to the current centre on the travel axis.</summary>
    private const double SameCellEpsilon = 1.0;

    /// <summary>
    /// Where the camera would have to sit to centre <paramref name="world"/>,
    /// without moving it. Separate from <see cref="CenterOnWindow"/> so an
    /// animated jump can compute its destination without firing CameraChanged
    /// twice on the way - every one of those wakes the minimap and re-syncs the
    /// overview.
    /// </summary>
    public static (double x, double y) CameraToCenterOn(Canvas canvas, IScreens screens, WorldRect world)
    {
        var view = screens.PrimaryWorkingArea;
        return (
            world.X + world.W / 2 - view.Width / (2 * canvas.Zoom),
            world.Y + world.H / 2 - view.Height / (2 * canvas.Zoom));
    }

    /// <summary>Centre the canvas camera on <paramref name="world"/>.</summary>
    public static void CenterOnWindow(Canvas canvas, IScreens screens, WorldRect world)
    {
        var (x, y) = CameraToCenterOn(canvas, screens, world);
        canvas.SetCamera(x, y);
    }

    /// <summary>
    /// The largest fraction of any single monitor that <paramref name="r"/>
    /// covers, in the range 0..1. Zero means the window has no pixels anywhere.
    ///
    /// Deliberately a fraction of the *screen*, not of the window. Asking how
    /// much of the window is visible cannot separate the two cases that matter:
    /// a small window sitting off to one side and a large window hanging off an
    /// edge can both be 40% visible, and only one of them fills the view.
    ///
    /// Per monitor rather than against the whole virtual desktop, because a
    /// window filling one screen of three covers a third of the desktop, which is
    /// plainly not "a sliver".
    /// </summary>
    public static double ScreenCoverage(WindowRect r, IScreens screens)
    {
        if (r.W <= 0 || r.H <= 0) return 0;

        double best = 0;
        foreach (var bounds in screens.AllBounds)
        {
            int overlapW = Math.Min(r.X + r.W, bounds.Right) - Math.Max(r.X, bounds.X);
            int overlapH = Math.Min(r.Y + r.H, bounds.Bottom) - Math.Max(r.Y, bounds.Y);
            if (overlapW <= 0 || overlapH <= 0) continue;

            long screenArea = (long)bounds.Width * bounds.Height;
            if (screenArea <= 0) continue;

            best = Math.Max(best, (long)overlapW * overlapH / (double)screenArea);
        }
        return best;
    }


    /// <summary>
    /// The window to move to when navigating <paramref name="direction"/> from
    /// wherever the camera currently is, or null if there is nothing that way.
    ///
    /// Works off real window positions rather than grid indices, so it behaves
    /// sensibly on a hand-arranged canvas and reduces to exact cell stepping once
    /// <see cref="GridArranger"/> has laid things out.
    /// </summary>
    public static IntPtr? FindNeighbour(Canvas canvas, IScreens screens, NavDirection direction)
    {
        var view = screens.PrimaryWorkingArea;
        var (vx, vy, vw, vh) = canvas.GetViewport(view.Width, view.Height);
        double fromX = vx + vw / 2.0;
        double fromY = vy + vh / 2.0;

        IntPtr? best = null;
        double bestScore = double.MaxValue;
        long bestZ = long.MinValue;

        foreach (var (hWnd, rect) in canvas.Windows)
        {
            if (rect.State != WindowState.Normal || rect.PinnedToScreen) continue;

            double dx = rect.X + rect.W / 2.0 - fromX;
            double dy = rect.Y + rect.H / 2.0 - fromY;

            double along, across;
            switch (direction)
            {
                case NavDirection.Right: along =  dx; across = Math.Abs(dy); break;
                case NavDirection.Left:  along = -dx; across = Math.Abs(dy); break;
                case NavDirection.Down:  along =  dy; across = Math.Abs(dx); break;
                default:                 along = -dy; across = Math.Abs(dx); break;
            }
            if (along <= SameCellEpsilon) continue;

            double score = along + across * PerpendicularPenalty;
            if (score > bestScore) continue;
            // Deterministic tiebreak: prefer the more recently fronted window.
            if (score == bestScore && rect.ZOrder <= bestZ) continue;

            bestScore = score;
            bestZ = rect.ZOrder;
            best = hWnd;
        }

        return best;
    }
}
