using System.Diagnostics;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Stale_startup_claim_is_rejected_before_native_storage_is_created(DatabaseProfileId profile)
    {
        var authority = new Authority { RejectRenewal = true };
        string path = Path.Combine(_root, "stale-startup");
        await Assert.ThrowsExactlyAsync<StaleShardPlacementException>(async () =>
            await PartitionOwnedNode.CreateAsync(path, profile, authority.Lease, Routing, authority, Options));
        Assert.IsFalse(Directory.Exists(Path.Combine(path, PartitionOwnedNode.NativeDirectory)),
            "An unrenewable claim must not begin native recovery or provision storage.");
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Semantic, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    [DataRow(DatabaseProfileId.Semantic, true)]
    public async Task Native_startup_remains_supervised_and_unpublished_during_a_long_pause(DatabaseProfileId profile, bool loseAuthority)
    {
        var authority = new Authority();
        var options = new ShardPlacementLeaseSupervisorOptions(TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100));
        string path = Path.Combine(_root, "paused-startup");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "EntityForge.Distributed",
            Sample = (ref ActivityCreationOptions<ActivityContext> activity) =>
                activity.Name == "participant.native-recovery" ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            ActivityStarted = activity =>
            {
                if (activity.OperationName != "participant.native-recovery")
                {
                    return;
                }

                started.TrySetResult();
                // The listener is a deterministic test-only pause at a real recovery
                // stage. Production recovery remains asynchronous and cancellable.
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Recovery test gate timed out.");
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        Task<PartitionOwnedNode> opening = Task.Run(async () =>
            await PartitionOwnedNode.CreateAsync(path, profile, authority.Lease, Routing, authority, options));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (loseAuthority)
            {
                authority.RejectRenewal = true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            Assert.IsFalse(opening.IsCompleted, "No node may be exposed before native recovery completes.");
            Assert.IsFalse(File.Exists(Path.Combine(path, PartitionOwnedNode.EnrollmentFile)));
            if (!loseAuthority)
            {
                Assert.IsTrue(authority.Renewals > 1, "The original lease window must be renewed during recovery.");
            }
        }
        finally
        {
            release.Set();
        }
        if (loseAuthority)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await opening);
        }
        else
        {
            await using var node = await opening;
            Assert.AreEqual(authority.Lease.Placement.Claim, node.CurrentLease.Placement.Claim);
        }
    }
}
