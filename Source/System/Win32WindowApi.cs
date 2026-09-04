using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace CanvasDesktop;

/// <summary>
/// Production implementation of IWindowApi wrapping Win32 APIs.
/// </summary>
internal sealed class Win32WindowApi : IWindowApi
{
    private readonly IScreens _screens;

    public Win32WindowApi(IScreens? screens = null)
    {
        _screens = screens ?? WinFormsScreens.Instance;
    }

    private static readonly HashSet<string> ExcludedClasses = new()
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow",

        // Win11 shell surfaces: taskbar thumbnail previews, Task View, Alt-Tab.
        // Unowned, unparented, WS_VISIBLE and uncloaked for as long as they are
        // on screen, so they pass every other test here and get registered as
        // ordinary windows — which puts a phantom on the minimap, hands the
        // reprojector a shell window to move around, and makes the canvas churn
        // (and previously blackout recentring) every time the pointer crosses
        // the taskbar.
        "XamlExplorerHostIslandWindow",
        "TaskListThumbnailWnd",
        "MultitaskingViewFrame",
        "ForegroundStaging"
    };

    public bool IsWindowVisible(IntPtr hWnd)
    {
        return PInvoke.IsWindowVisible((HWND)hWnd);
    }

    public int GetWindowStyle(IntPtr hWnd)
    {
        return PInvoke.GetWindowLong((HWND)hWnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
    }

    public (int x, int y, int w, int h) GetWindowRect(IntPtr hWnd)
    {
        PInvoke.GetWindowRect((HWND)hWnd, out RECT rect);
        return (rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
    }

    public unsafe (int left, int top, int right, int bottom) GetFrameInset(IntPtr hWnd)
    {
        PInvoke.GetWindowRect((HWND)hWnd, out RECT full);
        RECT visual;
        HRESULT hr = PInvoke.DwmGetWindowAttribute((HWND)hWnd,
            DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
            &visual,
            (uint)sizeof(RECT));

        if (hr.Failed)
            return (0, 0, 0, 0);

        return (
            Math.Max(0, visual.left - full.left),
            Math.Max(0, visual.top - full.top),
            Math.Max(0, full.right - visual.right),
            Math.Max(0, full.bottom - visual.bottom)
        );
    }

    public unsafe uint GetWindowProcessId(IntPtr hWnd)
    {
        uint pid;
        _ = PInvoke.GetWindowThreadProcessId((HWND)hWnd, &pid);
        return pid;
    }

    public IntPtr GetWindowOwner(IntPtr hWnd)
    {
        return PInvoke.GetWindow((HWND)hWnd, GET_WINDOW_CMD.GW_OWNER);
    }


    public unsafe string GetWindowTitle(IntPtr hWnd)

    {
        HWND h = (HWND)hWnd;
        int len = PInvoke.GetWindowTextLength(h);
        if (len <= 0) return "";
        Span<char> buffer = len < 512 ? stackalloc char[len + 1] : new char[len + 1];
        int written;
        fixed (char* p = buffer)
        {
            written = PInvoke.GetWindowText(h, new PWSTR(p), buffer.Length);
        }
        return new string(buffer[..written]);
    }

    public (string name, string exe) GetProcessInfo(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            string name = proc.ProcessName;
            string exe = Path.GetFileName(proc.MainModule?.FileName ?? name);
            return (name, exe);
        }
        catch
        {
            return ($"PID {pid}", "");
        }
    }

    public byte[]? GetProcessIconBgra(uint pid, int sizePx)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            string? path = proc.MainModule?.FileName;
            if (string.IsNullOrEmpty(path)) return null;

            // ExtractAssociatedIcon rather than WM_GETICON: no cross-process
            // SendMessage, so an unresponsive app can't stall the UI thread.
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;

            using var bmp = new System.Drawing.Bitmap(sizePx, sizePx,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.Clear(System.Drawing.Color.Transparent);
                using var bitmapIcon = icon.ToBitmap();
                g.DrawImage(bitmapIcon, 0, 0, sizePx, sizePx);
            }

            var rect = new System.Drawing.Rectangle(0, 0, sizePx, sizePx);
            var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                // Format32bppArgb is BGRA in memory on little-endian, which is
                // what DXGI_FORMAT_B8G8R8A8_UNORM wants.
                var pixels = new byte[sizePx * sizePx * 4];
                for (int y = 0; y < sizePx; y++)
                {
                    IntPtr row = data.Scan0 + y * data.Stride;
                    System.Runtime.InteropServices.Marshal.Copy(row, pixels, y * sizePx * 4, sizePx * 4);
                }
                return pixels;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
        catch
        {
            // Protected or exited process, or an exe with no icon resource.
            return null;
        }
    }

    public unsafe bool IsManageable(IntPtr hWnd, uint ownPid, bool allowMinimized = false)

    {
        HWND h = (HWND)hWnd;
        if (!PInvoke.IsWindowVisible(h))
            return false;

        uint pid;
        _ = PInvoke.GetWindowThreadProcessId(h, &pid);
        if (pid == ownPid)
            return false;

        int style = PInvoke.GetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        int exStyle = PInvoke.GetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

        if ((style & (int)WINDOW_STYLE.WS_MAXIMIZE) != 0)
            return false;
        if (!allowMinimized && (style & (int)WINDOW_STYLE.WS_MINIMIZE) != 0)
            return false;

        if ((exStyle & (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0 &&
            (exStyle & (int)WINDOW_EX_STYLE.WS_EX_APPWINDOW) == 0)
            return false;

        if (PInvoke.GetParent(h) != HWND.Null)
            return false;

        int cloaked;
        HRESULT cloakedHr = PInvoke.DwmGetWindowAttribute(h, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED,
            &cloaked, sizeof(int));
        if (cloakedHr.Succeeded && cloaked != 0)
            return false;

        Span<char> classBuf = stackalloc char[256];
        int classLen;
        fixed (char* p = classBuf)
        {
            classLen = PInvoke.GetClassName(h, new PWSTR((char*)p), classBuf.Length);
        }
        if (classLen == 0)
            return false;
        string className = new string(classBuf[..classLen]);
        if (ExcludedClasses.Contains(className))
            return false;

        return true;
    }

    public void SetWindowPosition(IntPtr hWnd, int x, int y, int w, int h, uint flags)
    {
        PInvoke.SetWindowPos((HWND)hWnd, HWND.Null, x, y, w, h, (SET_WINDOW_POS_FLAGS)flags);
    }

    public unsafe void InvalidateWindow(IntPtr hWnd)
    {
        PInvoke.RedrawWindow((HWND)hWnd, null, (HRGN)IntPtr.Zero,
            REDRAW_WINDOW_FLAGS.RDW_INVALIDATE | REDRAW_WINDOW_FLAGS.RDW_ERASE |
            REDRAW_WINDOW_FLAGS.RDW_ALLCHILDREN);
    }

    public void ClipWindow(IntPtr hWnd)

    {
        HRGN rgn = PInvoke.CreateRectRgn(0, 0, 0, 0);
        _ = PInvoke.SetWindowRgn((HWND)hWnd, rgn, true);
    }

    public void UnclipWindow(IntPtr hWnd)
    {
        _ = PInvoke.SetWindowRgn((HWND)hWnd, (HRGN)IntPtr.Zero, true);
    }

    public void BatchMove(List<BatchMoveItem> items, bool isAsync, bool isTransient, System.Threading.CancellationToken ct = default)
    {
        if (items.Count == 0)
            return;

        HDWP hdwp = PInvoke.BeginDeferWindowPos(items.Count);
        bool useBatch = hdwp != default(HDWP);
        int deferred = 0; // items currently riding on hdwp

        try
        {
            foreach (var item in items)
            {
                // On cancel, stop queuing new moves and fall through to the
                // finally block - accumulated DeferWindowPos entries still need EndDeferWindowPos.
                if (ct.IsCancellationRequested)
                    return;

                SET_WINDOW_POS_FLAGS flags = FlagsFor(item, isAsync, isTransient);

                HWND target = (HWND)item.HWnd;
                var r = item.Rect;
                if (useBatch)
                {
                    hdwp = PInvoke.DeferWindowPos(hdwp, target, HWND.Null, r.X, r.Y, r.W, r.H, flags);
                    if (hdwp == default(HDWP))
                    {
                        // DeferWindowPos destroys the HDWP when it fails, and every
                        // move already queued on it goes with it. Replay those
                        // individually before falling back, otherwise a mid-batch
                        // failure silently strands the windows processed so far.
                        useBatch = false;
                        ApplyRange(items, 0, deferred, isAsync, isTransient);
                        PInvoke.SetWindowPos(target, HWND.Null, r.X, r.Y, r.W, r.H, flags);
                    }
                    else
                    {
                        deferred++;
                    }
                }
                else
                {
                    PInvoke.SetWindowPos(target, HWND.Null, r.X, r.Y, r.W, r.H, flags);
                }
            }
        }
        finally
        {
            if (useBatch && hdwp != default(HDWP))
                PInvoke.EndDeferWindowPos(hdwp);
        }
    }

    private static SET_WINDOW_POS_FLAGS FlagsFor(BatchMoveItem item, bool isAsync, bool isTransient)
    {
        SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
        if (item.PosOnly)  flags |= SET_WINDOW_POS_FLAGS.SWP_NOSIZE;
        if (isAsync)       flags |= SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS;
        if (isTransient)   flags |= SET_WINDOW_POS_FLAGS.SWP_NOSENDCHANGING;
        return flags;
    }

    /// <summary>Apply items [start, end) one at a time, bypassing the defer batch.</summary>
    private static void ApplyRange(List<BatchMoveItem> items, int start, int end, bool isAsync, bool isTransient)
    {
        for (int i = start; i < end; i++)
        {
            var it = items[i];
            var r = it.Rect;
            PInvoke.SetWindowPos((HWND)it.HWnd, HWND.Null, r.X, r.Y, r.W, r.H,
                FlagsFor(it, isAsync, isTransient));
        }
    }

    public unsafe void EnumWindows(Func<IntPtr, bool> callback)

    {
        WNDENUMPROC proc = (HWND hWnd, LPARAM _) => callback(hWnd);
        PInvoke.EnumWindows(proc, 0);
        GC.KeepAlive(proc);
    }

    public IReadOnlyList<(int x, int y, int w, int h)> GetScreenWorkingAreas()
    {
        var src = _screens.AllWorkingAreas;
        var areas = new (int, int, int, int)[src.Count];
        for (int i = 0; i < src.Count; i++)
        {
            var s = src[i];
            areas[i] = (s.X, s.Y, s.Width, s.Height);
        }
        return areas;
    }
}
