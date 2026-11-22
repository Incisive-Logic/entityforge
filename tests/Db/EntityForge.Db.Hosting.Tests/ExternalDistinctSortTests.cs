using System.Buffers.Binary;
using EntityForge.Db.Query.Planning;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class ExternalDistinctSortTests
{
    private static void Encode(int value, Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Distinct_early_consumer_exit_releases_all_stage_reservations(bool ordered)
    {
        var budget = new ExternalSortDiskBudget(65536);
        await using (var sorter = new ExternalDistinctSort<int>(value => value.ToString(), Encode,
            bytes => BinaryPrimitives.ReadInt32LittleEndian(bytes), ordered ? Comparer<int>.Default : null, budget, 2))
        {
            for (int value = 0; value < 20; value++)
            {
                await sorter.AddAsync(value, default);
            }
            await foreach (int value in sorter.ReadAsync())
            {
                break;
            }
        }
        Assert.AreEqual(0L, budget.BytesInUse);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Distinct_merges_global_keys_and_releases_shared_storage(bool ordered)
    {
        var budget = new ExternalSortDiskBudget(65536);
        await using (var sorter = new ExternalDistinctSort<int>(value => (value % 5).ToString(), Encode,
            bytes => BinaryPrimitives.ReadInt32LittleEndian(bytes), ordered ? Comparer<int>.Default : null, budget, 2))
        {
            for (int value = 99; value >= 0; value--)
            {
                await sorter.AddAsync(value, default);
            }
            var result = new List<int>();
            await foreach (int value in sorter.ReadAsync())
            {
                result.Add(value);
            }
            Assert.AreEqual(5, result.Count);
            CollectionAssert.AreEquivalent(new[] { 0, 1, 2, 3, 4 }, result.Select(value => value % 5).ToArray());
            if (ordered)
            {
                CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, result);
            }
        }
        Assert.AreEqual(0L, budget.BytesInUse);
    }

    [TestMethod]
    public async Task Independent_operators_share_one_disk_limit()
    {
        var budget = new ExternalSortDiskBudget(60);
        await using (var first = new ExternalMergeSort<int>(Comparer<int>.Default, Encode,
            bytes => BinaryPrimitives.ReadInt32LittleEndian(bytes), maximumBufferedRows: 1, sharedDiskBudget: budget))
        await using (var second = new ExternalMergeSort<int>(Comparer<int>.Default, Encode,
            bytes => BinaryPrimitives.ReadInt32LittleEndian(bytes), maximumBufferedRows: 1, sharedDiskBudget: budget))
        {
            await first.AddAsync(1, default);
            await first.AddAsync(2, default);
            Assert.AreEqual(40L, budget.BytesInUse);
            await second.AddAsync(3, default);
            await Assert.ThrowsExactlyAsync<QuerySortBudgetException>(() => second.AddAsync(4, default).AsTask());
            Assert.AreEqual(40L, budget.BytesInUse);
        }
        Assert.AreEqual(0L, budget.BytesInUse);
    }

    [TestMethod]
    public async Task Distinct_ordering_counts_overlapping_stages_before_returning_rows()
    {
        var budget = new ExternalSortDiskBudget(500);
        await using (var sorter = new ExternalDistinctSort<int>(value => value.ToString(), Encode,
            bytes => BinaryPrimitives.ReadInt32LittleEndian(bytes), Comparer<int>.Default, budget, 1))
        {
            for (int value = 0; value < 10; value++)
            {
                await sorter.AddAsync(value, default);
            }
            await Assert.ThrowsExactlyAsync<QuerySortBudgetException>(async () =>
            {
                await foreach (int value in sorter.ReadAsync())
                {
                    Assert.Fail("Exceeded shared storage quota returned a row.");
                }
            });
        }
        Assert.AreEqual(0L, budget.BytesInUse);
    }
}
