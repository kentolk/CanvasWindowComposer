using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// Unified injectable source for everything the orchestrator listens to:
/// low-level mouse hook signals, system hotkeys, and Win32 window-lifecycle
/// events. Production wraps RawMouseInput + MessageWindow + Win32EventRouter
/// trio; tests use <c>FakeInputRouter</c> to drive synthetic input.
/// </summary>
internal interface IInputRouter
{
    /// <summary>
    /// Raised on the UI thread when the hook has accumulated mouse input
    /// that needs draining (pan delta, drag end, or zoom notch).
    /// </summary>
    event Action? InputAvailable;

    bool TryDrainPanDelta(out int dx, out int dy);
    bool TryDrainDragEnded();
    bool TryDrainZoom();

    event Action? DragStarted;
    event Action? ButtonDown;

    /// <summary>
    /// Ctrl+Alt+middle-click at a screen position: centre the canvas on the
    /// window under that point.
    /// </summary>
    event Action<int, int>? CenterRequested;

    event Action? SearchHotkey;
    event Action? OverviewHotkey;

    /// <summary>Ctrl+Alt+Arrow: jump the camera to the neighbouring window.</summary>
    event Action<NavDirection>? NavigateHotkey;

    /// <summary>Ctrl+Alt+G: lay the canvas out in a grid.</summary>
    event Action? ArrangeGridHotkey;


    /// <summary>
    /// Raised when the user presses Esc while the Esc hotkey is enabled.
    /// Esc is registered/unregistered on demand (via <see cref="EnableEscHotkey"/>
    /// / <see cref="DisableEscHotkey"/>) so we don't steal it globally.
    /// </summary>
    event Action? EscPressed;
    void EnableEscHotkey();
    void DisableEscHotkey();

    event Action<IntPtr>? WindowMinimized;
    event Action<IntPtr>? WindowDestroyed;
    event Action<IntPtr>? WindowShown;
    event Action<IntPtr>? WindowRestored;
    event Action<IntPtr>? WindowFocused;
    event Action<IntPtr>? WindowMoved;
    event Action? AltTabStarted;
    event Action? AltTabEnded;

    bool Enabled { get; set; }
    void SetExtraPanSurfaces(IEnumerable<IntPtr> handles);
    void ClearExtraPanSurfaces();

    /// <summary>
    /// Install a system-wide block of middle-mouse-button clicks. Used while
    /// the overview is panning: the overlay is WS_EX_TRANSPARENT, so without
    /// this the MMB click would reach whatever app is under the cursor.
    /// </summary>
    void EnableMiddleButtonBlock();
    void DisableMiddleButtonBlock();
}
