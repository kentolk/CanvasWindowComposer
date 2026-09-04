using System;
using System.Collections.Generic;

namespace CanvasDesktop.Tests;

internal sealed class FakeWindowApi : IWindowApi
{
    public class WindowInfo
    {
        public int X, Y, W, H;
        public int Style;
        public bool Visible = true;
        public uint ProcessId = 1;
        public IntPtr Owner = IntPtr.Zero;
        public bool Manageable = true;
        public string Title = "";
    }

    public readonly Dictionary<IntPtr, WindowInfo> Windows = new();
    public readonly HashSet<IntPtr> ClippedWindows = new();
    public readonly List<BatchMoveItem> LastBatch = new();
    public readonly List<(IntPtr hWnd, int x, int y, int w, int h, uint flags)> SetPositionCalls = new();
    public List<(int x, int y, int w, int h)> ScreenAreas = new() { (0, 0, 1920, 1080) };

    // Windows returned by EnumWindows, in order
    public List<IntPtr> EnumOrder = new();

    public void AddWindow(IntPtr hWnd, int x, int y, int w, int h,
        uint pid = 1, int style = 0, bool manageable = true, string title = "")
    {
        Windows[hWnd] = new WindowInfo
        {
            X = x, Y = y, W = w, H = h,
            ProcessId = pid, Style = style, Manageable = manageable, Title = title
        };
        if (!EnumOrder.Contains(hWnd))
            EnumOrder.Add(hWnd);
    }

    public bool IsWindowVisible(IntPtr hWnd) =>
        Windows.TryGetValue(hWnd, out var w) && w.Visible;

    public int GetWindowStyle(IntPtr hWnd) =>
        Windows.TryGetValue(hWnd, out var w) ? w.Style : 0;

    public (int x, int y, int w, int h) GetWindowRect(IntPtr hWnd) =>
        Windows.TryGetValue(hWnd, out var w) ? (w.X, w.Y, w.W, w.H) : (0, 0, 0, 0);

    public (int left, int top, int right, int bottom) GetFrameInset(IntPtr hWnd)
    {
        return (0, 0, 0, 0);
    }

    public uint GetWindowProcessId(IntPtr hWnd) =>
        Windows.TryGetValue(hWnd, out var w) ? w.ProcessId : 0;

    public IntPtr GetWindowOwner(IntPtr hWnd) =>
        Windows.TryGetValue(hWnd, out var w) ? w.Owner : IntPtr.Zero;

    /// <summary>Test hook: what <see cref="WindowFromPoint"/> should return.</summary>
    public Func<int, int, IntPtr>? HitTest;

    public IntPtr WindowFromPoint(int x, int y) => HitTest?.Invoke(x, y) ?? IntPtr.Zero;


    public string GetWindowTitle(IntPtr hWnd) =>
        Windows.TryGetValue(hWnd, out var w) ? w.Title : "";

    public Dictionary<uint, (string name, string exe)> Processes = new();
    public (string name, string exe) ProcessFallback = ("", "");

    public (string name, string exe) GetProcessInfo(uint pid)
    {
        return Processes.TryGetValue(pid, out var info) ? info : ProcessFallback;
    }

    public Dictionary<uint, byte[]> ProcessIcons = new();

    public byte[]? GetProcessIconBgra(uint pid, int sizePx)
    {
        return ProcessIcons.TryGetValue(pid, out var px) ? px : null;
    }

    public bool IsManageable(IntPtr hWnd, uint ownPid, bool allowMinimized = false)
    {
        if (!Windows.TryGetValue(hWnd, out var w)) return false;
        if (w.ProcessId == ownPid) return false;
        return w.Manageable;
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;

    public void SetWindowPosition(IntPtr hWnd, int x, int y, int w, int h, uint flags)
    {
        SetPositionCalls.Add((hWnd, x, y, w, h, flags));
        if (Windows.TryGetValue(hWnd, out var win))
        {
            // Honour SWP_NOMOVE / SWP_NOSIZE the way Win32 does, so callers that
            // pass a rect but suppress half of it are modelled faithfully.
            if ((flags & SWP_NOMOVE) == 0) { win.X = x; win.Y = y; }
            if ((flags & SWP_NOSIZE) == 0) { win.W = w; win.H = h; }
        }
    }

    public readonly List<IntPtr> InvalidatedWindows = new();

    public void InvalidateWindow(IntPtr hWnd) => InvalidatedWindows.Add(hWnd);

    public void ClipWindow(IntPtr hWnd) => ClippedWindows.Add(hWnd);


    public void UnclipWindow(IntPtr hWnd) => ClippedWindows.Remove(hWnd);

    /// <summary>
    /// Park inside <see cref="BatchMove"/> until the batch's token is cancelled.
    /// Only cancellable calls block — i.e. the ones coming off
    /// <see cref="ProjectionWorker"/>'s thread — so a test can hold a worker
    /// batch in flight and drive the UI thread past it.
    /// </summary>
    public bool BlockCancellableBatches;

    /// <summary>Signalled once a blocked batch is parked.</summary>
    public readonly System.Threading.ManualResetEventSlim BatchEntered = new(false);

    public void BatchMove(List<BatchMoveItem> items, bool isAsync, bool isTransient, System.Threading.CancellationToken ct = default)
    {
        if (BlockCancellableBatches && ct.CanBeCanceled)
        {
            BatchEntered.Set();
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
        }

        LastBatch.Clear();
        LastBatch.AddRange(items);
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) return;
            if (Windows.TryGetValue(item.HWnd, out var win))
            {
                var r = item.Rect;
                win.X = r.X; win.Y = r.Y;
                // PosOnly becomes SWP_NOSIZE in Win32WindowApi.BatchMove.
                if (!item.PosOnly) { win.W = r.W; win.H = r.H; }
            }
        }
    }

    public void EnumWindows(Func<IntPtr, bool> callback)
    {
        foreach (var hWnd in EnumOrder)
        {
            if (!callback(hWnd)) break;
        }
    }

    public int GetScreenWorkingAreasCalls;

    public IReadOnlyList<(int x, int y, int w, int h)> GetScreenWorkingAreas()
    {
        GetScreenWorkingAreasCalls++;
        return ScreenAreas;
    }
}
