using System;

namespace CanvasDesktop.Tests;

internal sealed class FakeVirtualDesktops : IVirtualDesktops
{
    public Guid CurrentDesktopId { get; set; } = Guid.Empty;
    public event Action<Guid>? DesktopChanged;

    public bool DefaultIsOnCurrent = true;

    public bool IsOnCurrentDesktop(IntPtr hWnd)
    {
        return DefaultIsOnCurrent;
    }

    /// <summary>Test helper: change <see cref="CurrentDesktopId"/> and raise <see cref="DesktopChanged"/>.</summary>
    public void SwitchTo(Guid newId)
    {
        CurrentDesktopId = newId;
        DesktopChanged?.Invoke(newId);
    }

    /// <summary>
    /// Test helper: deliver a notification for <paramref name="notifiedId"/> while
    /// <see cref="CurrentDesktopId"/> has already advanced to <paramref name="currentId"/>.
    /// Models the real service, where detection runs on a poll thread and the
    /// notification is marshalled to the UI thread — two switches inside one poll
    /// interval leave the property ahead of the notification being handled.
    /// </summary>
    public void RaiseStaleSwitch(Guid notifiedId, Guid currentId)
    {
        CurrentDesktopId = currentId;
        DesktopChanged?.Invoke(notifiedId);
    }
}
