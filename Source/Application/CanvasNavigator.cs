using System;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// Ctrl+Alt+Arrow to glide the camera to the neighbouring window, Ctrl+Alt+G to
/// lay the canvas out in a grid.
///
/// Owns the frame timer that drives <see cref="CameraAnimator"/>. Panning
/// normally gets its smoothness from the overview overlay's live DWM thumbnails,
/// but a navigation jump has no overlay up, so this has to push real windows
/// itself every frame — via the async projection worker, which coalesces to the
/// latest batch and keeps the UI thread free.
/// </summary>
internal sealed class CanvasNavigator : IDisposable
{
    /// <summary>~60fps. The animation is short, so this timer runs in bursts.</summary>
    private const int FrameIntervalMs = 16;

    /// <summary>How often the edge watch samples the cursor and button state.</summary>
    private const int EdgePollMs = 50;

    /// <summary>
    /// How close to the edge counts. Deliberately tiny: the pointer stops at
    /// Right-1 when you shove it into the edge, so this catches a deliberate
    /// push without firing on a merely nearby cursor.
    /// </summary>
    private const int EdgeThresholdPx = 2;

    /// <summary>Hold time before the first advance, and the gap between repeats.</summary>
    private const long EdgeDwellMs = 400;
    private const long EdgeRepeatMs = 700;
    private const int KeyStateDownBit = 0x8000;


    private readonly Canvas _canvas;
    private readonly WindowManager _wm;
    private readonly IScreens _screens;
    private readonly IInputRouter _input;
    private readonly IAppConfig _config;

    private readonly CameraAnimator _animator;
    private readonly IClock _clock;
    private readonly Timer _frameTimer;
    private readonly Timer _edgeTimer;
    private readonly EdgeTrigger _edgeTrigger = new(EdgeDwellMs, EdgeRepeatMs);


    public CanvasNavigator(
        Canvas canvas,
        WindowManager wm,
        IInputRouter input,
        IScreens screens,
        IAppConfig config,
        IClock? clock = null)
    {

        _canvas = canvas;
        _wm = wm;
        _screens = screens;
        _input = input;
        _config = config;
        _clock = clock ?? SystemClock.Instance;
        _animator = new CameraAnimator(canvas, clock);

        _frameTimer = new Timer { Interval = FrameIntervalMs };
        _frameTimer.Tick += OnFrame;

        _edgeTimer = new Timer { Interval = EdgePollMs };
        _edgeTimer.Tick += OnEdgeTick;
        ApplyEdgeConfig();
        config.Changed += OnConfigChanged;


        input.NavigateHotkey += OnNavigate;
        input.ArrangeGridHotkey += OnArrangeGrid;

        wm.WindowRegistered += OnWindowRegistered;

    }

    /// <summary>Lay out the grid and frame the window that was most recently in front.</summary>
    public void ArrangeGrid()
    {
        var work = _screens.PrimaryWorkingArea;
        // Read per-arrange rather than cached, so editing GridColumns in
        // config.ini applies on the next Ctrl+Alt+G with no restart.
        var cells = GridArranger.Arrange(_canvas, _config.GridColumns, work.Width, work.Height, _wm.IsGridEligible);


        if (cells.Count == 0) return;

        var front = GridArranger.FrontMostCell(_canvas, cells);
        var (camX, camY) = GridArranger.CameraForCell(front, work.Width, work.Height);

        // Snap rather than glide: everything moved at once, so an eased camera
        // on top would just muddy an already-large visual change.
        _animator.Cancel();
        _frameTimer.Stop();
        _wm.SuspendReconcile = true;
        _canvas.SetCamera(camX, camY);


        // ApplyLayout, not Commit: the grid is a layout change, so the cell sizes
        // have to reach the real windows. Commit projects move-only.
        _wm.ApplyLayout();
        _wm.SuspendReconcile = false;
    }


    private void OnArrangeGrid()
    {
        ArrangeGrid();
    }

    private void OnConfigChanged()
    {
        ApplyEdgeConfig();
    }

    private void ApplyEdgeConfig()
    {
        if (_config.EnableDragEdgeNavigation)
        {
            _edgeTimer.Start();
        }
        else
        {
            _edgeTimer.Stop();
            _edgeTrigger.Reset();
        }
    }

    /// <summary>
    /// Poll for "left button held against a screen edge". There is no way to ask
    /// the system whether a drag-and-drop is in progress — DoDragDrop is a modal
    /// loop inside the source process — so this is the best signal available,
    /// and the dwell in <see cref="EdgeTrigger"/> is what keeps it from firing
    /// on an incidental hold.
    /// </summary>
    private void OnEdgeTick(object? sender, EventArgs e)
    {
        // Don't fight an animation already in flight.
        if (_animator.IsAnimating) return;

        NavDirection? edge = EdgeUnderCursor();
        bool held = (PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_LBUTTON) & KeyStateDownBit) != 0;

        if (_edgeTrigger.Update(held, edge, _clock.TickCount64) && edge.HasValue)
            Navigate(edge.Value);
    }

    /// <summary>Which edge of its monitor the cursor is pressed against, if any.</summary>
    private NavDirection? EdgeUnderCursor()
    {
        PInvoke.GetCursorPos(out var pt);

        foreach (var bounds in _screens.AllBounds)
        {
            if (pt.X < bounds.X || pt.X >= bounds.Right) continue;
            if (pt.Y < bounds.Y || pt.Y >= bounds.Bottom) continue;

            if (pt.X >= bounds.Right - EdgeThresholdPx) return NavDirection.Right;
            if (pt.X < bounds.X + EdgeThresholdPx) return NavDirection.Left;
            if (pt.Y >= bounds.Bottom - EdgeThresholdPx) return NavDirection.Down;
            if (pt.Y < bounds.Y + EdgeThresholdPx) return NavDirection.Up;
            return null;
        }
        return null;
    }


    /// <summary>
    /// A window just opened. If everything else is already sitting on the grid,
    /// drop the newcomer into the first free cell instead of leaving it wherever
    /// its app placed it — otherwise a tidy layout degrades the moment you open
    /// anything. Deliberately conditional: on a hand-arranged canvas this does
    /// nothing, so it never fights a layout you positioned yourself.
    /// </summary>
    private void OnWindowRegistered(IntPtr hWnd)
    {
        if (!_config.AutoGridNewWindows) return;

        // A dialog belongs beside the window that opened it, not alone in a
        // screen-sized cell on the other side of the canvas.
        if (!_wm.IsGridEligible(hWnd)) return;

        var work = _screens.PrimaryWorkingArea;
        int columns = _config.GridColumns;
        if (!GridArranger.IsArranged(_canvas, columns, work.Width, work.Height,
                exclude: hWnd, eligible: _wm.IsGridEligible))
            return;

        var cell = GridArranger.FirstFreeCell(_canvas, columns, work.Width, work.Height, hWnd,
            eligible: _wm.IsGridEligible);

        var (cellX, cellY) = GridArranger.CameraForCell(cell, work.Width, work.Height);

        _wm.SuspendReconcile = true;
        _canvas.SetWindow(hWnd, cellX, cellY, work.Width, work.Height);

        // Follow the newcomer. Windows gives a new window focus, and leaving the
        // camera behind would park it in a cell the user cannot see — the
        // opposite of what opening a window is supposed to do.
        _canvas.SetCamera(cellX, cellY);
        _canvas.Commit();
        _wm.ApplyLayout(only: hWnd);
        _wm.SuspendReconcile = false;
    }


    private void OnNavigate(NavDirection direction)
    {
        Navigate(direction);
    }

    /// <summary>Glide the camera to the neighbouring window in <paramref name="direction"/>.</summary>
    public void Navigate(NavDirection direction)
    {

        if (CanvasNavigation.FindNeighbour(_canvas, _screens, direction) is not IntPtr target)
            return;
        if (!_canvas.Windows.TryGetValue(target, out var world))
            return;

        var (destX, destY) = CanvasNavigation.CameraToCenterOn(_canvas, _screens, world);

        // Our own per-frame reprojection raises LOCATIONCHANGE, which feeds back
        // through ReconcileWindow. The projection worker coalesces frames, so
        // _lastScreen can already hold a newer position than the window has
        // actually reached - reconcile reads that lag as a user drag and
        // rewrites the world coordinates, by a different amount per window. That
        // is what pulled grid cells out of alignment while navigating. The
        // overview suspends reconcile while panning for exactly this reason.
        _wm.SuspendReconcile = true;
        _animator.AnimateTo(destX, destY);
        _frameTimer.Start();

    }

    private void OnFrame(object? sender, EventArgs e)
    {
        bool running = _animator.Tick();

        if (running)
        {
            // Async + transient: hand the batch to the projection worker rather
            // than blocking the UI thread mid-animation.
            _wm.Reproject(isAsync: true, isTransient: true);
            return;
        }

        _frameTimer.Stop();
        // One synchronous commit at the end so windows are at their final
        // positions before the next paint, matching how a pan ends.
        _canvas.Commit();

        // Flipping this back runs a catch-up reconcile, so anything the user
        // genuinely moved during the glide is still picked up.
        _wm.SuspendReconcile = false;
    }


    public void Dispose()
    {
        _input.NavigateHotkey -= OnNavigate;
        _input.ArrangeGridHotkey -= OnArrangeGrid;

        _wm.WindowRegistered -= OnWindowRegistered;
        _config.Changed -= OnConfigChanged;
        _frameTimer.Stop();
        _frameTimer.Dispose();
        _edgeTimer.Stop();
        _edgeTimer.Dispose();

    }
}
