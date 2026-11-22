using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Hosting.Tests;
using EntityForge.Db.Hosting;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [TestCategory("DistributedBenchmark")]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public Task Distributed_benchmark_records_verified_operations(DatabaseProfileId profileId) => RunBenchmarkAsync(profileId, "distributed-loopback");

    [DataTestMethod]
    [TestCategory("DistributedBenchmark")]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public Task Distributed_process_benchmark_records_verified_operations(DatabaseProfileId profileId) => RunBenchmarkAsync(profileId, "distributed-processes");

    private async Task RunBenchmarkAsync(DatabaseProfileId profileId, string deployment)
    {
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_ENTITY_BENCHMARK") != "1")
        {
            Assert.Inconclusive("Explicit benchmark opt-in required, with isolated etcd.");
        }
        int Setting(string name, int fallback, int minimum, int maximum)
        {
            string? text = Environment.GetEnvironmentVariable("ENTITYFORGE_ENTITY_BENCHMARK_" + name);
            int value = text is null ? fallback : int.Parse(text, CultureInfo.InvariantCulture);
            if (value < minimum || value > maximum)
            {
                throw new ArgumentOutOfRangeException(name);
            }
            return value;
        }
        int size = Setting("ROWS", 100, 1, 10000), iterations = Setting("ITERATIONS", 100, 1, 100000);
        int concurrency = Setting("CLIENTS", 1, 1, 32), warmup = Setting("WARMUP", 20, 0, 10000);
        if (size < concurrency)
        {
            throw new ArgumentException("Rows must be at least Clients.");
        }
        string workload = Environment.GetEnvironmentVariable("ENTITYFORGE_ENTITY_BENCHMARK_WORKLOAD") ?? "entity-find";
        if (workload is not ("entity-find" or "entity-replace" or "entity-delete-recreate" or "entity-insert" or "entity-mixed" or "entity-balanced" or "entity-hot-key"))
        {
            throw new ArgumentException("Unknown distributed entity workload.");
        }
        string output = Environment.GetEnvironmentVariable("ENTITYFORGE_ENTITY_BENCHMARK_REPORT_DIRECTORY") ?? throw new ArgumentException("Report directory required.");
        Directory.CreateDirectory(output);
        string profile = profileId == DatabaseProfileId.PropertyGraph ? "graph" : profileId == DatabaseProfileId.Rdf ? "rdf" : "semantic";

        string reportPath = Path.Combine(output, $"{profile}-{deployment}-{workload}.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            schema = 16,
            status = "incomplete",
            profile,
            deployment,
            workload
        }));
        object? report = null;
        async Task Run(HttpPartitionEntityClient remote, int ownerCount, BenchmarkCluster? cluster)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            Func<BenchmarkItem, CancellationToken, Task> save;
            Func<string, CancellationToken, Task<BenchmarkItem?>> find;
            Func<string, CancellationToken, Task> delete;
            if (profileId == DatabaseProfileId.PropertyGraph)
            {
                save = async (item, token) => { await remote.SaveGraphAsync(item, token: token); };
                find = async (id, token) => (await remote.ReadGraphAsync<BenchmarkItem>(id, token: token)).Value;
                delete = async (id, token) => { await remote.DeleteGraphAsync<BenchmarkItem>(id, token: token); };
            }
            else if (profileId == DatabaseProfileId.Rdf)
            {
                save = async (item, token) => { await remote.SaveRdfAsync(item, token: token); };
                find = async (id, token) => (await remote.ReadRdfAsync<BenchmarkItem>(id, token: token)).Value;
                delete = async (id, token) => { await remote.DeleteRdfAsync<BenchmarkItem>(id, token: token); };
            }
            else
            {
                save = async (item, token) => { await remote.SaveSemanticAsync(item, token: token); };
                find = async (id, token) => (await remote.ReadSemanticAsync<BenchmarkItem>(id, token: token)).Value;
                delete = async (id, token) => { await remote.DeleteSemanticAsync<BenchmarkItem>(id, token: token); };
            }
            int abortedAttempts = 0;
            async Task RetryAbortedAsync(Func<Task> action, CancellationToken token)
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        await action();
                        return;
                    }
                    catch (EntityForge.Db.Mapping.EntityOperationAbortedException)
                    {
                        Interlocked.Increment(ref abortedAttempts);
                        if (attempt == 15)
                        {
                            throw;
                        }
                        // Only a durably aborted identity permits a fresh operation identity.
                        await Task.Delay(Math.Min(1 << attempt, 128), token);
                    }
                }
            }
            var scenario = new BenchmarkScenario(size, concurrency, workload,
                (item, token) => RetryAbortedAsync(() => save(item, token), token), find,
                (id, token) => RetryAbortedAsync(() => delete(id, token), token), deadline);
            var sampler = new WorkloadResourceSampler([_root]);
            using var stop = new CancellationTokenSource();
            Task sampling = sampler.RunAsync(stop.Token, deadline);
            Task monitoring = cluster?.MonitorAsync(stop.Token, deadline, sampler.MaximumWorkingSetBytes) ?? Task.CompletedTask;
            try
            {
                long loadStarted = Stopwatch.GetTimestamp();
                await scenario.LoadAsync();
                double loadSeconds = Stopwatch.GetElapsedTime(loadStarted).TotalSeconds;
                await scenario.VerifyAsync();
                int verifiedWarmupRequests = await scenario.WindowAsync(warmup, "warmup", null);
                int setupAndWarmupAborts = abortedAttempts;
                var latency = new double[iterations];
                using var process = Process.GetCurrentProcess();
                var clusterBefore = cluster is null ? null : await cluster.ObserveAsync(deadline.Token);
                TimeSpan cpu = process.TotalProcessorTime;
                long allocated = GC.GetTotalAllocatedBytes();
                long started = Stopwatch.GetTimestamp();
                int verifiedRequests = await scenario.WindowAsync(iterations, "measured", latency);
                double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                double cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds;
                long allocatedBytes = GC.GetTotalAllocatedBytes() - allocated;
                var clusterAfter = cluster is null ? null : await cluster.ObserveAsync(deadline.Token);
                await scenario.VerifyAsync(warmup, iterations);
                await stop.CancelAsync();
                await Task.WhenAll(sampling, monitoring);
                Array.Sort(latency);
                double Percentile(double q) => latency[(int)Math.Ceiling(iterations * q) - 1];
                report = new
                {
                    schema = 16,
                    status = "completed",
                    profile,
                    deployment,
                    workload,
                    size,
                    iterations,
                    concurrency,
                    warmup,
                    loadSeconds,
                    loadedRows = size,
                    loadRowsPerSecond = size / loadSeconds,
                    loadContract = "Sequential mapped Save through HTTPS gateway",
                    clusterBefore,
                    clusterAfter,
                    verifiedRequests,
                    verifiedWarmupRequests,
                    mappedDatasetVerified = true,
                    mode = "fixed-request-count",
                    keyDistribution = scenario.KeyDistribution,
                    verifiedMutatingOperations = scenario.MeasuredWrites,
                    verifiedReadOperations = verifiedRequests - scenario.MeasuredWrites,
                    setupAndWarmupAborts,
                    measuredAbortedAttempts = abortedAttempts - setupAndWarmupAborts,
                    retryPolicy = "At most sixteen write attempts; only durable aborts retry with a fresh operation identity and exponential 1-128 ms delay. Retry time is included. Uncertain commits and transport failures never restart automatically.",
                    operationContract = "Shared mapped-entity scenario: find, verified replacement, delete/recreate, growing insert, deterministic 80/20 or 50/50 mixed operations, or single-hot-key overwrites as selected by workload",
                    datasetVerificationContract = "Exact generated mapped identities and fields before/after, absent identity, every operation result verified; inserts additionally checked. Hot-key reads accept issued values and final value must be acknowledged from the measured phase; no increment or linearizability claim. Not a physical index audit.",
                    timingContract = "Whole verified application operations. Main counters describe the test process; child owner/gateway counters are separate when present. External etcd excluded. Setup/loading/final verification excluded.",
                    topology = new
                    {
                        ownerCount,
                        participantTransport = "HTTPS",
                        participantProcesses = cluster is null ? 1 : 3,
                        placementAuthority = "external-etcd",
                        transactionAuthority = cluster is null ? "local-file" : "external-etcd",
                        clock = "same-host test bounded clock; no cross-machine clock qualification",
                        storageLayout = "profile-owned"
                    },
                    seconds,
                    requestsPerSecond = verifiedRequests / seconds,
                    p50Milliseconds = Percentile(.5),
                    p95Milliseconds = Percentile(.95),
                    p99Milliseconds = Percentile(.99),
                    latencyMilliseconds = latency,
                    cpuSeconds,
                    allocatedBytes,
                    process.WorkingSet64,
                    process.PeakWorkingSet64,
                    databaseBytes = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length),
                    resourceObservations = sampler.Samples,
                    generatedDatasetSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("entity-item-v1:" + size.ToString(CultureInfo.InvariantCulture)))),
                    assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EntityForgeDbServer).Assembly.Location))),
                    loadedEntityForgeAssemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && a.GetName().Name?.StartsWith("EntityForge.", StringComparison.Ordinal) == true)
                        .OrderBy(a => a.GetName().Name, StringComparer.Ordinal).Select(a => new { name = a.GetName().Name, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) }).ToArray(),
                    runtime = RuntimeInformation.FrameworkDescription,
                    os = RuntimeInformation.OSDescription,
                    serverBefore = (object?)null,
                    serverAfter = (object?)null,
                    serverProcessId = (int?)null
                };
            }
            finally
            {
                await stop.CancelAsync();
                try
                {
                    await Task.WhenAll(sampling, monitoring);
                }
                finally
                {
                    await File.WriteAllTextAsync(Path.ChangeExtension(reportPath, ".resources.json"), JsonSerializer.Serialize(new
                    {
                        schema = 1,
                        status = "observations-only",
                        resourceObservations = sampler.Samples
                    }));
                }
            }
        }
        if (deployment == "distributed-processes")
        {
            string endpoint = Environment.GetEnvironmentVariable("ENTITYFORGE_TEST_ETCD_ENDPOINT") ?? throw new ArgumentException("Isolated etcd endpoint required.");
            using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await using var cluster = await BenchmarkCluster.OpenAsync(_root, profileId, endpoint, startup.Token);
            await Run(cluster.Client, 2, cluster);
        }
        else
        {
            await WithProfileFragments(profileId, async (_, _, _, remote, _, nodes, _) => await Run(remote, nodes.Length, null), bothCompact: true, allRemoteParticipants: true);
        }
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
