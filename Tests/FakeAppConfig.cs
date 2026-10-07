using System;

namespace CanvasDesktop.Tests;

internal sealed class FakeAppConfig : IAppConfig
{
    public bool DisableSearch { get; set; }
    public bool DisableAltPan { get; set; }
    public bool DisableGreedyDraw { get; set; }
    public bool ShowScreenFixedWindowsDuringPan { get; set; } = true;
    public bool DisableMouseCurve { get; set; }
    public bool DisableZoomHotkey { get; set; }
    public int GridColumns { get; set; } = GridArranger.DefaultColumns;
    public bool EnableDragEdgeNavigation { get; set; }
    public bool AutoGridNewWindows { get; set; } = true;
    public bool FollowFocusedWindows { get; set; } = true;
    public bool EnableOverviewRightClickResize { get; set; } = true;




    public event Action? Changed;

    /// <summary>Test helper: raise <see cref="Changed"/> after flipping flags.</summary>
    public void RaiseChanged()
    {
        Changed?.Invoke();
    }
}
