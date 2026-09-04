using System;

namespace CanvasDesktop;

/// <summary>
/// Abstracts virtual-desktop discovery so window-manager / state-cache code
/// can be tested without the COM <c>IVirtualDesktopManager</c>.
/// </summary>
internal interface IVirtualDesktops
{
    Guid CurrentDesktopId { get; }
    bool IsOnCurrentDesktop(IntPtr hWnd);

    /// <summary>
    /// Raised when the active virtual desktop changes, carrying the id that was
    /// detected at that moment. Subscribers must use the supplied id rather than
    /// re-reading <see cref="CurrentDesktopId"/>: detection runs on a poll thread
    /// and the notification is marshalled to the UI thread, so by the time a
    /// handler runs the property may already describe a later switch.
    /// </summary>
    event Action<Guid>? DesktopChanged;
}
