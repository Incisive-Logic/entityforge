using EntityForge.Query.Querying;
using EntityForge.Query.Values;
using EntityForge.Db.Semantic.Engine;
using System.Reflection;
using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic;
using EntityForge.Db.Storage.Pages;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public Task Semantic_historical_filtered_query_job_completes_before_later_append(bool fail, bool queued) =>
        CheckFilteredQueryAppendAsync(fail, queued, null);

    [TestMethod]
    [DataRow("before")]
    [DataRow("after")]
    [DataRow("unrelated")]
    public Task Semantic_fresh_filtered_query_job_retains_predicate_admission(string filterValue) =>
        CheckFilteredQueryAppendAsync(false, false, filterValue);

    [TestMethod]
    [DataRow("before", false)]
    [DataRow("after", false)]
    [DataRow("unrelated", false)]
    [DataRow("unrelated", true)]
    public Task Semantic_fresh_frontier_query_waits_for_durable_append(string filterValue, bool fail) =>
        CheckFilteredQueryAppendAsync(fail, false, filterValue, frontier: true);

    private async Task CheckFilteredQueryAppendAsync(bool fail, bool queued, string? freshFilter, bool frontier = false, bool fragment = false)
    {
        bool fresh = freshFilter is not null;
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "query-job-append"), DatabaseProfileId.Semantic,
            authority.Lease, Routing, authority, Options, timestampClock: ReadClock);
        var adapter = await node.CreateSemanticExecutionAsync();
        async Task Save(string value)
        {
            var capture = adapter.CaptureSave(new Item("one", value), SemanticEntityWriteOperation.Create());
            await adapter.ExecuteAsync(node.Domain, capture.OperationId, capture.Export());
        }
        await Save("before");
        using var http = new HttpClient();
        var store = new HttpSemanticEntityClient(http, node.Domain.Generation).EntityStore<Item>();
        var manifest = store.Manifests[0];
        string property = manifest.ProfileBindings.Single(p => p.MemberIdentity == manifest.Members.Single(m => m.ClrName == "Name").Identity).StoredBinding;
        var query = new PortableEntityQueryRequest(manifest.ProfileTypeBinding,
            new(manifest.ProfileTypeBinding, Fields: [new(property, "name")]),
            Filter: new("propertyComparison", property, "equal", new(JsonSerializer.SerializeToElement(freshFilter ?? "before"))),
            First: fragment ? null : 8, ConceptMatchMode: "direct");
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20));
        var time = TestQueryTime(cut);
        var log = typeof(DatabaseKernel).GetField("_commitLog", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node.Kernel)!;
        var stream = typeof(GroupedRedoDatabaseCommitLog).GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(log)!;
        using var pause = new PausedReadAdmissionWal { FailOnRelease = fail };
        pause.ShouldPause = () => ((DatabaseTransactionFootprint[]?)typeof(DatabaseKernel).GetField("_appendReadFootprints", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node.Kernel)) is { Length: > 0 } footprints &&
            footprints.Any(f => f.WritesAny(new HashSet<string> { SemanticReadResources.Entity(SemanticEntityReadDecoder.SemanticIdentity(store.DescribePartitionRead("one").Item2.Identity)) }));
        stream.GetType().GetField("_observer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(stream, pause);
        pause.Armed = true;
        var planningEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var planningRelease = new ManualResetEventSlim();
        var planningHook = (AsyncLocal<Action?>)typeof(SemanticDatabaseEngine).Assembly
            .GetType("EntityForge.Db.Semantic.Engine.SemanticCommittedChangePlanner")!
            .GetField("AfterProjection", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var writing = Task.Run(async () =>
        {
            if (queued)
            {
                planningHook.Value = () =>
            {
                planningEntered.TrySetResult();
                if (!planningRelease.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Planning was not released.");
                }
            };
            }

            try
            {
                await Save("after");
            }
            finally
            {
                planningHook.Value = null;
            }
        });
        Task<byte[]> StartRead(CancellationToken readToken = default, Guid? jobId = null) => node.ReadCutAsync(cut, new("query-job", ReadTime: time,
            QueryJob: new(jobId ?? Guid.NewGuid(), new("entity", EntityQuery: query, CompleteQuery: !frontier, ProfileFragments: fragment, SemanticFrontier: frontier && !fragment ? new(0) : null), Finish: true)), readToken).AsTask();
        Task<byte[]>? reading = null;
        try
        {
            if (queued)
            {
                await planningEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                reading = StartRead();
                Assert.IsFalse(reading.IsCompleted, "Query job must first queue behind actual planning.");
                planningRelease.Set();
            }
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(node.Kernel.TimestampCostState!.CompareTo(time) > 0);
            if (fresh)
            {
                time = node.Kernel.TimestampCostState!;
            }

            reading ??= StartRead();
            if (fresh)
            {
                await Task.Delay(75);
                Assert.IsFalse(reading.IsCompleted, "Fresh predicates use normal admission until the append finishes.");
            }
            else
            {
                var early = JsonSerializer.Deserialize<PartitionQueryJobResult>(await reading.WaitAsync(TimeSpan.FromMilliseconds(250)), PartitionParticipantWire.Json)!;
                StringAssert.Contains(early.Value!.Element.GetRawText(), "before");
            }
            Assert.IsFalse(writing.IsCompleted);
        }
        finally
        {
            planningRelease.Set();
            pause.Release.Set();
        }
        try
        {
            if (fail)
            {
                await Assert.ThrowsAsync<Exception>(async () => await writing.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.AreEqual(DatabaseKernelState.RecoveryRequired, node.Kernel.State);
                Assert.IsNull(typeof(DatabaseKernel).GetField("_appendReadTimes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node.Kernel));
            }
            else
            {
                await writing.WaitAsync(TimeSpan.FromSeconds(5));
            }

            if (fail && fresh)
            {
                await Assert.ThrowsAsync<Exception>(async () => await reading!.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.IsTrue(reading!.IsCompleted, "The failed append must terminate the waiting read, not merely time out the test.");
                return;
            }
            var result = JsonSerializer.Deserialize<PartitionQueryJobResult>(await reading!.WaitAsync(TimeSpan.FromSeconds(5)), PartitionParticipantWire.Json)!;
            Assert.IsTrue(result.Completed);
            if (!fresh)
            {
                StringAssert.Contains(result.Value!.Element.GetRawText(), "before");
            }
            else if (freshFilter == "after")
            {
                StringAssert.Contains(result.Value!.Element.GetRawText(), "after");
            }
            else
            {
                Assert.IsFalse(result.Value!.Element.GetRawText().Contains("before", StringComparison.Ordinal));
            }

            Assert.AreEqual(0, node.ActiveQueryJobCount);
        }
        finally
        {
            await node.AbandonCutAsync(cut.Id);
        }
    }
}
