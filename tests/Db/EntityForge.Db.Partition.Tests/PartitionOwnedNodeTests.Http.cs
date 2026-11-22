using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    private const string PartitionToken = "partition-qualification-private-token";

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Https_local_mapped_save_uses_shared_request_arbitration_without_coordinated_decision(DatabaseProfileId profile)
    {
        var placement = new Authority();
        string authorityPath = Path.Combine(_root, "authority");
        await using var decisions = FilePartitionTransactionAuthority.Open(authorityPath);
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile,
            placement.Lease, Routing, placement, Options, transactionAuthority: decisions);
        using var certificate = PartitionCertificate();
        await using var service = new PartitionParticipantHttpService(node);
        await using var host = await StartPartitionHostAsync(service, certificate);
        using var client = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
        });
        var remote = new HttpPartitionTransactionParticipant(client, PartitionAddress(host), PartitionToken, node.Domain, decisions.Identity);
        Guid operation;
        byte[] captured;
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            var adapter = await node.CreateGraphExecutionAsync();
            var request = adapter.CaptureSave(new Item("one", "value"), Guid.NewGuid());
            operation = request.OperationId;
            captured = request.Export();
        }
        else if (profile == DatabaseProfileId.Rdf)
        {
            var adapter = await node.CreateRdfExecutionAsync();
            var request = adapter.CaptureSave(new Item("one", "value"), Guid.NewGuid());
            operation = request.OperationId;
            captured = request.Export();
        }
        else
        {
            var adapter = await node.CreateSemanticExecutionAsync();
            var request = adapter.CaptureSave(new Item("one", "value"), SemanticEntityWriteOperation.Create());
            operation = request.OperationId;
            captured = request.Export();
        }
        var result = await remote.ExecuteMappedAsync(operation, captured);
        var replay = await remote.ExecuteMappedAsync(operation, captured);
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Receipt.Outcome);
        Assert.AreEqual(result.Receipt.Version, replay.Receipt.Version);
        Assert.AreEqual(node.Domain.Identity, result.Completion.Members.Single().Identity);
        Assert.AreEqual(operation, result.Completion.OperationId);
        Assert.AreEqual(1, Directory.GetFiles(authorityPath, "*.application").Length, "The same operation identity must be arbitrated across owners.");
        Assert.AreEqual(0, Directory.GetFiles(authorityPath, "*.plan").Length, "Local saves do not need a coordinated native plan.");
        Assert.AreEqual(0, Directory.GetFiles(authorityPath, "*.decision").Length);
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Https_participants_recover_lost_application_reply_and_return_scoped_positions(DatabaseProfileId profile)
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
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile, a.Lease, routing, a, Options, transactionAuthority: decisions);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), profile, b.Lease, routing, b, Options, transactionAuthority: decisions);
        using var certificate = PartitionCertificate();
        await using var serviceA = new PartitionParticipantHttpService(first);
        await using var serviceB = new PartitionParticipantHttpService(second);
        await using var hostA = await StartPartitionHostAsync(serviceA, certificate);
        await using var hostB = await StartPartitionHostAsync(serviceB, certificate);
        var lostReply = new LosePartitionApplyReply(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
        });
        using var client = new HttpClient(lostReply) { Timeout = TimeSpan.FromSeconds(30) };
        var remoteA = new HttpPartitionTransactionParticipant(client, PartitionAddress(hostA), PartitionToken, first.Domain, decisions.Identity);
        var remoteB = new HttpPartitionTransactionParticipant(client, PartitionAddress(hostB), PartitionToken, second.Domain, decisions.Identity);
        Guid operation = Guid.NewGuid();
        var plan = new PartitionTransactionPlan(operation, routing.MapVersion,
            [new(first.Domain, new(0), NativePayload(profile, routing, new("a"), operation)),
                new(second.Domain, new(0), NativePayload(profile, routing, new("b"), operation))]);
        var coordinator = new PartitionTransactionCoordinator(decisions);
        var failure = await Assert.ThrowsExactlyAsync<PartitionDecisionDeliveryException>(async () =>
            await coordinator.ExecuteAsync(plan, [remoteA, remoteB]));
        Assert.IsTrue(failure.Decision.Commit, "Response loss must preserve the already durable shared decision.");
        Assert.AreEqual(1L, first.Kernel.CurrentVersion.Value);
        Assert.AreEqual(1L, second.Kernel.CurrentVersion.Value, "Parallel delivery drains the other owner even when one reply is lost.");
        var completed = await coordinator.ExecuteAsync(plan, [remoteA, remoteB]);
        Assert.IsTrue(completed.Decision.Commit);
        Assert.AreEqual(2, completed.Completion.Position.Positions.Count);
        Assert.IsTrue(completed.Completion.Members.All(member => member.Receipt.Version.Value == 1));
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, completed.Completion.Members[0].Receipt.Outcome);
        Assert.AreEqual(operation, completed.Completion.OperationId);
        var replay = await coordinator.ExecuteAsync(plan, [remoteA, remoteB]);
        Assert.IsTrue(replay.Completion.Members.All(member => member.Receipt.Outcome == DatabaseCommitOutcome.IdempotentReplay));
        await serviceA.DisposeAsync();
        var closed = await Assert.ThrowsExactlyAsync<HttpRequestException>(async () => await remoteA.ApplyPartitionDecisionAsync(operation));
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, closed.StatusCode);
    }

    [TestMethod]
    public async Task Https_participant_rejects_wrong_authentication_and_physical_target_before_preparation()
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var authority = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.PropertyGraph,
            authority.Lease, routing, authority, Options, transactionAuthority: decisions);
        using var certificate = PartitionCertificate();
        await using var service = new PartitionParticipantHttpService(node);
        await using var host = await StartPartitionHostAsync(service, certificate);
        using var client = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
        });
        var unauthorized = new HttpPartitionTransactionParticipant(client, PartitionAddress(host), "incorrect", node.Domain, decisions.Identity);
        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(async () => await unauthorized.ApplyPartitionDecisionAsync(Guid.NewGuid()));
        Assert.AreEqual(HttpStatusCode.Unauthorized, error.StatusCode);
        var wrongDomain = new PartitionExecutionDomain(node.Domain.Generation, node.Domain.Partition, node.Domain.RoutingFingerprint,
            node.Domain.Profile, Guid.NewGuid());
        var wrong = new HttpPartitionTransactionParticipant(client, PartitionAddress(host), PartitionToken, wrongDomain, decisions.Identity);
        error = await Assert.ThrowsExactlyAsync<HttpRequestException>(async () => await wrong.ApplyPartitionDecisionAsync(Guid.NewGuid()));
        Assert.AreEqual(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.AreEqual(0L, node.Kernel.CurrentVersion.Value);
    }

    private sealed class LosePartitionApplyReply(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _lost;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (request.RequestUri!.AbsolutePath.EndsWith("/apply", StringComparison.Ordinal) &&
                response.IsSuccessStatusCode && Interlocked.Exchange(ref _lost, 1) == 0)
            {
                response.Dispose();
                throw new IOException("Simulated loss after remote application returned.");
            }
            return response;
        }
    }

    private static X509Certificate2 PartitionCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(temporary.Export(X509ContentType.Pfx), null);
    }

    private static async ValueTask<WebApplication> StartPartitionHostAsync(PartitionParticipantHttpService service, X509Certificate2 certificate, int port = 0)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ENTITYFORGE_SEMANTIC_SCOPE_OUTPUT")))
        {
            builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Error);
        }
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        service.Map(app, new(PartitionToken));
        try
        {
            await app.StartAsync();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static Uri PartitionAddress(WebApplication host) => new(host.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
}
