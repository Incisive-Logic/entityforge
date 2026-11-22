using dotnet_etcd;
using Etcdserverpb;
using Google.Protobuf;
using Grpc.Core;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Query_home_checks_real_authority_after_local_or_remote_completion(bool http, bool revoke)
    {
        await WithQueryHome(async (node, placements, home, decisions) =>
        {
            using var certificate = PartitionCertificate();
            await using var service = new PartitionParticipantHttpService(node);
            await using var host = await StartPartitionHostAsync(service, certificate);
            using var client = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
            });
            IPartitionReadParticipant peer = http ? new HttpPartitionTransactionParticipant(client, PartitionAddress(host),
                PartitionToken, node.Domain, node.TransactionAuthorityIdentity!)
            {
                TypedQueryProtocol = true
            }
            : node;
            var cut = await peer.AcquireHistoryProtectionAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
            try
            {
                var result = await peer.ReadQueryJobValueAsync(cut, new("query-job", QueryJob:
                    new(Guid.NewGuid(), new("gql", "MATCH (n) RETURN n"), Finish: true, Home: home.Binding, Sequence: 1), ReadTime: TestQueryTime(cut)));
                Assert.IsTrue(result.Completed);
                Assert.IsNotNull(result.Pending);
                if (revoke)
                {
                    await placements.ReleaseAsync(node.CurrentLease);
                    await Assert.ThrowsExactlyAsync<StaleShardPlacementException>(() => home.ValidateAsync([result.Pending], default).AsTask());
                }
                else
                {
                    // Lease replacement with the same claim must not look like reassignment.
                    await placements.RenewAsync(node.CurrentLease, TimeSpan.FromMinutes(5));
                    await home.ValidateAsync([result.Pending], default);
                }
            }
            finally
            {
                await peer.AbandonCutAsync(cut.Id);
            }
        });
    }

    [DataTestMethod]
    [DataRow("job")]
    [DataRow("cut")]
    [DataRow("selection")]
    [DataRow("sequence")]
    [DataRow("authority")]
    [DataRow("missing")]
    [DataRow("cleanup")]
    public async Task Query_home_rejects_mismatched_or_unfinished_completion(string fault)
    {
        await WithQueryHome(async (node, placements, home, decisions) =>
        {
            var peer = new QueryHomeFaultPeer(node, result =>
            {
                if (result.Pending is not { } pending)
                {
                    return result;
                }

                if (fault == "cleanup")
                {
                    throw new IOException("Injected failed cleanup response.");
                }

                return result with
                {
                    Pending = fault switch
                    {
                        "job" => pending with { Job = Guid.NewGuid() },
                        "cut" => pending with { Cut = pending.Cut with { Id = Guid.NewGuid() } },
                        "selection" => pending with { Selection = "changed" },
                        "sequence" => pending with { Sequence = pending.Sequence + 1 },
                        "authority" => pending with { Authority = pending.Authority with { Cluster = pending.Authority.Cluster + 1 } },
                        _ => null
                    }
                };
            });
            await Assert.ThrowsAsync<Exception>(async () => await PartitionRemoteRead.ExecuteAsync([peer], Routing,
                async (read, token) =>
                {
                    read.UseAsEpoch();
                    var job = read.BeginQuery(home);
                    try
                    {
                        await job.ReadAsync("a", new("gql", "MATCH (n) RETURN n"), true, token);
                        await job.CompleteAsync(false);
                    }
                    catch
                    {
                        await job.CompleteAsync(true);
                        throw;
                    }
                    return true;
                }, useSnapshots: true));
            await using var frozen = await node.Kernel.PartitionTransactions.FreezeAsync();
        });
    }

    [TestMethod]
    public async Task Query_home_rejects_sequence_change_and_wrong_capability_then_drains()
    {
        await WithQueryHome(async (node, placements, home, decisions) =>
        {
            var cut = await node.AcquireHistoryProtectionAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
            Guid job = Guid.NewGuid();
            try
            {
                var initial = new PartitionQueryJobCommand(job, new("gql", "MATCH (n) RETURN n"), Home: home.Binding, Sequence: 1);
                await node.ReadQueryJobValueAsync(cut, new("query-job", QueryJob: initial, ReadTime: TestQueryTime(cut)));
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadQueryJobValueAsync(cut, new("query-job",
                    QueryJob: initial with
                    {
                        Sequence = 3,
                        Finish = true
                    }, ReadTime: TestQueryTime(cut))).AsTask());
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadQueryJobValueAsync(cut, new("query-job",
                    QueryJob: initial with
                    {
                        Home = home.Binding with
                        {
                            Namespace = "/wrong/"
                        },
                        Finish = true
                    }, ReadTime: TestQueryTime(cut))).AsTask());
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadQueryJobValueAsync(cut,
                    new("query-job", QueryJob: initial, ReadTime: TestQueryTime(cut))).AsTask());
                await Task.WhenAll(
                    node.ReadQueryJobValueAsync(cut, new("query-job", QueryJob: initial with
                    {
                        Sequence = 3
                    }, ReadTime: TestQueryTime(cut))).AsTask(),
                    node.ReadQueryJobValueAsync(cut, new("query-job", QueryJob: initial with
                    {
                        Sequence = 2
                    }, ReadTime: TestQueryTime(cut))).AsTask());
                var completed = await node.ReadQueryJobValueAsync(cut, new("query-job", QueryJob: initial with
                {
                    Sequence = 4,
                    Finish = true
                }, ReadTime: TestQueryTime(cut)));
                Assert.IsTrue(completed.Completed);
                await home.ValidateAsync([completed.Pending!], default);
            }
            finally
            {
                await node.AbandonCutAsync(cut.Id);
            }
            await using var frozen = await node.Kernel.PartitionTransactions.FreezeAsync();
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Query_home_full_frontiers_preserve_paging_multiplicity_and_corruption_checks(bool wide) =>
        await WithQueryHomePair((first, second, client, routing, decisions) => AssertQueryFrontiersAsync(first, second, client, routing, wide, decisions));

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Query_home_mixed_coordinated_writes_never_return_a_partial_pair(bool commands)
    {
        await WithQueryHomePair(async (first, second, client, routing, decisions) =>
        {
            var owners = new[] { first, second };
            var sibling = new PartitionApplicationClient(decisions, routing, owners, ReadClock) { QueryHome = client.QueryHome, CompleteOwnerCommands = commands };
            await using var siblingLifetime = new PartitionApplicationHttpService(sibling);
            var vertices = owners.Select(owner =>
            {
                Guid id;
                do
                {
                    id = Guid.NewGuid();
                } while (routing.RouteGraphVertex(id) != owner.Domain.Partition);
                return new GraphVertexId(id);
            }).ToArray();
            for (int index = 0; index < owners.Length; index++)
            {
                var operation = EntityOperationIds.New();
                await owners[index].Kernel.CommitWithReceiptAsync(operation, GraphTransactionCodec.Encode(new(operation,
                    [new CreateVertex(vertices[index], ["Pair"]),
                        new SetVertexProperty(vertices[index], "key", new DatabaseValue.String("pair")),
                        new SetVertexProperty(vertices[index], "value", new DatabaseValue.String("0"))], owners[index].Kernel.CurrentVersion, [])));
            }
            var request = new PartitionQueryRequest(first.Domain.Generation, DatabaseProfileId.PropertyGraph, "gql",
                "MATCH (n:Pair) WHERE n.key = \"pair\" RETURN n.`value` AS pairValue");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _ = await client.QueryAsync(request, timeout.Token);
            _ = await sibling.QueryAsync(request, timeout.Token);
            for (int epoch = 1; epoch <= 8; epoch++)
            {
                var operation = EntityOperationIds.New();
                var plan = new PartitionTransactionPlan(operation, routing.MapVersion, owners.Select((owner, index) =>
                    new PartitionTransactionMember(owner.Domain, owner.Kernel.CurrentVersion, GraphTransactionCodec.Encode(new(operation,
                        [new SetVertexProperty(vertices[index], "value", new DatabaseValue.String(epoch.ToString()))], owner.Kernel.CurrentVersion, [])))).ToArray());
                await decisions.BindAsync(plan);
                await first.PrepareNativeAsync(plan);
                await second.PrepareNativeAsync(plan);
                await SelectTimestampDecisionAsync(decisions, plan, first, second);
                await first.ApplyPartitionDecisionAsync(operation);
                var reads = Enumerable.Range(0, 4).Select(index => (index % 2 == 0 ? client : sibling).QueryAsync(request, timeout.Token).AsTask()).ToArray();
                await Task.Delay(50, timeout.Token);
                Assert.IsTrue(reads.All(read => !read.IsCompleted), "A partially applied pair cannot certify a fresh query cut.");
                await second.ApplyPartitionDecisionAsync(operation);
                foreach (var answer in await Task.WhenAll(reads))
                {
                    var rows = answer.Value.GetProperty("matches").EnumerateArray().ToArray();
                    Assert.AreEqual(2, rows.Length);
                    Assert.IsTrue(rows.All(row => row.GetProperty("bindings").GetProperty("pairValue").GetString() == epoch.ToString()));
                }
            }
        }, commands);
    }

    private async Task WithQueryHomePair(Func<PartitionOwnedNode, PartitionOwnedNode, PartitionApplicationClient,
        DistributedRoutingBinding, IPartitionTransactionAuthority, Task> action, bool commands = false)
    {
        string? endpoint = Environment.GetEnvironmentVariable("ENTITYFORGE_TEST_ETCD_ENDPOINT");
        if (string.IsNullOrEmpty(endpoint))
        {
            Assert.Inconclusive("Set ENTITYFORGE_TEST_ETCD_ENDPOINT.");
            return;
        }
        using var etcd = new EtcdClient(endpoint, configureChannelOptions: options => options.Credentials = ChannelCredentials.Insecure);
        string prefix = "/entityforge/query-home-frontiers/" + Guid.NewGuid().ToString("N") + "/";
        using var placements = new GrpcEtcdShardPlacementAuthority([new EtcdClient(endpoint, configureChannelOptions: options => options.Credentials = ChannelCredentials.Insecure)], new([new(endpoint)], prefix));
        try
        {
            var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
            var generation = new DistributedDatabaseGeneration(Guid.NewGuid());
            var a = await placements.AcquireAsync(new("a"), new("a"), new("https://localhost:22001"), generation, routing.MapVersion, TimeSpan.FromMinutes(5));
            var b = await placements.AcquireAsync(new("b"), new("b"), new("https://localhost:22002"), generation, routing.MapVersion, TimeSpan.FromMinutes(5));
            await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
            await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.PropertyGraph, a, routing, placements, Options, transactionAuthority: decisions, timestampClock: ReadClock);
            await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.PropertyGraph, b, routing, placements, Options, transactionAuthority: decisions, timestampClock: ReadClock);
            await first.EnableQueryHomeAsync();
            await second.EnableQueryHomeAsync();
            await using var home = await PartitionQueryHomeAuthority.OpenAsync(placements, ReadClock);
            var client = new PartitionApplicationClient(decisions, routing, [first, second], ReadClock) { QueryHome = home, CompleteOwnerCommands = commands };
            try
            {
                await action(first, second, client, routing, decisions);
            }
            finally
            {
                await client.CloseReadEpochAsync();
            }
        }
        finally
        {
            await etcd.DeleteAsync(new DeleteRangeRequest { Key = ByteString.CopyFromUtf8(prefix), RangeEnd = ByteString.CopyFromUtf8(prefix[..^1] + "0") });
        }
    }

    [TestMethod]
    public async Task Query_home_keeps_original_view_across_a_writer_and_abandon_drains()
    {
        await WithQueryHome(async (node, placements, home, decisions) =>
        {
            var cut = await node.AcquireHistoryProtectionAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
            var job = new PartitionQueryJobCommand(Guid.NewGuid(), new("gql", "MATCH (n) RETURN n"), Home: home.Binding, Sequence: 1);
            try
            {
                var first = await node.ReadQueryJobValueAsync(cut, new("query-job", QueryJob: job, ReadTime: TestQueryTime(cut)));
                Assert.AreEqual(0, first.Value!.Element.GetProperty("relation").GetProperty("vertices").GetArrayLength());
                var operation = EntityOperationIds.New();
                await node.Kernel.CommitWithReceiptAsync(operation, GraphTransactionCodec.Encode(new(operation,
                    [new CreateVertex(new(Guid.NewGuid()), ["After"])], node.Kernel.CurrentVersion, [])));
                var last = await node.ReadQueryJobValueAsync(cut, new("query-job", QueryJob: job with
                {
                    Sequence = 2,
                    Finish = true
                }, ReadTime: TestQueryTime(cut)));
                Assert.AreEqual(0, last.Value!.Element.GetProperty("relation").GetProperty("vertices").GetArrayLength());
                await home.ValidateAsync([last.Pending!], default);
                var stale = await node.ReadQueryJobValueAsync(cut, new("query-job", QueryJob: job with
                {
                    Id = Guid.NewGuid(),
                    Finish = true
                }, ReadTime: TestQueryTime(cut)));
                Assert.IsTrue(stale.Valid);
                Assert.IsNotNull(stale.Pending);
                Assert.AreEqual(0, stale.Value!.Element.GetProperty("relation").GetProperty("vertices").GetArrayLength(), "The same timestamp remains historical after a later write.");
            }
            finally
            {
                await node.AbandonCutAsync(cut.Id);
            }
            var next = await node.AcquireHistoryProtectionAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
            _ = await node.ReadQueryJobValueAsync(next, new("query-job", QueryJob: job with
            {
                Id = Guid.NewGuid()
            }, ReadTime: TestQueryTime(next)));
            await node.AbandonCutAsync(next.Id);
            await Assert.ThrowsAsync<Exception>(() => node.ReadQueryJobValueAsync(next, new("query-job", QueryJob: job, ReadTime: TestQueryTime(next))).AsTask());
            await using var frozen = await node.Kernel.PartitionTransactions.FreezeAsync();
        });
    }

    [TestMethod]
    public async Task Query_home_accepts_timestamp_capability_and_binds_exact_read_time()
    {
        await WithQueryHome(async (node, placements, home, decisions) =>
        {
            var cut = await node.AcquireHistoryProtectionAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
            try
            {
                Assert.IsNotNull(cut.ClockDomain);
                var answer = await node.ReadQueryJobValueAsync(cut,
                    new("query-job", QueryJob: new(Guid.NewGuid(), new("gql", "MATCH (n) RETURN n"), Finish: true,
                        Home: home.Binding, Sequence: 1), ReadTime: TestQueryTime(cut)));
                Assert.AreEqual(TestQueryTime(cut), answer.Pending!.ReadTime);
                await home.ValidateAsync([answer.Pending], default);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => home.ValidateAsync(
                    [answer.Pending with { ReadTime = new(Guid.NewGuid(), answer.Pending.ReadTime!.Physical, answer.Pending.ReadTime.Logical, 0, true) }], default).AsTask());
            }
            finally
            {
                await node.AbandonCutAsync(cut.Id);
            }
        }, timestamp: true);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Query_home_restart_or_transfer_drains_jobs_and_rejects_old_handles(bool transfer)
    {
        await WithQueryHome(async (node, placements, home, decisions) =>
        {
            var cut = await node.AcquireHistoryProtectionAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
            var command = new PartitionQueryJobCommand(Guid.NewGuid(), new("gql", "MATCH (n) RETURN n"), Home: home.Binding, Sequence: 1);
            await node.ReadQueryJobValueAsync(cut, new("query-job", QueryJob: command, ReadTime: TestQueryTime(cut)));
            PartitionOwnedNode replacement;
            PartitionOwnedTransfer? movement = null;
            if (transfer)
            {
                string imagePath = Path.Combine(_root, "query-image");
                var image = await node.SealAndExportAsync(new(Guid.NewGuid()), imagePath).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                movement = new PartitionOwnedTransfer(imagePath, Path.Combine(_root, "query-destination"), image, Routing,
                    placements, new("destination"), new("https://localhost:22003"), Options, decisions, timestampClock: ReadClock);
                using var election = new EtcdClient(Environment.GetEnvironmentVariable("ENTITYFORGE_TEST_ETCD_ENDPOINT")!,
                    configureChannelOptions: options => options.Credentials = ChannelCredentials.Insecure);
                var electionLease = await election.LeaseGrantAsync(new LeaseGrantRequest { TTL = 60 });
                try
                {
                    string key = home.Binding.Namespace + "query-transfer-controller";
                    var value = ByteString.CopyFromUtf8("query-home-qualification");
                    var elected = await election.PutAsync(new PutRequest { Key = ByteString.CopyFromUtf8(key), Value = value, Lease = electionLease.ID });
                    replacement = await movement.CompleteAsync(new(key, elected.Header.Revision, electionLease.ID, value.ToByteArray()));
                }
                finally
                {
                    await election.LeaseRevokeAsync(new LeaseRevokeRequest { ID = electionLease.ID });
                }
            }
            else
            {
                // Preserve a current same-claim lease for the replacement. Normal
                // disposal revokes the supervisor's own lease; that is not a crash.
                var lease = await placements.RenewAsync(node.CurrentLease, Options.LeaseLifetime);
                await node.DisposeAsync();
                replacement = await PartitionOwnedNode.OpenAsync(Path.Combine(_root, "node"), DatabaseProfileId.PropertyGraph,
                    lease, Routing, placements, Options, transactionAuthority: decisions, timestampClock: ReadClock);
            }
            try
            {
                await replacement.EnableQueryHomeAsync();
                await Assert.ThrowsAsync<Exception>(() => replacement.ReadQueryJobValueAsync(cut,
                    new("query-job", QueryJob: command with
                    {
                        Sequence = 2,
                        Finish = true
                    }, ReadTime: TestQueryTime(cut))).AsTask());
                var next = await replacement.AcquireHistoryProtectionAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
                try
                {
                    var result = await replacement.ReadQueryJobValueAsync(next, new("query-job",
                        QueryJob: command with
                        {
                            Id = Guid.NewGuid(),
                            Finish = true
                        }, ReadTime: TestQueryTime(next)));
                    await home.ValidateAsync([result.Pending!], default);
                }
                finally
                {
                    await replacement.AbandonCutAsync(next.Id);
                }
            }
            finally
            {
                if (movement is not null)
                {
                    await movement.DisposeAsync();
                }
                else
                {
                    await replacement.DisposeAsync();
                }
            }
        });
    }

    [TestMethod]
    public async Task Query_home_expired_pending_completion_cannot_authorise_success()
    {
        await WithQueryHome(async (node, placements, home, decisions) =>
        {
            var cut = await node.AcquireHistoryProtectionAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(3));
            try
            {
                var result = await node.ReadQueryJobValueAsync(cut, new("query-job", QueryJob:
                    new(Guid.NewGuid(), new("gql", "MATCH (n) RETURN n"), Finish: true, Home: home.Binding, Sequence: 1), ReadTime: TestQueryTime(cut)));
                // Task.Delay may round a fractional millisecond down. Observe
                // the actual deadline before asserting the expired behaviour.
                var minimumDelay = TimeSpan.FromMilliseconds(1);
                while (cut.Expires > DateTimeOffset.UtcNow)
                {
                    var remaining = cut.Expires - DateTimeOffset.UtcNow;
                    await Task.Delay(remaining > minimumDelay ? remaining : minimumDelay);
                }
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => home.ValidateAsync([result.Pending!], default).AsTask());
            }
            finally
            {
                await node.AbandonCutAsync(cut.Id);
            }
        });
    }

    [TestMethod]
    public async Task Query_home_workspace_exhaustion_fails_then_releases_its_job()
    {
        await WithQueryHome(async (node, placements, home, decisions) =>
        {
            const long OwnerWorkspaceLimit = 64L * 1024 * 1024;
            var cut = await node.AcquireHistoryProtectionAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
            var command = new PartitionQueryJobCommand(Guid.NewGuid(), new("gql", "MATCH (n) RETURN n"), Finish: true, Home: home.Binding, Sequence: 1);
            try
            {
                node.ReserveQueryBytes(OwnerWorkspaceLimit);
                try
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => node.ReadQueryJobValueAsync(cut, new("query-job", QueryJob: command, ReadTime: TestQueryTime(cut))).AsTask());
                }
                finally
                {
                    node.ReleaseQueryBytes(OwnerWorkspaceLimit);
                }
                var result = await node.ReadQueryJobValueAsync(cut, new("query-job", QueryJob: command with
                {
                    Id = Guid.NewGuid()
                }, ReadTime: TestQueryTime(cut)));
                await home.ValidateAsync([result.Pending!], default);
            }
            finally
            {
                await node.AbandonCutAsync(cut.Id);
            }
        });
    }

    private async Task WithQueryHome(Func<PartitionOwnedNode, GrpcEtcdShardPlacementAuthority, PartitionQueryHomeAuthority, IPartitionTransactionAuthority, Task> action, bool timestamp = false, DatabaseProfileId profile = DatabaseProfileId.PropertyGraph, DistributedRoutingBinding? routingOverride = null)
    {
        var routing = routingOverride ?? Routing;
        string? endpoint = Environment.GetEnvironmentVariable("ENTITYFORGE_TEST_ETCD_ENDPOINT");
        if (string.IsNullOrEmpty(endpoint))
        {
            Assert.Inconclusive("Set ENTITYFORGE_TEST_ETCD_ENDPOINT.");
            return;
        }
        using var etcd = new EtcdClient(endpoint, configureChannelOptions: options => options.Credentials = ChannelCredentials.Insecure);
        string prefix = "/entityforge/query-home-tests/" + Guid.NewGuid().ToString("N") + "/";
        using var placements = new GrpcEtcdShardPlacementAuthority([new EtcdClient(endpoint, configureChannelOptions: options => options.Credentials = ChannelCredentials.Insecure)], new([new(endpoint)], prefix));
        try
        {
            var lease = await placements.AcquireAsync(new("a"), new("owner-a"), new("https://localhost:22001"), new(Guid.NewGuid()), routing.MapVersion, TimeSpan.FromMinutes(5));
            await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
            await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node"), profile,
                lease, routing, placements, Options, transactionAuthority: decisions,
                timestampClock: ReadClock);
            await node.EnableQueryHomeAsync();
            await using var home = await PartitionQueryHomeAuthority.OpenAsync(placements, ReadClock);
            await action(node, placements, home, decisions);
        }
        finally
        {
            await etcd.DeleteAsync(new DeleteRangeRequest { Key = ByteString.CopyFromUtf8(prefix), RangeEnd = ByteString.CopyFromUtf8(prefix[..^1] + "0") });
        }
    }

    private sealed class QueryHomeFaultPeer(IPartitionReadParticipant inner, Func<PartitionQueryJobResult, PartitionQueryJobResult> transform) : IPartitionReadParticipant
    {
        public PartitionExecutionDomain Domain => inner.Domain;

        public ValueTask<PartitionReadCut> AcquireCutAsync(Guid id, DateTimeOffset expires, CancellationToken token = default) => inner.AcquireCutAsync(id, expires, token);

        public ValueTask<PartitionReadCut> AcquireHistoryProtectionAsync(Guid id, DateTimeOffset expires, CancellationToken token = default) => inner.AcquireHistoryProtectionAsync(id, expires, token);

        public ValueTask<PartitionReadCut> AcquireSnapshotCutAsync(Guid id, DateTimeOffset expires, CancellationToken token = default) => inner.AcquireSnapshotCutAsync(id, expires, token);

        public ValueTask<byte[]> ReadCutAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default) => inner.ReadCutAsync(cut, query, token);

        public async ValueTask<PartitionQueryJobResult> ReadQueryJobValueAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default) => transform(await inner.ReadQueryJobValueAsync(cut, query, token));

        public ValueTask CompleteCutAsync(PartitionReadCut cut, CancellationToken token = default) => inner.CompleteCutAsync(cut, token);

        public ValueTask AbandonCutAsync(Guid id, CancellationToken token = default) => inner.AbandonCutAsync(id, token);
    }
}
