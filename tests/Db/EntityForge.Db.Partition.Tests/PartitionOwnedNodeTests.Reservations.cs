using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    private const string ReservationDescriptor = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [TestMethod]
    public async Task Empty_graph_participant_survives_unrelated_writes_and_recovery()
    {
        string path = Path.Combine(_root, "empty-reservation");
        Guid operation = EntityOperationIds.New();
        var opened = await OpenReservationStore(DatabaseProfileId.PropertyGraph, path);
        await using (opened.Database)
        {
            var version = opened.Kernel.CurrentVersion;
            var payload = GraphTransactionCodec.Encode(new(operation, [], version, []));
            Guid seed = Guid.NewGuid();
            await opened.Kernel.CommitAsync(seed, ReservationPayload(DatabaseProfileId.PropertyGraph, seed, [Guid.NewGuid()], null, create: true));
            await opened.Kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, version, payload);
            Guid unrelated = Guid.NewGuid();
            await opened.Kernel.CommitAsync(unrelated, ReservationPayload(DatabaseProfileId.PropertyGraph, unrelated, [Guid.NewGuid()], null, create: true));
        }
        opened = await OpenReservationStore(DatabaseProfileId.PropertyGraph, path);
        await using (opened.Database)
        {
            Assert.AreEqual(1, (await opened.Kernel.PartitionTransactions.ReadAsync()).Count(p => p.State == DurablePartitionTransactionState.Prepared));
            Guid unrelated = Guid.NewGuid();
            await opened.Kernel.CommitAsync(unrelated, ReservationPayload(DatabaseProfileId.PropertyGraph, unrelated, [Guid.NewGuid()], null, create: true));
            Assert.IsNotNull(await opened.Kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, true));
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Independent_reservations_and_native_writes_survive_advanced_checkpoint_and_reopen(DatabaseProfileId profile)
    {
        string path = Path.Combine(_root, "scoped-reservations");
        Guid a = EntityOperationIds.New(), b = EntityOperationIds.New();
        Guid[] subjects = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        var opened = await OpenReservationStore(profile, path);
        byte[] first, second;
        await using (opened.Database)
        {
            var kernel = opened.Kernel;
            Guid seed = Guid.NewGuid();
            await kernel.CommitAsync(seed, ReservationPayload(profile, seed, subjects, null, create: true));
            var version = kernel.CurrentVersion;
            first = ReservationPayload(profile, a, [subjects[0]], version);
            second = ReservationPayload(profile, b, [subjects[1]], version);
            await kernel.PartitionTransactions.PrepareAsync(a, ReservationDescriptor, version, first);
            Guid unrelated = Guid.NewGuid();
            await kernel.CommitAsync(unrelated, ReservationPayload(profile, unrelated, [subjects[2]], null));
            // The second selection predates an unrelated native write and the next checkpoint.
            await kernel.PartitionTransactions.PrepareAsync(b, ReservationDescriptor, version, second);
            Assert.AreEqual(2, (await kernel.PartitionTransactions.ReadAsync()).Count(p => p.State == DurablePartitionTransactionState.Prepared));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => kernel.CreateCheckpointAsync().AsTask());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => kernel.PartitionTransactions.FreezeAsync().AsTask());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => kernel.PartitionTransactions.AdmitReadAsync().AsTask());
            await AssertReservationConflict(kernel, profile, subjects[0]);
            await AssertReservationConflict(kernel, profile, subjects[1]);
        }
        opened = await OpenReservationStore(profile, path);
        await using (opened.Database)
        {
            var kernel = opened.Kernel;
            Assert.AreEqual(2L, kernel.CurrentVersion.Value);
            var records = await kernel.PartitionTransactions.ReadAsync();
            CollectionAssert.AreEqual(first, records.Single(p => p.Operation == a).Payload.ToArray());
            CollectionAssert.AreEqual(second, records.Single(p => p.Operation == b).Payload.ToArray());
            await AssertReservationConflict(kernel, profile, subjects[0]);
            var receipt = await kernel.PartitionTransactions.CompleteAsync(a, ReservationDescriptor, true);
            Assert.AreEqual(3L, receipt!.Version.Value);
            await AssertReservationConflict(kernel, profile, subjects[1]);
            Guid independent = Guid.NewGuid();
            await kernel.CommitAsync(independent, ReservationPayload(profile, independent, [subjects[0]], null));
            await kernel.PartitionTransactions.CompleteAsync(b, ReservationDescriptor, false);
            var replay = await kernel.PartitionTransactions.ResolveAsync(a, ReservationDescriptor);
            Assert.AreEqual(receipt.Version, replay!.Version);
            Assert.AreEqual(receipt.TransactionId, replay.TransactionId);
            Assert.AreEqual(receipt.ChangeCursor, replay.ChangeCursor);
            Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Outcome);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => kernel.PartitionTransactions.CompleteAsync(b, ReservationDescriptor, true).AsTask());
            await kernel.CreateCheckpointAsync();
            Assert.AreEqual(DatabaseKernelState.Ready, kernel.State);
        }
        opened = await OpenReservationStore(profile, path);
        await using (opened.Database)
        {
            Assert.AreEqual(3L, (await opened.Kernel.PartitionTransactions.ResolveAsync(a, ReservationDescriptor))!.Version.Value);
            Assert.AreEqual(DurablePartitionTransactionState.Aborted, (await opened.Kernel.PartitionTransactions.ReadAsync()).Single(p => p.Operation == b).State);
            for (int index = 0; index < subjects.Length; index++)
            {
                string expected = index == 1 ? "seed" : "changed";
                if (opened.Database is IGraphDatabase graph)
                {
                    Assert.AreEqual(new DatabaseValue.String(expected), graph.GetVertex(new(subjects[index]))!.Properties["name"]);
                }
                else
                {
                    Assert.AreEqual(new RdfLiteral(expected), ((IRdfDatabase)opened.Database).Find(
                        new(Subject: new RdfIri("urn:reservation:" + subjects[index].ToString("N")))).Single().Object);
                }
            }
        }
    }

    private static async Task AssertReservationConflict(DatabaseKernel kernel, DatabaseProfileId profile, Guid subject)
    {
        Guid conflicting = Guid.NewGuid();
        byte[] payload = ReservationPayload(profile, conflicting, [subject], null);
        await Assert.ThrowsExactlyAsync<DatabasePartitionConflictException>(() => kernel.CommitAsync(conflicting, payload).AsTask());
        await Assert.ThrowsExactlyAsync<DatabaseCommitGroupPreparationException>(() =>
            kernel.CommitGroupWithReceiptsAsync([new(conflicting, payload)]).AsTask());
        Assert.AreEqual(DatabaseKernelState.Ready, kernel.State);
    }

    private static async ValueTask<(IAsyncDisposable Database, DatabaseKernel Kernel)> OpenReservationStore(DatabaseProfileId profile, string path)
    {
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            var db = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal
            });
            return (db, ((GraphDatabaseEngine)db).PartitionKernel);
        }
        var rdf = await RdfDatabase.OpenLocalAsync(new(path)
        {
            VersioningMode = DatabaseVersioningMode.PartitionLocal
        });
        return (rdf, ((RdfDatabaseEngine)rdf).PartitionKernel);
    }

    private static byte[] ReservationPayload(DatabaseProfileId profile, Guid operation, Guid[] subjects,
        DatabaseVersion? readVersion, bool create = false)
    {
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            var mutations = subjects.SelectMany(id => create
                ? new GraphMutation[] { new CreateVertex(new(id), []), new SetVertexProperty(new(id), "name", new DatabaseValue.String("seed")) }
                : [new SetVertexProperty(new(id), "name", new DatabaseValue.String("changed"))]).ToArray();
            return GraphTransactionCodec.Encode(new(operation, mutations, readVersion,
                readVersion is null ? null : subjects.Select(id => (GraphReadPredicate)new WholeVertexRead(new(id))).ToArray()));
        }
        RdfQuad Quad(Guid id, string value) => new(new RdfIri("urn:reservation:" + id.ToString("N")), new RdfIri("urn:name"), new RdfLiteral(value));
        var quads = subjects.Select(id => Quad(id, "seed")).ToArray();
        return RdfTransactionCodec.Encode(new(operation,
            subjects.SelectMany(id => create ? new RdfMutation[] { new AssertRdfQuad(Quad(id, "seed")) }
                : [new RetractRdfQuad(Quad(id, "seed")), new AssertRdfQuad(Quad(id, "changed"))]).ToArray(),
            ReadVersion: readVersion, ReadPredicates: readVersion is null ? null : quads.Select(quad =>
                (RdfReadPredicate)RdfSubjectStateRead.Capture(quad.Subject, null, true, [quad])).ToArray()));
    }

    [TestMethod]
    public async Task Graph_read_reservations_share_reads_but_protect_absence_and_topology()
    {
        var opened = await OpenReservationStore(DatabaseProfileId.PropertyGraph, Path.Combine(_root, "read-reservations"));
        await using var db = opened.Database;
        var kernel = opened.Kernel;
        var absent = GraphVertexId.New();
        Guid a = EntityOperationIds.New(), b = EntityOperationIds.New();
        foreach (Guid id in new[] { a, b })
        {
            await kernel.PartitionTransactions.PrepareAsync(id, ReservationDescriptor, new(0),
                GraphTransactionCodec.Encode(new(id, [], new(0), [new VertexRead(absent), new OutgoingAdjacencyRead(absent)])));
        }

        Guid create = Guid.NewGuid();
        await Assert.ThrowsExactlyAsync<DatabasePartitionConflictException>(() => kernel.CommitAsync(create,
            GraphTransactionCodec.Encode(new(create, [new CreateVertex(absent, [])]))).AsTask());
        Guid topology = Guid.NewGuid();
        await Assert.ThrowsExactlyAsync<DatabasePartitionConflictException>(() => kernel.CommitAsync(topology,
            GraphTransactionCodec.Encode(new(topology, [new DeleteEdge(GraphEdgeId.New())]))).AsTask());
        await kernel.PartitionTransactions.CompleteAsync(a, ReservationDescriptor, false);
        await Assert.ThrowsExactlyAsync<DatabasePartitionConflictException>(() => kernel.CommitAsync(create,
            GraphTransactionCodec.Encode(new(create, [new CreateVertex(absent, [])]))).AsTask());
        await kernel.PartitionTransactions.CompleteAsync(b, ReservationDescriptor, true);
        await kernel.CommitAsync(create, GraphTransactionCodec.Encode(new(create, [new CreateVertex(absent, [])])));
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    public async Task Mapped_two_owner_reservations_share_shards_and_allow_unrelated_bound_saves(DatabaseProfileId profile, bool liveEtcd)
    {
        const int operations = 8;
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var a = new Authority();
        var b = new Authority
        {
            Lease = a.Lease with
            {
                Placement = a.Lease.Placement with
                {
                    Claim = a.Lease.Placement.Claim with
                    {
                        Shard = new("b"),
                        Owner = new("second")
                    }
                }
            }
        };
        await using var lifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"),
            a.Lease.Placement.Claim.Generation.Value, profile, routing, liveEtcd);
        var decisions = lifetime.Authority;
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile, a.Lease, routing, a, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), profile, b.Lease, routing, b, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        var client = new PartitionApplicationClient(decisions, routing, [first, second], ReadClock);
        using var http = new HttpClient();
        byte[] Capture(string id, string value, Guid operation) => profile == DatabaseProfileId.PropertyGraph
            ? new HttpGraphEntityClient(http, client.Generation).EntityStore<Item>().CaptureSave(new(id, value), new(operation)).Export()
            : new HttpRdfEntityClient(http, client.Generation).EntityStore<Item>().CaptureSave(new(id, value), new(operation)).Export();
        var left = new List<string>();
        var right = new List<string>();
        for (int i = 0; left.Count <= operations || right.Count <= operations; i++)
        {
            Guid id = EntityOperationIds.New();
            string identity = i.ToString();
            var target = PartitionApplicationRouting.Owner(client.Generation, profile, routing, id, Capture(identity, "seed", id)).Value == "a" ? left : right;
            if (target.Count <= operations)
            {
                target.Add(identity);
            }
        }
        foreach (string item in left.Concat(right))
        {
            Guid id = EntityOperationIds.New();
            await client.DeliverAsync(id, Capture(item, "seed", id));
        }
        var selected = Enumerable.Range(0, operations).Select(index =>
        {
            Guid id = EntityOperationIds.New();
            return (Id: id, Requests: new ReadOnlyMemory<byte>[] { Capture(left[index], "pair", id), Capture(right[index], "pair", id) });
        }).ToArray();
        foreach (var request in selected)
        {
            Assert.AreEqual(EntityOperationState.PlanBound, (await client.PrepareAsync(request.Id, request.Requests, CancellationToken.None)).State);
        }

        Assert.AreEqual(operations, (await first.Kernel.PartitionTransactions.ReadAsync()).Count(p => p.State == DurablePartitionTransactionState.Prepared));
        Guid unrelated = EntityOperationIds.New();
        await client.DeliverAsync(unrelated, Capture(left[^1], "unrelated", unrelated));
        Guid conflicting = PartitionOwnerOperation.New(routing, new("a"));
        await Assert.ThrowsExactlyAsync<EntityOperationAbortedException>(() => client.DeliverAsync(conflicting, Capture(left[0], "conflict", conflicting)).AsTask());
        var results = await Task.WhenAll(selected.Select(request => client.DeliverAsync(request.Id, request.Requests).AsTask()));
        Assert.IsTrue(results.All(result => result.Members.Count == 2));
        Assert.IsFalse((await first.Kernel.PartitionTransactions.ReadAsync()).Any(p => p.State == DurablePartitionTransactionState.Prepared));
        foreach (var request in selected)
        {
            Assert.IsTrue((await client.DeliverAsync(request.Id, request.Requests)).Members.All(member => member.Receipt.Outcome == DatabaseCommitOutcome.IdempotentReplay));
        }

        await first.Kernel.CreateCheckpointAsync();
        await second.Kernel.CreateCheckpointAsync();
    }

    [TestMethod]
    public async Task Transfer_preserves_multiple_scoped_preparations()
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var a = new Authority();
        var b = new Authority
        {
            Lease = a.Lease with
            {
                Placement = a.Lease.Placement with
                {
                    Claim = a.Lease.Placement.Claim with
                    {
                        Shard = new("b"),
                        Owner = new("second")
                    }
                }
            }
        };
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.PropertyGraph,
            a.Lease, routing, a, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.PropertyGraph,
            b.Lease, routing, b, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        byte[] Payload(Guid id, LogicalShardId owner)
        {
            var native = GraphTransactionCodec.Decode(id, NativePayload(DatabaseProfileId.PropertyGraph, routing, owner, id));
            var root = ((CreateVertex)native.Mutations[0]).Vertex;
            return GraphTransactionCodec.Encode(new(id, native.Mutations, new(0), [new VertexRead(root)]));
        }
        var plans = Enumerable.Range(0, 2).Select(_ =>
        {
            Guid id = EntityOperationIds.New();
            return new PartitionTransactionPlan(id, routing.MapVersion,
                [new(first.Domain, new(0), Payload(id, new("a"))), new(second.Domain, new(0), Payload(id, new("b")))]);
        }).ToArray();
        foreach (var plan in plans)
        {
            await decisions.BindAsync(plan);
            await first.PrepareNativeAsync(plan);
            await second.PrepareNativeAsync(plan);
        }
        string imagePath = Path.Combine(_root, "image");
        var image = await first.SealAndExportAsync(new(Guid.NewGuid()), imagePath);
        await using var transfer = new PartitionOwnedTransfer(imagePath, Path.Combine(_root, "destination"), image, routing, a,
            new("destination"), new("https://localhost:22001"), Options, decisions, timestampClock: ReadClock);
        var destination = await transfer.CompleteAsync();
        Assert.AreEqual(2, (await destination.Kernel.PartitionTransactions.ReadAsync()).Count(p => p.State == DurablePartitionTransactionState.Prepared));
        foreach (var plan in plans)
        {
            Assert.IsTrue((await new PartitionTransactionCoordinator(decisions).ExecuteAsync(plan, [destination, second])).Decision.Commit);
        }

        Assert.AreEqual(2L, destination.Kernel.CurrentVersion.Value);
    }
}
