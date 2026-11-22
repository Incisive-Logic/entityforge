using dotnet_etcd;
using Etcdserverpb;
using Google.Protobuf;
using Grpc.Core;
using EntityForge.Db.Kernel;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    private sealed class ServingTestClock(IDatabaseBoundedClock inner) : IDatabaseBoundedClock
    {
        internal Guid? DomainOverride;

        public Guid Domain => DomainOverride ?? inner.Domain;

        internal bool Healthy = true;
        internal TimeSpan UpperUncertainty;

        public DatabaseClockInterval Read()
        {
            if (!Healthy)
            {
                throw new InvalidOperationException("Injected clock monitoring loss.");
            }

            var now = inner.Read();
            return now with
            {
                LatestUtcTicks = checked(now.LatestUtcTicks + UpperUncertainty.Ticks)
            };
        }
    }

    private async Task<PartitionQueryPendingCompletion> ServingCompletion(PartitionOwnedNode node, PartitionQueryHomeAuthority home,
        DatabaseProfileId profile, bool remote, TimeSpan? lifetime = null)
    {
        using var certificate = PartitionCertificate();
        await using var service = new PartitionParticipantHttpService(node);
        await using var host = await StartPartitionHostAsync(service, certificate);
        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
            (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
        });
        IPartitionReadParticipant peer = remote ? new HttpPartitionTransactionParticipant(http, PartitionAddress(host),
            PartitionToken, node.Domain, node.TransactionAuthorityIdentity!)
        {
            TypedQueryProtocol = true
        }
        : node;
        var cut = await peer.AcquireHistoryProtectionAsync(Guid.NewGuid(), DateTimeOffset.UtcNow + (lifetime ?? TimeSpan.FromSeconds(20)));
        try
        {
            var selection = profile switch
            {
                DatabaseProfileId.Rdf => new PartitionQueryExchangeRequest("sparql", "SELECT ?s WHERE { ?s <urn:p> ?o }", ProfileFragments: true),
                DatabaseProfileId.Semantic => new PartitionQueryExchangeRequest("entity", EntityQuery: new("urn:absent", new("urn:absent"), ConceptMatchMode: "direct"), ProfileFragments: true),
                _ => new PartitionQueryExchangeRequest("gql", "MATCH (n) RETURN n")
            };
            var response = await peer.ReadQueryJobValueAsync(cut, new("query-job", QueryJob:
                new(Guid.NewGuid(), selection, Finish: true, Home: home.Binding, Sequence: 1), ReadTime: TestQueryTime(cut)));
            Assert.IsTrue(response.Valid && response.Completed);
            Assert.IsNotNull(response.Pending);
            return response.Pending;
        }
        finally
        {
            await peer.AbandonCutAsync(cut.Id);
        }
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    [DataRow(DatabaseProfileId.Semantic, false)]
    [DataRow(DatabaseProfileId.Semantic, true)]
    public async Task Serving_grants_preserve_profile_transport_completion_and_are_shared(DatabaseProfileId profile, bool remote, TimeSpan? lifetime = null)
    {
        await WithQueryHome(async (node, placements, control, _) =>
        {
            await using var home = await PartitionQueryHomeAuthority.OpenAsync(placements, ReadClock);
            var completed = await ServingCompletion(node, home, profile, remote);
            await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => home.ValidateAsync([completed], default).AsTask()));
            Assert.AreEqual(1L, home.ServingAcquisitions);
            await using (var sibling = await PartitionQueryHomeAuthority.OpenAsync(placements, ReadClock))
            {
                await sibling.ValidateAsync([completed], default);
            }

            await home.ValidateAsync([completed], default); // Sibling disposal must not revoke shared evidence.
            Assert.AreEqual(1L, home.ServingAcquisitions);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => home.ValidateAsync(
                [completed with { Authority = completed.Authority with { Cluster = completed.Authority.Cluster + 1 } }], default).AsTask());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => home.ValidateAsync([completed, completed], default).AsTask());
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => home.ValidateAsync([completed], cancelled.Token).AsTask());
            await placements.ReleaseAsync(node.CurrentLease);
            // Explicit read grants survive loss of write/liveness authority, but block any new owner.
            await home.ValidateAsync([completed], default);
            await Assert.ThrowsExactlyAsync<ShardServingGrantActiveException>(() => placements.AcquireAsync(new("a"), new("other"),
                new("https://localhost:22002"), new(node.Domain.Generation), Routing.MapVersion, TimeSpan.FromMinutes(1)).AsTask());
        }, profile: profile);
    }

    [TestMethod]
    public async Task Serving_grants_fail_closed_on_clock_loss_uncertainty_and_delayed_publication()
    {
        await WithQueryHome(async (node, placements, control, _) =>
        {
            var clock = new ServingTestClock(ReadClock);
            await using var home = await PartitionQueryHomeAuthority.OpenAsync(placements, clock);
            var completed = await ServingCompletion(node, home, DatabaseProfileId.PropertyGraph, false);
            placements.AfterQueryServingPublication = () => clock.UpperUncertainty = TimeSpan.FromSeconds(20);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => home.ValidateAsync([completed], default).AsTask());
            placements.AfterQueryServingPublication = null;
            clock.UpperUncertainty = TimeSpan.Zero;
            await home.ValidateAsync([completed], default); // Original publication remains authoritative.
            clock.Healthy = false;
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => home.ValidateAsync([completed], default).AsTask());
            clock.Healthy = true;
            await placements.ReleaseAsync(node.CurrentLease);
            clock.UpperUncertainty = TimeSpan.FromSeconds(20); // A paused/uncertain home cannot reuse an expired certificate.
            await Assert.ThrowsExactlyAsync<StaleShardPlacementException>(() => home.ValidateAsync([completed], default).AsTask());
            await Assert.ThrowsExactlyAsync<ShardServingGrantActiveException>(() => placements.AcquireAsync(new("a"), new("other"),
                new("https://localhost:22002"), new(node.Domain.Generation), Routing.MapVersion, TimeSpan.FromMinutes(1)).AsTask());
        });
    }

    [TestMethod]
    public async Task Serving_grants_ambiguous_publication_is_not_revoked_and_home_restart_reconciles()
    {
        await WithQueryHome(async (node, placements, control, _) =>
        {
            var completed = await ServingCompletion(node, control, DatabaseProfileId.PropertyGraph, false);
            await using (var home = await PartitionQueryHomeAuthority.OpenAsync(placements, ReadClock))
            {
                placements.AfterQueryServingPublication = () => throw new IOException("Lost publication reply.");
                await Assert.ThrowsExactlyAsync<IOException>(() => home.ValidateAsync([completed], default).AsTask());
                placements.AfterQueryServingPublication = null;
            }
            await using var restarted = await PartitionQueryHomeAuthority.OpenAsync(placements, ReadClock);
            await restarted.ValidateAsync([completed], default);
            await placements.ReleaseAsync(node.CurrentLease);
            await Assert.ThrowsExactlyAsync<ShardServingGrantActiveException>(() => placements.AcquireAsync(new("a"), new("other"),
                new("https://localhost:22002"), new(node.Domain.Generation), Routing.MapVersion, TimeSpan.FromMinutes(1)).AsTask());
        });
    }

    [TestMethod]
    public async Task Serving_grants_block_transfer_and_recovery_then_expire_for_reassignment()
    {
        await WithQueryHome(async (node, placements, control, _) =>
        {
            await using var home = await PartitionQueryHomeAuthority.OpenAsync(placements, ReadClock,
                new(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100)));
            var completed = await ServingCompletion(node, control, DatabaseProfileId.PropertyGraph, false);
            using var etcd = new EtcdClient(Environment.GetEnvironmentVariable("ENTITYFORGE_TEST_ETCD_ENDPOINT")!,
                configureChannelOptions: o => o.Credentials = ChannelCredentials.Insecure);
            var electedLease = await etcd.LeaseGrantAsync(new LeaseGrantRequest { TTL = 60 });
            var key = home.Binding.Namespace + "serving-transfer-controller";
            var value = ByteString.CopyFromUtf8("serving-test");
            var elected = await etcd.PutAsync(new PutRequest { Key = ByteString.CopyFromUtf8(key), Value = value, Lease = electedLease.ID });
            var controller = new ShardTransferControllerAuthority(key, elected.Header.Revision, electedLease.ID, value.ToByteArray());
            var transfer = new ShardTransferId(Guid.NewGuid());
            var destination = new ClusterNodeId("destination");
            var endpoint = new Uri("https://localhost:22003");
            try
            {
                await home.ValidateAsync([completed], default);
                await Assert.ThrowsExactlyAsync<ShardServingGrantActiveException>(() => placements.TransferAsync(transfer, node.CurrentLease,
                    destination, endpoint, Routing.MapVersion, TimeSpan.FromMinutes(1), controller).AsTask());
                ShardPlacementLease? next = null;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (next is null)
                {
                    try
                    {
                        next = await placements.TransferAsync(transfer, node.CurrentLease, destination, endpoint, Routing.MapVersion,
                        TimeSpan.FromMinutes(1), controller, timeout.Token);
                    }
                    catch (ShardServingGrantActiveException)
                    {
                        await Task.Delay(100, timeout.Token);
                    }
                }
                await Assert.ThrowsExactlyAsync<StaleShardPlacementException>(() => home.ValidateAsync([completed], default).AsTask());
                var grant = await placements.AcquireQueryServingGrantsAsync(home.Binding, [next.Placement.Claim], ReadClock,
                    TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100), default);
                Assert.AreEqual(next.Placement.Claim, grant[0].Claim);
                await placements.ReleaseAsync(next);
                await Assert.ThrowsExactlyAsync<ShardServingGrantActiveException>(() => placements.RecoverTransferredLeaseAsync(transfer,
                    node.CurrentLease.Placement.Claim, destination, endpoint).AsTask());
            }
            finally
            {
                await etcd.LeaseRevokeAsync(new LeaseRevokeRequest { ID = electedLease.ID });
            }
        });
    }

    [TestMethod]
    public async Task Serving_grants_survive_authority_isolation_only_while_clock_evidence_covers_them()
    {
        string? endpoint = Environment.GetEnvironmentVariable("ENTITYFORGE_TEST_ETCD_ENDPOINT");
        if (string.IsNullOrEmpty(endpoint))
        {
            Assert.Inconclusive("Set ENTITYFORGE_TEST_ETCD_ENDPOINT.");
            return;
        }
        var good = new EtcdClient(endpoint, configureChannelOptions: o => o.Credentials = ChannelCredentials.Insecure);
        using var unavailable = new EtcdClient("http://127.0.0.1:1", configureChannelOptions: o => o.Credentials = ChannelCredentials.Insecure);
        var clients = new List<EtcdClient> { good };
        string prefix = "/entityforge/serving-isolation/" + Guid.NewGuid().ToString("N") + "/";
        using var authority = new GrpcEtcdShardPlacementAuthority(clients, new([new(endpoint)], prefix, EndpointTimeout: TimeSpan.FromMilliseconds(250)));
        try
        {
            var owner = await authority.AcquireAsync(new("a"), new("owner"), new("https://localhost:22001"),
                new(Guid.NewGuid()), Routing.MapVersion, TimeSpan.FromMinutes(1));
            var binding = await authority.QueryBindingAsync(default);
            var clock = new ServingTestClock(ReadClock);
            await using var grants = new PartitionQueryServingGrants(authority, binding, clock, PartitionQueryServingOptions.Default);
            await grants.ValidateAsync([owner.Placement.Claim], default);
            clients[0] = unavailable; // Real failed RPC endpoint, not an observation callback.
            await grants.ValidateAsync([owner.Placement.Claim], default);
            Assert.AreEqual(1L, grants.Acquisitions);
            clock.UpperUncertainty = TimeSpan.FromSeconds(20);
            await Assert.ThrowsExactlyAsync<ShardPlacementAuthorityUnavailableException>(() => grants.ValidateAsync([owner.Placement.Claim], default).AsTask());
        }
        finally
        {
            clients[0] = good;
            await good.DeleteAsync(new DeleteRangeRequest { Key = ByteString.CopyFromUtf8(prefix), RangeEnd = ByteString.CopyFromUtf8(prefix[..^1] + "0") });
        }
    }

    [TestMethod]
    public async Task Serving_grants_complete_sealed_transfer_by_exact_retry_after_barrier_expiry()
    {
        await WithQueryHome(async (source, placements, control, decisions) =>
        {
            await using var home = await PartitionQueryHomeAuthority.OpenAsync(placements, ReadClock);
            var completed = await ServingCompletion(source, control, DatabaseProfileId.PropertyGraph, false);
            using var election = new EtcdClient(Environment.GetEnvironmentVariable("ENTITYFORGE_TEST_ETCD_ENDPOINT")!,
                configureChannelOptions: o => o.Credentials = ChannelCredentials.Insecure);
            var lease = await election.LeaseGrantAsync(new LeaseGrantRequest { TTL = 60 });
            var key = home.Binding.Namespace + "sealed-grant-transfer";
            var value = ByteString.CopyFromUtf8("sealed-grant-transfer");
            var elected = await election.PutAsync(new PutRequest { Key = ByteString.CopyFromUtf8(key), Value = value, Lease = lease.ID });
            try
            {
                var controller = new ShardTransferControllerAuthority(key, elected.Header.Revision, lease.ID, value.ToByteArray());
                await home.ValidateAsync([completed], default);
                string imagePath = Path.Combine(_root, "serving-image");
                var image = await source.SealAndExportAsync(new(Guid.NewGuid()), imagePath);
                await using var transfer = new PartitionOwnedTransfer(imagePath, Path.Combine(_root, "serving-destination"), image, Routing,
                    placements, new("destination"), new("https://localhost:22003"), Options, decisions, timestampClock: ReadClock);
                // Expire the real grant after the CAS failed but before its
                // outcome is classified. The atomic failure observation must
                // still identify the grant barrier, so exact retry remains usable.
                placements.AfterTransferCompareFailure = async () =>
                    await Task.Delay(PartitionQueryServingOptions.Default.Lifetime + TimeSpan.FromSeconds(1));
                try
                {
                    await Assert.ThrowsExactlyAsync<ShardServingGrantActiveException>(() => transfer.CompleteAsync(controller).AsTask());
                }
                finally
                {
                    placements.AfterTransferCompareFailure = null;
                }
                PartitionOwnedNode? destination = null;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (destination is null)
                {
                    try
                    {
                        destination = await transfer.CompleteAsync(controller, timeout.Token);
                    }
                    catch (ShardServingGrantActiveException)
                    {
                        await Task.Delay(100, timeout.Token);
                    }
                }
                await destination.EnableQueryHomeAsync();
                var fresh = await ServingCompletion(destination, home, DatabaseProfileId.PropertyGraph, false);
                await home.ValidateAsync([fresh], default);
                Assert.AreNotEqual(completed.Claim, fresh.Claim);
                await Assert.ThrowsExactlyAsync<StaleShardPlacementException>(() => home.ValidateAsync([completed], default).AsTask());
                Assert.AreSame(destination, await transfer.CompleteAsync(controller));
            }
            finally
            {
                await election.LeaseRevokeAsync(new LeaseRevokeRequest { ID = lease.ID });
            }
        });
    }
}
