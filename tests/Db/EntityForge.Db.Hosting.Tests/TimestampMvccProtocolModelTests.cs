namespace EntityForge.Db.Hosting.Tests;

// Executable G0 specification, not a substitute for native WAL/profile tests.
[TestClass]
public sealed class TimestampMvccProtocolModelTests
{
    private sealed class Owner
    {
        internal long FutureFloor;
        internal readonly Dictionary<string, (string Key, long Minimum)> Intents = new();
        internal readonly Dictionary<string, (long Time, bool Commit)> Decisions = new();
        internal readonly Dictionary<string, List<(long Time, int Value)>> Versions = new();

        internal long Prepare(string operation, string key, long clock)
        {
            if (Intents.TryGetValue(operation, out var existing))
            {
                if (existing.Key != key)
                {
                    throw new InvalidOperationException("Changed identity.");
                }

                return existing.Minimum;
            }
            long minimum = Math.Max(clock, checked(FutureFloor + 1));
            Intents.Add(operation, (key, minimum));
            return minimum;
        }

        internal void Decide(string operation, long timestamp, bool commit)
        {
            if (timestamp < Intents[operation].Minimum)
            {
                throw new InvalidOperationException("Decision violates durable prepare promise.");
            }

            if (Decisions.TryGetValue(operation, out var existing) && existing != (timestamp, commit))
            {
                throw new InvalidOperationException("Terminal decision cannot change.");
            }

            Decisions[operation] = (timestamp, commit);
        }

        internal void Apply(string operation, int value)
        {
            var decision = Decisions[operation];
            var intent = Intents[operation];
            if (decision.Commit)
            {
                if (!Versions.TryGetValue(intent.Key, out var versions))
                {
                    Versions[intent.Key] = versions = new();
                }

                versions.Add((decision.Time, value));
            }
            // Actual implementation must atomically publish effects/conflicts and
            // retire the reservation; this model performs one indivisible transition.
            Intents.Remove(operation);
        }

        internal int Read(string key, long timestamp, long uncertaintyLimit)
        {
            // Close the read point, not the uncertainty upper bound. Advancing
            // every write to the latter would introduce avoidable clock waits.
            FutureFloor = Math.Max(FutureFloor, timestamp);
            foreach (var intent in Intents)
            {
                if (intent.Value.Key != key || intent.Value.Minimum > uncertaintyLimit)
                {
                    continue;
                }

                if (!Decisions.TryGetValue(intent.Key, out var decision))
                {
                    throw new InvalidOperationException("Relevant decision unresolved.");
                }

                if (decision.Commit && decision.Time <= uncertaintyLimit)
                {
                    throw new InvalidOperationException("Relevant commit needs application or read restart.");
                }
            }
            if (!Versions.TryGetValue(key, out var versions))
            {
                return 0;
            }

            if (versions.Any(v => v.Time > timestamp && v.Time <= uncertaintyLimit))
            {
                throw new InvalidOperationException("Whole query must restart for uncertainty.");
            }

            return versions.Where(v => v.Time <= timestamp)
                .OrderByDescending(v => v.Time).Select(v => v.Value).FirstOrDefault();
        }
    }

    [TestMethod]
    public void EveryLegalTwoOwnerPrepareDecisionApplyReadScheduleAvoidsMixedResults()
    {
        int legal = 0, successful = 0, blocked = 0;
        foreach (int[] order in Permutations(Enumerable.Range(0, 7).ToArray()))
        {
            int Position(int step) => Array.IndexOf(order, step);
            if (Position(2) < Position(0) || Position(2) < Position(1) ||
                Position(3) < Position(2) || Position(4) < Position(2))
            {
                continue;
            }

            legal++;
            var a = new Owner();
            var b = new Owner();
            long minimumA = 0, minimumB = 0;
            int? readA = null, readB = null;
            bool failed = false;
            foreach (int step in order)
            {
                switch (step)
                {
                    case 0:
                        minimumA = a.Prepare("T", "key", 50);
                        break;
                    case 1:
                        minimumB = b.Prepare("T", "key", 50);
                        break;
                    case 2:
                        long commit = Math.Max(minimumA, minimumB);
                        a.Decide("T", commit, true);
                        b.Decide("T", commit, true);
                        break;
                    case 3:
                        a.Apply("T", 1);
                        break;
                    case 4:
                        b.Apply("T", 1);
                        break;
                    default:
                        try
                        {
                            if (step == 5)
                            {
                                readA = a.Read("key", 60, 60);
                            }
                            else
                            {
                                readB = b.Read("key", 60, 60);
                            }
                        }
                        catch (InvalidOperationException)
                        {
                            failed = true;
                        }
                        break;
                }
            }
            if (failed)
            {
                blocked++;
                continue;
            }
            successful++;
            Assert.AreEqual(readA, readB, string.Join(",", order));
        }
        Assert.AreEqual(168, legal);
        Assert.IsTrue(successful > 0 && blocked > 0);
    }

    [TestMethod]
    public void UnrelatedIntentDoesNotBlockAndCommittedUnappliedInsertIsNotAbsence()
    {
        var owner = new Owner();
        owner.Prepare("T", "inserted-key", 50);
        Assert.AreEqual(0, owner.Read("unrelated", 60, 60));
        owner.Decide("T", 50, true);
        Assert.ThrowsExactly<InvalidOperationException>(() => owner.Read("inserted-key", 60, 60));
        owner.Apply("T", 1);
        Assert.AreEqual(1, owner.Read("inserted-key", 60, 60));
    }

    [TestMethod]
    public void LaterPhysicalApplicationDoesNotChangeHistoricalVisibility()
    {
        var owner = new Owner();
        owner.Prepare("older", "a", 101);
        owner.Prepare("newer", "b", 102);
        owner.Decide("older", 101, true);
        owner.Decide("newer", 102, true);
        owner.Apply("newer", 2);
        owner.Apply("older", 1);
        Assert.AreEqual(1, owner.Read("a", 101, 101));
        Assert.AreEqual(0, owner.Read("b", 101, 101));
    }

    [TestMethod]
    public void ClockUncertaintyRestartsWholeQueryAndFutureAdmissionHonoursReadFence()
    {
        var owner = new Owner();
        owner.Prepare("T", "a", 103);
        owner.Decide("T", 103, true);
        owner.Apply("T", 1);
        Assert.ThrowsExactly<InvalidOperationException>(() => owner.Read("a", 100, 105));
        Assert.IsTrue(owner.Prepare("next", "b", 99) > 100);
        Assert.AreEqual(1, owner.Read("a", 105, 105));
    }

    [TestMethod]
    public void ReadFenceCannotPushAnExistingPrepareOrOverwriteItsCommit()
    {
        var owner = new Owner();
        long minimum = owner.Prepare("T", "a", 50);
        owner.Read("other", 100, 100);
        Assert.AreEqual(minimum, owner.Prepare("T", "a", 101));
        owner.Decide("T", 50, true);
        Assert.ThrowsExactly<InvalidOperationException>(() => owner.Decide("T", 50, false));
        Assert.ThrowsExactly<InvalidOperationException>(() => owner.Read("a", 100, 100));
    }

    [TestMethod]
    public void RecoveryMustRestorePreparedPromisesBeforeReadAdmission()
    {
        var original = new Owner();
        original.Prepare("T", "a", 50);
        original.Decide("T", 50, true);
        // Snapshot represents retained durable prepare + authoritative decision;
        // it deliberately has no physical effects yet.
        var recovered = new Owner();
        foreach (var item in original.Intents)
        {
            recovered.Intents.Add(item.Key, item.Value);
        }

        foreach (var item in original.Decisions)
        {
            recovered.Decisions.Add(item.Key, item.Value);
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => recovered.Read("a", 60, 60));
        recovered.Apply("T", 1);
        Assert.AreEqual(1, recovered.Read("a", 60, 60));
        // A deliberately broken recovery demonstrates the false-absence counterexample.
        Assert.AreEqual(0, new Owner().Read("a", 60, 60));
    }

    [TestMethod]
    public void RecoveryMustRestoreReadPromisesEvenWhenNoWriteWasCommitted()
    {
        var a = new Owner();
        var b = new Owner();
        Assert.AreEqual(0, a.Read("a", 60, 60));
        // A crashed after replying to the first branch of a still-running query.
        // A durable reserved ceiling must cover every read promise it issued.
        long durableReservedReadCeiling = 70;
        a = new Owner { FutureFloor = durableReservedReadCeiling };
        long commit = Math.Max(a.Prepare("T", "a", 50), b.Prepare("T", "b", 50));
        a.Decide("T", commit, true);
        b.Decide("T", commit, true);
        a.Apply("T", 1);
        b.Apply("T", 1);
        Assert.AreEqual(0, b.Read("b", 60, 60));

        // Resetting the promise to zero permits a later transaction below R and
        // produces the mixed result on the query's second branch.
        a = new Owner();
        b = new Owner();
        commit = Math.Max(a.Prepare("T", "a", 50), b.Prepare("T", "b", 50));
        a.Decide("T", commit, true);
        b.Decide("T", commit, true);
        a.Apply("T", 1);
        b.Apply("T", 1);
        Assert.AreEqual(1, b.Read("b", 60, 60));
    }

    private sealed class Protection
    {
        internal long Floor;
        internal long Generation = 1;
        internal bool Closed;
        internal int Active;
        internal long ProtectedAt;

        internal void Grant(long timestamp)
        {
            if (Closed || Active != 0 || timestamp < Floor)
            {
                throw new InvalidOperationException("Protection is not admissible.");
            }

            ProtectedAt = timestamp;
        }

        internal void Enter(long generation)
        {
            if (Closed || generation != Generation || ProtectedAt < Floor)
            {
                throw new InvalidOperationException("Expired protection.");
            }

            Active++;
        }

        internal void Reclaim(long floor)
        {
            if (!Closed || Active != 0 || floor < Floor)
            {
                throw new InvalidOperationException("Protection must close and drain first.");
            }

            Floor = floor;
            Generation++;
        }
    }

    [TestMethod]
    public void ProtectionClosureDrainsActiveUsersAndRejectsLateExpansion()
    {
        var protection = new Protection();
        protection.Grant(50);
        protection.Enter(1);
        protection.Closed = true; // expiry seals admission; it does not free an active pin
        Assert.ThrowsExactly<InvalidOperationException>(() => protection.Reclaim(100));
        Assert.ThrowsExactly<InvalidOperationException>(() => protection.Enter(1));
        protection.Active--;
        protection.Reclaim(100);
        protection.Closed = false;
        Assert.ThrowsExactly<InvalidOperationException>(() => protection.Grant(50));
        protection.Grant(100);
        Assert.ThrowsExactly<InvalidOperationException>(() => protection.Enter(1));
        protection.Enter(2);
    }

    [TestMethod]
    public void ReclamationRetainsBaseAndTombstoneAtReadFloor()
    {
        static (long Time, int? Value)[] Compact((long Time, int? Value)[] versions, long floor) =>
            versions.Where(v => v.Time > floor).Concat(versions.Where(v => v.Time <= floor)
                .OrderByDescending(v => v.Time).Take(1)).OrderBy(v => v.Time).ToArray();
        var unchanged = Compact(new (long, int?)[] { (20, 1), (50, 2) }, 100);
        Assert.AreEqual(1, unchanged.Length);
        Assert.AreEqual((50L, (int?)2), unchanged[0]);
        var deleted = Compact(new (long, int?)[] { (20, 1), (50, null), (120, 3) }, 100);
        Assert.AreEqual(2, deleted.Length);
        Assert.AreEqual((50L, (int?)null), deleted[0]);
    }

    private static IEnumerable<int[]> Permutations(int[] values)
    {
        if (values.Length == 0)
        {
            yield return Array.Empty<int>();
            yield break;
        }
        foreach (int value in values)
        {
            foreach (int[] suffix in Permutations(values.Where(v => v != value).ToArray()))
            {
                yield return new[] { value }.Concat(suffix).ToArray();
            }
        }
    }
}
