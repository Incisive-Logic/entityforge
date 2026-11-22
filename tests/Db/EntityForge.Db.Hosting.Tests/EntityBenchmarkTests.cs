using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Item = EntityForge.Db.Hosting.Tests.BenchmarkItem;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class EntityBenchmarkTests
{

    [DataTestMethod]
    [TestCategory("EntityBenchmark")]
    [DataRow(DatabaseProfileId.PropertyGraph, "embedded-api")]
    [DataRow(DatabaseProfileId.PropertyGraph, "same-process-http")]
    [DataRow(DatabaseProfileId.Rdf, "embedded-api")]
    [DataRow(DatabaseProfileId.Rdf, "same-process-http")]
    [DataRow(DatabaseProfileId.Semantic, "embedded-api")]
    [DataRow(DatabaseProfileId.Semantic, "same-process-http")]
    [DataRow(DatabaseProfileId.PropertyGraph, "separate-process-http")]
    [DataRow(DatabaseProfileId.Rdf, "separate-process-http")]
    [DataRow(DatabaseProfileId.Semantic, "separate-process-http")]
    public Task Entity_benchmark_records_verified_operations(DatabaseProfileId profileId, string deployment) => RunAsync(profileId, deployment);

    [DataTestMethod]
    [TestCategory("EntityBenchmark")]
    [DataRow("same-process-http")]
    [DataRow("separate-process-http")]
    public Task Semantic_native_benchmark_records_verified_operations(string deployment) => RunAsync(DatabaseProfileId.Semantic, deployment);

    [DataTestMethod]
    [TestCategory("EntityBenchmark")]
    [DataRow(DatabaseProfileId.PropertyGraph, "same-process-http")]
    [DataRow(DatabaseProfileId.Rdf, "same-process-http")]
    [DataRow(DatabaseProfileId.Semantic, "same-process-http")]
    [DataRow(DatabaseProfileId.PropertyGraph, "separate-process-http")]
    [DataRow(DatabaseProfileId.Rdf, "separate-process-http")]
    [DataRow(DatabaseProfileId.Semantic, "separate-process-http")]
    public Task Graphql_benchmark_records_verified_operations(DatabaseProfileId profileId, string deployment) => RunAsync(profileId, deployment);

    [DataTestMethod]
    [TestCategory("EntityBenchmark")]
    [DataRow(DatabaseProfileId.PropertyGraph, "same-process-http")]
    [DataRow(DatabaseProfileId.PropertyGraph, "separate-process-http")]
    [DataRow(DatabaseProfileId.Rdf, "same-process-http")]
    [DataRow(DatabaseProfileId.Rdf, "separate-process-http")]
    public Task Native_mutation_benchmark_records_verified_operations(DatabaseProfileId profileId, string deployment) => RunAsync(profileId, deployment, true);

    private async Task RunAsync(DatabaseProfileId profileId, string deployment, bool native = false)
    {
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_ENTITY_BENCHMARK") != "1")
        {
            Assert.Inconclusive("Explicit benchmark opt-in required.");
        }
        int Setting(string key, int fallback, int min, int max)
        {
            string? text = Environment.GetEnvironmentVariable("ENTITYFORGE_ENTITY_BENCHMARK_" + key);
            int value = text is null ? fallback : int.Parse(text, CultureInfo.InvariantCulture);
            if (value < min || value > max)
            {
                throw new ArgumentOutOfRangeException(key);
            }
            return value;
        }
        int size = Setting("ROWS", 1000, 1, 1_000_000), iterations = Setting("ITERATIONS", 100, 1, 100_000);
        int concurrency = Setting("CLIENTS", 1, 1, 32), warmup = Setting("WARMUP", 20, 0, 10000);
        if (size < concurrency)
        {
            throw new ArgumentException("Entity workload requires at least one entity per client.");
        }
        string workload = Environment.GetEnvironmentVariable("ENTITYFORGE_ENTITY_BENCHMARK_WORKLOAD") ?? "entity-find";
        bool semanticQuery = workload.StartsWith("semantic-", StringComparison.Ordinal);
        bool afterWrite = workload.EndsWith("after-write", StringComparison.Ordinal);
        bool graphql = workload.StartsWith("graphql-", StringComparison.Ordinal);
        if (workload is not ("entity-find" or "entity-replace" or "entity-delete-recreate" or "entity-insert" or "entity-mixed" or "entity-balanced" or "entity-hot-key"
            or "semantic-gql-point" or "semantic-gql-filter" or "semantic-gql-aggregate" or "semantic-sparql-point" or "semantic-sparql-filter" or "semantic-sparql-aggregate"
            or "semantic-gql-after-write" or "semantic-sparql-after-write"
            or "graphql-projection" or "graphql-paged"))
        {
            throw new ArgumentException("Unknown entity workload.");
        }
        string output = Environment.GetEnvironmentVariable("ENTITYFORGE_ENTITY_BENCHMARK_REPORT_DIRECTORY") ?? throw new ArgumentException("Report directory required.");
        string profile = profileId == DatabaseProfileId.PropertyGraph ? "graph" : profileId == DatabaseProfileId.Rdf ? "rdf" : "semantic";
        string? queryTemplate = workload switch
        {
            "semantic-gql-point" => "MATCH (v) WHERE v.scalar = 'initial-KEY' RETURN v.scalar AS answer",
            "semantic-gql-after-write" => "MATCH (v) WHERE v.scalar = 'VALUE' RETURN v.scalar AS answer",
            "semantic-gql-filter" => "MATCH (v) WHERE LEFT(v.scalar, 8) = 'initial-' RETURN v.scalar AS answer",
            "semantic-gql-aggregate" => "MATCH (v) WHERE LEFT(v.scalar, 8) = 'initial-' RETURN COUNT(*) AS total",
            "semantic-sparql-point" => "SELECT ?value WHERE { ?s ?p ?value FILTER(?value = \"initial-KEY\") }",
            "semantic-sparql-after-write" => "SELECT ?value WHERE { ?s ?p ?value FILTER(?value = \"VALUE\") }",
            "semantic-sparql-filter" => "SELECT ?value WHERE { ?s ?p ?value FILTER(STRSTARTS(STR(?value), \"initial-\")) }",
            "semantic-sparql-aggregate" => "SELECT (COUNT(?value) AS ?total) WHERE { ?s ?p ?value FILTER(STRSTARTS(STR(?value), \"initial-\")) }",
            _ => null
        };
        string path = Path.Combine(Path.GetTempPath(), "entityforge-entity-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
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
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            await using var host = await BenchmarkDatabase.OpenAsync(path, profileId, deployment, deadline.Token);
            var runtime = host.Runtime;
            var http = host.Http;
            Func<Item, CancellationToken, Task> save;
            Func<string, CancellationToken, Task<Item?>> find;
            Func<string, CancellationToken, Task> delete;
            EntityMappingManifest manifest = null!;
            BenchmarkNativeClient? nativeClient = null;
            if (native)
            {
                nativeClient = new BenchmarkNativeClient(http, profileId, host.Incarnation);
                save = nativeClient.SaveAsync;
                find = nativeClient.FindAsync;
                delete = nativeClient.DeleteAsync;
            }
            else if (profileId == DatabaseProfileId.PropertyGraph)
            {
                IGraphEntityClient client = deployment == "embedded-api" ? runtime!.GraphDatabase.Operations() : new HttpGraphEntityClient(http, host.Incarnation);
                var store = client.EntityStore<Item>();
                manifest = store.Mapping.Manifest;
                save = (item, token) => ResolveConflictAsync(async id => { await store.SaveAsync(item, id, token); }, async id => (await client.AbortAsync(id, token)).State);
                find = async (id, token) => await store.FindAsync(id, token);
                delete = (key, token) => ResolveConflictAsync(async id => { await store.DeleteAsync(key, id, token); }, async id => (await client.AbortAsync(id, token)).State);
            }
            else if (profileId == DatabaseProfileId.Rdf)
            {
                IRdfEntityClient client = deployment == "embedded-api" ? runtime!.RdfDatabase.Operations() : new HttpRdfEntityClient(http, host.Incarnation);
                var store = client.EntityStore<Item>();
                manifest = store.Mapping.Manifest;
                save = (item, token) => ResolveConflictAsync(async id => { await store.SaveAsync(item, id, token); }, async id => (await client.AbortAsync(id, token)).State);
                find = async (id, token) => await store.FindAsync(id, token);
                delete = (key, token) => ResolveConflictAsync(async id => { await store.DeleteAsync(key, id, token); }, async id => (await client.AbortAsync(id, token)).State);
            }
            else
            {
                ISemanticEntityClient client = deployment == "embedded-api" ? runtime!.Database.Operations() : new HttpSemanticEntityClient(http, host.Incarnation);
                var store = client.EntityStore<Item>();
                manifest = store.Manifests[0];
                save = async (item, token) => { await store.SaveAsync(item, cancellationToken: token); };
                find = async (id, token) => await store.FindAsync(id, token);
                delete = async (id, token) => { await store.DeleteAsync(id, cancellationToken: token); };
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
                    catch (EntityOperationAbortedException)
                    {
                        Interlocked.Increment(ref abortedAttempts);
                        if (attempt == 15)
                        {
                            throw;
                        }
                        await Task.Delay(Math.Min(128, 1 << attempt), token);
                    }
                }
            }
            var scenario = new BenchmarkScenario(size, concurrency, workload,
                (item, token) => RetryAbortedAsync(() => save(item, token), token), find,
                (id, token) => RetryAbortedAsync(() => delete(id, token), token), deadline);
            var expected = scenario.Expected;
            var sampler = new WorkloadResourceSampler([path]);
            using var samplingStop = new CancellationTokenSource();
            Task sampling = sampler.RunAsync(samplingStop.Token, deadline);
            Task serverSampling = host.MonitorAsync(samplingStop.Token, deadline, sampler.MaximumWorkingSetBytes);
            try
            {
                long loadStarted = Stopwatch.GetTimestamp();
                if (nativeClient is null)
                {
                    await scenario.LoadAsync();
                }
                else
                {
                    await nativeClient.LoadAsync(expected, deadline.Token);
                }
                double loadSeconds = Stopwatch.GetElapsedTime(loadStarted).TotalSeconds;
                await scenario.VerifyAsync();
                if (semanticQuery)
                {
                    int generation = 0;
                    scenario.QueryWrites = afterWrite;
                    scenario.Query = async key =>
                    {
                        if (afterWrite)
                        {
                            // Worker stripes own distinct keys. A unique value proves visibility
                            // of this write; a stale cached view cannot satisfy the oracle.
                            var replacement = expected[key] with
                            {
                                Name = "updated-" + Interlocked.Increment(ref generation).ToString(CultureInfo.InvariantCulture)
                            };
                            await RetryAbortedAsync(() => save(replacement, deadline.Token), deadline.Token);
                            expected[key] = replacement;
                        }
                        string query = queryTemplate!.Replace("KEY", key.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
                        query = query.Replace("VALUE", expected[key].Name, StringComparison.Ordinal);
                        bool sparql = workload.StartsWith("semantic-sparql", StringComparison.Ordinal);
                        using var response = sparql ? await http.GetAsync("/sparql?query=" + Uri.EscapeDataString(query), deadline.Token)
                            : await http.PostAsJsonAsync("/v1/semantic/gql/query", new
                            {
                                query
                            }, deadline.Token);
                        string body = await response.Content.ReadAsStringAsync(deadline.Token);
                        Assert.IsTrue(response.IsSuccessStatusCode, body);
                        using var json = JsonDocument.Parse(body);
                        var rows = sparql ? json.RootElement.GetProperty("results").GetProperty("bindings") : json.RootElement.GetProperty("matches");
                        if (workload.EndsWith("aggregate", StringComparison.Ordinal))
                        {
                            Assert.AreEqual(1, rows.GetArrayLength());
                            long total = sparql ? long.Parse(rows[0].GetProperty("total").GetProperty("value").GetString()!, CultureInfo.InvariantCulture)
                                : rows[0].GetProperty("bindings").GetProperty("total").GetInt64();
                            Assert.AreEqual((long)size, total);
                        }
                        else
                        {
                            string[] values = rows.EnumerateArray().Select(row => sparql ? row.GetProperty("value").GetProperty("value").GetString()!
                                : row.GetProperty("bindings").GetProperty("answer").GetString()!).ToArray();
                            string[] answers = afterWrite || workload.EndsWith("point", StringComparison.Ordinal) ? [expected[key].Name] : expected.Select(item => item.Name).ToArray();
                            CollectionAssert.AreEquivalent(answers, values);
                        }
                    };
                }
                if (graphql)
                {
                    string property = manifest.ProfileBindings.Single(binding => binding.MemberIdentity == manifest.Members.Single(member => member.ClrName == "Name").Identity).StoredBinding;
                    bool semanticProfile = profileId == DatabaseProfileId.Semantic;
                    queryTemplate = semanticProfile
                        ? "query Items($type:URI!,$property:URI!,$first:Int!,$after:String,$lease:UUID!){entities(concept:$type,conceptMatchMode:direct,first:$first,after:$after,snapshotLeaseId:$lease){nodes{name:field(property:$property){values{value}}} pageInfo{hasNextPage endCursor snapshotVersion}}}"
                        : "query Items($type:String!,$property:String!,$first:Int!,$after:String,$version:Long){entities(type:$type,first:$first,after:$after,snapshotVersion:$version){nodes{name:field(source:$property){values}} pageInfo{hasNextPage endCursor snapshotVersion}}}";
                    scenario.Query = async queryKey =>
                    {
                        var values = new List<string>();
                        string? after = null;
                        long? version = null;
                        int first = workload == "graphql-paged" ? 16 : size;
                        Guid? lease = null;
                        if (semanticProfile)
                        {
                            using var leaseResponse = await http.PostAsJsonAsync("/v1/semantic/snapshot-leases", new SnapshotLeaseRequest(300), deadline.Token);
                            leaseResponse.EnsureSuccessStatusCode();
                            lease = (await leaseResponse.Content.ReadFromJsonAsync<SnapshotLeaseResponse>(deadline.Token))!.LeaseId;
                        }
                        try
                        {
                            for (int page = 0; ; page++)
                            {
                                Assert.IsTrue(page <= (size + first - 1) / first, "Unexpected pagination loop.");
                                using var response = await http.PostAsJsonAsync("/graphql", new
                                {
                                    query = queryTemplate,
                                    variables = new
                                    {
                                        type = manifest.ProfileTypeBinding,
                                        property,
                                        first,
                                        after,
                                        version,
                                        lease
                                    }
                                }, deadline.Token);
                                string body = await response.Content.ReadAsStringAsync(deadline.Token);
                                Assert.IsTrue(response.IsSuccessStatusCode, body);
                                using var json = JsonDocument.Parse(body);
                                Assert.IsFalse(json.RootElement.TryGetProperty("errors", out _), body);
                                var entities = json.RootElement.GetProperty("data").GetProperty("entities");
                                foreach (var node in entities.GetProperty("nodes").EnumerateArray())
                                {
                                    var field = node.GetProperty("name").GetProperty("values");
                                    Assert.AreEqual(1, field.GetArrayLength(), body);
                                    values.Add(semanticProfile ? field[0].GetProperty("value").GetString()! : field[0].GetString()!);
                                }
                                var info = entities.GetProperty("pageInfo");
                                if (!info.GetProperty("hasNextPage").GetBoolean())
                                {
                                    break;
                                }
                                string? next = info.GetProperty("endCursor").GetString();
                                Assert.IsFalse(string.IsNullOrWhiteSpace(next));
                                Assert.AreNotEqual(after, next);
                                after = next;
                                version = info.GetProperty("snapshotVersion").GetInt64();
                            }
                            CollectionAssert.AreEquivalent(expected.Select(item => item.Name).ToArray(), values);
                        }
                        finally
                        {
                            if (lease is not null)
                            {
                                using var release = await http.DeleteAsync("/v1/semantic/snapshot-leases/" + lease, deadline.Token);
                                release.EnsureSuccessStatusCode();
                            }
                        }
                    };
                }
                int verifiedWarmupRequests = await scenario.WindowAsync(warmup, "warmup", null);
                int setupAndWarmupAborts = abortedAttempts + (nativeClient?.AbortedAttempts ?? 0);
                var latency = new double[iterations];
                var serverBefore = await host.ObserveAsync(deadline.Token);
                using var process = Process.GetCurrentProcess();
                TimeSpan cpu = process.TotalProcessorTime;
                long allocated = GC.GetTotalAllocatedBytes();
                double measurementStartSampleSeconds = sampler.ElapsedSeconds;
                long started = Stopwatch.GetTimestamp();
                int verifiedRequests = await scenario.WindowAsync(iterations, "measured", latency);
                double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                double cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds;
                long allocatedBytes = GC.GetTotalAllocatedBytes() - allocated;
                double measurementEndSampleSeconds = sampler.ElapsedSeconds;
                var serverAfter = await host.ObserveAsync(deadline.Token);
                await scenario.VerifyAsync(warmup, iterations);
                await samplingStop.CancelAsync();
                await sampling; // A failed resource sampler cannot produce a completed result.
                await serverSampling;
                Array.Sort(latency);
                double Percentile(double q) => latency[(int)Math.Ceiling(q * iterations) - 1];
                report = new
                {
                    schema = 16,
                    status = "completed",
                    completedUtc = DateTimeOffset.UtcNow,
                    profile,
                    deployment,
                    workload,
                    size,
                    iterations,
                    concurrency,
                    warmup,
                    verifiedRequests,
                    verifiedWarmupRequests,
                    queryTemplate,
                    verifiedMutatingOperations = scenario.MeasuredWrites,
                    verifiedReadOperations = verifiedRequests - scenario.MeasuredWrites,
                    mappedDatasetVerified = !native,
                    nativeDatasetVerified = native,
                    api = native ? "native-language" : "mapped-entity",
                    loadSeconds,
                    loadedRows = size,
                    loadRowsPerSecond = size / loadSeconds,
                    loadContract = native ? "Native INSERT batches of at most 128 records; committed HTTP responses; verification excluded" : "Sequential mapped Save operations; committed responses; verification excluded",
                    setupAndWarmupAborts,
                    measuredAbortedAttempts = abortedAttempts + (nativeClient?.AbortedAttempts ?? 0) - setupAndWarmupAborts,
                    retryPolicy = "Up to sixteen attempts, only after a confirmed abort; exponential 1-128 ms delay included in operation latency. HTTP resolve_operation responses are explicitly aborted under their original identity; only confirmed aborts retry. Transport errors and unresolved/committed outcomes fail.",
                    mode = "fixed-request-count",
                    keyDistribution = scenario.KeyDistribution,
                    datasetDefinition = "Generated logical Item records: Id=key-i, Name=initial-i for i in [0,size). Worker stripes are disjoint except entity-hot-key, which overwrites key-0; replacement names encode phase and request ordinal. No random data or seed.",
                    datasetVerificationContract = "Generated identity/name pairs checked through selected API before/after, plus absence and inserts. Hot-key reads accept issued values; final value must be acknowledged. Other operations verify exact values/absence. No physical index or unknown-extra-identity audit.",
                    operationContract = afterWrite ? "Mapped replacement with a unique value followed by a native query verifying its visibility; whole write-plus-query latency, no forced OS cache flush or per-request rebuild guarantee under concurrency" : graphql ? "Complete GraphQL field projection, consuming and verifying every page and exact value bag" : semanticQuery ? "Execute asserted read-only semantic text query and verify exact result bag or aggregate against generated mapped values" : workload switch
                    {
                        "entity-find" => "Find + exact-value verification",
                        "entity-replace" => "Save replacement + Find + exact-value verification",
                        "entity-insert" => "Save new identity + Find + exact-value verification; final growing dataset verified",
                        "entity-mixed" => "Every fifth ordinal replaces and verifies; other ordinals find and verify; worker-disjoint keys",
                        "entity-balanced" => "Even ordinals replace and verify; odd ordinals find and verify; worker-disjoint keys",
                        "entity-hot-key" => "Concurrent overwrites of key-0; read issued values and verify final acknowledged value; no increment or linearizability claim",
                        _ => "Delete + Find absent + Save original + Find + exact-value verification"
                    },
                    timingContract = "Whole verified application operations including retries. Native Save includes existence query then GQL or SPARQL update. Loading and final checks excluded. Closed-loop; no forced cache flush. Embedded fixture retains an idle HTTP listener.",
                    seconds,
                    requestsPerSecond = verifiedRequests / seconds,
                    p50Milliseconds = Percentile(.50),
                    p95Milliseconds = Percentile(.95),
                    p99Milliseconds = Percentile(.99),
                    latencyMilliseconds = latency,
                    cpuSeconds,
                    allocatedBytes,
                    process.WorkingSet64,
                    process.PeakWorkingSet64,
                    serverProcessId = host.ServerProcessId,
                    serverBefore,
                    serverAfter,
                    host.PeakServerWorkingSetBytes,
                    host.PeakCombinedWorkingSetBytes,
                    databaseBytes = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length),
                    resourceObservations = sampler.Samples,
                    measurementStartSampleSeconds,
                    measurementEndSampleSeconds,
                    maximumWorkingSetBytes = sampler.MaximumWorkingSetBytes,
                    maximumFixtureBytes = sampler.MaximumFixtureBytes,
                    generatedDatasetSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((native ? "native-item-v1:" : "entity-item-v1:") + size.ToString(CultureInfo.InvariantCulture)))),
                    assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EntityForgeDbServer).Assembly.Location))),
                    loadedEntityForgeAssemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && a.GetName().Name?.StartsWith("EntityForge.", StringComparison.Ordinal) == true)
                        .OrderBy(a => a.GetName().Name, StringComparer.Ordinal).Select(a => new { name = a.GetName().Name, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) }).ToArray(),
                    os = RuntimeInformation.OSDescription,
                    runtime = RuntimeInformation.FrameworkDescription,
                    qualification = "Bounded verified application workload; main CPU/memory counters describe the client/fixture process; separate-process server observations are recorded separately. No cross-machine performance claim. Hot-key overwrites are not atomic increments or a linearizability proof. No crash durability or sustained-capacity qualification."
                };
            }
            finally
            {
                await samplingStop.CancelAsync();
                try
                {
                    await Task.WhenAll(sampling, serverSampling);
                }
                finally
                {
                    await File.WriteAllTextAsync(Path.ChangeExtension(reportPath, ".resources.json"), JsonSerializer.Serialize(new
                    {
                        schema = 1,
                        status = "observations-only",
                        profile,
                        deployment,
                        workload,
                        resourceObservations = sampler.Samples,
                        maximumWorkingSetBytes = sampler.MaximumWorkingSetBytes,
                        maximumFixtureBytes = sampler.MaximumFixtureBytes
                    }));
                }
            }
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        // Disposal/cleanup failure must leave the original incomplete report, not a completed result.
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task ResolveConflictAsync(Func<Guid, Task> action, Func<Guid, Task<EntityOperationState>> abort)
    {
        Guid id = Guid.NewGuid();
        try
        {
            await action(id);
        }
        catch (Exception failure) when (failure is EntityForge.Db.Graph.Contracts.GraphWriteConflictException
            or EntityForge.Db.Rdf.Contracts.RdfWriteConflictException
            or HttpRequestException { StatusCode: System.Net.HttpStatusCode.Conflict } || failure is HttpRequestException httpFailure && Equals(httpFailure.Data["Code"], "resolve_operation"))
        {
            Assert.AreEqual(EntityOperationState.Aborted, await abort(id), "A conflict alone does not authorize a new operation identity.");
            throw new EntityOperationAbortedException(id, failure);
        }
    }
}
