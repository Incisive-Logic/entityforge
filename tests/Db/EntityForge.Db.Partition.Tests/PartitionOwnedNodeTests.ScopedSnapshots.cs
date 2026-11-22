using EntityForge.Db.Rdf;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    private static DatabasePartitionReadScope ReadScope(DatabaseProfileId profile, params Guid[] subjects) => new(profile,
        subjects.Select(subject => profile == DatabaseProfileId.PropertyGraph ? subject.ToString("N") :
            RdfPhysicalCodec.ResourceIdentity(new RdfIri("urn:reservation:" + subject.ToString("N")))));

    [TestMethod]
    public async Task Scoped_capture_requires_explicit_profile_qualification_and_matching_identity()
    {
        var authority = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "scope-authority"));
        await using var semantic = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "scope-semantic"), DatabaseProfileId.Semantic,
            authority.Lease, Routing, authority, Options, transactionAuthority: decisions);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await semantic.Kernel.PartitionTransactions.CaptureScopedSnapshotAsync(new(DatabaseProfileId.Semantic, ["x"])));
        var opened = await OpenReservationStore(DatabaseProfileId.PropertyGraph, Path.Combine(_root, "scope-graph"));
        await using var graph = opened.Database;
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await opened.Kernel.PartitionTransactions.CaptureScopedSnapshotAsync(new(DatabaseProfileId.Rdf, ["x"])));
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Scoped_snapshot_accepts_unrelated_local_and_coordinated_writes_but_rejects_relevant_writes(DatabaseProfileId profile)
    {
        var opened = await OpenReservationStore(profile, Path.Combine(_root, "scoped-stability"));
        await using var db = opened.Database;
        var kernel = opened.Kernel;
        Guid[] subjects = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        Guid seed = EntityOperationIds.New();
        await kernel.CommitAsync(seed, ReservationPayload(profile, seed, subjects, null, create: true));
        using var full = await kernel.PartitionTransactions.CaptureSnapshotAsync();
        using var scoped = await kernel.PartitionTransactions.CaptureScopedSnapshotAsync(ReadScope(profile, subjects[0]));
        Guid local = EntityOperationIds.New();
        await kernel.CommitAsync(local, ReservationPayload(profile, local, [subjects[1]], null));
        Assert.IsFalse(await kernel.PartitionTransactions.ValidateSnapshotAsync(full.Evidence, []));
        Assert.IsTrue(await kernel.PartitionTransactions.ValidateScopedSnapshotAsync(scoped, []));
        Guid coordinated = EntityOperationIds.New();
        await kernel.PartitionTransactions.PrepareAsync(coordinated, ReservationDescriptor, kernel.CurrentVersion,
            ReservationPayload(profile, coordinated, [subjects[2]], kernel.CurrentVersion));
        Assert.IsTrue(await kernel.PartitionTransactions.ValidateScopedSnapshotAsync(scoped, []));
        await kernel.PartitionTransactions.CompleteAsync(coordinated, ReservationDescriptor, true);
        Assert.IsTrue(await kernel.PartitionTransactions.ValidateScopedSnapshotAsync(scoped, []));
        Guid relevant = EntityOperationIds.New();
        await kernel.CommitAsync(relevant, ReservationPayload(profile, relevant, [subjects[0]], null));
        Assert.IsFalse(await kernel.PartitionTransactions.ValidateScopedSnapshotAsync(scoped, []));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => kernel.PartitionTransactions.ValidateSnapshotAsync(scoped.Evidence, []).AsTask());
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    public async Task Scoped_snapshot_preserves_partial_commit_detection_on_queried_resources(DatabaseProfileId profile, bool related)
    {
        var a = await OpenReservationStore(profile, Path.Combine(_root, "scoped-a"));
        var b = await OpenReservationStore(profile, Path.Combine(_root, "scoped-b"));
        await using var da = a.Database;
        await using var db = b.Database;
        Guid[] subjects = [Guid.NewGuid(), Guid.NewGuid()];
        Guid operation = EntityOperationIds.New();
        foreach (var kernel in new[] { a.Kernel, b.Kernel })
        {
            Guid seed = EntityOperationIds.New();
            await kernel.CommitAsync(seed, ReservationPayload(profile, seed, subjects, null, create: true));
            await kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, kernel.CurrentVersion,
                ReservationPayload(profile, operation, [subjects[0]], kernel.CurrentVersion));
        }
        await a.Kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, true);
        var scope = ReadScope(profile, subjects[related ? 0 : 1]);
        using var sa = await a.Kernel.PartitionTransactions.CaptureScopedSnapshotAsync(scope);
        using var sb = await b.Kernel.PartitionTransactions.CaptureScopedSnapshotAsync(scope);
        var prepared = sa.Evidence.Prepared.Concat(sb.Evidence.Prepared).Distinct().ToArray();
        Assert.AreEqual(!related, await a.Kernel.PartitionTransactions.ValidateScopedSnapshotAsync(sa, prepared));
        Assert.AreEqual(!related, await b.Kernel.PartitionTransactions.ValidateScopedSnapshotAsync(sb, prepared));
        await b.Kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, true);
        Assert.AreEqual(!related, await b.Kernel.PartitionTransactions.ValidateScopedSnapshotAsync(sb, prepared));
        using var freshA = await a.Kernel.PartitionTransactions.CaptureScopedSnapshotAsync(scope);
        using var freshB = await b.Kernel.PartitionTransactions.CaptureScopedSnapshotAsync(scope);
        Assert.IsTrue(await a.Kernel.PartitionTransactions.ValidateScopedSnapshotAsync(freshA, []));
        Assert.IsTrue(await b.Kernel.PartitionTransactions.ValidateScopedSnapshotAsync(freshB, []));
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Scoped_snapshot_protects_absence_and_rejects_relevant_prepare_abort_transitions(DatabaseProfileId profile)
    {
        var opened = await OpenReservationStore(profile, Path.Combine(_root, "scoped-absence"));
        await using var db = opened.Database;
        var kernel = opened.Kernel;
        Guid subject = Guid.NewGuid();
        using var absent = await kernel.PartitionTransactions.CaptureScopedSnapshotAsync(ReadScope(profile, subject));
        Guid create = EntityOperationIds.New();
        await kernel.CommitAsync(create, ReservationPayload(profile, create, [subject], null, create: true));
        Assert.IsFalse(await kernel.PartitionTransactions.ValidateScopedSnapshotAsync(absent, []));
        using var before = await kernel.PartitionTransactions.CaptureScopedSnapshotAsync(ReadScope(profile, subject));
        Guid operation = EntityOperationIds.New();
        await kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, kernel.CurrentVersion,
            ReservationPayload(profile, operation, [subject], kernel.CurrentVersion));
        Assert.IsFalse(await kernel.PartitionTransactions.ValidateScopedSnapshotAsync(before, []));
        using var pending = await kernel.PartitionTransactions.CaptureScopedSnapshotAsync(ReadScope(profile, subject));
        Assert.IsFalse(await kernel.PartitionTransactions.ValidateScopedSnapshotAsync(pending, []));
        await kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, false);
        Assert.IsFalse(await kernel.PartitionTransactions.ValidateScopedSnapshotAsync(pending, []));
    }

    [TestMethod]
    public async Task Scoped_snapshot_allows_unrelated_topology_but_rejects_relevant_changes_recovery_and_disposal()
    {
        var opened = await OpenReservationStore(DatabaseProfileId.PropertyGraph, Path.Combine(_root, "scoped-global"));
        await using var db = opened.Database;
        var kernel = opened.Kernel;
        Guid subject = Guid.NewGuid();
        Guid existing = Guid.NewGuid(), seed = EntityOperationIds.New();
        await kernel.CommitAsync(seed, ReservationPayload(DatabaseProfileId.PropertyGraph, seed, [existing, subject], null, create: true));
        using var scope = await kernel.PartitionTransactions.CaptureScopedSnapshotAsync(ReadScope(DatabaseProfileId.PropertyGraph, subject));
        Guid topology = EntityOperationIds.New();
        await kernel.CommitAsync(topology, GraphTransactionCodec.Encode(new(topology, [new DeleteVertex(new(existing))])));
        Assert.IsTrue(await kernel.PartitionTransactions.ValidateScopedSnapshotAsync(scope, []), "An unrelated isolated vertex deletion is scoped.");
        Guid relevant = EntityOperationIds.New();
        await kernel.CommitAsync(relevant, GraphTransactionCodec.Encode(new(relevant, [new DeleteVertex(new(subject))])));
        Assert.IsFalse(await kernel.PartitionTransactions.ValidateScopedSnapshotAsync(scope, []));
        using var current = await kernel.PartitionTransactions.CaptureScopedSnapshotAsync(ReadScope(DatabaseProfileId.PropertyGraph, subject));
        current.Dispose();
        Assert.IsFalse(await kernel.PartitionTransactions.ValidateScopedSnapshotAsync(current, []));
        using var faulted = await kernel.PartitionTransactions.CaptureScopedSnapshotAsync(ReadScope(DatabaseProfileId.PropertyGraph, subject));
        kernel.RequireRecovery("scope-test");
        await Assert.ThrowsExactlyAsync<EntityForgeDbRecoveryRequiredException>(() => kernel.PartitionTransactions.ValidateScopedSnapshotAsync(faulted, []).AsTask());
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Scoped_pin_rejects_widened_queries_and_changed_scope_on_retry(DatabaseProfileId profile)
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "scoped-node"), profile, authority.Lease, Routing, authority, Options);
        PartitionReadQuery query = profile == DatabaseProfileId.PropertyGraph ? new("vertex", Guid.NewGuid()) : new("subject", Subject: "urn:scope:one");
        PartitionReadQuery other = profile == DatabaseProfileId.PropertyGraph ? new("vertex", Guid.NewGuid()) : new("subject", Subject: "urn:scope:other");
        Guid id = Guid.NewGuid();
        var expires = DateTimeOffset.UtcNow.AddSeconds(20);
        var cut = await node.AcquireScopedSnapshotCutAsync(id, expires, [query]);
        Assert.IsNotNull(cut.Scope);
        var summary = JsonSerializer.Deserialize<DatabasePartitionSnapshotSummary>(cut.SnapshotEvidence!)!;
        Assert.AreEqual(DatabasePartitionSnapshotSummary.ScopedFormat, summary.Format);
        Assert.AreEqual(cut.Scope, summary.Scope);
        Assert.IsTrue(JsonSerializer.Deserialize<bool>(await node.ReadCutAsync(cut, new("validate-snapshot", SnapshotPrepared: []))));
        _ = await node.ReadCutAsync(cut, query);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadCutAsync(cut, other).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadCutAsync(cut, Batch([other])).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadCutAsync(cut with { Scope = null }, query).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.AcquireScopedSnapshotCutAsync(id, expires, [other]).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.AcquireSnapshotCutAsync(id, expires).AsTask());
        await node.CompleteCutAsync(cut);
        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ReadCutAsync(cut, query).AsTask());
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Scoped_snapshot_floor_and_reopen_preserve_relevant_evidence(DatabaseProfileId profile)
    {
        string path = Path.Combine(_root, "scoped-reopen");
        Guid subject = Guid.NewGuid(), operation = EntityOperationIds.New();
        DatabasePartitionSnapshot old;
        var opened = await OpenReservationStore(profile, path);
        await using (opened.Database)
        {
            Guid seed = EntityOperationIds.New();
            await opened.Kernel.CommitAsync(seed, ReservationPayload(profile, seed, [subject], null, create: true));
            await opened.Kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, opened.Kernel.CurrentVersion,
                ReservationPayload(profile, operation, [subject], opened.Kernel.CurrentVersion));
            await opened.Kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, true);
            using (old = await opened.Kernel.PartitionTransactions.CaptureScopedSnapshotAsync(ReadScope(profile, subject)))
            {
                if (profile == DatabaseProfileId.PropertyGraph)
                {
                    await Assert.ThrowsExactlyAsync<GraphSnapshotLeaseException>(() => opened.Kernel.PartitionTransactions.RetainAsync(DateTimeOffset.UtcNow.AddMinutes(-1)).AsTask());
                }
                else
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => opened.Kernel.PartitionTransactions.RetainAsync(DateTimeOffset.UtcNow.AddMinutes(-1)).AsTask());
                }

                Assert.IsTrue(await opened.Kernel.PartitionTransactions.ValidateScopedSnapshotAsync(old, []));
            }
            await opened.Kernel.PartitionTransactions.RetainAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
            Assert.IsFalse(await opened.Kernel.PartitionTransactions.ValidateScopedSnapshotAsync(old, []));
        }
        opened = await OpenReservationStore(profile, path);
        await using (opened.Database)
        {
            using var current = await opened.Kernel.PartitionTransactions.CaptureScopedSnapshotAsync(ReadScope(profile, subject));
            Assert.AreEqual(0, current.Evidence.Committed.Count, "Scoped certification must not transport retained commitment history.");
            Assert.IsFalse(await opened.Kernel.PartitionTransactions.ValidateScopedSnapshotAsync(current, [operation]));
            Assert.IsTrue(await opened.Kernel.PartitionTransactions.ValidateScopedSnapshotAsync(current, []));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => opened.Kernel.PartitionTransactions.ValidateScopedSnapshotAsync(old, []).AsTask());
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    public async Task Scoped_https_projection_ignores_unrelated_partial_commit_but_waits_for_related_commit(DatabaseProfileId profile, bool live)
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
        await using var control = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"),
            a.Lease.Placement.Claim.Generation.Value, profile, routing, live);
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile, a.Lease, routing, a, Options, transactionAuthority: control.Authority);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), profile, b.Lease, routing, b, Options, transactionAuthority: control.Authority);
        using var certificate = PartitionCertificate();
        await using var serviceA = new PartitionParticipantHttpService(first);
        await using var serviceB = new PartitionParticipantHttpService(second);
        await using var hostA = await StartPartitionHostAsync(serviceA, certificate);
        await using var hostB = await StartPartitionHostAsync(serviceB, certificate);
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint });
        var remoteA = new HttpPartitionTransactionParticipant(http, PartitionAddress(hostA), PartitionToken, first.Domain, control.Authority.Identity);
        var remoteB = new HttpPartitionTransactionParticipant(http, PartitionAddress(hostB), PartitionToken, second.Domain, control.Authority.Identity);
        Guid ForOwner(string owner)
        {
            while (true)
            {
                var id = Guid.NewGuid();
                var shard = profile == DatabaseProfileId.PropertyGraph ? routing.RouteGraphVertex(id) :
                    routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(new RdfIri("urn:reservation:" + id.ToString("N"))));
                if (shard.Value == owner)
                {
                    return id;
                }
            }
        }
        var subjects = new[] { new[] { ForOwner("a"), ForOwner("a") }, new[] { ForOwner("b"), ForOwner("b") } };
        var nodes = new[] { first, second };
        Guid operation = EntityOperationIds.New();
        for (int i = 0; i < nodes.Length; i++)
        {
            int index = i;
            await nodes[i].Authority.ExecuteAsync(async () =>
            {
                var kernel = nodes[index].Kernel;
                Guid seed = EntityOperationIds.New();
                await kernel.CommitAsync(seed, ReservationPayload(profile, seed, subjects[index], null, create: true));
                await kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, kernel.CurrentVersion,
                    ReservationPayload(profile, operation, [subjects[index][0]], kernel.CurrentVersion));
                return true;
            });
        }
        await first.Authority.ExecuteAsync(async () =>
        {
            await first.Kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, true);
            return true;
        });
        Dictionary<string, PartitionReadQuery[]> Queries(int index) => new[] { "a", "b" }.Select((owner, i) => (owner, id: subjects[i][index]))
            .ToDictionary(x => x.owner, x => new[] { profile == DatabaseProfileId.PropertyGraph ? new PartitionReadQuery("vertex", x.id) : new("subject", Subject: "urn:reservation:" + x.id.ToString("N")) });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var unrelated = Queries(1);
        var old = await PartitionRemoteRead.ExecuteAsync([remoteA, remoteB], routing, async (read, token) =>
            new[] { await read.ReadAsync("a", unrelated["a"][0], token), await read.ReadAsync("b", unrelated["b"][0], token) }, timeout.Token, scopes: unrelated);
        Assert.IsTrue(old.Value.All(value => System.Text.Encoding.UTF8.GetString(value).Contains("seed", StringComparison.Ordinal)));
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrapper = new ScopedReadParticipant(remoteB) { Settling = () => waiting.TrySetResult() };
        var related = Queries(0);
        var task = PartitionRemoteRead.ExecuteAsync([remoteA, wrapper], routing, async (read, token) =>
            new[] { await read.ReadAsync("a", related["a"][0], token), await read.ReadAsync("b", related["b"][0], token) }, timeout.Token, scopes: related).AsTask();
        await waiting.Task.WaitAsync(timeout.Token);
        Assert.IsFalse(task.IsCompleted);
        await second.Authority.ExecuteAsync(async () =>
        {
            await second.Kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, true);
            return true;
        });
        var changed = await task;
        Assert.IsTrue(changed.Value.All(value => System.Text.Encoding.UTF8.GetString(value).Contains("changed", StringComparison.Ordinal)));
    }

    private sealed class ScopedReadParticipant(IPartitionReadParticipant inner) : IPartitionReadParticipant
    {
        public PartitionExecutionDomain Domain => inner.Domain;

        internal Action? Settling;
        internal string? FailureStage;
        internal readonly TaskCompletionSource<PartitionReadCut> Acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Abandoned;

        public ValueTask<PartitionReadCut> AcquireCutAsync(Guid id, DateTimeOffset expires, CancellationToken token = default) => inner.AcquireCutAsync(id, expires, token);

        public async ValueTask<PartitionReadCut> AcquireScopedSnapshotCutAsync(Guid id, DateTimeOffset expires, PartitionReadQuery[] queries, CancellationToken token = default)
        {
            var cut = await inner.AcquireScopedSnapshotCutAsync(id, expires, queries, token);
            Acquired.TrySetResult(cut);
            if (FailureStage == "acquire")
            {
                throw new IOException("Lost acquisition response");
            }

            if (FailureStage == "cancel")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            return cut;
        }

        public ValueTask<byte[]> ReadCutAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default)
        {
            if (query.Kind == "settle-snapshot")
            {
                Settling?.Invoke();
            }

            return inner.ReadCutAsync(cut, query, token);
        }

        public async ValueTask CompleteCutAsync(PartitionReadCut cut, CancellationToken token = default)
        {
            await inner.CompleteCutAsync(cut, token);
            if (FailureStage == "complete")
            {
                throw new IOException("Lost completion response");
            }
        }

        public ValueTask AbandonCutAsync(Guid id, CancellationToken token = default)
        {
            Interlocked.Increment(ref Abandoned);
            return inner.AbandonCutAsync(id, token);
        }
    }

    [TestMethod]
    [DataRow("acquire")]
    [DataRow("complete")]
    [DataRow("cancel")]
    [DataRow("minimum")]
    public async Task Scoped_read_failure_joins_original_pin_cleanup(string failure)
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "scope-failure"), DatabaseProfileId.PropertyGraph, authority.Lease, Routing, authority, Options);
        var wrapper = new ScopedReadParticipant(node) { FailureStage = failure };
        var query = new PartitionReadQuery("vertex", Guid.NewGuid());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var task = PartitionRemoteRead.ExecuteAsync([wrapper], Routing, (read, token) => read.ReadAsync("a", query, token), timeout.Token,
            minimum: failure == "minimum" ? new([new(node.Domain.Identity, new(999))]) : null,
            scopes: new Dictionary<string, PartitionReadQuery[]> { ["a"] = [query] }).AsTask();
        var cut = await wrapper.Acquired.Task.WaitAsync(timeout.Token);
        if (failure == "cancel")
        {
            timeout.Cancel();
        }

        await Assert.ThrowsExactlyAsync<AggregateException>(() => task);
        Assert.AreEqual(1, wrapper.Abandoned);
        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ReadCutAsync(cut, query).AsTask());
        var fresh = await node.AcquireScopedSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(10), [query]);
        await node.CompleteCutAsync(fresh);
    }

    [TestMethod]
    public async Task Scoped_snapshot_mapping_change_invalidates_even_an_unrelated_projection()
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "scope-mapping"), DatabaseProfileId.PropertyGraph, authority.Lease, Routing, authority, Options);
        var query = new PartitionReadQuery("vertex", Guid.NewGuid());
        var cut = await node.AcquireScopedSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(15), [query]);
        using var http = new HttpClient();
        var request = new HttpGraphEntityClient(http, node.Domain.Generation).EntityStore<Item>().CaptureSave(new("mapping-only", "value"), new(EntityOperationIds.New()));
        await node.InitializeApplicationMappingAsync(request.Export());
        Assert.IsFalse(JsonSerializer.Deserialize<bool>(await node.ReadCutAsync(cut, new("validate-snapshot", SnapshotPrepared: []))));
        await node.CompleteCutAsync(cut);
    }

    [TestMethod]
    public void Scoped_evidence_rejects_missing_scope_and_unsupported_query_shapes()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new DatabasePartitionReadScope(DatabaseProfileId.PropertyGraph, []));
        Assert.ThrowsExactly<ArgumentException>(() => new DatabasePartitionReadScope(DatabaseProfileId.PropertyGraph, Enumerable.Repeat("x", DatabasePartitionReadScope.MaximumResources + 1)));
        Assert.ThrowsExactly<NotSupportedException>(() => new DatabasePartitionReadScope((DatabaseProfileId)byte.MaxValue, ["x"]));
        Assert.ThrowsExactly<ArgumentException>(() => new DatabasePartitionSnapshotSummary(DatabasePartitionSnapshotSummary.ScopedFormat, new(0), new string('A', 64), [], []));
        Assert.ThrowsExactly<ArgumentException>(() => new DatabasePartitionSnapshotSummary(DatabasePartitionSnapshotSummary.CurrentFormat, new(0), new string('A', 64), [], [], new string('B', 64)));
        foreach (string kind in new[] { "mapping", "outgoing", "edge", "semantic-project" })
        {
            Assert.ThrowsExactly<InvalidDataException>(() => new PartitionScopedRead(DatabaseProfileId.PropertyGraph, Routing, new("a"), [new(kind, Guid.NewGuid())]));
        }
    }

    [TestMethod]
    public async Task Scoped_snapshot_does_not_miss_dependency_through_a_read_only_transaction_member()
    {
        var a = await OpenReservationStore(DatabaseProfileId.PropertyGraph, Path.Combine(_root, "readonly-a"));
        var b = await OpenReservationStore(DatabaseProfileId.PropertyGraph, Path.Combine(_root, "readonly-b"));
        await using var da = a.Database;
        await using var db = b.Database;
        Guid x = Guid.NewGuid(), y = Guid.NewGuid(), transaction = EntityOperationIds.New();
        foreach (var kernel in new[] { a.Kernel, b.Kernel })
        {
            Guid seed = EntityOperationIds.New();
            await kernel.CommitAsync(seed, ReservationPayload(DatabaseProfileId.PropertyGraph, seed, [x, y], null, create: true));
        }
        await a.Kernel.PartitionTransactions.PrepareAsync(transaction, ReservationDescriptor, a.Kernel.CurrentVersion,
            ReservationPayload(DatabaseProfileId.PropertyGraph, transaction, [x], a.Kernel.CurrentVersion));
        await b.Kernel.PartitionTransactions.PrepareAsync(transaction, ReservationDescriptor, b.Kernel.CurrentVersion,
            GraphTransactionCodec.Encode(new(transaction, [], b.Kernel.CurrentVersion, [new WholeVertexRead(new(y))])));
        // T read y, then releases B. C overwrites y while T's write to x is still
        // unapplied on A. Old x/new y must not be accepted merely because B's
        // read-only T member is absent from its write-filtered evidence.
        await b.Kernel.PartitionTransactions.CompleteAsync(transaction, ReservationDescriptor, true);
        Guid c = EntityOperationIds.New();
        await b.Kernel.CommitAsync(c, ReservationPayload(DatabaseProfileId.PropertyGraph, c, [y], null));
        using var sa = await a.Kernel.PartitionTransactions.CaptureScopedSnapshotAsync(ReadScope(DatabaseProfileId.PropertyGraph, x));
        using var sb = await b.Kernel.PartitionTransactions.CaptureScopedSnapshotAsync(ReadScope(DatabaseProfileId.PropertyGraph, y));
        Assert.IsTrue(sa.Evidence.Prepared.Contains(transaction));
        Assert.IsFalse(sb.Evidence.Committed.Contains(transaction));
        Assert.IsFalse(await a.Kernel.PartitionTransactions.ValidateScopedSnapshotAsync(sa, []));
        Assert.IsFalse(await b.Kernel.PartitionTransactions.ValidateScopedSnapshotAsync(sb, sa.Evidence.Prepared));
        await a.Kernel.PartitionTransactions.CompleteAsync(transaction, ReservationDescriptor, true);
    }
}
