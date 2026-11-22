using System.Text.Json;
using EntityForge.Db.Query.Planning;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class ExternalCountGroupsTests
{
    private sealed class Identity
    {
        public int Key
        {
            get; set;
        }

        public string Padding { get; set; } = String.Empty;
    }

    private sealed class IdentityComparer : IEqualityComparer<Identity>
    {
        public bool Equals(Identity? left, Identity? right) => ReferenceEquals(left, right) ||
            left is not null && right is not null && left.Key == right.Key && left.Padding == right.Padding;

        // Deliberate collisions exercise equality instead of relying on a hash match.
        public int GetHashCode(Identity value) => 0;
    }

    private static ExternalCountGroups<Identity> Create(ExternalSortDiskBudget budget, int groups = 4096, int bytes = 8 * 1024 * 1024, bool fast = false) =>
        new(value => value.Key.ToString(), (value, stream) => JsonSerializer.Serialize(stream, value),
            data => JsonSerializer.Deserialize<Identity>(data)!, budget, groups, bytes, fast ? new IdentityComparer() : null);

    [TestMethod]
    [DataRow(2, false, false)]
    [DataRow(4096, false, false)]
    [DataRow(2, true, false)]
    [DataRow(2, false, true)]
    [DataRow(4096, false, true)]
    [DataRow(2, true, true)]
    public async Task Partial_counts_merge_exactly_across_spills_and_release_storage(int bufferedGroups, bool stopEarly, bool fast)
    {
        var budget = new ExternalSortDiskBudget(4 * 1024 * 1024);
        await using (var counts = Create(budget, bufferedGroups, fast: fast))
        {
            for (int index = 0; index < 12003; index++)
            {
                await counts.AddAsync(new()
                {
                    Key = index % 17
                }, default);
            }
            var keys = new HashSet<int>();
            await foreach (var group in counts.ReadAsync(default))
            {
                Assert.IsTrue(keys.Add(group.Value.Key));
                Assert.AreEqual((long)(12002 - group.Value.Key) / 17 + 1, group.Count);
                if (stopEarly)
                {
                    break;
                }
            }
            Assert.AreEqual(stopEarly ? 1 : 17, keys.Count);
        }
        Assert.AreEqual(0L, budget.BytesInUse);
    }

    [TestMethod]
    public async Task Count_identity_lookup_owns_values_and_preserves_canonical_fallback_across_spills()
    {
        var budget = new ExternalSortDiskBudget(65536);
        int keyCalls = 0;
        await using (var counts = new ExternalCountGroups<Identity>(value =>
        {
            keyCalls++;
            return value.Key.ToString();
        }, (value, stream) => JsonSerializer.Serialize(stream, value),
            data => JsonSerializer.Deserialize<Identity>(data)!, budget, maximumGroups: 2,
            identityComparer: new IdentityComparer()))
        {
            var callerOwned = new Identity { Key = 3, Padding = "first" };
            await counts.AddAsync(callerOwned, default);
            callerOwned.Key = 7;
            await counts.AddAsync(new()
            {
                Key = 3,
                Padding = "first"
            }, default);
            Assert.AreEqual(1, keyCalls);
            // Finer identity equality must fall back to the canonical key, without
            // retaining an unbounded set of alternate representations.
            await counts.AddAsync(new()
            {
                Key = 3,
                Padding = "other"
            }, default);
            await counts.AddAsync(new()
            {
                Key = 3,
                Padding = "other"
            }, default);
            Assert.AreEqual(3, keyCalls);
            await counts.AddAsync(new()
            {
                Key = 9
            }, default);
            await counts.AddAsync(new()
            {
                Key = 11
            }, default);
            await counts.AddAsync(new()
            {
                Key = 3,
                Padding = "first"
            }, default);
            Assert.AreEqual(6, keyCalls);
            var actual = new Dictionary<int, long>();
            await foreach (var group in counts.ReadAsync(default))
            {
                actual.Add(group.Value.Key, group.Count);
            }
            Assert.AreEqual(3, actual.Count);
            Assert.AreEqual(5L, actual[3]);
            Assert.AreEqual(1L, actual[9]);
            Assert.AreEqual(1L, actual[11]);
        }
        Assert.AreEqual(0L, budget.BytesInUse);
    }

    [TestMethod]
    public async Task Count_groups_capture_identity_before_caller_mutation()
    {
        var budget = new ExternalSortDiskBudget(65536);
        await using var counts = Create(budget);
        var input = new Identity { Key = 3 };
        await counts.AddAsync(input, default);
        input.Key = 7;
        await counts.AddAsync(new()
        {
            Key = 3
        }, default);
        int rows = 0;
        await foreach (var group in counts.ReadAsync(default))
        {
            Assert.AreEqual(3, group.Value.Key);
            Assert.AreEqual(2L, group.Count);
            rows++;
        }
        Assert.AreEqual(1, rows);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Count_groups_refuse_oversized_identity_and_disk_exhaustion(bool fast)
    {
        var budget = new ExternalSortDiskBudget(1);
        await using (var counts = Create(budget, bytes: 128, fast: fast))
        {
            await Assert.ThrowsExactlyAsync<QuerySortBudgetException>(async () =>
                await counts.AddAsync(new()
                {
                    Key = 1,
                    Padding = new string('x', 1024)
                }, default));
        }
        await using (var counts = Create(budget, fast: fast))
        {
            await counts.AddAsync(new()
            {
                Key = 1
            }, default);
            await Assert.ThrowsExactlyAsync<QuerySortBudgetException>(async () =>
            {
                await foreach (var group in counts.ReadAsync(default))
                {
                    Assert.Fail("A disk-budget failure cannot yield completed counts.");
                }
            });
        }
        Assert.AreEqual(0L, budget.BytesInUse);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Count_groups_cancelled_append_releases_disk_and_rejects_further_appends(bool fast)
    {
        var budget = new ExternalSortDiskBudget(65536);
        await using (var counts = Create(budget, groups: 1, fast: fast))
        {
            await counts.AddAsync(new()
            {
                Key = 1
            }, default);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await counts.AddAsync(new()
                {
                    Key = 2
                }, cancellation.Token));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await counts.AddAsync(new()
                {
                    Key = 3
                }, default));
        }
        Assert.AreEqual(0L, budget.BytesInUse);
    }
}
