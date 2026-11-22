namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class SlottedPageCompactionTests
{
    [TestMethod]
    public void Compaction_preserves_records_when_reused_slot_order_differs_from_physical_order()
    {
        var page = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, new(1));
        Assert.IsTrue(page.TryInsert(new byte[8], 1, out ushort first));
        byte[] secondBytes = Enumerable.Repeat((byte)2, 8).ToArray();
        Assert.IsTrue(page.TryInsert(secondBytes, 2, out ushort second));
        page.Delete(first, 3);
        byte[] replacement = Enumerable.Repeat((byte)3, 16).ToArray();
        Assert.IsTrue(page.TryInsert(replacement, 4, out ushort reused));
        Assert.AreEqual(first, reused);
        page.Compact(5);
        CollectionAssert.AreEqual(replacement, page.Read(reused).ToArray());
        CollectionAssert.AreEqual(secondBytes, page.Read(second).ToArray());
        Assert.AreEqual(0, page.FragmentedBytes);
        Assert.AreEqual(5L, page.PageLsn);
        var decoded = SlottedDataPage.Decode(page.Encode(), new(1));
        CollectionAssert.AreEqual(secondBytes, decoded.Read(second).ToArray());
    }

    [TestMethod]
    public void Repeated_slot_reuse_and_compaction_matches_independent_record_oracle()
    {
        var page = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, new(7));
        var oracle = new Dictionary<ushort, byte[]>();
        var random = new Random(8173);
        for (int step = 1; step <= 1000; step++)
        {
            if (oracle.Count > 0 && random.Next(3) == 0)
            {
                ushort selected = oracle.Keys.ElementAt(random.Next(oracle.Count));
                page.Delete(selected, step);
                oracle.Remove(selected);
            }
            else
            {
                byte[] value = new byte[random.Next(1, 257)];
                random.NextBytes(value);
                if (page.TryInsert(value, step, out ushort slot))
                {
                    oracle.Add(slot, value);
                }
            }
            if (step % 7 == 0)
            {
                page.Compact(step);
                page = SlottedDataPage.Decode(page.Encode(), new(7));
            }
            foreach (var pair in oracle)
            {
                CollectionAssert.AreEqual(pair.Value, page.Read(pair.Key).ToArray(), $"Step {step}, slot {pair.Key}");
            }
            Assert.AreEqual(oracle.Count, page.OccupiedRecords.Count);
        }
    }
}
