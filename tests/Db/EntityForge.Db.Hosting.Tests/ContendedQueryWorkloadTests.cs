using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Contended_native_updates_preserve_atomic_reads_and_acknowledgements(bool rdf)
    {
        int clients = Setting("CONTENTION_CLIENTS", 2, 2, 32);
        int steps = Setting("CONTENTION_STEPS", 20, 1, 1000);
        string profile = rdf ? "rdf" : "graph";
        string? reportPath = Environment.GetEnvironmentVariable("ENTITYFORGE_QUERY_BENCHMARK_REPORT_DIRECTORY") is { Length: > 0 } output
            ? Path.Combine(output, profile + "-contention.json") : null;
        if (reportPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                status = "incomplete",
                profile,
                clients,
                steps
            }));
        }
        _directory = NewDirectory();
        string oracleDirectory = NewDirectory();
        Directory.CreateDirectory(oracleDirectory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        CancellationToken token = deadline.Token;
        long successes = 0, conflicts = 0, reads = 0, readsOverlappingUpdates = 0;
        int activeUpdates = 0, peakActiveUpdates = 0;
        var updateLatency = new WorkloadLatencyHistogram();
        var conflictLatency = new WorkloadLatencyHistogram();
        var readLatency = new WorkloadLatencyHistogram();
        var sampler = new WorkloadResourceSampler([_directory, oracleDirectory]);
        using var samplingStop = new CancellationTokenSource();
        Task sampling = sampler.RunAsync(samplingStop.Token, deadline);
        string query = rdf
            ? "SELECT ?a ?b WHERE { <urn:counter> <urn:first> ?a ; <urn:second> ?b }"
            : "MATCH (n:Contention) RETURN n.firstValue AS a,n.secondValue AS b";
        string statement = rdf
            ? "DELETE { <urn:counter> <urn:first> ?a ; <urn:second> ?b } INSERT { <urn:counter> <urn:first> ?next ; <urn:second> ?next } WHERE { <urn:counter> <urn:first> ?a ; <urn:second> ?b BIND(?a + 1 AS ?next) }"
            : "MATCH (n:Contention) LET nextValue=n.firstValue+1 SET n.firstValue=nextValue,n.secondValue=nextValue";
        EntityForgeDbHost OpenHost()
        {
            var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory)
                .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0").ConfigureLogging(log => log.ClearProviders()));
            return (rdf ? builder.UseRdfProfile() : builder.UsePropertyGraphProfile()).Build();
        }
        async Task<int> Read(HttpClient http)
        {
            using var response = rdf
                ? await http.PostAsync("/sparql", new StringContent(query, Encoding.UTF8, "application/sparql-query"), token)
                : await http.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query
                }, token);
            string body = await response.Content.ReadAsStringAsync(token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
            using var document = JsonDocument.Parse(body);
            var rows = rdf ? document.RootElement.GetProperty("results").GetProperty("bindings")
                : document.RootElement.GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength(), "A concurrent snapshot must contain exactly one counter pair.");
            int Value(string name) => rdf
                ? Int32.Parse(rows[0].GetProperty(name).GetProperty("value").GetString()!, CultureInfo.InvariantCulture)
                : rows[0].GetProperty("bindings").GetProperty(name).GetInt32();
            int value = Value("a");
            Assert.AreEqual(value, Value("b"), "A reader observed a partially applied update.");
            return value;
        }
        async Task<HttpResponseMessage> Update(HttpClient http, Guid operation)
        {
            int active = Interlocked.Increment(ref activeUpdates);
            int peak;
            do
            {
                peak = Volatile.Read(ref peakActiveUpdates);
            }
            while (active > peak && Interlocked.CompareExchange(ref peakActiveUpdates, active, peak) != peak);
            try
            {
                return rdf
                    ? await http.PostAsync("/sparql/update", new StringContent(statement, Encoding.UTF8, "application/sparql-update"), token)
                    : await http.PostAsJsonAsync("/v1/graph/gql/update", new
                    {
                        statement,
                        transactionId = operation
                    }, token);
            }
            finally
            {
                Interlocked.Decrement(ref activeUpdates);
            }
        }
        long started = Stopwatch.GetTimestamp();
        double measuredSeconds;
        try
        {
            await using (var host = OpenHost())
            {
                await host.StartAsync(token);
                var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
                using (var seed = Client(host, WriterToken))
                {
                    using var response = rdf
                        ? await seed.PostAsync("/sparql/update", new StringContent(
                            "INSERT DATA { <urn:counter> <urn:first> 0 ; <urn:second> 0 }", Encoding.UTF8, "application/sparql-update"), token)
                        : await seed.PostAsJsonAsync("/v1/graph/gql/update", new
                        {
                            statement = "INSERT (n:Contention {firstValue:0,secondValue:0})",
                            transactionId = Guid.NewGuid()
                        }, token);
                    Assert.AreEqual(rdf ? HttpStatusCode.NoContent : HttpStatusCode.OK, response.StatusCode,
                        await response.Content.ReadAsStringAsync(token));
                }
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var writersDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                async Task Guard(Func<Task> action)
                {
                    try
                    {
                        await start.Task.WaitAsync(token);
                        await action();
                    }
                    catch
                    {
                        await deadline.CancelAsync();
                        throw;
                    }
                }
                Task[] writers = Enumerable.Range(0, clients).Select(actor => Guard(async () =>
                {
                    using var http = Client(host, WriterToken);
                    var operations = rdf ? null : new HttpGraphEntityClient(http,
                        ((IEntityMappingReplayHost)runtime.GraphDatabase).DatabaseIncarnation);
                    using var journal = new FileStream(Path.Combine(oracleDirectory, actor + ".acks"),
                        FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
                    for (int step = 0; step < steps; step++)
                    {
                        Guid operation = Guid.NewGuid();
                        long requestStart = Stopwatch.GetTimestamp();
                        using var response = await Update(http, operation);
                        string body = await response.Content.ReadAsStringAsync(token);
                        TimeSpan elapsed = Stopwatch.GetElapsedTime(requestStart);
                        if (!rdf && response.StatusCode == HttpStatusCode.Conflict)
                        {
                            using var problem = JsonDocument.Parse(body);
                            Assert.AreEqual("write_conflict", problem.RootElement.GetProperty("code").GetString());
                            Assert.AreEqual(EntityOperationState.Aborted, (await operations!.AbortAsync(operation)).State);
                            conflictLatency.Add(elapsed);
                            Interlocked.Increment(ref conflicts);
                            continue;
                        }
                        Assert.AreEqual(rdf ? HttpStatusCode.NoContent : HttpStatusCode.OK, response.StatusCode, body);
                        journal.Write(operation.ToByteArray());
                        journal.Flush(flushToDisk: true);
                        Interlocked.Increment(ref successes);
                        updateLatency.Add(elapsed);
                    }
                })).ToArray();
                Task reader = Guard(async () =>
                {
                    using var http = Client(host, ReaderToken);
                    int previous = 0;
                    do
                    {
                        long acknowledgedBeforeRead = Volatile.Read(ref successes);
                        bool overlapsUpdate = Volatile.Read(ref activeUpdates) > 0;
                        long requestStart = Stopwatch.GetTimestamp();
                        int actual = await Read(http);
                        readLatency.Add(Stopwatch.GetElapsedTime(requestStart));
                        Assert.IsTrue(actual >= previous && actual >= acknowledgedBeforeRead && actual <= clients * steps,
                            "Counter regressed, lost an acknowledged increment, or exceeded the attempted work.");
                        previous = actual;
                        Interlocked.Increment(ref reads);
                        if (overlapsUpdate)
                        {
                            Interlocked.Increment(ref readsOverlappingUpdates);
                        }
                        await Task.Delay(1, token);
                    }
                    while (!writersDone.Task.IsCompleted);
                });
                started = Stopwatch.GetTimestamp();
                start.SetResult();
                async Task JoinWriters()
                {
                    try
                    {
                        await Task.WhenAll(writers);
                    }
                    finally
                    {
                        writersDone.TrySetResult();
                    }
                }
                await Task.WhenAll(JoinWriters(), reader);
                measuredSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            }
        }
        finally
        {
            await samplingStop.CancelAsync();
            await sampling;
        }
        var acknowledged = new HashSet<Guid>();
        foreach (string path in Directory.EnumerateFiles(oracleDirectory, "*.acks"))
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, token);
            Assert.AreEqual(0, bytes.Length % 16, "Truncated independent acknowledgement log.");
            for (int offset = 0; offset < bytes.Length; offset += 16)
            {
                Assert.IsTrue(acknowledged.Add(new Guid(bytes.AsSpan(offset, 16))), "Duplicate acknowledgement identity.");
            }
        }
        Assert.IsTrue(successes > 0 && reads > 0);
        Assert.IsTrue(peakActiveUpdates >= 2 && readsOverlappingUpdates > 0,
            "Qualification requires overlapping client update requests and reads issued during updates.");
        Assert.AreEqual((long)clients * steps, successes + conflicts);
        Assert.AreEqual(0L, conflicts,
            "Updates through one hosted database must be admitted before snapshot capture; this fixture has no external writers.");
        Assert.AreEqual(successes, (long)acknowledged.Count);
        await using (var reopened = OpenHost())
        {
            await reopened.StartAsync(token);
            using var http = Client(reopened, ReaderToken);
            Assert.AreEqual(acknowledged.Count, await Read(http), "Persisted increments must equal independently logged acknowledgements.");
        }
        if (reportPath is not null)
        {
            var assemblies = Directory.EnumerateFiles(Path.GetDirectoryName(typeof(EntityForgeDbHostTests).Assembly.Location)!, "EntityForge*.dll")
                .Order(StringComparer.Ordinal).Select(path =>
                {
                    using var stream = File.OpenRead(path);
                    return new
                    {
                        name = Path.GetFileName(path),
                        sha256 = Convert.ToHexString(SHA256.HashData(stream))
                    };
                }).ToArray();
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                schema = 1,
                status = "verified",
                profile,
                clients,
                steps,
                successes,
                conflicts,
                reads,
                measuredSeconds,
                readsOverlappingUpdates,
                peakActiveUpdates,
                assemblies,
                platform = new
                {
                    RuntimeInformation.OSDescription,
                    RuntimeInformation.FrameworkDescription,
                    RuntimeInformation.ProcessArchitecture,
                    Environment.ProcessorCount
                },
                updates = updateLatency.Summary(),
                rejected = conflictLatency.Summary(),
                queries = readLatency.Summary(),
                resources = sampler.Samples,
                qualification = "Shared-key increment correctness and atomic snapshot observations; independent fsynced acknowledgement count verified after graceful restart. No conflict retries. Same-process HTTP clients, server and oracle; not power-loss, endurance or reference-hardware performance qualification."
            }), token);
        }
    }
}
