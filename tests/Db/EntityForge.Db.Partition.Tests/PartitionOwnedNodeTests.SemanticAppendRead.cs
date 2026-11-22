using System.Reflection;
using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, false, true)]
    [DataRow(true, true, true)]
    public async Task Semantic_field_projection_completes_during_unrelated_bound_append(bool batch, bool fail, bool queued)
    {
        var placement = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "semantic-append-read"), DatabaseProfileId.Semantic,
            placement.Lease, Routing, placement, Options, timestampClock: ReadClock);
        var adapter = await node.CreateSemanticExecutionAsync();
        async Task Save(string id, string value)
        {
            var request = adapter.CaptureSave(new Item(id, value), SemanticEntityWriteOperation.Create());
            await adapter.ExecuteAsync(node.Domain, request.OperationId, request.Export());
        }
        await Save("one", "preserved-value");
        await Save("two", "before");
        using var http = new HttpClient();
        var producer = new HttpSemanticEntityClient(http, node.Domain.Generation).EntityStore<Item>();
        var (_, recipe) = producer.DescribePartitionRead("one");
        var identity = SemanticEntityReadDecoder.SemanticIdentity(recipe.Identity);
        var fields = recipe.Shapes[0].Fields!.Value.Deserialize<SemanticReadField[]>(EntityForge.Db.Mapping.EntityReadRecipe.Json)!;
        var query = new PartitionReadQuery("semantic-project", Subject: JsonSerializer.Serialize(new SemanticProjectionRead(identity, fields), PartitionParticipantWire.Json));
        if (batch)
        {
            query = new PartitionReadQuery("batch", Batch: new PartitionReadBatch([], [query]));
        }

        var (_, conflictingRecipe) = producer.DescribePartitionRead("two");
        var conflictingQuery = new PartitionReadQuery("semantic-project", Subject: JsonSerializer.Serialize(
            new SemanticProjectionRead(SemanticEntityReadDecoder.SemanticIdentity(conflictingRecipe.Identity), fields), PartitionParticipantWire.Json));
        if (batch)
        {
            conflictingQuery = new PartitionReadQuery("batch", Batch: new PartitionReadBatch([], [query.Batch!.Queries[0], conflictingQuery]));
        }

        using var pin = await node.Kernel.PartitionTransactions.ProtectTimestampHistoryAsync();
        async Task<string> Read() => System.Text.Encoding.UTF8.GetString((await node.ReadTimestampProjectionAsync(pin,
            PartitionTimestampClock.Read(ReadClock.Domain, ReadClock), pin.Position, query, default)).Encode());
        var log = typeof(DatabaseKernel).GetField("_commitLog", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node.Kernel)!;
        var stream = typeof(GroupedRedoDatabaseCommitLog).GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(log)!;
        using var pause = new PausedReadAdmissionWal { FailOnRelease = fail };
        pause.ShouldPause = () =>
        {
            var footprints = (DatabaseTransactionFootprint[]?)typeof(DatabaseKernel).GetField("_appendReadFootprints",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node.Kernel);
            return footprints is { Length: > 0 } && footprints.Any(footprint => footprint.WritesAny(new HashSet<string>
            {
                SemanticReadResources.Entity(SemanticEntityReadDecoder.SemanticIdentity(conflictingRecipe.Identity))
            }));
        };
        stream.GetType().GetField("_observer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(stream, pause);
        StringAssert.Contains(await Read(), "preserved-value");
        using var disposedPin = await node.Kernel.PartitionTransactions.ProtectTimestampHistoryAsync();
        disposedPin.Dispose();
        long reservationForces = node.Kernel.TimestampReservationForces;
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
                await Save("two", "after");
            }
            finally
            {
                planningHook.Value = null;
            }
        });
        try
        {
            Task<string>[]? waitingReads = null;
            if (queued)
            {
                await planningEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                waitingReads = Enumerable.Range(0, 16).Select(_ => Read()).ToArray();
                Assert.IsTrue(waitingReads.All(read => !read.IsCompleted), "Every read must first queue behind planning.");
                planningRelease.Set();
            }
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (waitingReads is not null)
            {
                foreach (string result in await Task.WhenAll(waitingReads).WaitAsync(TimeSpan.FromMilliseconds(250)))
                {
                    StringAssert.Contains(result, "preserved-value");
                }
            }

            StringAssert.Contains(await Read().WaitAsync(TimeSpan.FromMilliseconds(250)), "preserved-value");
            Assert.IsFalse(writing.IsCompleted, "Read must complete before the write is forced.");
            Assert.AreEqual(reservationForces, node.Kernel.TimestampReservationForces,
                "Concurrent reads must use an existing durable reservation without forcing or extending it.");
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
                await node.ReadTimestampProjectionAsync(disposedPin, PartitionTimestampClock.Read(ReadClock.Domain, ReadClock),
                    pin.Position, query, default));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
                await node.ReadTimestampProjectionAsync(pin, PartitionTimestampClock.Read(ReadClock.Domain, ReadClock),
                    pin.Position, conflictingQuery, cancellation.Token));
        }
        finally
        {
            planningRelease.Set();
            pause.Release.Set();
        }
        if (fail)
        {
            await Assert.ThrowsAsync<Exception>(async () => await writing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsNull(typeof(DatabaseKernel).GetField("_appendReadFootprints", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node.Kernel));
            Assert.AreEqual(DatabaseKernelState.RecoveryRequired, node.Kernel.State);
            return;
        }
        await writing.WaitAsync(TimeSpan.FromSeconds(5));
        StringAssert.Contains(await Read(), "preserved-value");
    }
}
