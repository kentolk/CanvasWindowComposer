using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class EdgeTriggerTests
{
    private const long Dwell = 400;
    private const long Repeat = 700;

    private static EdgeTrigger New() => new(Dwell, Repeat);

    [Fact]
    public void Update_ButtonUp_NeverFires()
    {
        var t = New();
        for (long now = 0; now < 5000; now += 50)
            Assert.False(t.Update(buttonDown: false, NavDirection.Right, now));
    }

    [Fact]
    public void Update_AtEdgeButNotYetDwelled_DoesNotFire()
    {
        var t = New();
        Assert.False(t.Update(true, NavDirection.Right, 0));
        Assert.False(t.Update(true, NavDirection.Right, Dwell - 1));
    }

    [Fact]
    public void Update_FiresOnceTheDwellElapses()
    {
        var t = New();
        t.Update(true, NavDirection.Right, 0);
        Assert.True(t.Update(true, NavDirection.Right, Dwell));
    }

    [Fact]
    public void Update_HoldingRepeatsOnTheShorterInterval()
    {
        var t = New();
        t.Update(true, NavDirection.Right, 0);
        Assert.True(t.Update(true, NavDirection.Right, Dwell));

        Assert.False(t.Update(true, NavDirection.Right, Dwell + Repeat - 1));
        Assert.True(t.Update(true, NavDirection.Right, Dwell + Repeat));
    }

    [Fact]
    public void Update_LeavingTheEdgeCancelsTheDwell()
    {
        var t = New();
        t.Update(true, NavDirection.Right, 0);
        t.Update(true, null, 200);          // pulled away before it fired

        Assert.False(t.Update(true, NavDirection.Right, Dwell));
    }

    [Fact]
    public void Update_ReleasingTheButtonCancelsTheDwell()
    {
        var t = New();
        t.Update(true, NavDirection.Right, 0);
        t.Update(false, NavDirection.Right, 200);

        Assert.False(t.Update(true, NavDirection.Right, Dwell));
    }

    [Fact]
    public void Update_SwitchingEdgeRestartsTheDwell()
    {
        var t = New();
        t.Update(true, NavDirection.Right, 0);
        // Sliding from the right edge to the bottom must not inherit the dwell
        // already served against the right.
        Assert.False(t.Update(true, NavDirection.Down, Dwell));
        Assert.True(t.Update(true, NavDirection.Down, Dwell * 2));
    }

    [Fact]
    public void Update_AfterFiringRepeatsWithoutReDwelling()
    {
        var t = New();
        t.Update(true, NavDirection.Left, 0);

        int fires = 0;
        for (long now = Dwell; now <= Dwell + Repeat * 3; now += 50)
            if (t.Update(true, NavDirection.Left, now)) fires++;

        Assert.Equal(4, fires);
    }

    [Fact]
    public void Reset_ClearsAPendingDwell()
    {
        var t = New();
        t.Update(true, NavDirection.Up, 0);
        t.Reset();

        Assert.False(t.Update(true, NavDirection.Up, Dwell));
    }
}
