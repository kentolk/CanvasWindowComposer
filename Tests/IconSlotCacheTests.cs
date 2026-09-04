using System;
using System.Collections.Generic;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class IconSlotCacheTests
{
    private const int IconPx = 16;
    private const int Capacity = 4;

    private static (IconSlotCache cache, FakeWindowApi api, List<int> uploads) Create(int capacity = Capacity)
    {
        var api = new FakeWindowApi();
        var uploads = new List<int>();
        var cache = new IconSlotCache(api, capacity, IconPx, (slot, _) => uploads.Add(slot));
        return (cache, api, uploads);
    }

    private static void AddWindowWithIcon(FakeWindowApi api, int hWnd, uint pid)
    {
        api.AddWindow((IntPtr)hWnd, 0, 0, 100, 100, pid: pid);
        api.ProcessIcons[pid] = new byte[IconPx * IconPx * 4];
    }

    [Fact]
    public void SlotFor_AssignsASlotWhenAnIconExists()
    {
        var (cache, api, uploads) = Create();
        AddWindowWithIcon(api, 1, pid: 10);

        Assert.Equal(0, cache.SlotFor((IntPtr)1));
        Assert.Single(uploads);
    }

    [Fact]
    public void SlotFor_WindowsOfTheSameProcessShareOneSlotAndOneUpload()
    {
        var (cache, api, uploads) = Create();
        AddWindowWithIcon(api, 1, pid: 10);
        AddWindowWithIcon(api, 2, pid: 10);

        int a = cache.SlotFor((IntPtr)1);
        int b = cache.SlotFor((IntPtr)2);

        Assert.Equal(a, b);
        Assert.Single(uploads);
    }

    [Fact]
    public void SlotFor_DifferentProcessesGetDifferentSlots()
    {
        var (cache, api, uploads) = Create();
        AddWindowWithIcon(api, 1, pid: 10);
        AddWindowWithIcon(api, 2, pid: 20);

        Assert.NotEqual(cache.SlotFor((IntPtr)1), cache.SlotFor((IntPtr)2));
        Assert.Equal(2, uploads.Count);
    }

    [Fact]
    public void SlotFor_NoIconAvailable_ReturnsMinusOne()
    {
        var (cache, api, uploads) = Create();
        api.AddWindow((IntPtr)1, 0, 0, 100, 100, pid: 10); // no icon registered

        Assert.Equal(-1, cache.SlotFor((IntPtr)1));
        Assert.Empty(uploads);
    }

    [Fact]
    public void SlotFor_AFailedReadIsNotRetried()
    {
        var (cache, api, _) = Create();
        api.AddWindow((IntPtr)1, 0, 0, 100, 100, pid: 10);
        cache.SlotFor((IntPtr)1);

        // The icon becomes available later. A protected process would otherwise
        // be re-probed on every snapshot, i.e. every pan frame.
        api.ProcessIcons[10] = new byte[IconPx * IconPx * 4];

        Assert.Equal(-1, cache.SlotFor((IntPtr)1));
    }

    [Fact]
    public void SlotFor_ResolvesEachWindowOnlyOnce()
    {
        var (cache, api, _) = Create();
        AddWindowWithIcon(api, 1, pid: 10);

        cache.SlotFor((IntPtr)1);
        api.Windows[(IntPtr)1].ProcessId = 999; // would resolve differently if re-read

        Assert.Equal(0, cache.SlotFor((IntPtr)1));
    }

    [Fact]
    public void SlotFor_StopsAllocatingAtCapacity()
    {
        var (cache, api, uploads) = Create(capacity: 2);
        for (int i = 1; i <= 4; i++)
            AddWindowWithIcon(api, i, pid: (uint)(10 * i));

        var slots = new List<int>();
        for (int i = 1; i <= 4; i++)
            slots.Add(cache.SlotFor((IntPtr)i));

        Assert.Equal(new[] { 0, 1, -1, -1 }, slots);
        Assert.Equal(2, uploads.Count);
    }

    [Fact]
    public void Forget_DropsTheWindowButKeepsTheProcessSlot()
    {
        var (cache, api, uploads) = Create();
        AddWindowWithIcon(api, 1, pid: 10);
        AddWindowWithIcon(api, 2, pid: 10);
        cache.SlotFor((IntPtr)1);

        cache.Forget((IntPtr)1);

        // Closing one window of an app must not cost the app its slot, or a
        // long session would exhaust the atlas through open/close churn.
        Assert.Equal(0, cache.SlotFor((IntPtr)2));
        Assert.Single(uploads);
    }
}
