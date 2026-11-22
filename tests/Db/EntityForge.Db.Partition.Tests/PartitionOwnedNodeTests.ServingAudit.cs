using System.Net;
using EntityForge.Db.Kernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow("clock")]
    [DataRow("generation")]
    [DataRow("partition")]
    public async Task Serving_completion_requires_home_clock_and_physical_association(string fault)
    {
        await WithQueryHome(async (node, _, home, _) =>
        {
            var completed = await ServingCompletion(node, home, DatabaseProfileId.PropertyGraph, false);
            var changed = fault switch
            {
                "clock" => completed with { Cut = completed.Cut with { ClockDomain = Guid.NewGuid() } },
                "generation" => completed with { Claim = completed.Claim with { Generation = new(Guid.NewGuid()) } },
                _ => completed with { Claim = completed.Claim with { Shard = new("b") } }
            };
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => home.ValidateAsync([changed], default).AsTask());
            Assert.AreEqual(0L, home.ServingAcquisitions);
        });
    }

    [TestMethod]
    public async Task Serving_completion_rechecks_cut_deadline_after_authority_acquisition()
    {
        await WithQueryHome(async (node, placements, home, _) =>
        {
            var completed = await ServingCompletion(node, home, DatabaseProfileId.PropertyGraph, false, TimeSpan.FromSeconds(1));
            var deadline = completed.Cut.Expires;
            bool observedPublication = false;
            placements.AfterQueryServingPublicationAsync = async () =>
            {
                observedPublication = true;
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining);
                }

                await Task.Delay(20);
            };
            try
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => home.ValidateAsync([completed], default).AsTask());
            }
            finally
            {
                placements.AfterQueryServingPublicationAsync = null;
            }
            Assert.IsTrue(observedPublication, "The real cut must expire during, not before, acquisition.");
            Assert.AreEqual(0L, home.ValidatedCompletions);
        });
    }

    [TestMethod]
    public async Task Serving_clock_domain_change_during_publication_fails_and_exact_domain_reconciles()
    {
        await WithQueryHome(async (node, placements, control, _) =>
        {
            var clock = new ServingTestClock(ReadClock);
            await using var home = await PartitionQueryHomeAuthority.OpenAsync(placements, clock);
            var completed = await ServingCompletion(node, control, DatabaseProfileId.PropertyGraph, false);
            placements.AfterQueryServingPublication = () => clock.DomainOverride = Guid.NewGuid();
            try
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => home.ValidateAsync([completed], default).AsTask());
            }
            finally
            {
                placements.AfterQueryServingPublication = null;
                clock.DomainOverride = null;
            }
            await home.ValidateAsync([completed], default);
            Assert.AreEqual(1L, home.ValidatedCompletions);
        });
    }

    [TestMethod]
    public async Task Serving_cancellation_after_publication_preserves_authority_without_reporting_success()
    {
        await WithQueryHome(async (node, placements, home, _) =>
        {
            var completed = await ServingCompletion(node, home, DatabaseProfileId.PropertyGraph, false);
            using var cancellation = new CancellationTokenSource();
            placements.AfterQueryServingPublication = cancellation.Cancel;
            try
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => home.ValidateAsync([completed], cancellation.Token).AsTask());
            }
            finally
            {
                placements.AfterQueryServingPublication = null;
            }
            Assert.AreEqual(0L, home.ValidatedCompletions);
            await home.ValidateAsync([completed], default);
            Assert.AreEqual(1L, home.ServingAcquisitions);
            Assert.AreEqual(1L, home.ValidatedCompletions);
            await placements.ReleaseAsync(node.CurrentLease);
            await Assert.ThrowsExactlyAsync<ShardServingGrantActiveException>(() => placements.AcquireAsync(node.Domain.Partition,
                new("replacement"), new("https://localhost:22002"), new(node.Domain.Generation), Routing.MapVersion,
                Options.LeaseLifetime).AsTask());
        });
    }

    [TestMethod]
    public async Task Serving_cache_shutdown_joins_refresh_and_rejects_waiting_work_without_revoking_grants()
    {
        await WithQueryHome(async (node, placements, control, _) =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var home = await PartitionQueryHomeAuthority.OpenAsync(placements, ReadClock);
            var completed = await ServingCompletion(node, control, DatabaseProfileId.PropertyGraph, false);
            placements.AfterQueryServingPublicationAsync = async () =>
            {
                entered.SetResult();
                await release.Task;
            };
            var first = home.ValidateAsync([completed], default).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var waiting = home.ValidateAsync([completed], default).AsTask();
            var closing = home.DisposeAsync().AsTask();
            try
            {
                Assert.IsFalse(closing.IsCompleted, "Shutdown must join the accepted refresh.");
                await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => home.ValidateAsync([completed], default).AsTask());
            }
            finally
            {
                release.SetResult();
                placements.AfterQueryServingPublicationAsync = null;
            }
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => first);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => waiting);
            await closing.WaitAsync(TimeSpan.FromSeconds(10));
            await home.DisposeAsync();
            await using var sibling = await PartitionQueryHomeAuthority.OpenAsync(placements, ReadClock);
            await sibling.ValidateAsync([completed], default);
        });
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Semantic, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    [DataRow(DatabaseProfileId.Semantic, true)]
    public async Task Public_gateway_uses_serving_grants_by_default_for_each_profile(DatabaseProfileId profile, bool failCleanup)
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        await WithQueryHome(async (first, placements, _, authority) =>
        {
            var lease = await placements.AcquireAsync(new("b"), new("owner-b"), new("https://localhost:22002"),
                new(first.Domain.Generation), routing.MapVersion, Options.LeaseLifetime);
            await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "default-b"), profile,
                lease, routing, placements, Options, transactionAuthority: authority, timestampClock: ReadClock);
            await using var local = new PartitionDatabaseNode(first);
            using var certificate = PartitionCertificate();
            await using var remoteService = new PartitionParticipantHttpService(second);
            await using var remoteHost = await StartPartitionHostAsync(remoteService, certificate);
            using var http = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
            });
            var decisions = new PartitionDecisionAuthority(authority);
            // No serving option, environment flag or explicit protocol selection.
            await using var gateway = new PartitionDatabaseGateway(decisions, routing,
                [new(local.Identity, new("https://localhost:22001")), new(second.Domain.Identity, PartitionAddress(remoteHost))],
                http, PartitionToken, placements, timestampClock: ReadClock, localNode: local);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            await using var app = builder.Build();
            gateway.MapApplication(app, new(PartitionToken));
            await app.StartAsync();
            var client = new HttpPartitionEntityClient(http, PartitionAddress(app), PartitionToken, first.Domain.Generation, profile);
            var result = profile switch
            {
                DatabaseProfileId.Rdf => await client.QuerySparqlAsync("SELECT ?s WHERE { ?s <urn:p> ?o }"),
                DatabaseProfileId.Semantic => await client.QuerySemanticAsync(new("urn:absent", new("urn:absent"), ConceptMatchMode: "direct")),
                _ => await client.QueryGqlAsync("MATCH (n) RETURN n")
            };
            await app.StopAsync();
            if (failCleanup)
            {
                // Retain the actual serving authority to test its lifetime, not
                // just the gateway's ingress flag, after a remote drain fails.
                var field = typeof(PartitionDatabaseGateway).GetField("_queryHome",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var home = (PartitionQueryHomeAuthority)field.GetValue(gateway)!;
                Assert.IsNotNull(home);
                var completed = await ServingCompletion(first, home, profile, false);
                await remoteHost.StopAsync();
                await Assert.ThrowsAsync<Exception>(() => gateway.DisposeAsync().AsTask());
                await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => home.ValidateAsync([completed], default).AsTask());
                await gateway.DisposeAsync();
                return;
            }
            await gateway.DisposeAsync();
            // Gateway retirement drains its parent pins but must not revoke the
            // issued grant, which another home may still use.
            await placements.ReleaseAsync(second.CurrentLease);
            await Assert.ThrowsExactlyAsync<ShardServingGrantActiveException>(() => placements.AcquireAsync(new("b"), new("replacement"),
                new("https://localhost:22003"), new(first.Domain.Generation), routing.MapVersion, Options.LeaseLifetime).AsTask());
            await remoteHost.StopAsync();
            Assert.AreEqual(0, second.ActiveQueryJobCount);
            Assert.AreEqual(0L, second.RetainedQueryWorkspaceBytes);
            Assert.AreEqual(0, second.Kernel.TimestampProtectionCount);
        }, profile: profile, routingOverride: routing);
    }
}
