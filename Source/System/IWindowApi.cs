using System;
using System.Collections.Generic;
using System.Threading;

namespace CanvasDesktop;

/// <summary>One entry of a batched <see cref="IWindowApi.BatchMove"/> call.</summary>
/// <param name="PosOnly">If true, the call uses SWP_NOSIZE (move only).</param>
internal readonly record struct BatchMoveItem(IntPtr HWnd, WindowRect Rect, bool PosOnly);

/// <summary>
/// Abstracts Win32 window operations so WindowManager can be unit-tested.
/// </summary>
internal interface IWindowApi
{
    // Query
    bool IsWindowVisible(IntPtr hWnd);
    int GetWindowStyle(IntPtr hWnd);
    (int x, int y, int w, int h) GetWindowRect(IntPtr hWnd);
    (int left, int top, int right, int bottom) GetFrameInset(IntPtr hWnd);
    uint GetWindowProcessId(IntPtr hWnd);

    /// <summary>
    /// The window that owns <paramref name="hWnd"/>, or zero if it stands alone.
    ///
    /// Not the same question as GetParent, which only reports the owner for
    /// top-level windows carrying WS_POPUP. A framework dialog — Fork's "rename
    /// branch", a settings sheet — is typically WS_OVERLAPPED with an owner, so
    /// GetParent says zero and it looks like an ordinary application window.
    /// </summary>
    IntPtr GetWindowOwner(IntPtr hWnd);

    /// <summary>Top-level window under a screen point, or zero.</summary>
    IntPtr WindowFromPoint(int x, int y);


    string GetWindowTitle(IntPtr hWnd);

    /// <summary>
    /// Returns (process name, exe filename) for <paramref name="pid"/>, or a
    /// fallback if the process isn't accessible (exited, denied, etc.).
    /// </summary>
    (string name, string exe) GetProcessInfo(uint pid);

    /// <summary>
    /// The application icon for <paramref name="pid"/>, rasterised to
    /// <paramref name="sizePx"/> square as straight-alpha BGRA bytes, or
    /// null if it can't be read (protected process, exited, no icon).
    ///
    /// Keyed on the process rather than the window: every window of an app shares
    /// its icon, and reading it is expensive enough to be worth doing once.
    /// </summary>
    byte[]? GetProcessIconBgra(uint pid, int sizePx);


    // Filtering
    bool IsManageable(IntPtr hWnd, uint ownPid, bool allowMinimized = false);

    // Mutation
    void SetWindowPosition(IntPtr hWnd, int x, int y, int w, int h, uint flags);
    /// <summary>
    /// Mark a window and its children dirty so the owning application repaints.
    ///
    /// Deliberately does not force the paint synchronously: Chromium and Gecko
    /// suspend compositing for windows they believe are off-screen, and pulling
    /// WM_PAINT out of a suspended renderer on our UI thread is how you stall
    /// both processes. Flagging it dirty lets the app repaint on its own clock.
    /// </summary>
    void InvalidateWindow(IntPtr hWnd);

    void ClipWindow(IntPtr hWnd);

    void UnclipWindow(IntPtr hWnd);
    /// <summary>
    /// Apply a batch of window position changes. If <paramref name="ct"/> is
    /// cancelled during the batch, aborts cleanly without finalizing the batch —
    /// partially-applied moves are OK because the next scheduled batch will
    /// reconcile to the final state.
    /// </summary>
    void BatchMove(List<BatchMoveItem> items, bool isAsync, bool isTransient, CancellationToken ct = default);

    // Enumeration
    void EnumWindows(Func<IntPtr, bool> callback);

    // Screen geometry
    IReadOnlyList<(int x, int y, int w, int h)> GetScreenWorkingAreas();
}
