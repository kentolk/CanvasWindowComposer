using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class WindowManagerTests
{
    private static (Canvas canvas, FakeWindowApi api, WindowManager wm) Create(FakeAppConfig? config = null)
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var wm = new WindowManager(canvas, api, config ?? new FakeAppConfig(), new FakeInputRouter(), new FakeClock());
        return (canvas, api, wm);
    }

    // ==================== REPROJECT ====================

    [Fact]
    public void Reproject_ProjectsOnScreenWindowsToBatch()
    {
        var (canvas, api, wm) = Create();

        // Place window at world (100, 200) — on-screen with default 1920x1080
        canvas.SetWindow((IntPtr)1, 100, 200, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);

        wm.Reproject();

        Assert.Single(api.LastBatch);
        var item = api.LastBatch[0];
        Assert.Equal((IntPtr)1, item.HWnd);
        Assert.Equal(100, item.Rect.X);
        Assert.Equal(200, item.Rect.Y);
    }

    [Fact]
    public void Reproject_ClipsOffScreenWindows()
    {
        var (canvas, api, wm) = Create();

        // Place window far off-screen
        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);

        wm.Reproject();

        Assert.Contains((IntPtr)1, api.ClippedWindows);
    }

    [Fact]
    public void Reproject_UnclipsWindowThatMovesOnScreen()
    {
        var (canvas, api, wm) = Create();

        // Start off-screen, get clipped
        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        wm.Reproject();
        Assert.Contains((IntPtr)1, api.ClippedWindows);

        // Move on-screen
        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        wm.Reproject();
        Assert.DoesNotContain((IntPtr)1, api.ClippedWindows);
    }

    [Fact]
    public void Reproject_SkipsMaximizedWindows()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        canvas.MaximizeWindow((IntPtr)1);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600,
            style: (int)Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_MAXIMIZE);

        wm.Reproject();

        Assert.Empty(api.LastBatch);
    }

    [Fact]
    public void Reproject_SkipsMinimizedWindows()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        canvas.CollapseWindow((IntPtr)1); // canvas state drives Reproject now
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);

        wm.Reproject();

        Assert.Empty(api.LastBatch);
    }

    [Fact]
    public void Reproject_MultipleWindows_BatchesAll()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 100, 100, 400, 300);
        canvas.SetWindow((IntPtr)2, 600, 200, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300);
        api.AddWindow((IntPtr)2, 0, 0, 400, 300);

        wm.Reproject();

        Assert.Equal(2, api.LastBatch.Count);
    }

    [Fact]
    public void Reproject_SkipsPinnedWindows()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 100, 100, 400, 300);
        canvas.SetPinnedToScreen((IntPtr)1, true);
        api.AddWindow((IntPtr)1, 100, 100, 400, 300);

        wm.Reproject();

        Assert.Empty(api.LastBatch);
    }

    // ==================== RECONCILE ====================

    [Fact]
    public void ReconcileWindow_DetectsManualMove()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 100, 200, 800, 600);
        api.AddWindow((IntPtr)1, 100, 200, 800, 600);

        // Reproject to establish _lastScreen
        wm.Reproject();

        // Simulate user dragging the window to a new position
        api.Windows[(IntPtr)1].X = 300;
        api.Windows[(IntPtr)1].Y = 400;

        wm.ReconcileWindow((IntPtr)1);

        // Canvas should reflect the new position
        var world = canvas.Windows[(IntPtr)1];
        Assert.Equal(300, world.X);
        Assert.Equal(400, world.Y);
    }

    [Fact]
    public void ReconcileWindow_IgnoresSmallMoves()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 100, 200, 800, 600);
        api.AddWindow((IntPtr)1, 100, 200, 800, 600);
        wm.Reproject();

        // Move by 1px — within 2px threshold
        api.Windows[(IntPtr)1].X = 101;

        wm.ReconcileWindow((IntPtr)1);

        // Canvas should NOT have changed
        var world = canvas.Windows[(IntPtr)1];
        Assert.Equal(100, world.X);
    }

    [Fact]
    public void ReconcileWindow_IgnoresClippedWindows()
    {
        var (canvas, api, wm) = Create();

        // Place off-screen to get clipped
        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        wm.Reproject();
        Assert.Contains((IntPtr)1, api.ClippedWindows);

        // Move the clipped window
        api.Windows[(IntPtr)1].X = 9999;

        wm.ReconcileWindow((IntPtr)1);

        // Canvas should NOT reflect the move (clipped windows are ignored)
        var world = canvas.Windows[(IntPtr)1];
        Assert.Equal(5000, world.X);
    }

    // ==================== REMOVE STALE ====================

    [Fact]
    public void RemoveStale_RemovesInvisibleWindows()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        canvas.SetWindow((IntPtr)2, 500, 200, 400, 300);
        api.AddWindow((IntPtr)1, 100, 100, 800, 600);
        api.AddWindow((IntPtr)2, 500, 200, 400, 300);

        // Window 1 disappears
        api.Windows[(IntPtr)1].Visible = false;

        wm.RemoveStale();

        Assert.False(canvas.HasWindow((IntPtr)1));
        Assert.True(canvas.HasWindow((IntPtr)2));
    }

    [Fact]
    public void RemoveStale_KeepsVisibleWindows()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        api.AddWindow((IntPtr)1, 100, 100, 800, 600);

        wm.RemoveStale();

        Assert.True(canvas.HasWindow((IntPtr)1));
    }

    // ==================== DISCOVER NEW WINDOWS ====================

    [Fact]
    public void DiscoverNewWindows_RegistersNewManageableWindows()
    {
        var (canvas, api, wm) = Create();

        // Window exists in the system but not in canvas
        api.AddWindow((IntPtr)1, 200, 300, 800, 600, pid: 999);

        wm.DiscoverNewWindows();

        Assert.True(canvas.HasWindow((IntPtr)1));
    }

    [Fact]
    public void DiscoverNewWindows_SkipsAlreadyTrackedWindows()
    {
        var (canvas, api, wm) = Create();

        // Pre-tracked at world (100, 100); the API has it at a different
        // screen rect. If discover wrongly registered it again, SetWindowFromScreen
        // would overwrite the world coords with the API's rect.
        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        api.AddWindow((IntPtr)1, 999, 888, 800, 600, pid: 999);

        wm.DiscoverNewWindows();

        Assert.Single(canvas.Windows);
        var world = canvas.Windows[(IntPtr)1];
        Assert.Equal(100, world.X);
        Assert.Equal(100, world.Y);
    }

    [Fact]
    public void DiscoverNewWindows_SkipsNonManageableWindows()
    {
        var (canvas, api, wm) = Create();

        api.AddWindow((IntPtr)1, 200, 300, 800, 600, pid: 999, manageable: false);

        wm.DiscoverNewWindows();

        Assert.False(canvas.HasWindow((IntPtr)1));
    }

    // ==================== RESET ====================

    [Fact]
    public void Reset_UnclipsAllAndClearsCanvas()
    {
        var (canvas, api, wm) = Create();

        // Place off-screen to get clipped
        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        wm.Reproject();
        Assert.Contains((IntPtr)1, api.ClippedWindows);

        wm.Reset();

        Assert.DoesNotContain((IntPtr)1, api.ClippedWindows);
        Assert.Empty(canvas.Windows);
    }

    [Fact]
    public void Reset_RestoresWorldPositionsViaBatch()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 300, 400, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);

        wm.Reset();

        // Batch should contain world coordinates
        Assert.Single(api.LastBatch);
        var item = api.LastBatch[0];
        Assert.Equal(300, item.Rect.X);
        Assert.Equal(400, item.Rect.Y);
        Assert.Equal(800, item.Rect.W);
        Assert.Equal(600, item.Rect.H);
    }

    [Fact]
    public void Reset_DoesNotMovePinnedWindowsBackToCanvas()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 3000, 4000, 800, 600);
        canvas.SetPinnedToScreen((IntPtr)1, true);
        api.AddWindow((IntPtr)1, 200, 100, 800, 600);

        wm.Reset();

        Assert.Empty(api.LastBatch);
        Assert.Equal(200, api.Windows[(IntPtr)1].X);
        Assert.Equal(100, api.Windows[(IntPtr)1].Y);
        Assert.Empty(canvas.Windows);
    }

    // ==================== UNCLIP / RECLIP ====================

    [Fact]
    public void UnclipAll_RestoresAllClippedWindows()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 5000, 5000, 400, 300);
        canvas.SetWindow((IntPtr)2, 6000, 6000, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300);
        api.AddWindow((IntPtr)2, 0, 0, 400, 300);
        wm.Reproject();

        Assert.Equal(2, api.ClippedWindows.Count);

        wm.UnclipAll();

        Assert.Empty(api.ClippedWindows);
    }

    [Fact]
    public void ReclipAll_ReclipsAfterUnclip()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 5000, 5000, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300);
        wm.Reproject();
        Assert.Single(api.ClippedWindows);

        wm.UnclipAll();
        Assert.Empty(api.ClippedWindows);

        wm.ReclipAll();
        Assert.Single(api.ClippedWindows);
    }

    // ==================== PINNED TO SCREEN ====================

    [Fact]
    public void SetWindowPinnedToScreen_UnclipsWindowAndSkipsFutureProjection()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 5000, 5000, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300);
        wm.Reproject();
        Assert.Contains((IntPtr)1, api.ClippedWindows);

        wm.SetWindowPinnedToScreen((IntPtr)1, true);
        api.LastBatch.Clear();
        wm.Reproject();

        Assert.True(canvas.IsPinnedToScreen((IntPtr)1));
        Assert.DoesNotContain((IntPtr)1, api.ClippedWindows);
        Assert.Empty(api.LastBatch);
    }

    [Fact]
    public void SetWindowPinnedToScreen_PreservesSizeOfSmallOffScreenWindow()
    {
        var (canvas, api, wm) = Create();

        // Off-screen and smaller than the canvas projection floor, so pinning
        // has to fall back to the projected rect to bring it into view. That
        // rect carries the 200x100 floor, which must not reach the real window.
        canvas.SetWindow((IntPtr)1, 5000, 5000, 150, 80);
        api.AddWindow((IntPtr)1, 5000, 5000, 150, 80);
        wm.Reproject();

        wm.SetWindowPinnedToScreen((IntPtr)1, true);

        Assert.Equal(150, api.Windows[(IntPtr)1].W);
        Assert.Equal(80, api.Windows[(IntPtr)1].H);
    }

    [Fact]
    public void SetWindowPinnedToScreen_UnpinConvertsCurrentScreenPositionToWorld()
    {
        var (canvas, api, wm) = Create();

        canvas.Pan(100, 50);
        canvas.SetWindow((IntPtr)1, 300, 400, 400, 300);
        api.AddWindow((IntPtr)1, 300, 400, 400, 300);
        wm.SetWindowPinnedToScreen((IntPtr)1, true);

        api.Windows[(IntPtr)1].X = 700;
        api.Windows[(IntPtr)1].Y = 250;

        wm.SetWindowPinnedToScreen((IntPtr)1, false);

        var world = canvas.Windows[(IntPtr)1];
        var expected = canvas.ScreenToWorld(700, 250);
        Assert.False(canvas.IsPinnedToScreen((IntPtr)1));
        Assert.Equal(expected.x, world.X);
        Assert.Equal(expected.y, world.Y);
    }

    // ==================== COLLAPSED ====================

    [Fact]
    public void Reproject_SkipsCollapsedWindows()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 100, 100, 400, 300);
        canvas.SetWindow((IntPtr)2, 600, 200, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300);
        api.AddWindow((IntPtr)2, 0, 0, 400, 300);

        canvas.CollapseWindow((IntPtr)1);

        wm.Reproject();

        Assert.Single(api.LastBatch);
        Assert.Equal((IntPtr)2, api.LastBatch[0].HWnd);
    }

    // ==================== APP CONFIG FLAGS ====================

    [Fact]
    public void Reproject_WhenDisableGreedyDraw_DoesNotClipOffScreen()
    {
        var cfg = new FakeAppConfig { DisableGreedyDraw = true };
        var (canvas, api, wm) = Create(cfg);

        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);

        wm.Reproject();

        // Greedy draw is disabled — window should be batched at its world coords,
        // never clipped.
        Assert.DoesNotContain((IntPtr)1, api.ClippedWindows);
        Assert.Single(api.LastBatch);
        Assert.Equal(5000, api.LastBatch[0].Rect.X);
    }

    [Fact]
    public void Reproject_WhenSuspendGreedyDraw_DoesNotClipOffScreen()
    {
        var cfg = new FakeAppConfig(); // DisableGreedyDraw = false
        var (canvas, api, wm) = Create(cfg);

        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);

        wm.SuspendGreedyDraw = true;
        wm.Reproject();

        Assert.DoesNotContain((IntPtr)1, api.ClippedWindows);
    }

    // ==================== REPROJECT WINDOW ====================

    [Fact]
    public void ReprojectWindow_SetsPositionDirectly()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 200, 300, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);

        wm.ReprojectWindow((IntPtr)1);

        Assert.Single(api.SetPositionCalls);
        var call = api.SetPositionCalls[0];
        Assert.Equal((IntPtr)1, call.hWnd);
        Assert.Equal(200, call.x);
        Assert.Equal(300, call.y);
    }

    [Fact]
    public void ReprojectWindow_RegistersUnknownWindow()
    {
        var (canvas, api, wm) = Create();

        // Window not in canvas, but exists in system
        api.AddWindow((IntPtr)1, 400, 500, 800, 600, pid: 999);

        wm.ReprojectWindow((IntPtr)1);

        Assert.True(canvas.HasWindow((IntPtr)1));
    }

    [Fact]
    public void ReprojectWindow_PreservesSizeOfWindowSmallerThanProjectionFloor()
    {
        var (canvas, api, wm) = Create();

        // Canvas.WorldToScreenSize floors projections at 200x100. A window below
        // that floor must still keep its real size — ReprojectWindow moves it,
        // it doesn't get to resize it.
        canvas.SetWindow((IntPtr)1, 300, 400, 150, 80);
        api.AddWindow((IntPtr)1, 300, 400, 150, 80);

        wm.ReprojectWindow((IntPtr)1);

        Assert.Single(api.SetPositionCalls);
        var call = api.SetPositionCalls[0];
        Assert.Equal(300, call.x);
        Assert.Equal(400, call.y);
        Assert.Equal(150, api.Windows[(IntPtr)1].W);
        Assert.Equal(80, api.Windows[(IntPtr)1].H);
    }

    [Fact]
    public void ReprojectWindow_UsesNoSizeFlag()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 200, 300, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);

        wm.ReprojectWindow((IntPtr)1);

        const uint SWP_NOSIZE = 0x0001;
        uint flags = api.SetPositionCalls[0].flags;
        Assert.True((flags & SWP_NOSIZE) != 0, "ReprojectWindow must move without resizing");
    }

    [Fact]
    public void Reproject_QueriesMonitorLayoutOncePerBatch()
    {
        var (canvas, api, wm) = Create();

        for (int i = 1; i <= 10; i++)
        {
            canvas.SetWindow((IntPtr)i, i * 50, 100, 800, 600);
            api.AddWindow((IntPtr)i, i * 50, 100, 800, 600);
        }

        api.GetScreenWorkingAreasCalls = 0;
        wm.Reproject();

        // Monitor topology is fixed for the duration of a batch. Querying it per
        // window put a Screen.AllScreens / EnumDisplayMonitors round-trip plus two
        // array allocations on every window of every pan frame.
        Assert.Equal(1, api.GetScreenWorkingAreasCalls);
    }

    [Fact]
    public void Reproject_ClippingPathAlsoReusesTheSameLayoutQuery()
    {
        var (canvas, api, wm) = Create(new FakeAppConfig { DisableGreedyDraw = false });

        for (int i = 1; i <= 6; i++)
        {
            canvas.SetWindow((IntPtr)i, 9000 + i * 50, 9000, 800, 600); // all off-screen
            api.AddWindow((IntPtr)i, 0, 0, 800, 600);
        }

        api.GetScreenWorkingAreasCalls = 0;
        wm.Reproject();

        Assert.Equal(6, api.ClippedWindows.Count);
        Assert.Equal(1, api.GetScreenWorkingAreasCalls);
    }

    // ==================== LAYOUT (SIZE-APPLYING) ====================

    [Fact]
    public void Reproject_DoesNotClaimToHaveAppliedASizeItSuppressed()
    {
        var (canvas, api, wm) = Create();

        // Canvas says 1920x1040, the real window is 800x600. Reproject moves it
        // but passes SWP_NOSIZE, so the real size does not change.
        canvas.SetWindow((IntPtr)1, 0, 0, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        wm.Reproject();

        canvas.SetWindow((IntPtr)1, 100, 100, 1920, 1040);
        wm.Reproject();

        // A LOCATIONCHANGE now arrives for the move we just made. Reconcile must
        // not read the untouched size as a user resize and stomp the canvas.
        wm.ReconcileWindow((IntPtr)1);

        Assert.Equal(1920, canvas.Windows[(IntPtr)1].W);
        Assert.Equal(1040, canvas.Windows[(IntPtr)1].H);
    }

    [Fact]
    public void ApplyLayout_ResizesRealWindowsToTheirCanvasSize()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 0, 0, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        canvas.SetWindow((IntPtr)1, 0, 0, 1920, 1040);

        wm.ApplyLayout();

        Assert.Equal(1920, api.Windows[(IntPtr)1].W);
        Assert.Equal(1040, api.Windows[(IntPtr)1].H);
    }

    [Fact]
    public void ApplyLayout_EmitsSizeCarryingBatchItems()
    {
        var (canvas, api, wm) = Create();
        canvas.SetWindow((IntPtr)1, 0, 0, 1920, 1040);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);

        wm.ApplyLayout();

        Assert.Single(api.LastBatch);
        Assert.False(api.LastBatch[0].PosOnly);
    }

    [Fact]
    public void ApplyLayout_SurvivesAReconcilePass()
    {
        var (canvas, api, wm) = Create();
        canvas.SetWindow((IntPtr)1, 0, 0, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        canvas.SetWindow((IntPtr)1, 0, 0, 1920, 1040);

        wm.ApplyLayout();
        wm.Reconcile();

        Assert.Equal(1920, canvas.Windows[(IntPtr)1].W);
        Assert.Equal(1040, canvas.Windows[(IntPtr)1].H);
    }

    [Fact]
    public void ApplyLayout_ThenPanning_KeepsTheAppliedSize()
    {
        var (canvas, api, wm) = Create();
        canvas.SetWindow((IntPtr)1, 0, 0, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        canvas.SetWindow((IntPtr)1, 0, 0, 1920, 1040);
        wm.ApplyLayout();

        // This is the reported bug: arrange the grid, then navigate. Each move
        // fires LOCATIONCHANGE -> ReconcileWindow.
        for (int i = 0; i < 5; i++)
        {
            canvas.Pan(-200, 0);
            wm.Reproject();
            wm.ReconcileWindow((IntPtr)1);
        }

        Assert.Equal(1920, canvas.Windows[(IntPtr)1].W);
        Assert.Equal(1040, canvas.Windows[(IntPtr)1].H);
    }

    [Fact]
    public void ApplyLayout_RecordsWhatTheWindowActuallyBecame()
    {
        var (canvas, api, wm) = Create();
        canvas.SetWindow((IntPtr)1, 0, 0, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        canvas.SetWindow((IntPtr)1, 0, 0, 1920, 1040);
        wm.ApplyLayout();

        // An app with a maximum size refuses the rect we asked for. The canvas
        // keeps the ideal cell, but Reconcile must not fight the real window.
        api.Windows[(IntPtr)1].W = 900;
        api.Windows[(IntPtr)1].H = 700;
        wm.ReconcileWindow((IntPtr)1);
        double afterFirst = canvas.Windows[(IntPtr)1].W;

        wm.ReconcileWindow((IntPtr)1);
        Assert.Equal(afterFirst, canvas.Windows[(IntPtr)1].W);
    }

    [Fact]
    public void ApplyLayout_SkipsMinimizedAndPinnedWindows()
    {
        var (canvas, api, wm) = Create();
        canvas.SetWindow((IntPtr)1, 0, 0, 1920, 1040);
        canvas.SetWindow((IntPtr)2, 0, 0, 1920, 1040);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        api.AddWindow((IntPtr)2, 0, 0, 800, 600);
        canvas.CollapseWindow((IntPtr)1);
        canvas.SetPinnedToScreen((IntPtr)2, true);

        wm.ApplyLayout();

        Assert.Equal(800, api.Windows[(IntPtr)1].W);
        Assert.Equal(800, api.Windows[(IntPtr)2].W);
    }

    // ==================== RESET REACHABILITY ====================

    [Fact]
    public void Reset_BringsOffScreenWindowsBackWhereTheUserCanReachThem()
    {
        var (canvas, api, wm) = Create();

        // Grid cell (2,1) on a 1920x1040 layout — nowhere near the screen.
        canvas.SetWindow((IntPtr)1, 3840, 1040, 1920, 1040);
        api.AddWindow((IntPtr)1, 0, 0, 1920, 1040);

        wm.Reset();

        var moved = api.Windows[(IntPtr)1];
        bool onScreen = moved.X + moved.W > 0 && moved.X < 1920
                     && moved.Y + moved.H > 0 && moved.Y < 1080;
        Assert.True(onScreen, $"window left at ({moved.X},{moved.Y}) is unreachable after exit");
    }

    [Fact]
    public void Reset_ScreenSizedWindowsLandAtTheScreenOrigin()
    {
        var (canvas, api, wm) = Create();
        api.ScreenAreas = new() { (0, 0, 1920, 1040) };

        canvas.SetWindow((IntPtr)1, 3840, 2080, 1920, 1040);
        api.AddWindow((IntPtr)1, 0, 0, 1920, 1040);

        wm.Reset();

        Assert.Equal(0, api.Windows[(IntPtr)1].X);
        Assert.Equal(0, api.Windows[(IntPtr)1].Y);
    }

    [Fact]
    public void Reset_LeavesAlreadyReachableWindowsWhereTheyAre()
    {
        var (canvas, api, wm) = Create();

        canvas.SetWindow((IntPtr)1, 300, 200, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);

        wm.Reset();

        // Exit should restore, not rearrange.
        Assert.Equal(300, api.Windows[(IntPtr)1].X);
        Assert.Equal(200, api.Windows[(IntPtr)1].Y);
    }

    [Fact]
    public void SuspendReconcile_ProtectsWorldCoordsFromOurOwnLaggingMoves()
    {
        var (canvas, api, wm) = Create();
        canvas.SetWindow((IntPtr)1, 0, 0, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        wm.Reproject();

        // An animated camera reprojects every frame through the async worker,
        // which coalesces: _lastScreen already holds the newest frame while the
        // real window may still be sitting on an older one. Without suspension
        // that lag reads as a user drag and rewrites the world position, which
        // is what made grid cells drift apart during navigation.
        wm.SuspendReconcile = true;
        canvas.Pan(-400, 0);
        wm.Reproject();
        api.Windows[(IntPtr)1].X = 0; // worker hasn't caught up yet
        wm.ReconcileWindow((IntPtr)1);

        Assert.Equal(0, canvas.Windows[(IntPtr)1].X);
    }

    [Fact]
    public void ReconcileWindow_WithoutSuspension_MisreadsALaggingMoveAsAUserDrag()
    {
        // The other half of the pair above: this is what the navigator was doing
        // before it suspended reconcile, and why grid cells drifted apart. Not a
        // defect in Reconcile — it cannot tell our own lagging move from a real
        // drag, which is exactly why callers that reproject in a loop must
        // suspend it.
        var (canvas, api, wm) = Create();
        canvas.SetWindow((IntPtr)1, 0, 0, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        wm.Reproject();

        canvas.Pan(-400, 0);
        wm.Reproject();
        api.Windows[(IntPtr)1].X = 0;
        wm.ReconcileWindow((IntPtr)1);

        Assert.Equal(400, canvas.Windows[(IntPtr)1].X);

    }

    // ==================== GRID ELIGIBILITY ====================

    private const int WsThickFrame = 0x00040000;

    [Fact]
    public void IsGridEligible_TrueForAnOrdinaryResizableWindow()
    {
        var (_, api, wm) = Create();
        api.AddWindow((IntPtr)1, 0, 0, 800, 600, style: WsThickFrame);

        Assert.True(wm.IsGridEligible((IntPtr)1));
    }

    [Fact]
    public void IsGridEligible_FalseForAnOwnedDialog()
    {
        var (_, api, wm) = Create();
        api.AddWindow((IntPtr)1, 0, 0, 800, 600, style: WsThickFrame);
        api.AddWindow((IntPtr)2, 100, 100, 400, 200, style: WsThickFrame);
        api.Windows[(IntPtr)2].Owner = (IntPtr)1;

        // GetParent reports zero for a WS_OVERLAPPED dialog, which is why the
        // owner has to be checked separately.
        Assert.False(wm.IsGridEligible((IntPtr)2));
    }

    [Fact]
    public void IsGridEligible_FalseForAFixedSizeWindow()
    {
        var (_, api, wm) = Create();
        api.AddWindow((IntPtr)1, 0, 0, 400, 200, style: 0); // no WS_THICKFRAME

        // It cannot fill a cell even if asked, so putting it in one just leaves
        // a small window floating in a screen-sized gap.
        Assert.False(wm.IsGridEligible((IntPtr)1));
    }

    // ==================== REPAINT ON RETURN TO SCREEN ====================

    [Fact]
    public void Reproject_WindowComingBackOnScreen_IsInvalidated()
    {
        var (canvas, api, wm) = Create();
        canvas.SetWindow((IntPtr)1, 9000, 9000, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        wm.Reproject(); // parks it off-screen

        api.InvalidatedWindows.Clear();
        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        wm.Reproject();

        // Chromium and Gecko suspend compositing off-screen; without a nudge the
        // window returns showing a surface sized to the old rect.
        Assert.Contains((IntPtr)1, api.InvalidatedWindows);
    }

    [Fact]
    public void Reproject_WindowStayingOnScreen_IsNotInvalidated()
    {
        var (canvas, api, wm) = Create();
        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        api.AddWindow((IntPtr)1, 100, 100, 800, 600);
        wm.Reproject();

        api.InvalidatedWindows.Clear();
        canvas.SetWindow((IntPtr)1, 200, 150, 800, 600);
        wm.Reproject();

        // An ordinary pan must not spray invalidations at every window.
        Assert.Empty(api.InvalidatedWindows);
    }

    [Fact]
    public void Reproject_WindowStayingOffScreen_IsNotInvalidated()
    {
        var (canvas, api, wm) = Create();
        canvas.SetWindow((IntPtr)1, 9000, 9000, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        wm.Reproject();

        api.InvalidatedWindows.Clear();
        canvas.SetWindow((IntPtr)1, 9500, 9500, 800, 600);
        wm.Reproject();

        Assert.Empty(api.InvalidatedWindows);
    }

    [Fact]
    public void Reproject_InvalidatesOnceNotOnEveryFrame()
    {
        var (canvas, api, wm) = Create();
        canvas.SetWindow((IntPtr)1, 9000, 9000, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        wm.Reproject();

        api.InvalidatedWindows.Clear();
        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        wm.Reproject();
        wm.Reproject();
        wm.Reproject();

        Assert.Single(api.InvalidatedWindows);
    }

    [Fact]
    public void ApplyLayout_InvalidatesEveryWindowItResized()
    {
        var (canvas, api, wm) = Create();
        canvas.SetWindow((IntPtr)1, 0, 0, 1920, 1040);
        canvas.SetWindow((IntPtr)2, 1920, 0, 1920, 1040);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        api.AddWindow((IntPtr)2, 0, 0, 800, 600);

        wm.ApplyLayout();

        // A resize is exactly what strands a suspended renderer with a surface
        // sized to the old rect — the half-drawn-window symptom.
        Assert.Contains((IntPtr)1, api.InvalidatedWindows);
        Assert.Contains((IntPtr)2, api.InvalidatedWindows);
    }

    [Fact]
    public void RemoveWindow_ForgetsOffScreenTracking()
    {
        var (canvas, api, wm) = Create();
        canvas.SetWindow((IntPtr)1, 9000, 9000, 800, 600);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        wm.Reproject();

        wm.RemoveWindow((IntPtr)1);

        // Re-registering the same handle must not inherit a stale "was
        // off-screen" flag and fire a spurious invalidation.
        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        api.InvalidatedWindows.Clear();
        wm.Reproject();

        Assert.Empty(api.InvalidatedWindows);
    }

    [Fact]
    public void Reproject_SkipsWindowsAlreadyAtTheirTargetPosition()
    {
        var (canvas, api, wm) = Create();
        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        canvas.SetWindow((IntPtr)2, 900, 100, 800, 600);
        api.AddWindow((IntPtr)1, 100, 100, 800, 600);
        api.AddWindow((IntPtr)2, 900, 100, 800, 600);
        wm.Reproject();

        api.LastBatch.Clear();
        wm.Reproject();

        // Each batch entry is a cross-process SetWindowPos the UI thread blocks
        // on. A commit that moves nothing should cost nothing.
        Assert.Empty(api.LastBatch);
    }

    [Fact]
    public void Reproject_StillMovesWindowsThatActuallyChanged()
    {
        var (canvas, api, wm) = Create();
        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        canvas.SetWindow((IntPtr)2, 900, 100, 800, 600);
        api.AddWindow((IntPtr)1, 100, 100, 800, 600);
        api.AddWindow((IntPtr)2, 900, 100, 800, 600);
        wm.Reproject();

        api.LastBatch.Clear();
        canvas.SetWindow((IntPtr)2, 400, 300, 800, 600);
        wm.Reproject();

        Assert.Single(api.LastBatch);
        Assert.Equal((IntPtr)2, api.LastBatch[0].HWnd);
    }

    [Fact]
    public void Reproject_AfterAPanMovesEverything()
    {
        var (canvas, api, wm) = Create();
        for (int i = 1; i <= 4; i++)
        {
            canvas.SetWindow((IntPtr)i, i * 100, 100, 400, 300);
            api.AddWindow((IntPtr)i, i * 100, 100, 400, 300);
        }
        wm.Reproject();

        api.LastBatch.Clear();
        canvas.Pan(-50, 0);
        wm.Reproject();

        Assert.Equal(4, api.LastBatch.Count);
    }

    // ==================== ASYNC PROJECTION ====================

    [Fact]
    public void Commit_AfterCancellingAnInFlightAsyncBatch_StillMovesTheRealWindows()
    {
        // A camera jump followed by a commit — how every "take me to this window"
        // path ends: the foreground recentre, search, a new window claiming a
        // grid cell.
        //
        // SetCamera schedules an async batch and records those positions in
        // _lastScreen straight away. Commit then cancels that batch. If the
        // commit's own batch still trusts _lastScreen it finds every window
        // "already there" and issues nothing, so the canvas model sits at the new
        // camera while the real windows never left the old one — and the next pan
        // is what finally drags them across.
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var clock = new FakeClock();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter(),
            clock, vds: null, useAsyncProjection: true);

        canvas.SetWindow((IntPtr)1, 5000, 5000, 400, 300);
        api.AddWindow((IntPtr)1, 0, 0, 400, 300);

        clock.Now = 10_000;
        canvas.SetCamera(4900, 4900); // window projects to (100, 100)
        canvas.Commit();
        Assert.Equal((100, 100, 400, 300), api.GetWindowRect((IntPtr)1));

        // Hold the next worker batch in flight so the commit has something real
        // to cancel, the way a slow-acking window does.
        api.BlockCancellableBatches = true;
        clock.Now = 11_000; // past the reproject throttle, so the camera change schedules
        canvas.SetCamera(4700, 4700); // window projects to (300, 300)
        Assert.True(api.BatchEntered.Wait(TimeSpan.FromSeconds(5)), "worker never picked up the batch");

        canvas.Commit();

        Assert.Equal((300, 300, 400, 300), api.GetWindowRect((IntPtr)1));
    }
}
