using System.Buffers.Binary;
using EntityForge.Db.Query.Planning;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class ExternalMergeSortTests
{
    [TestMethod]
    [DataRow(false, "argument")]
    [DataRow(true, "argument")]
    [DataRow(false, "budget")]
    [DataRow(true, "budget")]
    [DataRow(false, "cancel")]
    [DataRow(true, "cancel")]
    [DataRow(false, "operation")]
    [DataRow(true, "operation")]
    public async Task External_sort_preserves_comparison_failure_and_releases_prior_runs(bool append, string kind)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception expected = kind switch
        {
            "argument" => new ArgumentException("query comparison failed"),
            "budget" => new QuerySortBudgetException("query comparison budget exhausted"),
            "cancel" => new OperationCanceledException(cancellation.Token),
            _ => new InvalidOperationException("original comparison failure", new FormatException("nested cause"))
        };
        expected.Data["EntityForge.GqlStatus"] = "22003";
        bool fail = false;
        var budget = new ExternalSortDiskBudget(65536);
        string? scratch;
        await using (var sorter = new ExternalMergeSort<int>(Comparer<int>.Create((left, right) =>
            fail ? throw expected : left.CompareTo(right)), Encode,
            bytes => BinaryPrimitives.ReadInt32LittleEndian(bytes), maximumBufferedRows: 2, sharedDiskBudget: budget))
        {
            foreach (int value in new[] { 4, 3, 2, 1 })
            {
                await sorter.AddAsync(value, default);
            }
            scratch = sorter.ScratchDirectory;
            Assert.IsNotNull(scratch);
            Assert.IsTrue(budget.BytesInUse > 0);
            fail = true;
            Exception? actual = null;
            try
            {
                if (append)
                {
                    await sorter.AddAsync(0, default);
                }
                else
                {
                    await foreach (int value in sorter.ReadAsync())
                    {
                        Assert.Fail("A failed comparison must not yield a partial answer.");
                    }
                }
            }
            catch (Exception error)
            {
                actual = error;
            }
            Assert.AreSame(expected, actual, "Preserve the original exception, including status and cancellation token.");
            Assert.IsNotNull(actual);
            Assert.AreEqual("22003", actual.Data["EntityForge.GqlStatus"]);
        }
        Assert.IsFalse(Directory.Exists(scratch));
        Assert.AreEqual(0L, budget.BytesInUse);
    }

    private sealed class MutableRow
    {
        public int Value
        {
            get; set;
        }
    }

    [TestMethod]
    public async Task External_sort_captures_values_before_the_caller_reuses_a_row()
    {
        await using var sorter = new ExternalMergeSort<MutableRow>(
            Comparer<MutableRow>.Create((left, right) => left.Value.CompareTo(right.Value)),
            (row, stream) => Encode(row.Value, stream),
            bytes => new MutableRow { Value = BinaryPrimitives.ReadInt32LittleEndian(bytes) });
        var row = new MutableRow { Value = 2 };
        await sorter.AddAsync(row, default);
        row.Value = 1;
        await sorter.AddAsync(row, default);
        row.Value = 999;
        var values = new List<int>();
        await foreach (var result in sorter.ReadAsync())
        {
            values.Add(result.Value);
        }
        CollectionAssert.AreEqual(new[] { 1, 2 }, values);
    }

    private static void Encode(int value, Stream stream)
    {
        Span<byte> result = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(result, value);
        stream.Write(result);
    }

    private static ExternalMergeSort<int> Sorter(int rows = 1024, long diskBytes = 16 * 1024 * 1024,
        ExternalSortDiskBudget? budget = null) =>
        new(Comparer<int>.Default, Encode, bytes => BinaryPrimitives.ReadInt32LittleEndian(bytes),
            maximumBufferedRows: rows, maximumBufferedBytes: 4096, maximumRecordBytes: 4,
            maximumDiskBytes: diskBytes, sharedDiskBudget: budget);

    [TestMethod]
    public async Task External_sort_rejects_oversized_codec_writes_before_creating_runs()
    {
        await using var sorter = new ExternalMergeSort<int>(Comparer<int>.Default, Encode,
            bytes => BinaryPrimitives.ReadInt32LittleEndian(bytes), maximumRecordBytes: 3);
        await Assert.ThrowsExactlyAsync<QuerySortBudgetException>(() => sorter.AddAsync(1, default).AsTask());
        Assert.IsNull(sorter.ScratchDirectory);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await foreach (var value in sorter.ReadAsync())
            {
                Assert.Fail("A failed sort produced a partial answer.");
            }
        });
    }

    [TestMethod]
    public async Task External_sort_merges_100000_values_with_duplicates_across_multiple_passes()
    {
        string? scratch;
        await using (var sorter = Sorter())
        {
            // 98 initial runs force a merge pass before the final 16-way merge.
            for (int value = 99_999; value >= 0; value--)
            {
                await sorter.AddAsync(value / 3, default);
            }
            int count = 0;
            await foreach (int value in sorter.ReadAsync())
            {
                Assert.AreEqual(count / 3, value);
                count++;
            }
            Assert.AreEqual(100_000, count);
            scratch = sorter.ScratchDirectory;
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => sorter.AddAsync(99, default).AsTask());
        }
        Assert.IsNotNull(scratch);
        Assert.IsFalse(Directory.Exists(scratch));
    }

    [TestMethod]
    [DataRow("quota")]
    [DataRow("cancel")]
    [DataRow("break")]
    [DataRow("corrupt")]
    [DataRow("truncated")]
    [DataRow("merge-quota")]
    [DataRow("runs")]
    public async Task External_sort_failures_and_early_disposal_remove_owned_runs(string scenario)
    {
        string? scratch = null;
        var budget = new ExternalSortDiskBudget(65536);
        await using (var sorter = Sorter(rows: scenario is "merge-quota" or "runs" ? 1 : 2,
            diskBytes: scenario == "quota" ? 80 : scenario == "merge-quota" ? 900 : 65536, budget: budget))
        {
            using var cancellation = new CancellationTokenSource();
            async Task Run()
            {
                for (int value = scenario == "runs" ? 257 : scenario == "merge-quota" ? 19 : 9; value >= 0; value--)
                {
                    await sorter.AddAsync(value, default);
                }
                scratch = sorter.ScratchDirectory;
                if (scenario is "corrupt" or "truncated")
                {
                    string path = Directory.GetFiles(scratch!, "*.run")[0];
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    if (scenario == "truncated")
                    {
                        stream.SetLength(0);
                    }
                    else
                    {
                        stream.Position = 4;
                        int value = stream.ReadByte();
                        stream.Position = 4;
                        stream.WriteByte((byte)(value ^ 1));
                    }
                }
                int count = 0;
                await foreach (int value in sorter.ReadAsync(cancellation.Token))
                {
                    count++;
                    if (scenario == "break")
                    {
                        break;
                    }
                    if (scenario == "cancel")
                    {
                        cancellation.Cancel();
                    }
                }
                if (scenario == "break")
                {
                    Assert.AreEqual(1, count);
                }
            }
            if (scenario is "quota" or "merge-quota" or "runs")
            {
                await Assert.ThrowsExactlyAsync<QuerySortBudgetException>(Run);
            }
            else if (scenario is "corrupt" or "truncated")
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(Run);
            }
            else if (scenario == "cancel")
            {
                await Assert.ThrowsAsync<OperationCanceledException>(Run);
            }
            else
            {
                await Run();
            }
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => sorter.AddAsync(99, default).AsTask());
            scratch = sorter.ScratchDirectory;
            Assert.IsTrue(budget.BytesInUse > 0);
            await sorter.DisposeAsync();
            Assert.AreEqual(0L, budget.BytesInUse);
        }
        Assert.IsNotNull(scratch);
        Assert.IsFalse(Directory.Exists(scratch));
        Assert.AreEqual(0L, budget.BytesInUse, "Repeated disposal must not release another operator's allowance.");

        string? recoveryScratch;
        await using (var recovered = Sorter(rows: 1, budget: budget))
        {
            // Reuse the same allowance after failure, forcing another merge pass.
            for (int value = 31; value >= 0; value--)
            {
                await recovered.AddAsync(value, default);
            }
            var values = new List<int>();
            await foreach (int value in recovered.ReadAsync())
            {
                values.Add(value);
            }
            CollectionAssert.AreEqual(Enumerable.Range(0, 32).ToArray(), values);
            recoveryScratch = recovered.ScratchDirectory;
        }
        Assert.AreEqual(0L, budget.BytesInUse);
        Assert.IsNotNull(recoveryScratch);
        Assert.IsFalse(Directory.Exists(recoveryScratch));
    }
}
