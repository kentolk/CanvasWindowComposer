using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class OverviewStateTests
{
    [Fact]
    public void NewState_StartsHidden()
    {
        var s = new OverviewState();
        Assert.Equal(OverviewMode.Hidden, s.CurrentMode);
        Assert.Equal(OverviewState.HiddenConfig, s.CurrentConfig);
    }

    [Fact]
    public void SetMode_ToSameMode_ReturnsFalse()
    {
        var s = new OverviewState();
        Assert.False(s.SetMode(OverviewMode.Hidden));
    }

    [Fact]
    public void SetMode_ToDifferentMode_ReturnsTrueAndUpdatesConfig()
    {
        var s = new OverviewState();
        Assert.True(s.SetMode(OverviewMode.Panning));
        Assert.Equal(OverviewMode.Panning, s.CurrentMode);
        Assert.Equal(OverviewState.PanningConfig, s.CurrentConfig);
    }

    [Fact]
    public void ConfigFor_ReturnsExpectedConfig()
    {
        Assert.Equal(OverviewState.HiddenConfig, OverviewState.ConfigFor(OverviewMode.Hidden));
        Assert.Equal(OverviewState.PanningConfig, OverviewState.ConfigFor(OverviewMode.Panning));
        Assert.Equal(OverviewState.ZoomingConfig, OverviewState.ConfigFor(OverviewMode.Zooming));
    }

    [Fact]
    public void SetMode_EveryTransitionLandsOnMatchingConfig()
    {
        // OverviewMode is internal, so this can't be a [Theory] on a public
        // test method — walk the full from/to matrix in one Fact instead.
        var modes = new[] { OverviewMode.Hidden, OverviewMode.Panning, OverviewMode.Zooming };
        var state = new OverviewState();

        foreach (var target in modes)
        {
            foreach (var from in modes)
            {
                state.SetMode(from);
                bool changed = state.SetMode(target);

                Assert.Equal(from != target, changed);
                Assert.Equal(target, state.CurrentMode);
                Assert.Equal(OverviewState.ConfigFor(target), state.CurrentConfig);
            }
        }
    }


    [Fact]
    public void PanningAcceptsInertiaAndZoomingTakesInput()
    {
        // These two flags are what the manager branches on for click-through and
        // for whether a fling keeps the overview alive; pin them so a config
        // tweak can't quietly swap the two modes' behaviour.
        Assert.True(OverviewState.PanningConfig.InertiaAllowed);
        Assert.False(OverviewState.PanningConfig.InputEnabled);

        Assert.False(OverviewState.ZoomingConfig.InertiaAllowed);
        Assert.True(OverviewState.ZoomingConfig.InputEnabled);

        Assert.False(OverviewState.HiddenConfig.InputEnabled);
        Assert.False(OverviewState.HiddenConfig.InertiaAllowed);
    }
}
