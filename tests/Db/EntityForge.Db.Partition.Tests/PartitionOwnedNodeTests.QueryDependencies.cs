using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;
using EntityForge.Db.Durability;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public void Predicate_change_notifications_protect_readers_without_serialising_independent_writers()
    {
        var first = new DatabaseTransactionFootprint(new(1), ["row:a"], ["row:a"], ["column:value"]);
        var second = new DatabaseTransactionFootprint(new(1), ["row:b"], ["row:b"], ["column:value"]);
        var predicate = new DatabaseTransactionFootprint(new(1), ["column:value"], []);
        Assert.IsFalse(first.ConflictsWith(second));
        Assert.IsFalse(second.ConflictsWith(first));
        Assert.IsTrue(first.ConflictsWith(predicate));
        Assert.IsTrue(predicate.ConflictsWith(first));
        Assert.IsTrue(first.ConflictsWith(first));
        Assert.IsTrue(first.ConflictsWith(DatabaseTransactionFootprint.WholePartition));
        Assert.IsFalse(first.ConflictsWith(DatabaseTransactionFootprint.OperationMetadata));
        var empty = new DatabaseTransactionFootprint(new(1), [], []);
        Assert.IsTrue(empty.IsScopedPreparation);
        Assert.IsFalse(empty.ConflictsWith(first));
        Assert.IsFalse(new DatabaseTransactionFootprint(new(1), [], ["row:a"]).IsScopedPreparation);
        Assert.IsFalse(DatabaseTransactionFootprint.OperationMetadata.IsScopedPreparation);
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    public async Task Query_dependencies_allow_independent_preparations_and_preserve_conflicts_after_reopen(DatabaseProfileId profile, bool nativeHome)
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
        await using var control = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        var homes = new Dictionary<LogicalShardId, IPartitionCoordinatorHomeParticipant>();
        IPartitionTransactionAuthority authority = nativeHome ? new PartitionNativeHomeAuthority(control, routing, shard => homes[shard]) : control;
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile, a.Lease, routing, a, Options, transactionAuthority: authority, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), profile, b.Lease, routing, b, Options, transactionAuthority: authority, timestampClock: ReadClock);
        homes[first.Domain.Partition] = first;
        homes[second.Domain.Partition] = second;
        using var certificate = PartitionCertificate();
        await using var serviceA = new PartitionParticipantHttpService(first);
        await using var serviceB = new PartitionParticipantHttpService(second);
        await using var hostA = await StartPartitionHostAsync(serviceA, certificate);
        await using var hostB = await StartPartitionHostAsync(serviceB, certificate);
        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
        });
        var remoteA = new HttpPartitionTransactionParticipant(http, PartitionAddress(hostA), PartitionToken, first.Domain, authority.Identity);
        var remoteB = new HttpPartitionTransactionParticipant(http, PartitionAddress(hostB), PartitionToken, second.Domain, authority.Identity);
        homes[first.Domain.Partition] = remoteA;
        homes[second.Domain.Partition] = remoteB;
        var client = new PartitionApplicationClient(authority, routing, [remoteA, remoteB], ReadClock);
        var vertices = Enumerable.Range(0, 100).Select(i => new GraphVertexId(new Guid(i + 1, 0, 0, new byte[8])))
            .Where(id => routing.RouteGraphVertex(id.Value).Value == "a").Take(2).ToArray();
        var subjects = Enumerable.Range(0, 100).Select(i => new RdfIri($"urn:scope:{i}"))
            .Where(id => routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(id)).Value == "a").Take(2).ToArray();
        var number = new RdfIri("urn:number");
        var value = new RdfIri("urn:value");
        byte[] Write(Guid id, int row, string property, string text) => profile == DatabaseProfileId.PropertyGraph
            ? GraphTransactionCodec.Encode(new(id, [new SetVertexProperty(vertices[row], property, new DatabaseValue.String(text))]))
            : RdfTransactionCodec.Encode(new(id, [new AssertRdfQuad(new(subjects[row], new("urn:" + property), new RdfLiteral(text)))]));
        var seed = EntityOperationIds.New();
        byte[] seedPayload = profile == DatabaseProfileId.PropertyGraph
            ? GraphTransactionCodec.Encode(new(seed, vertices.SelectMany((id, row) => new GraphMutation[]
            {
                new CreateVertex(id, ["Item"]), new SetVertexProperty(id, "number", new DatabaseValue.String(row.ToString())),
                new SetVertexProperty(id, "value", new DatabaseValue.String("before"))
            }).ToArray()))
            : RdfTransactionCodec.Encode(new(seed, subjects.SelectMany((id, row) => new RdfMutation[]
            {
                new AssertRdfQuad(new(id, number, new RdfLiteral(row.ToString()))),
                new AssertRdfQuad(new(id, value, new RdfLiteral("before")))
            }).ToArray()));
        await first.Kernel.CommitWithReceiptAsync(seed, seedPayload);
        PartitionQueryWrite Request(Guid operation, int row, string updated = "after") => new(first.Domain.Generation, profile, operation,
            profile == DatabaseProfileId.PropertyGraph ? "gql" : "sparql", profile == DatabaseProfileId.PropertyGraph
                ? $"MATCH (n:Item) WHERE n.`number` = \"{row}\" SET n.`value` = \"{updated}\""
                : $"DELETE {{ ?s <urn:value> ?old }} INSERT {{ ?s <urn:value> \"{updated}\" }} WHERE {{ ?s <urn:number> \"{row}\" . ?s <urn:value> ?old }}");
        async Task<(PartitionApplicationBinding Application, PartitionTransactionPlan Plan)> Capture(PartitionQueryWrite request)
        {
            var captured = await PartitionRemoteRead.ExecuteAsync(new IPartitionReadParticipant[] { remoteA, remoteB }, routing,
                async (read, token) => new PartitionQueryUpdate.Evidence(read.Position,
                    [new(first.Domain.Identity, await read.ReadAsync("a", new("query-relation"), token)),
                        new(second.Domain.Identity, await read.ReadAsync("b", new("query-relation"), token))]), default, useSnapshots: true);
            var application = new PartitionApplicationBinding(request.Operation, [first.Domain.Identity, second.Domain.Identity],
                [PartitionQueryUpdate.Encode(request)], planningEvidence: JsonSerializer.SerializeToUtf8Bytes(await PartitionQueryUpdate.MaterializeAsync(request, captured.Value, routing, default), PartitionParticipantWire.Json));
            var members = new[]
            {
                await PartitionQueryUpdate.CompileAsync(application, first.Domain, routing, default),
                await PartitionQueryUpdate.CompileAsync(application, second.Domain, routing, default)
            };
            Assert.IsTrue(members.All(m => PartitionQueryUpdate.HasCurrentStateDependencies(profile, request.Operation, m.Payload.Span)));
            application = PartitionQueryUpdateEvidence.Narrow(application, routing, members);
            foreach (var member in members)
            {
                CollectionAssert.AreEqual(member.Payload.ToArray(), (await PartitionQueryUpdate.CompileAsync(application, member.Domain, routing, default)).Payload.ToArray());
            }

            return (application, new(request.Operation, routing.MapVersion, members));
        }
        async Task Select((PartitionApplicationBinding Application, PartitionTransactionPlan Plan) captured)
        {
            if (authority is PartitionNativeHomeAuthority combined)
            {
                await combined.SelectCombinedAsync(captured.Application, captured.Plan, default);
            }
            else
            {
                await authority.BindApplicationAsync(captured.Application);
                await authority.BindAsync(captured.Plan);
            }
        }
        // Both selections see exactly the same version and target distinct rows on
        // the same owner. Both must remain prepared at once, including over TLS.
        var left = Request(EntityOperationIds.New(), 0);
        var right = Request(EntityOperationIds.New(), 1);
        var leftCut = await Capture(left);
        var rightCut = await Capture(right);
        await Select(leftCut);
        await Select(rightCut);
        foreach (var plan in new[] { leftCut.Plan, rightCut.Plan })
        {
            foreach (var participant in new[] { remoteA, remoteB })
            {
                if (!nativeHome || participant.Domain.Partition != PartitionCoordinatorHome.Owner(plan.Operation, routing))
                {
                    await participant.PrepareNativeAsync(plan);
                }
            }
        }

        Assert.AreEqual(2, (await first.Kernel.PartitionTransactions.ReadAsync()).Count(p => p.State == DurablePartitionTransactionState.Prepared));
        // Snapshot capture must work while unrelated native preparations exist.
        var lease = await remoteA.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(5), default);
        await remoteA.CompleteCutAsync(lease, default);
        var collision = EntityOperationIds.New();
        await Assert.ThrowsExactlyAsync<DatabasePartitionConflictException>(() => first.Kernel.CommitWithReceiptAsync(collision, Write(collision, 0, "number", "99")).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.Kernel.CreateCheckpointAsync().AsTask());
        var receiptLeft = await client.UpdateQueryAsync(left, default);
        var receiptRight = await client.UpdateQueryAsync(right, default);
        Assert.IsTrue((await authority.ReadDecisionAsync(left.Operation))!.Commit);
        Assert.IsTrue((await authority.ReadDecisionAsync(right.Operation))!.Commit);
        // Current-state dependencies do not require the selected historical view.
        // An unrelated row changes, then checkpointing retires old read versions.
        var delayed = Request(EntityOperationIds.New(), 0, "delayed");
        var delayedCut = await Capture(delayed);
        var unrelated = EntityOperationIds.New();
        await first.Kernel.CommitWithReceiptAsync(unrelated, Write(unrelated, 1, "value", "unrelated"));
        await first.Kernel.CreateCheckpointAsync();
        await second.Kernel.CreateCheckpointAsync();
        await Select(delayedCut);
        await client.UpdateQueryAsync(delayed, default);
        using (var observed = JsonDocument.Parse(await first.ReadFrozenAsync(first.Kernel.CurrentVersion, new("query-relation"), default)))
        {
            if (profile == DatabaseProfileId.PropertyGraph)
            {
                var item = observed.RootElement.GetProperty("vertices").EnumerateArray().Single(v => v.GetProperty("id").GetProperty("value").GetGuid() == vertices[0].Value);
                Assert.AreEqual(new DatabaseValue.String("delayed"), GraphWireJson.Vertex(item, vertices[0]).Properties["value"]);
            }
            else
            {
                var actual = observed.RootElement.GetProperty("quads").EnumerateArray().Select(RdfWireJson.Quad).Where(q => q.Subject == subjects[0] && q.Predicate == value).ToArray();
                Assert.AreEqual(1, actual.Length);
                Assert.AreEqual(new RdfLiteral("delayed"), actual[0].Object);
            }
        }
        // The absence of a match is still a read. A previously unselected row
        // entering the selection must reject even an otherwise empty mutation.
        var phantom = Request(EntityOperationIds.New(), 99);
        var phantomCut = await Capture(phantom);
        var insertedMatch = EntityOperationIds.New();
        await first.Kernel.CommitWithReceiptAsync(insertedMatch, Write(insertedMatch, 1, "number", "99"));
        await Select(phantomCut);
        await Assert.ThrowsExactlyAsync<EntityOperationAbortedException>(() => client.UpdateQueryAsync(phantom, default).AsTask());
        Assert.IsFalse((await authority.ReadDecisionAsync(phantom.Operation))!.Commit);
        // A selected row changed before preparation is a definite conflict over
        // the protocol, not HTTP 500. Native home must durably retain its abort.
        Guid staleOperation;
        do
        {
            staleOperation = EntityOperationIds.New();
        }
        while (nativeHome && PartitionCoordinatorHome.Owner(staleOperation, routing) != first.Domain.Partition);
        var stale = Request(staleOperation, 0, "stale");
        var staleCut = await Capture(stale);
        var intervening = EntityOperationIds.New();
        await first.Kernel.CommitWithReceiptAsync(intervening, Write(intervening, 0, "value", "intervening"));
        await Select(staleCut);
        if (!nativeHome)
        {
            await Assert.ThrowsExactlyAsync<DatabasePartitionConflictException>(() => remoteA.PrepareNativeAsync(staleCut.Plan).AsTask());
        }

        await Assert.ThrowsExactlyAsync<EntityOperationAbortedException>(() => client.UpdateQueryAsync(stale, default).AsTask());
        Assert.IsFalse((await authority.ReadDecisionAsync(stale.Operation))!.Commit);
        if (nativeHome)
        {
            Assert.IsNotNull((await first.Kernel.PartitionTransactions.ReadAsync()).Single(p => p.Operation == stale.Operation).Timestamp);
        }

        await serviceA.DisposeAsync();
        await serviceB.DisposeAsync();
        await hostA.StopAsync();
        await hostB.StopAsync();
        await first.DisposeAsync();
        await second.DisposeAsync();
        await using var reopenedA = await PartitionOwnedNode.OpenAsync(Path.Combine(_root, "a"), profile, a.Lease, routing, a, Options, transactionAuthority: authority, timestampClock: ReadClock);
        await using var reopenedB = await PartitionOwnedNode.OpenAsync(Path.Combine(_root, "b"), profile, b.Lease, routing, b, Options, transactionAuthority: authority, timestampClock: ReadClock);
        homes[reopenedA.Domain.Partition] = reopenedA;
        homes[reopenedB.Domain.Partition] = reopenedB;
        // Native-home recovery must finish before accepting new applications.
        if (nativeHome)
        {
            await Task.WhenAll(reopenedA.CompleteNativeHomeRecoveryAsync().AsTask(), reopenedB.CompleteNativeHomeRecoveryAsync().AsTask());
        }

        var recovered = new PartitionApplicationClient(authority, routing, [reopenedA, reopenedB], ReadClock);
        var replay = await recovered.UpdateQueryAsync(right, default);
        Assert.IsTrue(receiptRight.Position.Covers(replay.Position) && replay.Position.Covers(receiptRight.Position));
        await Assert.ThrowsExactlyAsync<EntityOperationAbortedException>(() => recovered.UpdateQueryAsync(stale, default).AsTask());
    }
}
