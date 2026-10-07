using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// Consumes Canvas state and applies it to real windows.
/// Handles enumeration, positioning, reconciliation.
/// </summary>
internal sealed class WindowManager : IDisposable
{
    private const int ReconcileTolerancePx = 2;
    private const int ClipEdgeOffsetPx = 1;
    private const int FallbackScreenWidth = 1920;
    private const int FallbackScreenHeight = 1080;
    private const int ReprojectThrottleMs = 200;

    private readonly Canvas _canvas;
    private readonly IWindowApi _win32;
    private readonly IAppConfig _config;
    private readonly IClock _clock;
    private readonly IVirtualDesktops? _vds;
    private readonly ProjectionWorker? _projection;

    // Track last projected screen positions to detect manual moves
    private readonly Dictionary<IntPtr, (int x, int y, int w, int h)> _lastScreen = new();

    // Windows with clipped (empty) region to prevent them from fighting off-screen
    private readonly HashSet<IntPtr> _clippedWindows = new();

    // Windows currently projected outside every monitor.
    //
    // Tracked so we can nudge one to repaint when it comes back. Chromium and
    // Gecko both suspend compositing for windows they consider off-screen, and
    // a window resized while suspended returns with a surface still sized to the
    // old rect - it renders as a blank band or a half-drawn window until
    // something invalidates it. Nothing else in the pipeline does.
    private readonly HashSet<IntPtr> _offScreen = new();

    private long _lastReprojectTick;

    // Temporarily suspends greedy draw (SetWindowRgn clipping)
    public bool SuspendGreedyDraw { get; set; }

    /// <summary>
    /// While true, <see cref="OnCameraChanged"/> short-circuits — no transient
    /// reproject is scheduled on the worker. Set during overview Zooming, when
    /// click-through is off and real-window positions don't need to track the
    /// camera for hit-testing. Eliminates the worker-job wait at overview close
    /// (we'd otherwise queue a batch from the close-time SetCamera and then
    /// immediately wait for it inside ReprojectSync). <see cref="OnCommitted"/>
    /// bypasses this gate so the explicit close commit still runs.
    /// </summary>
    public bool SuspendProjection { get; set; }

    private bool _suspendReconcile;
    private bool _reconcilePending;

    /// <summary>
    /// While true, <see cref="Reconcile"/> and <see cref="ReconcileWindow"/>
    /// short-circuit and only flag that a reconcile was requested. Set by the
    /// overview lifecycle so our own reprojections don't feed back through
    /// WindowMoved -> Reconcile and overwrite canvas world coords with
    /// rounding-drifted screen coords. When flipped back to false, if any
    /// reconcile call was suppressed during the on period, a full
    /// <see cref="Reconcile"/> runs to catch up on any drift.
    /// </summary>
    public bool SuspendReconcile
    {
        get { return _suspendReconcile; }
        set
        {
            if (_suspendReconcile == value) return;
            _suspendReconcile = value;
            if (!value && _reconcilePending)
            {
                _reconcilePending = false;
                Reconcile();
            }
        }
    }

    public WindowManager(
        Canvas canvas,
        IWindowApi win32,
        IAppConfig config,
        IInputRouter input,
        IClock? clock = null,
        IVirtualDesktops? vds = null,
        bool useAsyncProjection = false)
    {
        _canvas = canvas;
        _win32 = win32;
        _config = config;
        _clock = clock ?? SystemClock.Instance;
        _vds = vds;
        _projection = useAsyncProjection ? new ProjectionWorker(win32) : null;

        canvas.Committed       += OnCommitted;
        canvas.CameraChanged   += OnCameraChanged;
        canvas.CollapseChanged += OnReprojectWindowEvent;
        canvas.MaximizeChanged += OnReprojectWindowEvent;

        input.WindowMinimized += OnWindowMinimizedEvent;
        input.WindowRestored  += OnWindowRestoredEvent;
        input.WindowDestroyed += OnWindowDestroyedEvent;
        input.WindowShown     += OnWindowShownEvent;
        input.WindowMoved     += OnWindowMovedEvent;
        input.WindowFocused   += OnWindowFocusedEvent;
        input.AltTabStarted   += OnAltTabStarted;
        input.AltTabEnded     += OnAltTabEnded;
    }

    /// <summary>Background tick for window discovery + stale removal.</summary>
    public void Tick()
    {
        DiscoverNewWindows();
        RemoveStale();
    }

    public void Dispose()
    {
        _projection?.Dispose();
    }

    private void OnCommitted()
    {
        // Sync — pan-end / overview-close commits need windows at their final
        // positions before the next paint, otherwise users see a one-frame
        // jitter as the worker batch lands after the form repaints. Cost is a
        // UI-thread block on cross-process SetWindowPos SendMessage round-trips,
        // tracked in Tracing/pan-hitch-findings.md as a known cost we accept
        // here over the visual artifact.
        ReprojectSync();
    }

    private void OnCameraChanged()
    {
        if (SuspendProjection) return;

        // Overview renders its own camera + thumbnails, so real windows don't
        // need to track every frame — but clicks pass through the overlay
        // (WS_EX_TRANSPARENT) and hit whichever real window is under the
        // cursor, so we keep HWND positions roughly in sync for WindowFromPoint.
        // Throttled; final reproject on overview close comes via OnCommitted.
        long now = _clock.TickCount64;
        if (now - _lastReprojectTick > ReprojectThrottleMs)
        {
            Reproject(isAsync: true, isTransient: true);
            _lastReprojectTick = now;
        }
    }

    private void OnReprojectWindowEvent(IntPtr hWnd)
    {
        ReprojectWindow(hWnd);
    }

    private void OnWindowMinimizedEvent(IntPtr hWnd)
    {
        if (_canvas.HasWindow(hWnd))
            _canvas.CollapseWindow(hWnd);
    }

    private void OnWindowRestoredEvent(IntPtr hWnd)
    {
        if (_canvas.HasWindow(hWnd))
            _canvas.ExpandWindow(hWnd);
        ReprojectWindow(hWnd);
    }

    private void OnWindowDestroyedEvent(IntPtr hWnd)
    {
        RemoveWindow(hWnd);
    }

    private void OnWindowShownEvent(IntPtr hWnd)
    {
        TryRegisterWindow(hWnd);
    }

    private void OnWindowMovedEvent(IntPtr hWnd)
    {
        if (_canvas.HasWindow(hWnd))
            ReconcileWindow(hWnd);
    }

    private void OnWindowFocusedEvent(IntPtr hWnd)
    {
        _canvas.BringToForeground(hWnd);
    }

    private void OnAltTabStarted()
    {
        SuspendGreedyDraw = true;
        UnclipAll();
    }

    private void OnAltTabEnded()
    {
        SuspendGreedyDraw = false;
        ReclipAll();
    }

    /// <summary>
    /// Project all canvas windows to screen. Call after Pan.
    /// </summary>
    public void Reproject(bool isAsync = false, bool isTransient = false)
    {
        var batch = BuildReprojectBatch(out var returning);

        if (_projection != null)
            _projection.Schedule(batch, isAsync: isAsync, isTransient: isTransient);
        else
            _win32.BatchMove(batch, isAsync: isAsync, isTransient: isTransient);

        // After the move, so the repaint lands at the new position rather than
        // the off-screen one the window is leaving.
        InvalidateAll(returning);
    }

    /// <summary>
    /// Like <see cref="Reproject"/>, but bypasses the <see cref="ProjectionWorker"/>
    /// and applies the batch synchronously on the calling thread. Use this when
    /// the caller depends on the windows being at their final positions before
    /// the next visible frame (e.g. just before the overview overlay hides).
    /// Cancels any in-flight worker batch so the sync run doesn't have to wait
    /// for it.
    /// </summary>
    public void ReprojectSync(bool isAsync = false, bool isTransient = false)
    {
        // Whatever we just cancelled had already been written into _lastScreen,
        // so on an interrupted batch that bookkeeping describes moves that never
        // happened and the "already there" skip below would drop every one of
        // them — leaving the canvas model at the new camera and the real windows
        // at the old one.
        bool interrupted = _projection?.ClearPending() ?? false;
        var batch = BuildReprojectBatch(out var returning, force: interrupted);
        _win32.BatchMove(batch, isAsync: isAsync, isTransient: isTransient);
        InvalidateAll(returning);

    }

    private List<BatchMoveItem> BuildReprojectBatch()
    {
        return BuildReprojectBatch(out _);
    }

    /// <param name="force">
    /// Include windows whose <see cref="_lastScreen"/> entry already matches the
    /// projection. Only for callers that know that record is unreliable.
    /// </param>
    private List<BatchMoveItem> BuildReprojectBatch(out List<IntPtr> returning, bool force = false)
    {
        var batch = new List<BatchMoveItem>();
        returning = new List<IntPtr>();

        // Monitor layout can't change mid-batch, and every lookup goes through
        // Screen.AllScreens -> EnumDisplayMonitors plus two array allocations.
        // Querying it once per window made pan cost scale with window count for
        // data that only changes on DisplaySettingsChanged.
        IReadOnlyList<(int x, int y, int w, int h)> screens = _win32.GetScreenWorkingAreas();

        foreach (var (hWnd, world) in _canvas.Windows)
        {
            if (world.State != WindowState.Normal || world.PinnedToScreen)
                continue;

            var r = _canvas.WorldToScreen(world);
            bool onScreen = IsOnAnyScreen(screens, r.X, r.Y, r.W, r.H);

            if (!onScreen)
            {
                _offScreen.Add(hWnd);
            }
            else if (_offScreen.Remove(hWnd))
            {
                returning.Add(hWnd);
            }

            bool wasClipped = _clippedWindows.Contains(hWnd);
            if (!_config.DisableGreedyDraw && !SuspendGreedyDraw && !onScreen)
            {
                if (!wasClipped)
                {
                    _win32.ClipWindow(hWnd);
                    _clippedWindows.Add(hWnd);
                    var (px, py) = ClampToScreenEdge(screens, r.X, r.Y, r.W, r.H);
                    var clipped = new WindowRect(px, py, r.W, r.H);
                    batch.Add(new BatchMoveItem(hWnd, clipped, PosOnly: true));
                    _lastScreen[hWnd] = WithKnownSize(hWnd, px, py, r);

                }
                continue;
            }

            if (wasClipped)
            {
                _win32.UnclipWindow(hWnd);
                _clippedWindows.Remove(hWnd);
            }

            // Skip windows already where we want them. Every entry here becomes
            // a cross-process SetWindowPos that sends WM_WINDOWPOSCHANGING /
            // CHANGED synchronously to the owning thread, so the UI thread
            // blocks on each one — a commit that moves nothing still cost a
            // round trip per window, and a busy app stalled the whole canvas.
            // The navigation glide ends with exactly such a commit, since the
            // projection worker already applied those positions.
            if (!force && _lastScreen.TryGetValue(hWnd, out var prev) && prev.x == r.X && prev.y == r.Y)
                continue;

            batch.Add(new BatchMoveItem(hWnd, r, PosOnly: true));
            _lastScreen[hWnd] = WithKnownSize(hWnd, r.X, r.Y, r);


        }

        return batch;
    }

    /// <summary>
    /// Position for <see cref="_lastScreen"/> after a move-only projection.
    ///
    /// The batch passes SWP_NOSIZE, so the window's size did not change and the
    /// entry has to keep whatever size we last knew it to be. Recording the
    /// projected size instead makes <see cref="ReconcileWindow"/> compare the
    /// real window against a size that was never applied, read the difference as
    /// a user resize, and write the old size back over the canvas — which is
    /// what made a grid layout collapse back to the original sizes on the first
    /// pan after arranging it.
    /// </summary>
    private (int x, int y, int w, int h) WithKnownSize(IntPtr hWnd, int x, int y, WindowRect projected)
    {
        return _lastScreen.TryGetValue(hWnd, out var known)
            ? (x, y, known.w, known.h)
            : (x, y, projected.W, projected.H);
    }

    /// <summary>
    /// Push both position AND size of every canvas window to the real windows.
    ///
    /// Ordinary reprojection is deliberately move-only: panning must never
    /// resize anything. A layout change is the one case where the canvas's world
    /// size is authoritative and has to be applied — otherwise the grid exists
    /// only in the model and the minimap, and the real windows keep whatever
    /// size they happened to have.
    /// </summary>
    public void ApplyLayout(IntPtr? only = null)
    {
        // A queued move-only batch would land after ours and undo half of it.
        _projection?.ClearPending();

        var batch = new List<BatchMoveItem>();
        foreach (var (hWnd, world) in _canvas.Windows)
        {
            if (only.HasValue && hWnd != only.Value)
                continue;
            if (world.State != WindowState.Normal || world.PinnedToScreen)
                continue;
            batch.Add(new BatchMoveItem(hWnd, _canvas.WorldToScreen(world), PosOnly: false));
        }

        _win32.BatchMove(batch, isAsync: false, isTransient: false);

        // Read back what actually happened. An app with a minimum size, a fixed
        // aspect ratio, or a size grip it refuses to give up will not take the
        // rect we asked for, and _lastScreen has to hold the truth or the next
        // Reconcile reads the shortfall as a user resize.
        foreach (var item in batch)
        {
            var (ax, ay, aw, ah) = _win32.GetWindowRect(item.HWnd);
            _lastScreen[item.HWnd] = (ax, ay, aw, ah);

            // A resize is precisely the case that strands a suspended renderer
            // with a surface sized to the old rect.
            _win32.InvalidateWindow(item.HWnd);
        }

    }

    /// <summary>
    /// Project a single window (e.g., after restore from minimized).

    /// Returns true if the window was reprojected, false if skipped.
    /// </summary>
    public bool ReprojectWindow(IntPtr hWnd)
    {
        uint ownPid = (uint)Environment.ProcessId;
        if (!_win32.IsManageable(hWnd, ownPid))
            return false;

        if (!_canvas.HasWindow(hWnd))
            RegisterWindow(hWnd);

        if (!_canvas.Windows.TryGetValue(hWnd, out var world))
            return false;

        if (world.PinnedToScreen)
            return true;

        var r = _canvas.WorldToScreen(world);

        // Move only, never resize. WorldToScreen applies Canvas's minimum-size
        // floor (MinWindowWidth/MinWindowHeight), which is meaningful inside the
        // canvas model but must not be pushed onto a real window — a genuinely
        // small window would be inflated to 200x100 every time it was restored
        // from minimize or changed maximize state. The batch path already avoids
        // this via PosOnly -> SWP_NOSIZE; match it, and keep _lastScreen tracking
        // the real size so Reconcile doesn't read the floor as a user resize.
        var (_, _, actualW, actualH) = _win32.GetWindowRect(hWnd);

        _win32.SetWindowPosition(hWnd, r.X, r.Y, actualW, actualH,
            (uint)(SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                   SET_WINDOW_POS_FLAGS.SWP_NOSIZE));

        _lastScreen[hWnd] = (r.X, r.Y, actualW, actualH);
        return true;
    }

    /// <summary>
    /// Detect windows the user manually moved/resized and update the canvas.
    /// </summary>
    public void Reconcile()
    {
        if (_suspendReconcile)
        {
            _reconcilePending = true;
            return;
        }
        foreach (var (hWnd, _) in _canvas.Windows)
            ReconcileWindow(hWnd);
    }

    /// <summary>Update a single window's world position from its actual screen position.</summary>
    public void ReconcileWindow(IntPtr hWnd)
    {
        if (_suspendReconcile)
        {
            _reconcilePending = true;
            return;
        }
        if (!IsWindowActive(hWnd))
            return;

        int style = _win32.GetWindowStyle(hWnd);
        bool isMaximized = (style & (int)WINDOW_STYLE.WS_MAXIMIZE) != 0;

        // Keep canvas's maximize state in sync with Win32
        if (_canvas.HasWindow(hWnd))
        {
            if (isMaximized && !_canvas.IsMaximized(hWnd))
                _canvas.MaximizeWindow(hWnd);
            else if (!isMaximized && _canvas.IsMaximized(hWnd))
                _canvas.UnmaximizeWindow(hWnd);
        }

        // Skip reprojecting maximized windows — their full-screen rect isn't a meaningful canvas position
        if (isMaximized)
            return;

        if (!_lastScreen.TryGetValue(hWnd, out var last))
            return;

        var (ax, ay, aw, ah) = _win32.GetWindowRect(hWnd);

        if (Math.Abs(ax - last.x) <= ReconcileTolerancePx && Math.Abs(ay - last.y) <= ReconcileTolerancePx &&
            Math.Abs(aw - last.w) <= ReconcileTolerancePx && Math.Abs(ah - last.h) <= ReconcileTolerancePx)
            return;

        // Don't reconcile clipped windows — they're hidden and we don't
        // care where the app thinks they are
        if (_clippedWindows.Contains(hWnd))
            return;

        _canvas.SetWindowFromScreen(hWnd, ax, ay, aw, ah);
        _lastScreen[hWnd] = (ax, ay, aw, ah);
    }

    /// <summary>Remove windows from canvas that no longer exist.</summary>
    public void RemoveStale()
    {
        var stale = new List<IntPtr>();
        foreach (var hWnd in _canvas.Windows.Keys)
        {
            if (!_win32.IsWindowVisible(hWnd))
                stale.Add(hWnd);
        }
        foreach (var hWnd in stale)
            RemoveWindow(hWnd);
    }

    private void InvalidateAll(List<IntPtr> windows)
    {
        foreach (var hWnd in windows)
            _win32.InvalidateWindow(hWnd);
    }

    /// <summary>Drop a single window from canvas and internal tracking.</summary>

    public void RemoveWindow(IntPtr hWnd)
    {
        _canvas.RemoveWindow(hWnd);
        _lastScreen.Remove(hWnd);
        _clippedWindows.Remove(hWnd);
        _offScreen.Remove(hWnd);

    }

    /// <summary>Restore regions on all clipped windows (for overview thumbnails).</summary>
    public void UnclipAll()
    {
        foreach (var hWnd in _clippedWindows)
            _win32.UnclipWindow(hWnd);
    }

    /// <summary>Re-clip windows that should be off-screen.</summary>
    public void ReclipAll()
    {
        foreach (var hWnd in _clippedWindows)
            _win32.ClipWindow(hWnd);
    }

    /// <summary>
    /// Recovery action for the tray "Refresh" menu: enumerate every visible
    /// top-level window (not just canvas-tracked ones — third-party tools like
    /// Aero Snap or screen recorders can leave stray clip regions on windows
    /// we never registered) and clear any window region + force a full repaint.
    /// </summary>
    public void RefreshAllWindows()
    {
        _clippedWindows.Clear();
        _offScreen.Clear();

        _win32.EnumWindows(hWnd =>
        {
            if (_win32.IsWindowVisible(hWnd))
            {
                _win32.UnclipWindow(hWnd);
                _win32.InvalidateWindow(hWnd);

            }
            return true;
        });
    }

    /// <summary>
    /// Whether a window should take part in grid layout.
    ///
    /// Being on the canvas and belonging in a grid cell are different questions.
    /// A dialog — "rename branch", a settings sheet — should still pan with
    /// everything else, but giving it a full screen-sized cell of its own strands
    /// it away from the window it belongs to. Two signals rule those out:
    /// an owner window, and the absence of WS_THICKFRAME (a fixed-size window
    /// cannot fill a cell even if we ask it to).
    /// </summary>
    public bool IsGridEligible(IntPtr hWnd)
    {
        if (_win32.GetWindowOwner(hWnd) != IntPtr.Zero)
            return false;

        int style = _win32.GetWindowStyle(hWnd);
        return (style & (int)WINDOW_STYLE.WS_THICKFRAME) != 0;
    }

    /// <summary>
    /// The top-level window under a screen point, or zero if there is nothing
    /// there. Respects the SetWindowRgn clipping applied to off-screen windows,
    /// so a point over a clipped-away region correctly hits whatever is beneath.
    /// </summary>
    public IntPtr WindowAt(int screenX, int screenY)
    {
        return _win32.WindowFromPoint(screenX, screenY);
    }

    /// <summary>Raised after a newly discovered window is fully registered.</summary>


    public event Action<IntPtr>? WindowRegistered;

    /// <summary>Register a new window into the canvas from its screen position.</summary>

    public void RegisterWindow(IntPtr hWnd)
    {
        uint ownPid = (uint)Environment.ProcessId;
        if (!_win32.IsManageable(hWnd, ownPid))
            return;

        var (sx, sy, sw, sh) = _win32.GetWindowRect(hWnd);

        _canvas.SetWindowFromScreen(hWnd, sx, sy, sw, sh);
        _lastScreen[hWnd] = (sx, sy, sw, sh);

        // Fired only once the canvas entry AND _lastScreen are both seeded.
        // Anything that repositions a new window has to run after this point,
        // or the seeding below would overwrite _lastScreen with the window's
        // pre-move rect and the next Reconcile would drag it back.
        WindowRegistered?.Invoke(hWnd);
    }


    public bool SetWindowPinnedToScreen(IntPtr hWnd, bool pinned)
    {
        uint ownPid = (uint)Environment.ProcessId;
        if (!_win32.IsManageable(hWnd, ownPid, allowMinimized: true))
            return _canvas.IsPinnedToScreen(hWnd);

        if (!_canvas.HasWindow(hWnd))
            RegisterWindow(hWnd);

        if (!_canvas.Windows.TryGetValue(hWnd, out var world))
            return false;

        if (pinned)
            PinWindowToScreen(hWnd, world);
        else
            UnpinWindowFromScreen(hWnd);

        return _canvas.IsPinnedToScreen(hWnd);
    }

    public bool ToggleWindowPinnedToScreen(IntPtr hWnd)
    {
        return SetWindowPinnedToScreen(hWnd, !_canvas.IsPinnedToScreen(hWnd));
    }

    /// <summary>
    /// Register a single HWND if it passes the full "new window" filter chain
    /// (not already tracked, manageable, on current virtual desktop).
    /// Event-driven counterpart to DiscoverNewWindows.
    /// </summary>
    public void TryRegisterWindow(IntPtr hWnd)
    {
        if (_canvas.HasWindow(hWnd)) return;
        if (_vds != null && !_vds.IsOnCurrentDesktop(hWnd)) return;
        RegisterWindow(hWnd);
    }

    /// <summary>Reset: restore all windows to world positions, clear canvas.</summary>
    public void Reset()
    {
        // Drop any in-flight worker batch so it can't stomp on the sync reset below.
        _projection?.ClearPending();

        foreach (var hWnd in _clippedWindows)
            _win32.UnclipWindow(hWnd);
        _clippedWindows.Clear();

        _canvas.ResetCamera();

        var batch = new List<BatchMoveItem>();
        IReadOnlyList<(int x, int y, int w, int h)> screens = _win32.GetScreenWorkingAreas();

        foreach (var (hWnd, world) in _canvas.Windows)
        {
            if (!IsWindowActive(hWnd))
                continue;
            if (world.PinnedToScreen)
                continue;

            var rect = new WindowRect((int)world.X, (int)world.Y, (int)world.W, (int)world.H);

            // World coordinates are unbounded — a window parked in a far grid
            // cell sits thousands of pixels off-screen. Restoring it there on
            // exit leaves it invisible and unclickable, with only Alt-Tab to get
            // it back. Reset means "hand the windows back to the user", so
            // anything off-screen gets clamped into the nearest monitor.
            if (!IsOnAnyScreen(screens, rect.X, rect.Y, rect.W, rect.H))
                rect = MoveIntoNearestScreen(screens, rect);

            batch.Add(new BatchMoveItem(hWnd, rect, PosOnly: false));
        }


        _win32.BatchMove(batch, isAsync: false, isTransient: false);
        _canvas.ClearWindows();
        _lastScreen.Clear();
    }

    // ==================== PRIVATE ====================

    public void DiscoverNewWindows()
    {
        uint ownPid = (uint)Environment.ProcessId;
        var toAdd = new List<IntPtr>();

        _win32.EnumWindows(hWnd =>
        {
            if (_canvas.HasWindow(hWnd)) return true;
            if (!_win32.IsManageable(hWnd, ownPid)) return true;
            if (_vds != null && !_vds.IsOnCurrentDesktop(hWnd)) return true;
            toAdd.Add(hWnd);
            return true;
        });

        foreach (var hWnd in toAdd)
            RegisterWindow(hWnd);
    }

    private bool IsWindowActive(IntPtr hWnd)
    {
        if (!_win32.IsWindowVisible(hWnd))
            return false;
        int style = _win32.GetWindowStyle(hWnd);
        return (style & (int)WINDOW_STYLE.WS_MINIMIZE) == 0;
    }

    private void PinWindowToScreen(IntPtr hWnd, WorldRect world)
    {
        var rect = ResolvePinnedScreenRect(hWnd, world);

        if (_clippedWindows.Remove(hWnd))
            _win32.UnclipWindow(hWnd);

        _canvas.SetPinnedToScreen(hWnd, true);
        _lastScreen[hWnd] = (rect.X, rect.Y, rect.W, rect.H);
        _win32.SetWindowPosition(hWnd, rect.X, rect.Y, rect.W, rect.H,
            (uint)(SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE));
    }

    private void UnpinWindowFromScreen(IntPtr hWnd)
    {
        var (x, y, w, h) = _win32.GetWindowRect(hWnd);
        _canvas.SetWindowFromScreen(hWnd, x, y, w, h);
        _canvas.SetPinnedToScreen(hWnd, false);
        _lastScreen[hWnd] = (x, y, w, h);
    }

    private WindowRect ResolvePinnedScreenRect(IntPtr hWnd, WorldRect world)
    {
        IReadOnlyList<(int x, int y, int w, int h)> screens = _win32.GetScreenWorkingAreas();
        var (ax, ay, aw, ah) = _win32.GetWindowRect(hWnd);
        var actual = new WindowRect(ax, ay, aw, ah);
        if (!_clippedWindows.Contains(hWnd) && IsOnAnyScreen(screens, actual.X, actual.Y, actual.W, actual.H))

            return actual;

        // Take the position from the projection but keep the window's real size.
        // WorldToScreen applies the canvas minimum-size floor, and pinning is
        // allowed to move a window into view — not to grow it.
        var p = _canvas.WorldToScreen(world);
        var projected = new WindowRect(p.X, p.Y, aw, ah);
        if (IsOnAnyScreen(screens, projected.X, projected.Y, projected.W, projected.H))
            return projected;

        return MoveIntoNearestScreen(screens, projected);

    }

    private static WindowRect MoveIntoNearestScreen(IReadOnlyList<(int x, int y, int w, int h)> screens, WindowRect rect)
    {
        (int x, int y, int w, int h) screen = screens.Count > 0
            ? screens[0]
            : (0, 0, FallbackScreenWidth, FallbackScreenHeight);
        int rectCx = rect.X + rect.W / 2;
        int rectCy = rect.Y + rect.H / 2;
        long bestDistance = long.MaxValue;

        foreach (var candidate in screens)
        {
            int screenCx = candidate.x + candidate.w / 2;
            int screenCy = candidate.y + candidate.h / 2;
            long dx = rectCx - screenCx;
            long dy = rectCy - screenCy;
            long distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                screen = candidate;
            }
        }

        int w = rect.W > 0 ? rect.W : Math.Min(FallbackScreenWidth, Math.Max(1, screen.w));
        int h = rect.H > 0 ? rect.H : Math.Min(FallbackScreenHeight, Math.Max(1, screen.h));
        int maxX = screen.x + Math.Max(0, screen.w - w);
        int maxY = screen.y + Math.Max(0, screen.h - h);
        int x = Math.Clamp(rect.X, screen.x, maxX);
        int y = Math.Clamp(rect.Y, screen.y, maxY);

        return new WindowRect(x, y, w, h);
    }

    /// <summary>
    /// Clamp window position so it sits just outside the nearest screen edge.
    /// This hides DWM border/shadow effects that would bleed onto the visible area.
    /// </summary>
    private static (int x, int y) ClampToScreenEdge(IReadOnlyList<(int x, int y, int w, int h)> screens, int sx, int sy, int sw, int sh)
    {
        // Find the nearest screen
        int bestDist = int.MaxValue;
        var nearest = screens.Count > 0 ? screens[0] : (0, 0, FallbackScreenWidth, FallbackScreenHeight);

        foreach (var (left, top, width, height) in screens)
        {
            int cx = sx + sw / 2;
            int cy = sy + sh / 2;
            int scx = left + width / 2;
            int scy = top + height / 2;
            int dist = Math.Abs(cx - scx) + Math.Abs(cy - scy);
            if (dist < bestDist)
            {
                bestDist = dist;
                nearest = (left, top, width, height);
            }
        }

        int nLeft = nearest.Item1;
        int nTop = nearest.Item2;
        int nRight = nLeft + nearest.Item3;
        int nBottom = nTop + nearest.Item4;

        // Park 1px inside the nearest edge so the OS considers it "on-screen"
        int px = sx, py = sy;

        if (sx + sw <= nLeft)
        {
            px = nLeft - sw + ClipEdgeOffsetPx;
        }
        else if (sx >= nRight)
        {
            px = nRight - ClipEdgeOffsetPx;
        }

        if (sy + sh <= nTop)
        {
            py = nTop - sh + ClipEdgeOffsetPx;
        }
        else if (sy >= nBottom)
        {
            py = nBottom - ClipEdgeOffsetPx;
        }

        return (px, py);
    }

    /// <summary>Check if a rect overlaps with any monitor's working area (excludes taskbars).</summary>
    private static bool IsOnAnyScreen(IReadOnlyList<(int x, int y, int w, int h)> screens, int rx, int ry, int rw, int rh)
    {
        foreach (var (left, top, width, height) in screens)

        {
            int right = left + width;
            int bottom = top + height;
            if (rx + rw > left && rx < right &&
                ry + rh > top  && ry < bottom)
                return true;
        }
        return false;
    }
}
