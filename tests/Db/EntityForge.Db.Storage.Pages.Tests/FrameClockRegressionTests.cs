namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class FrameClockRegressionTests
{
    [TestMethod]
    public void Clock_preserves_successor_order_across_eviction_invalidation_and_wraparound()
    {
        using var cache = new PhysicalPageFrameCache(4, 2);
        PhysicalPageFrame Add(long id, PageAccessClass access = PageAccessClass.Normal)
        {
            var frame = cache.Add(new(id), [], access);
            frame.PinCount = 0;
            frame.Usage = 0;
            return frame;
        }
        var first = Add(1);
        var second = Add(2);
        var third = Add(3);
        Add(4);
        first.PinCount = 1;
        second.Dirty = true;
        Assert.IsTrue(cache.TryEvict(PageAccessClass.Normal, out _));
        Assert.IsFalse(cache.TryGet(third.Id, PageAccessClass.Normal, out _));
        // The hand points at 4; remove it through invalidation rather than eviction.
        cache.InvalidateFrom(4);
        Add(5);
        Add(6, PageAccessClass.SequentialScan);
        Assert.IsTrue(cache.TryEvict(PageAccessClass.Normal, out _));
        Assert.IsFalse(cache.TryGet(new(5), PageAccessClass.Normal, out _));
        Assert.IsFalse(cache.TryEvict(PageAccessClass.Normal, out _));
        Assert.IsTrue(cache.TryEvict(PageAccessClass.SequentialScan, out _));
        first.PinCount = 0;
        second.Dirty = false;
        Assert.IsTrue(cache.TryEvict(PageAccessClass.Normal, out _));
        Assert.IsFalse(cache.TryGet(first.Id, PageAccessClass.Normal, out _));
        Assert.IsTrue(cache.TryEvict(PageAccessClass.Normal, out _));
        Assert.IsFalse(cache.TryEvict(PageAccessClass.Normal, out _));
        Add(7);
        Assert.IsTrue(cache.TryEvict(PageAccessClass.Normal, out _));
    }
}
