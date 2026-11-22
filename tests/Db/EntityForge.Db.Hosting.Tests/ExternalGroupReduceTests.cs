using System.Buffers.Binary;
using EntityForge.Db.Query.Planning;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class ExternalGroupReduceTests
{
    private sealed class State(int key)
    {
        internal int Key { get; } = key;

        internal int Count
        {
            get; set;
        }

        internal int Last { get; set; } = -1;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Group_reduction_preserves_sequence_across_runs_and_releases_storage(bool stopEarly)
    {
        var budget = new ExternalSortDiskBudget(4 * 1024 * 1024);
        await using (var groups = new ExternalGroupReduce<int, State>(value => (value % 4).ToString(),
            (value, stream) =>
            {
                Span<byte> bytes = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
                stream.Write(bytes);
            }, bytes => BinaryPrimitives.ReadInt32LittleEndian(bytes), value => new(value % 4),
            (state, value) =>
            {
                Assert.AreEqual(state.Key, value % 4);
                Assert.IsTrue(value > state.Last);
                state.Last = value;
                state.Count++;
            }, budget))
        {
            for (int index = 0; index < 12000; index++)
            {
                await groups.AddAsync(index, default);
            }
            int count = 0;
            await foreach (var group in groups.ReadAsync(default))
            {
                Assert.AreEqual(3000, group.Count);
                count++;
                if (stopEarly)
                {
                    break;
                }
            }
            Assert.AreEqual(stopEarly ? 1 : 4, count);
        }
        Assert.AreEqual(0L, budget.BytesInUse);
    }
}
