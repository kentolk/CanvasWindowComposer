using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

/// <summary>
/// The selection-advance logic behind Alt+S hold-to-cycle. SearchOverlay itself
/// is a Form and can't be instantiated headlessly, but the index arithmetic is
/// static and is the part that actually has edge cases.
/// </summary>
public class SearchOverlayCyclingTests
{
    [Fact]
    public void NextIndex_AdvancesByOne()
    {
        Assert.Equal(1, SearchOverlay.NextIndex(0, 5));
        Assert.Equal(3, SearchOverlay.NextIndex(2, 5));
    }

    [Fact]
    public void NextIndex_WrapsAtTheEnd()
    {
        // Alt-Tab convention: keep tapping and you loop, you don't get stuck on
        // the last entry.
        Assert.Equal(0, SearchOverlay.NextIndex(4, 5));
    }

    [Fact]
    public void NextIndex_FromNoSelection_StartsAtTheTop()
    {
        Assert.Equal(0, SearchOverlay.NextIndex(-1, 5));
    }

    [Fact]
    public void NextIndex_EmptyList_ReturnsNoSelection()
    {
        Assert.Equal(-1, SearchOverlay.NextIndex(-1, 0));
        Assert.Equal(-1, SearchOverlay.NextIndex(0, 0));
    }

    [Fact]
    public void NextIndex_SingleEntry_StaysPut()
    {
        Assert.Equal(0, SearchOverlay.NextIndex(0, 1));
    }

    [Fact]
    public void NextIndex_CyclesThroughEveryEntryExactlyOnce()
    {
        const int count = 5;
        int index = -1;
        var seen = new bool[count];

        for (int i = 0; i < count; i++)
        {
            index = SearchOverlay.NextIndex(index, count);
            Assert.False(seen[index], $"index {index} visited twice in one lap");
            seen[index] = true;
        }

        Assert.All(seen, Assert.True);
    }
}
