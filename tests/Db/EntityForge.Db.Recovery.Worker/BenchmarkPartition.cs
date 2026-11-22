using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using dotnet_etcd;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Kernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Recovery.Worker;

internal sealed record BenchmarkPartitionConfiguration(string Root, string Endpoint, string Prefix,
    Guid Generation, DatabaseProfileId Profile, string Role, int Port, string Token, string Certificate,
    PartitionRemoteOwner[] Owners);

internal sealed record BenchmarkPartitionReady(int ProcessId, string Address, PartitionRemoteOwner? Owner);

internal static class BenchmarkPartition
{
    // A same-machine fixture clock, never a production cross-host clock provider.
    private sealed class Clock(Guid domain) : IDatabaseBoundedClock
    {
        public Guid Domain => domain;

        public DatabaseClockInterval Read()
        {
            long ticks = DateTime.UtcNow.Ticks;
            return new(ticks - 10000, ticks + 10000);
        }
    }

    internal static async Task RunAsync()
    {
        var config = JsonSerializer.Deserialize<BenchmarkPartitionConfiguration>(await Console.In.ReadLineAsync()
            ?? throw new IOException("Missing partition benchmark configuration."))!;
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        using var etcd = new EtcdClient(config.Endpoint);
        using var placements = new GrpcEtcdShardPlacementAuthority([new EtcdClient(config.Endpoint)],
            new([new(config.Endpoint)], config.Prefix + "placements/", EndpointTimeout: TimeSpan.FromSeconds(2)));
        var decisions = await PartitionDecisionAuthority.OpenEtcdAsync(etcd, config.Prefix + "decisions/",
            config.Generation, config.Profile, routing);
        using var certificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(config.Certificate), null);
        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
        });
        var clock = new Clock(config.Generation);
        string address = $"https://127.0.0.1:{config.Port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, config.Port, listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        PartitionDatabaseNode? node = null;
        PartitionDatabaseGateway? gateway = null;
        try
        {
            PartitionRemoteOwner? owner = null;
            if (config.Role == "gateway")
            {
                gateway = new(decisions, routing, config.Owners, http, config.Token, placements, timestampClock: clock);
                gateway.MapApplication(app, new(config.Token));
                await app.StartAsync();
            }
            else
            {
                var lease = await placements.AcquireAsync(new(config.Role), new(config.Role), new(address),
                    new(config.Generation), routing.MapVersion, TimeSpan.FromMinutes(5));
                node = await PartitionDatabaseNode.CreateAsync(Path.Combine(config.Root, config.Role), config.Profile,
                    lease, routing, placements, decisions,
                    new ShardPlacementLeaseSupervisorOptions(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20)),
                    options: new PartitionDatabaseNodeOptions { TimestampClock = clock });
                node.MapParticipant(app, new(config.Token));
                await app.StartAsync();
                await node.CompleteRecoveryAsync(enableDecisionNotifications: true);
                owner = new(node.Identity, new(address));
            }
            await Reply(new BenchmarkPartitionReady(Environment.ProcessId, address, owner));
            while (await Console.In.ReadLineAsync() is { } command && command != "stop")
            {
                if (command != "observe")
                {
                    throw new ArgumentException("Unknown partition benchmark command.");
                }
                using var process = Process.GetCurrentProcess();
                await Reply(new BenchmarkServerObservation(process.TotalProcessorTime.TotalSeconds,
                    GC.GetTotalAllocatedBytes(), process.WorkingSet64, process.PeakWorkingSet64));
            }
        }
        finally
        {
            await app.StopAsync();
            if (gateway is not null)
            {
                await gateway.DisposeAsync();
            }
            if (node is not null)
            {
                await node.DisposeAsync();
            }
        }
    }

    private static async Task Reply<T>(T value)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value));
        await Console.Out.FlushAsync();
    }
}
