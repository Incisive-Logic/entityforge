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
    public async Task Final_projection_uses_original_snapshot_then_releases_pin(DatabaseProfileId profile, bool scoped)
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "final"), profile, authority.Lease, Routing, authority, Options);
        Guid subject = Guid.NewGuid();
        var query = profile == DatabaseProfileId.PropertyGraph ? new PartitionReadQuery("vertex", subject)
            : new("subject", Subject: "urn:reservation:" + subject.ToString("N"));
        var cut = scoped ? await node.AcquireScopedSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(15), [query])
            : await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(15));
        Assert.IsTrue(JsonSerializer.Deserialize<bool>(await node.ReadCutAsync(cut, new("validate-snapshot", SnapshotPrepared: []))));
        // Mutation after certification must not replace the pinned result.
        await node.Authority.ExecuteAsync(async () =>
        {
            Guid seed = Guid.NewGuid();
            await node.Kernel.CommitAsync(seed,
            ReservationPayload(profile, seed, [subject], null, create: true));
            return true;
        });
        var value = JsonSerializer.Deserialize<JsonElement[]>(await node.ReadAndCompleteCutAsync(cut, Batch([query])))!.Single();
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            Assert.AreEqual(JsonValueKind.Null, value.ValueKind);
        }
        else
        {
            Assert.AreEqual(0, value.GetArrayLength());
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ReadCutAsync(cut, query).AsTask());
        // A lost final reply cannot cause a new projection against a later view.
        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ReadAndCompleteCutAsync(cut, Batch([query])).AsTask());
        await node.CompleteCutAsync(cut);
        await node.AbandonCutAsync(cut.Id);
        await node.Authority.ExecuteAsync(async () =>
        {
            await node.Kernel.PartitionTransactions.RetainAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
            return true;
        });
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("scope")]
    [DataRow("shape")]
    [DataRow("placement")]
    public async Task Final_projection_failure_discharges_original_pin(string failure)
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "final-failure"), DatabaseProfileId.PropertyGraph, authority.Lease, Routing, authority, Options);
        var query = new PartitionReadQuery("vertex", Guid.NewGuid());
        var cut = await node.AcquireScopedSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(15), [query]);
        var request = Batch([query]);
        if (failure == "scope")
        {
            request = Batch([new("vertex", Guid.NewGuid())]);
        }

        if (failure == "shape")
        {
            request = Batch([new("outgoing", query.Identity)]);
        }

        if (failure == "placement")
        {
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
        }

        await Assert.ThrowsAsync<Exception>(() => node.ReadAndCompleteCutAsync(cut, request,
            failure == "cancel" ? new CancellationToken(true) : default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => node.ReadCutAsync(cut, query).AsTask());
        await node.AbandonCutAsync(cut.Id);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Final_projection_joins_every_owner_and_keeps_chunk_order(bool fail)
    {
        var (routing, a, b) = SnapshotParticipants();
        var query = new PartitionReadQuery("vertex", Guid.NewGuid());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int readsA = 0, completeA = 0, completeB = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Reading = (request, _) =>
        {
            if (request.Kind == "validate-snapshot")
            {
                return ValueTask.FromResult("true"u8.ToArray());
            }

            Interlocked.Increment(ref readsA);
            return ValueTask.FromResult(JsonSerializer.SerializeToUtf8Bytes(request.Batch!.Queries.Select(_ => 1)));
        };
        b.Reading = (request, _) => ValueTask.FromResult(request.Kind == "validate-snapshot" ? "true"u8.ToArray() : JsonSerializer.SerializeToUtf8Bytes(request.Batch!.Queries.Select(_ => 2)));
        a.Completing = _ =>
        {
            Interlocked.Increment(ref completeA);
            return ValueTask.CompletedTask;
        };
        b.Completing = async token =>
        {
            Interlocked.Increment(ref completeB);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            if (fail)
            {
                throw new IOException("Completion lost");
            }
        };
        var projection = new PartitionReadProjection(Enumerable.Repeat(("a", query), PartitionReadBatch.MaximumQueries + 1).Append(("b", query)));
        var task = PartitionRemoteRead.ExecuteAsync([a, b], routing, (read, token) => projection.ExecuteAsync(read, token, completeFinal: true), timeout.Token, useSnapshots: true).AsTask();
        await entered.Task.WaitAsync(timeout.Token);
        Assert.IsFalse(task.IsCompleted);
        release.SetResult();
        if (fail)
        {
            await Assert.ThrowsExactlyAsync<AggregateException>(() => task);
        }
        else
        {
            var result = (await task).Value.Select(v => JsonSerializer.Deserialize<int>(v)).ToArray();
            CollectionAssert.AreEqual(Enumerable.Repeat(1, PartitionReadBatch.MaximumQueries + 1).Append(2).ToArray(), result);
        }
        Assert.AreEqual(2, readsA);
        Assert.AreEqual(1, completeA);
        Assert.AreEqual(1, completeB);
        Assert.AreEqual(0, a.Active.Count + b.Active.Count);
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    public async Task Final_projection_https_preserves_cleanup_on_lost_reply(DatabaseProfileId profile, bool live)
    {
        var authority = new Authority();
        await using var control = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "final-authority"), authority.Lease.Placement.Claim.Generation.Value, profile, Routing, live);
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "final-http"), profile, authority.Lease, Routing, authority, Options, transactionAuthority: control.Authority);
        using var certificate = PartitionCertificate();
        await using var service = new PartitionParticipantHttpService(node);
        await using var host = await StartPartitionHostAsync(service, certificate);
        using var handler = new FinalReplyHandler { InnerHandler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == certificate.Thumbprint } };
        using var http = new HttpClient(handler);
        var remote = new HttpPartitionTransactionParticipant(http, PartitionAddress(host), PartitionToken, node.Domain, control.Authority.Identity);
        var query = profile == DatabaseProfileId.PropertyGraph ? new PartitionReadQuery("vertex", Guid.NewGuid()) : new("subject", Subject: "urn:final");
        foreach (bool lose in new[] { false, true })
        {
            handler.Lose = lose;
            var cut = await remote.AcquireScopedSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(15), [query]);
            if (lose)
            {
                await Assert.ThrowsAsync<Exception>(() => remote.ReadAndCompleteCutAsync(cut, Batch([query])).AsTask());
            }
            else
            {
                Assert.AreEqual(1, JsonSerializer.Deserialize<JsonElement[]>(await remote.ReadAndCompleteCutAsync(cut, Batch([query])))!.Length);
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() => node.ReadCutAsync(cut, query).AsTask());
            await remote.AbandonCutAsync(cut.Id);
        }
        Assert.AreEqual(2, handler.FinalRequests);
    }

    private sealed class FinalReplyHandler : DelegatingHandler
    {
        internal bool Lose;
        internal int FinalRequests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (request.RequestUri!.AbsolutePath.EndsWith("read-complete-cut", StringComparison.Ordinal))
            {
                response.EnsureSuccessStatusCode();
                FinalRequests++;
                if (Lose)
                {
                    response.Dispose();
                    throw new IOException("Lost final projection reply");
                }
            }
            return response;
        }
    }
}
