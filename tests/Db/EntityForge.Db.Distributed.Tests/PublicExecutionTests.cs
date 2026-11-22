using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Reflection;
using dotnet_etcd;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;
using EntityForge.Db.Semantic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Distributed.Tests;

// Deliberately NOT a friend assembly. This fixture can use only the shipped public surface.
[TestClass]
public sealed partial class PublicExecutionTests
{
    private const string Token = "production-stack-qualification-internal-credential";

    [EntityType("public-stack-tests", "item")]
    public sealed record Item(string Id, string Name);

    private sealed class Clock(Guid domain) : IDatabaseBoundedClock
    {
        public Guid Domain => domain;

        public DatabaseClockInterval Read()
        {
            long t = DateTime.UtcNow.Ticks;
            return new(t - TimeSpan.TicksPerMillisecond, t + TimeSpan.TicksPerMillisecond);
        }
    }

    [TestMethod]
    public void Production_dependency_closure_does_not_load_legacy_execution()
    {
        var pending = new Queue<Assembly>();
        var visited = new HashSet<string>();
        pending.Enqueue(typeof(PartitionDatabaseNode).Assembly);
        while (pending.TryDequeue(out var assembly))
        {
            if (!visited.Add(assembly.FullName!))
            {
                continue;
            }

            Assert.IsFalse(assembly.GetName().Name!.Contains("Prototypes.Visibility", StringComparison.Ordinal));
            Assert.IsFalse(assembly.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>()
                .Any(a => a.AssemblyName == typeof(PublicExecutionTests).Assembly.GetName().Name),
                $"Public-only qualification must not have friend access to {assembly.GetName().Name}.");
            foreach (var reference in assembly.GetReferencedAssemblies().Where(r => r.Name!.StartsWith("EntityForge.", StringComparison.Ordinal)))
            {
                pending.Enqueue(Assembly.Load(reference));
            }
        }
        Assert.IsNull(typeof(PartitionDatabaseNode).Assembly.GetType("EntityForge.Db.Prototypes.Visibility.Sharding.DistributedTransactionRuntime"));
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, false, true)]
    [DataRow(DatabaseProfileId.Rdf, false, false)]
    [DataRow(DatabaseProfileId.Rdf, true, false)]
    [DataRow(DatabaseProfileId.Rdf, false, true)]
    [DataRow(DatabaseProfileId.Semantic, false, false)]
    [DataRow(DatabaseProfileId.Semantic, true, false)]
    public async Task Public_host_save_read_resolution_and_reopen(DatabaseProfileId profile, bool timestamps, bool nativeHome)
    {
        string? endpoint = Environment.GetEnvironmentVariable("ENTITYFORGE_ETCD_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Assert.Inconclusive("Set ENTITYFORGE_ETCD_ENDPOINT for live public composition qualification.");
        }

        string prefix = "/entityforge/production-stack-tests/" + Guid.NewGuid().ToString("N") + "/";
        string root = Path.Combine(Path.GetTempPath(), "ef-public-stack-" + Guid.NewGuid().ToString("N"));
        using var etcd = new EtcdClient(endpoint!);
        var placements = PartitionServingPlacement.OpenEtcd(etcd, prefix + "placements/");
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a")])).Binding;
        Guid generation = Guid.NewGuid();
        var control = await PartitionDecisionAuthority.OpenEtcdAsync(etcd, prefix + "decisions/", generation, profile, routing);
        var clock = new Clock(Guid.NewGuid());
        var options = new PartitionDatabaseNodeOptions { TimestampClock = clock };
        using var certificate = Certificate();
        var participant = Host(certificate);
        var application = Host(certificate);
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => c?.Thumbprint == certificate.Thumbprint });
        var directory = new Dictionary<LogicalShardId, PartitionRemoteOwner>();
        var decisions = nativeHome ? control.WithNativeHomes(routing, shard => directory[shard], http, Token, placements) : control;
        PartitionDatabaseNode? node = null;
        PartitionDatabaseGateway? gateway = null;
        try
        {
            var lease = await placements.AcquireAsync(new("a"), new("owner"), new Uri("https://127.0.0.1:1"), new(generation), routing.MapVersion, TimeSpan.FromMinutes(5));
            node = await PartitionDatabaseNode.CreateAsync(root, profile, lease, routing, placements, decisions, options: options);
            node.MapParticipant(participant, new(Token));
            await participant.StartAsync();
            directory.Add(new("a"), new(node.Identity, Address(participant)));
            await node.CompleteRecoveryAsync();
            gateway = new(decisions, routing, [new(node.Identity, Address(participant))], http, Token, placements, clock);
            gateway.MapApplication(application, new(Token + "-application"));
            await application.StartAsync();
            var client = new HttpPartitionEntityClient(http, Address(application), Token + "-application", generation, profile);
            var capabilities = await client.GetExecutionCapabilitiesAsync();
            Assert.AreEqual("bounded-clock-mvcc", capabilities.ReadProtocol);
            Assert.IsTrue(capabilities.DeclarativeQueries);
            Assert.AreEqual(profile != DatabaseProfileId.Semantic, capabilities.QueryUpdates);
            var item = new Item("one", "public");
            var receipt = profile switch
            {
                DatabaseProfileId.PropertyGraph => await client.SaveGraphAsync(item),
                DatabaseProfileId.Rdf => await client.SaveRdfAsync(item),
                _ => await client.SaveSemanticAsync(item)
            };
            var read = profile switch
            {
                DatabaseProfileId.PropertyGraph => await client.ReadGraphAsync<Item>(item.Id, minimumPosition: receipt.Position),
                DatabaseProfileId.Rdf => await client.ReadRdfAsync<Item>(item.Id, minimumPosition: receipt.Position),
                _ => await client.ReadSemanticAsync<Item>(item.Id, minimumPosition: receipt.Position)
            };
            Assert.AreEqual(item, read.Value);
            PartitionQueryResult queryResult;
            if (profile == DatabaseProfileId.PropertyGraph)
            {
                queryResult = await client.QueryGqlAsync("MATCH (n) RETURN n", receipt.Position);
                Assert.AreEqual(1, queryResult.Value.GetProperty("matches").GetArrayLength());
                var update = await client.ExecuteGqlUpdateAsync(EntityOperationIds.New(), "MATCH (n) SET n.queried = true");
                Assert.AreEqual(1, update.Position.Positions.Count);
            }
            else if (profile == DatabaseProfileId.Rdf)
            {
                queryResult = await client.QuerySparqlAsync("SELECT ?s WHERE { ?s ?p ?o }", receipt.Position);
                Assert.IsTrue(queryResult.Value.GetProperty("bindings").GetArrayLength() > 0);
                var update = await client.ExecuteSparqlUpdateAsync(EntityOperationIds.New(), "INSERT { ?s <urn:queried> \"yes\" } WHERE { ?s ?p ?o }");
                Assert.AreEqual(1, update.Position.Positions.Count);
            }
            else
            {
                var concept = new HttpSemanticEntityClient(http, generation).EntityStore<Item>().Manifests[0].ProfileTypeBinding;
                queryResult = await client.QuerySemanticAsync(new(concept, new(concept)), receipt.Position);
                Assert.AreEqual(1, queryResult.Value.GetProperty("entities").GetArrayLength());
                var graphql = await client.QueryGraphQlAsync(new($$"""{ entities(concept: "{{concept}}") { nodes { identity } pageInfo { hasNextPage } } }"""));
                Assert.AreEqual(1, graphql.Value.GetProperty("data").GetProperty("entities").GetProperty("nodes").GetArrayLength());
                Assert.IsFalse(graphql.Value.GetProperty("extensions").TryGetProperty("snapshotVersion", out _));
                bool streamCompleted = false;
                int streamedNodes = 0;
                await foreach (var frame in client.StreamGraphQlAsync(new($$"""{ entities(concept: "{{concept}}") { nodes { identity } pageInfo { hasNextPage } } }""")))
                {
                    if (frame.Kind == "item" && frame.Section == "/data/entities/nodes")
                    {
                        streamedNodes++;
                    }

                    if (frame.Kind == "complete")
                    {
                        streamCompleted = true;
                    }
                }
                Assert.IsTrue(streamCompleted && streamedNodes == 1);
            }
            Assert.IsTrue(queryResult.Position.Covers(receipt.Position));
            Assert.IsFalse(queryResult.Value.TryGetProperty("snapshotVersion", out _));

            Assert.AreEqual(EntityOperationState.Committed, (await client.ResolveAsync(receipt.OperationId)).State);
            var identity = node.Identity;
            await application.StopAsync();
            await gateway.DisposeAsync();
            gateway = null;
            await participant.StopAsync();
            await node.DisposeAsync();
            node = null;
            await using var reopened = await PartitionDatabaseNode.OpenAsync(root, profile, lease, routing, placements, decisions, options: options);
            Assert.AreEqual(identity, reopened.Identity);
        }
        finally
        {
            await application.DisposeAsync();
            if (gateway is not null)
            {
                await gateway.DisposeAsync();
            }

            await participant.DisposeAsync();
            if (node is not null)
            {
                await node.DisposeAsync();
            }
            // The test owns this exact generated directory and authority prefix.
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }

            var cleanup = new Etcdserverpb.TxnRequest();
            cleanup.Success.Add(new Etcdserverpb.RequestOp
            {
                RequestDeleteRange = new Etcdserverpb.DeleteRangeRequest
                {
                    Key = Google.Protobuf.ByteString.CopyFromUtf8(prefix),
                    RangeEnd = Google.Protobuf.ByteString.CopyFromUtf8(prefix[..^1] + "0")
                }
            });
            await etcd.TransactionAsync(cleanup);
        }
    }

    private static WebApplication Host(X509Certificate2 certificate)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certificate)));
        return builder.Build();
    }

    private static Uri Address(WebApplication app) => new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());

    private static X509Certificate2 Certificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var issued = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(issued.Export(X509ContentType.Pfx), null);
    }
}
