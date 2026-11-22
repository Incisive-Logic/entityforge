using EntityForge.Db.Prototypes.Pages;

namespace EntityForge.Db.Prototypes.Pages.Tests;

[TestClass]
public sealed class SlottedPrototypePageTests
{
    private const int TestPageSize = 8 * 1024;
    private const int FirstPayloadSize = 200;
    private const int SecondPayloadSize = 400;

    [TestMethod]
    public void Stable_slot_survives_delete_reuse_and_compaction()
    {
        var page = new SlottedPrototypePage(TestPageSize, pageId: 12, generation: 3);
        Assert.IsTrue(page.TryInsert(Enumerable.Repeat((byte)1, FirstPayloadSize).ToArray(), out ushort first));
        Assert.IsTrue(page.TryInsert(Enumerable.Repeat((byte)2, SecondPayloadSize).ToArray(), out ushort second));
        page.Delete(first);
        Assert.IsTrue(page.TryInsert(Enumerable.Repeat((byte)3, FirstPayloadSize).ToArray(), out ushort reused));
        page.Compact();

        Assert.AreEqual(first, reused);
        Assert.AreEqual(SecondPayloadSize, page.Read(second).Length);
        Assert.AreEqual(FirstPayloadSize, page.Read(reused).Length);
        Assert.AreEqual(0, page.FragmentedBytes);
        SlottedPrototypePage.Validate(page.Encode());
    }

    [TestMethod]
    public void Checksum_rejects_corrupt_page_image()
    {
        var page = new SlottedPrototypePage(TestPageSize, pageId: 1);
        Assert.IsTrue(page.TryInsert(Enumerable.Repeat((byte)7, FirstPayloadSize).ToArray(), out _));
        byte[] encoded = page.Encode();
        encoded[^1] ^= byte.MaxValue;

        Assert.ThrowsExactly<PrototypePageCorruptionException>(() => SlottedPrototypePage.Validate(encoded));
    }

    [TestMethod]
    public void Experiment_is_deterministic_and_emits_all_candidate_page_sizes()
    {
        var experiment = new PageLayoutExperiment();
        PageLayoutExperimentResult first = experiment.Run();
        PageLayoutExperimentResult second = experiment.Run();

        Assert.AreEqual(first.ToJson(), second.ToJson());
        CollectionAssert.AreEqual(PageLayoutExperimentOptions.Default.PageSizes.ToArray(),
            first.Results.Select(result => result.PageSize).ToArray());
        Assert.IsTrue(first.Results.All(result => result.PageCount > 0));
        Assert.IsTrue(first.Results.All(result => result.OverflowRecordCount > 0));
        Assert.IsTrue(first.Results.All(result => result.FragmentedBytesBeforeCompaction > 0));
        Assert.IsTrue(first.Results.All(result => result.FragmentedBytesAfterCompaction == 0));
    }
}
