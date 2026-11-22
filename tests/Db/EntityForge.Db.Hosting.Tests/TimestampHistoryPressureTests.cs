using System.Collections.Immutable;
using EntityForge.Db.Durability;
using EntityForge.Db.Kernel;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class TimestampHistoryPressureTests
{
    [TestMethod]
    public void Evidence_budget_is_reserved_before_commit_and_released_by_abandon_or_trim()
    {
        Guid domain = Guid.NewGuid(), first = Guid.NewGuid(), second = Guid.NewGuid();
        DurableTimestampState Time(long value) => new(domain, value, 0, 0, true);
        var history = new DatabaseLogicalHistory<string, string>("base", Time(0), (_, value) => value);
        const long charge = 200L * 1024 * 1024;
        history.Reserve(first, charge);
        Assert.ThrowsExactly<InvalidOperationException>(() => history.Reserve(second, charge));
        history.Abandon(first);
        history.Reserve(second, charge);
        history.Publish(second, Time(1), "committed");
        Assert.AreEqual(charge, history.RetainedBytes);
        Assert.ThrowsExactly<InvalidOperationException>(() => history.Reserve(first, charge));
        history.Trim(Time(1));
        history.Reserve(first, charge);
        history.Reserve(first, charge / 2); // same candidate replaces, not double-charges
        history.Publish(first, Time(2), "next");
        Assert.AreEqual(charge / 2, history.RetainedBytes);
    }

    [TestMethod]
    public void Repeated_history_reclamation_retains_unchanged_base_keys_and_tombstones()
    {
        Guid domain = Guid.NewGuid();
        DurableTimestampState Time(long value) => new(domain, value, 0, 0, true);
        var history = new DatabaseLogicalHistory<ImmutableDictionary<int, int>, (int Key, int? Value)>(
            ImmutableDictionary<int, int>.Empty.Add(0, 17), Time(0),
            (before, effect) => effect.Value is int value ? before.SetItem(effect.Key, value) : before.Remove(effect.Key));
        for (int epoch = 0; epoch < 100; epoch++)
        {
            long start = epoch * 100;
            for (int item = 1; item <= 100; item++)
            {
                history.RequireCapacity(1);
                history.Publish(Guid.NewGuid(), Time(start + item), (item, item % 2 == 0 ? null : epoch));
            }
            var protectedView = history.Read(Time(start + 50));
            history.Trim(Time(start + 50));
            Assert.AreEqual(50, history.Count);
            Assert.AreEqual(17, history.Read(Time(start + 100))[0]);
            Assert.IsFalse(history.Read(Time(start + 100)).ContainsKey(2));
            Assert.AreEqual(protectedView, history.Read(Time(start + 50)));
            history.Trim(Time(start + 100));
            Assert.AreEqual(0, history.Count);
        }
    }

    [TestMethod]
    public void Full_protected_history_rejects_before_publication_and_recovers_capacity_after_drainage()
    {
        Guid domain = Guid.NewGuid();
        DurableTimestampState Time(long value) => new(domain, value, 0, 0, true);
        var history = new DatabaseLogicalHistory<string, string>("base", Time(0), (_, value) => value);
        const int capacity = DatabaseLogicalHistory<string, string>.MaximumVersions;
        for (int item = 1; item <= capacity; item++)
        {
            history.RequireCapacity(1);
            history.Publish(Guid.NewGuid(), Time(item), item.ToString());
        }
        Assert.ThrowsExactly<InvalidOperationException>(() => history.RequireCapacity(1));
        Assert.AreEqual("base", history.Read(Time(0)));
        history.Trim(Time(capacity));
        history.RequireCapacity(1);
        history.Publish(Guid.NewGuid(), Time(capacity + 1), "next");
        Assert.AreEqual(capacity.ToString(), history.Read(Time(capacity)));
        Assert.AreEqual("next", history.Read(Time(capacity + 1)));
    }
}
