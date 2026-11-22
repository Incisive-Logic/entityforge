using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    public async Task Read_epoch_https_reuses_pin_and_preserves_minimum_after_write(DatabaseProfileId profile, bool live)
    {
        var authority = new Authority();
        await using var control = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "epoch-http-authority"), authority.Lease.Placement.Claim.Generation.Value, profile, Routing, live);
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "epoch-http"), profile, authority.Lease, Routing, authority, Options, transactionAuthority: control.Authority, timestampClock: ReadClock);
        using var certificate = PartitionCertificate();
        await using var service = new PartitionParticipantHttpService(node);
        await using var host = await StartPartitionHostAsync(service, certificate);
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == certificate.Thumbprint };
        using var http = new HttpClient(handler);
        var remote = new HttpPartitionTransactionParticipant(http, PartitionAddress(host), PartitionToken, node.Domain, control.Authority.Identity);
        await using var epochs = new PartitionReadEpoch([remote], Routing, ReadClock);
        var query = Batch([profile == DatabaseProfileId.PropertyGraph ? new("vertex", Guid.NewGuid()) : new PartitionReadQuery("subject", Subject: "urn:epoch-http")]);
        var first = await epochs.ExecuteAsync((read, ct) => read.ReadAsync("a", query, ct), null, default);
        var repeated = await epochs.ExecuteAsync((read, ct) => read.ReadAsync("a", query, ct), null, default);
        Assert.AreEqual(first.Position.Positions[0], repeated.Position.Positions[0]);
        Guid operation = Guid.NewGuid();
        await node.Authority.ExecuteAsync(async () =>
        {
            await node.Kernel.CommitAsync(operation, NativePayload(profile, Routing, new("a"), operation));
            return true;
        });
        var minimum = new DatabasePositionVector([new(node.Domain.Identity, node.Kernel.CurrentVersion)]);
        var after = await epochs.ExecuteAsync((read, ct) => read.ReadAsync("a", query, ct), minimum, default);
        Assert.IsTrue(after.Position.Covers(minimum));
        await epochs.DisposeAsync();
        await node.Authority.ExecuteAsync(async () =>
        {
            await node.Kernel.PartitionTransactions.RetainAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
            return true;
        });
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Read_epoch_partial_application_cannot_refresh_to_a_mixed_cut(DatabaseProfileId profile)
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
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "epoch-decisions"));
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "epoch-a"), profile, a.Lease, routing, a, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "epoch-b"), profile, b.Lease, routing, b, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        await using var epochs = new PartitionReadEpoch([first, second], routing, ReadClock);
        Guid target;
        do
        {
            target = Guid.NewGuid();
        } while (routing.RouteGraphVertex(target) != new LogicalShardId("a"));
        string subject;
        do
        {
            subject = "urn:absent:" + Guid.NewGuid().ToString("N");
        } while (routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(new EntityForge.Db.Rdf.Contracts.RdfIri(subject))) != new LogicalShardId("a"));
        var query = Batch([profile == DatabaseProfileId.PropertyGraph ? new("vertex", target) : new PartitionReadQuery("subject", Subject: subject)]);
        Guid secondTarget;
        do
        {
            secondTarget = Guid.NewGuid();
        } while (routing.RouteGraphVertex(secondTarget) != new LogicalShardId("b"));
        string secondSubject;
        do
        {
            secondSubject = "urn:absent:" + Guid.NewGuid().ToString("N");
        } while (routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(new EntityForge.Db.Rdf.Contracts.RdfIri(secondSubject))) != new LogicalShardId("b"));
        var secondQuery = Batch([profile == DatabaseProfileId.PropertyGraph ? new("vertex", secondTarget) : new PartitionReadQuery("subject", Subject: secondSubject)]);
        await epochs.ExecuteAsync((read, ct) => read.ReadAsync("a", query, ct), null, default);
        Guid operation = Guid.NewGuid();
        byte[] Payload(Guid vertex, string rdfSubject) => profile == DatabaseProfileId.PropertyGraph
            ? GraphTransactionCodec.Encode(new(operation, [new CreateVertex(new(vertex), []),
                new SetVertexProperty(new(vertex), "name", new DatabaseValue.String("value"))]))
            : RdfTransactionCodec.Encode(new(operation, [new AssertRdfQuad(new(new RdfIri(rdfSubject),
                new RdfIri("urn:name"), new RdfLiteral("value")))]));
        var plan = new PartitionTransactionPlan(operation, routing.MapVersion,
            [new(first.Domain, first.Kernel.CurrentVersion, Payload(target, subject)),
                new(second.Domain, second.Kernel.CurrentVersion, Payload(secondTarget, secondSubject))]);
        await decisions.BindAsync(plan);
        await first.PrepareNativeAsync(plan);
        await second.PrepareNativeAsync(plan);
        var decision = await SelectTimestampDecisionAsync(decisions, plan, first, second);
        await first.ApplyPartitionDecisionAsync(operation);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = epochs.ExecuteAsync(async (read, ct) =>
        {
            var local = await read.ReadAsync("a", query, ct);
            var remote = await read.ReadAsync("b", secondQuery, ct);
            StringAssert.Contains(System.Text.Encoding.UTF8.GetString(remote), "value");
            return local;
        }, null, timeout.Token).AsTask();
        await Task.Delay(100, timeout.Token);
        Assert.IsFalse(pending.IsCompletedSuccessfully, "A partial application must not become a visible refreshed epoch.");
        await second.ApplyPartitionDecisionAsync(operation);
        var result = await pending;
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(result.Value), "value");
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Read_epoch_kernel_is_profile_neutral_and_rejects_changed_or_released_history(DatabaseProfileId profile)
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "epoch-native"), profile, authority.Lease, Routing, authority, Options, timestampClock: ReadClock);
        var semantic = profile == DatabaseProfileId.Semantic ? await node.CreateSemanticExecutionAsync() : null;
        if (semantic is not null)
        {
            var seed = semantic.CaptureSave(new Item("one", "before"), EntityForge.Db.Semantic.SemanticEntityWriteOperation.Create());
            await semantic.ExecuteAsync(node.Domain, seed.OperationId, seed.Export());
        }
        var pin = await node.Kernel.PartitionTransactions.CaptureSnapshotAsync();
        Assert.IsTrue(await node.Kernel.PartitionTransactions.ValidateReadEpochAsync(pin));
        Guid operation = Guid.NewGuid();
        if (semantic is null)
        {
            await node.Authority.ExecuteAsync(async () =>
            {
                await node.Kernel.CommitAsync(operation, NativePayload(profile, Routing, new("a"), operation));
                return true;
            });
        }
        else
        {
            var change = semantic.CaptureSave(new Item("one", "after"), EntityForge.Db.Semantic.SemanticEntityWriteOperation.Create());
            await semantic.ExecuteAsync(node.Domain, change.OperationId, change.Export());
        }
        Assert.IsFalse(await node.Kernel.PartitionTransactions.ValidateReadEpochAsync(pin));
        pin.Dispose();
        using var fresh = await node.Kernel.PartitionTransactions.CaptureSnapshotAsync();
        Assert.IsTrue(await node.Kernel.PartitionTransactions.ValidateReadEpochAsync(fresh));
        fresh.Dispose();
        Assert.IsFalse(await node.Kernel.PartitionTransactions.ValidateReadEpochAsync(fresh));
    }

    [TestMethod]
    public async Task Timestamp_epoch_reuses_history_protection_without_physical_certification()
    {
        var (routing, a, b) = SnapshotParticipants();
        a.ClockDomain = b.ClockDomain = ReadClock.Domain;
        int captures = 0, validate = 0, projectedA = 0, projectedB = 0;
        a.Acquiring = b.Acquiring = _ =>
        {
            Interlocked.Increment(ref captures);
            return ValueTask.CompletedTask;
        };
        a.Reading = (query, _) => Reply(query, true);
        b.Reading = (query, _) => Reply(query, false);
        ValueTask<byte[]> Reply(PartitionReadQuery query, bool first)
        {
            if (query.Kind == "validate-snapshot")
            {
                Interlocked.Increment(ref validate);
                return ValueTask.FromResult("true"u8.ToArray());
            }
            Assert.AreEqual("timestamp-project", query.Kind);
            if (first)
            {
                Interlocked.Increment(ref projectedA);
            }
            else
            {
                Interlocked.Increment(ref projectedB);
            }

            return ValueTask.FromResult("17"u8.ToArray());
        }
        await using (var epochs = new PartitionReadEpoch([a, b], routing, ReadClock))
        {
            await Task.WhenAll(Enumerable.Range(0, 32).Select(async _ =>
                Assert.AreEqual(17, JsonSerializer.Deserialize<int>((await epochs.ExecuteAsync(
                    (read, ct) => read.ReadAsync("a", new("vertex", Guid.NewGuid()), ct), null, default)).Value))));
            Assert.AreEqual(2, captures);
            Assert.AreEqual(0, validate, "Timestamp protection must not certify an unused physical cut.");
            Assert.AreEqual(32, projectedA);
            Assert.AreEqual(0, projectedB);
        }
        Assert.AreEqual(0, a.Active.Count + b.Active.Count);
    }

    [TestMethod]
    public async Task Timestamp_epoch_preserves_attempt_and_drains_active_borrowers()
    {
        var (routing, a, b) = SnapshotParticipants();
        a.ClockDomain = b.ClockDomain = ReadClock.Domain;
        int attempt = 0;
        a.Reading = b.Reading = (query, _) => ValueTask.FromResult(query.Kind == "validate-snapshot" ? "true"u8.ToArray() : "19"u8.ToArray());
        var epochs = new PartitionReadEpoch([a, b], routing, ReadClock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = epochs.ExecuteAsync(async (read, ct) =>
        {
            Interlocked.Increment(ref attempt);
            var value = await read.ReadAsync("a", new("vertex", Guid.NewGuid()), ct);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return value;
        }, null, default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disposal = epochs.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted);
        release.SetResult();
        Assert.AreEqual(19, JsonSerializer.Deserialize<int>((await task).Value));
        await disposal;
        Assert.AreEqual(1, attempt);
        Assert.AreEqual(0, a.Active.Count + b.Active.Count);
    }

    [TestMethod]
    public async Task Read_epoch_receiver_checks_placement_and_freshness_on_every_projection()
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "epoch-read"), DatabaseProfileId.PropertyGraph, authority.Lease, Routing, authority, Options, timestampClock: ReadClock);
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        var query = new PartitionReadQuery("timestamp-project", Projection: Batch([new("vertex", Guid.NewGuid())]), ReadTime: TestQueryTime(cut));
        Assert.IsNotNull(await node.ReadCutAsync(cut, query));
        Guid operation = Guid.NewGuid();
        await node.Authority.ExecuteAsync(async () =>
        {
            await node.Kernel.CommitAsync(operation, NativePayload(DatabaseProfileId.PropertyGraph, Routing, new("a"), operation));
            return true;
        });
        Assert.IsNotNull(await node.ReadCutAsync(cut, query), "Later writes do not invalidate the admitted timestamp.");
        authority.Lease = authority.Lease with
        {
            Placement = authority.Lease.Placement with
            {
                Claim = authority.Lease.Placement.Claim with
                {
                    FencingToken = new(2)
                }
            }
        };
        await Assert.ThrowsAsync<Exception>(() => node.ReadCutAsync(cut, query).AsTask());
        await node.AbandonCutAsync(cut.Id);
    }
}
