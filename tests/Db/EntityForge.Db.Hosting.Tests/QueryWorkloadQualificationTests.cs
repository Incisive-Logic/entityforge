using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Recovery.Worker;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("QueryBenchmark")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Native_query_benchmark_records_verified_results_and_environment(bool rdf)
    {
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_QUERY_BENCHMARK") != "1")
        {
            Assert.Inconclusive("Opt in with ENTITYFORGE_QUERY_BENCHMARK=1; this measurement is not a production qualification verdict.");
        }
        int size = Setting("ROWS", 1000, 1, 1_000_000);
        int iterations = Setting("ITERATIONS", 100, 1, 100_000);
        int concurrency = Setting("CLIENTS", 1, 1, 32);
        int warmup = Setting("WARMUP", 20, 0, 10_000);
        int warmupSeconds = Setting("WARMUP_SECONDS", 0, 0, 3600);
        int measurementSeconds = Setting("MEASUREMENT_SECONDS", 0, 0, 3600);
        int loadCheckpointEveryBatches = Setting("LOAD_CHECKPOINT_EVERY_BATCHES", 0, 0, 100_000);
        bool reopenAfterLoad = Setting("REOPEN_AFTER_LOAD", 0, 0, 1) == 1;
        bool restoreAfterLoad = Setting("RESTORE_AFTER_LOAD", 0, 0, 1) == 1;
        bool verifyInChildProcess = Setting("VERIFY_IN_CHILD_PROCESS", 0, 0, 1) == 1;
        QueryWorkloadProcessResult? childVerification = null;
        QueryWorkloadProcessObservation? childResourceObservation = null;
        int backupFixtureGiB = Setting("BACKUP_FIXTURE_GIB", 8, 8, 16);
        long maximumFixtureBytes = WorkloadBackupCapacity.SelectFixtureBytes(backupFixtureGiB, restoreAfterLoad);
        bool restoredBeforeVerification = false;
        double backupSeconds = 0, restoreSeconds = 0;
        string? backupSha256 = null;
        long backupAdditionalBytesReserved = 0;
        bool reopenedBeforeVerification = false, finalLoadCheckpointCreated = false;
        double reopenSeconds = 0;
        bool storedDatasetFullyVerified = false;
        double datasetVerificationSeconds = 0;
        int loadBatches = 0, loadCheckpoints = 0;
        double loadCheckpointSeconds = 0;
        string workload = Environment.GetEnvironmentVariable("ENTITYFORGE_QUERY_BENCHMARK_WORKLOAD") ?? "point-lookup";
        if (workload is not ("point-lookup" or "two-hop" or "two-hop-join" or "two-hop-product" or "grouped-aggregate" or "incoming-scan"))
        {
            throw new ArgumentException("Benchmark workload must be point-lookup, two-hop, two-hop-join, two-hop-product, grouped-aggregate or incoming-scan.");
        }
        bool aggregate = workload == "grouped-aggregate";
        bool incomingScan = workload == "incoming-scan";
        bool backgroundRoutes = Setting("BACKGROUND_ROUTES", 0, 0, 1) == 1;
        int requestedRdfQuads = rdf ? Setting("RDF_QUADS", 0, 0, 10_000_000) : 0;
        if (requestedRdfQuads is > 0 and < 100)
        {
            throw new ArgumentException("An exact RDF dataset must contain at least 100 quads.");
        }
        const int bucketCount = 10;
        const int aggregateTemporaryBytes = 64 * 1024 * 1024;
        bool hasRoutes = backgroundRoutes || workload is "two-hop" or "two-hop-join" or "two-hop-product" or "incoming-scan";
        if (requestedRdfQuads > 0)
        {
            size = requestedRdfQuads / (1 + (hasRoutes ? 5 : 0) + (aggregate ? 1 : 0));
        }
        int fanOut = hasRoutes ? Math.Min(size, 5) : 0;
        int remainderQuads = requestedRdfQuads == 0 ? 0 : requestedRdfQuads - size * (1 + fanOut + (aggregate ? 1 : 0));
        int expectedRdfQuads = size * (1 + fanOut + (aggregate ? 1 : 0)) + remainderQuads;
        string datasetShape = Environment.GetEnvironmentVariable("ENTITYFORGE_QUERY_BENCHMARK_DATASET_SHAPE") ?? "ring";
        if (datasetShape is not ("ring" or "hubs") || datasetShape == "hubs" && fanOut == 0)
        {
            throw new ArgumentException("Dataset shape must be ring, or hubs with route edges enabled.");
        }
        const int seed = 39075;
        var keyDistribution = new WorkloadKeyDistribution(
            Environment.GetEnvironmentVariable("ENTITYFORGE_QUERY_BENCHMARK_KEY_DISTRIBUTION") ?? "uniform",
            incomingScan && datasetShape == "hubs" ? fanOut : size);
        if (aggregate && keyDistribution.Mode != "uniform")
        {
            throw new ArgumentException("KEY_DISTRIBUTION=hotspot is not applicable to complete grouped aggregation.");
        }

        var random = new Random(seed);
        int[] keys = Enumerable.Range(0, iterations + warmup)
            .Select(_ => keyDistribution.Next(random)).ToArray();
        string profile = rdf ? "rdf" : "graph";
        string? reportPath = null;
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_QUERY_BENCHMARK_REPORT_DIRECTORY") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            reportPath = Path.Combine(output, profile + "-" + workload + (datasetShape == "ring" ? "" : "-" + datasetShape) +
                (keyDistribution.Mode == "uniform" ? "" : "-hotspot") + ".json");
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                status = "incomplete",
                startedUtc = DateTimeOffset.UtcNow,
                workload,
                datasetShape,
                keyDistribution = keyDistribution.Mode,
                profile,
                size,
                iterations,
                concurrency,
                warmupSeconds,
                measurementSeconds,
                backgroundRoutes,
                requestedRdfQuads,
                remainderQuads,
                loadCheckpointEveryBatches,
                reopenAfterLoad,
                restoreAfterLoad,
                backupFixtureGiB,
                maximumFixtureBytes,
                verifyInChildProcess,
                childVerification,
                childResourceObservation,
                detail = "This attempt has not completed; inspect the test result for failures."
            }));
        }
        _directory = NewDirectory();
        string activeDirectory = _directory;
        string? backupDirectory = restoreAfterLoad ? NewDirectory() : null;
        string? verificationDirectory = verifyInChildProcess ? NewDirectory() : null;
        if (verificationDirectory is not null)
        {
            Directory.CreateDirectory(verificationDirectory);
        }
        EntityForgeDbHost BuildHost()
        {
            var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(activeDirectory)
            .UseQueryScans(new()
            {
                PageSize = 256,
                MaximumActiveScans = concurrency,
                MaximumSortTemporaryBytes = aggregate ? aggregateTemporaryBytes : 0
            })
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0").ConfigureLogging(log => log.ClearProviders()));
            return (rdf ? builder.UseRdfProfile() : builder.UsePropertyGraphProfile()).Build();
        }
        int deadlineSeconds = 3600 + warmupSeconds + measurementSeconds;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(deadlineSeconds));
        await using var hostOwner = new QueryWorkloadHost(BuildHost);
        var host = await hostOwner.OpenAsync(deadline.Token);
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        using var samplingStop = new CancellationTokenSource();
        int sampleIntervalSeconds = Math.Max(1, (int)Math.Ceiling(deadlineSeconds / 4090D));
        var resourceSampler = new WorkloadResourceSampler(new[] { _directory, backupDirectory, verificationDirectory }.OfType<string>().ToArray(),
            intervalSeconds: sampleIntervalSeconds, maximumFixtureBytes: maximumFixtureBytes);
        var phases = new List<QueryWorkloadPhase>();
        void Phase(string name) => phases.Add(new(name, resourceSampler.ElapsedSeconds));
        Task sampling = resourceSampler.RunAsync(samplingStop.Token, deadline);
        async Task CheckpointLoad()
        {
            long checkpointStarted = Stopwatch.GetTimestamp();
            try
            {
                await runtime.CreateCheckpointAsync(deadline.Token);
                loadCheckpoints++;
            }
            finally
            {
                loadCheckpointSeconds += Stopwatch.GetElapsedTime(checkpointStarted).TotalSeconds;
            }
        }
        async Task LoadedBatch()
        {
            loadBatches++;
            if (loadCheckpointEveryBatches != 0 && loadBatches % loadCheckpointEveryBatches == 0)
            {
                await CheckpointLoad();
            }
        }
        try
        {
            Phase("load");
            using var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            string Lexical(int key) => key.ToString(CultureInfo.InvariantCulture);
            Guid Identity(int key, int offset = 0)
            {
                byte[] bytes = new byte[16];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, key + 1);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), offset);
                return new(bytes);
            }
            for (int key = 0; key < size; key++)
            {
                checksum.AppendData(Encoding.UTF8.GetBytes("n," + Lexical(key) + "\n"));
                if (aggregate)
                {
                    checksum.AppendData(Encoding.UTF8.GetBytes($"b,{Lexical(key)},{Lexical(key % bucketCount)}\n"));
                }
            }
            foreach (int[] batch in Enumerable.Range(0, size).Chunk(1000))
            {
                if (rdf)
                {
                    var mutations = new List<RdfMutation>();
                    foreach (int key in batch)
                    {
                        var subject = new RdfIri("urn:benchmark:" + Lexical(key));
                        mutations.Add(new AssertRdfQuad(new(subject, new("urn:key"), new RdfLiteral(Lexical(key)))));
                        if (aggregate)
                        {
                            mutations.Add(new AssertRdfQuad(new(subject, new("urn:bucket"), new RdfLiteral(Lexical(key % bucketCount)))));
                        }
                    }
                    await runtime.RdfDatabase.CommitAsync(new(Guid.NewGuid(), mutations), deadline.Token);
                }
                else
                {
                    var mutations = new List<GraphMutation>(batch.Length * 2);
                    foreach (int key in batch)
                    {
                        var vertex = new GraphVertexId(Identity(key));
                        mutations.Add(new CreateVertex(vertex, ["Benchmark"]));
                        mutations.Add(new SetVertexProperty(vertex, "key", new DatabaseValue.Int64(key)));
                        if (aggregate)
                        {
                            mutations.Add(new SetVertexProperty(vertex, "bucket", new DatabaseValue.Int64(key % bucketCount)));
                        }
                    }
                    await runtime.GraphDatabase.CommitAsync(new(Guid.NewGuid(), mutations), deadline.Token);
                }
                await LoadedBatch();
            }
            if (fanOut != 0)
            {
                foreach (int[] batch in Enumerable.Range(0, size).Chunk(1000 / fanOut))
                {
                    var graphMutations = new List<GraphMutation>();
                    var rdfMutations = new List<RdfMutation>();
                    foreach (int key in batch)
                    {
                        for (int offset = 1; offset <= fanOut; offset++)
                        {
                            int target = datasetShape == "hubs" ? offset - 1 : (key + offset) % size;
                            checksum.AppendData(Encoding.UTF8.GetBytes($"e,{Lexical(key)},{Lexical(target)}\n"));
                            if (rdf)
                            {
                                rdfMutations.Add(new AssertRdfQuad(new(new RdfIri("urn:benchmark:" + Lexical(key)),
                                    new("urn:route"), new RdfIri("urn:benchmark:" + Lexical(target)))));
                            }
                            else
                            {
                                graphMutations.Add(new CreateEdge(new(Identity(key, offset)), new(Identity(key)), new(Identity(target)), "route"));
                            }
                        }
                    }
                    if (rdf)
                    {
                        await runtime.RdfDatabase.CommitAsync(new(Guid.NewGuid(), rdfMutations), deadline.Token);
                    }
                    else
                    {
                        await runtime.GraphDatabase.CommitAsync(new(Guid.NewGuid(), graphMutations), deadline.Token);
                    }
                    await LoadedBatch();
                }
            }
            if (rdf && remainderQuads > 0)
            {
                var mutations = new List<RdfMutation>();
                for (int index = 0; index < remainderQuads; index++)
                {
                    checksum.AppendData(Encoding.UTF8.GetBytes($"remainder,{Lexical(index)}\n"));
                    mutations.Add(new AssertRdfQuad(new(new RdfIri("urn:benchmark:remainder:" + Lexical(index)),
                        new("urn:remainder"), new RdfLiteral(Lexical(index)))));
                }
                await runtime.RdfDatabase.CommitAsync(new(Guid.NewGuid(), mutations), deadline.Token);
                await LoadedBatch();
            }
            if (reopenAfterLoad || restoreAfterLoad || verifyInChildProcess)
            {
                Phase("final-load-checkpoint");
                if (loadCheckpointEveryBatches == 0 || loadBatches % loadCheckpointEveryBatches != 0)
                {
                    await CheckpointLoad();
                    finalLoadCheckpointCreated = true;
                }
                long reopenStarted = Stopwatch.GetTimestamp();
                // Do not keep the disposed runtime reachable while its replacement recovers.
                runtime = null!;
                host = null!;
                try
                {
                    Phase("stop-loaded-host");
                    await hostOwner.DisposeAsync();
                    if (restoreAfterLoad)
                    {
                        string checkpointPath = Path.Combine(_directory, EntityForgeDbHostOptions.DefaultCheckpointFileName);
                        string pagePath = Path.Combine(_directory, EntityForgeDbHostOptions.DefaultPageFileName);
                        string journalPath = Path.Combine(_directory, EntityForgeDbHostOptions.DefaultChangeJournalFileName);
                        long journalBytes = Directory.Exists(journalPath)
                            ? new DirectoryInfo(journalPath).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length) : 0;
                        backupAdditionalBytesReserved = WorkloadBackupCapacity.RequiredAdditionalBytes(
                            new FileInfo(checkpointPath).Length, new FileInfo(pagePath).Length, journalBytes);
                        long fixtureBytes = new DirectoryInfo(_directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
                        WorkloadBackupCapacity.Require(fixtureBytes,
                            WorkloadVolume.AvailableFreeBytes(backupDirectory!), backupAdditionalBytesReserved, maximumFixtureBytes);
                        var backupManager = new LocalDatabaseBackupManager();
                        string archivePath = Path.Combine(backupDirectory!, "dataset.efbackup");
                        long backupStarted = Stopwatch.GetTimestamp();
                        Phase("backup");
                        var backup = await backupManager.CreateAsync(checkpointPath, journalPath, pagePath, archivePath, deadline.Token);
                        backupSeconds = Stopwatch.GetElapsedTime(backupStarted).TotalSeconds;
                        backupSha256 = backup.Sha256;
                        activeDirectory = Path.Combine(backupDirectory!, "restored");
                        long restoreStarted = Stopwatch.GetTimestamp();
                        Phase("restore");
                        var restored = await backupManager.RestoreAsync(archivePath, activeDirectory,
                            EntityForgeDbHostOptions.DefaultCheckpointFileName, EntityForgeDbHostOptions.DefaultCommitLogFileName,
                            EntityForgeDbHostOptions.DefaultManifestFileName, EntityForgeDbHostOptions.DefaultChangeJournalFileName,
                            EntityForgeDbHostOptions.DefaultPageFileName, new GroupedRedoRestoreLogInitializer(), deadline.Token);
                        restoreSeconds = Stopwatch.GetElapsedTime(restoreStarted).TotalSeconds;
                        Assert.AreEqual(backup, restored);
                    }
                    if (verifyInChildProcess)
                    {
                        Phase("child-verification");
                        var request = new QueryDatasetVerificationRequest(activeDirectory,
                            Path.Combine(verificationDirectory!, "result.json"), Guid.NewGuid(), rdf,
                            size, fanOut, aggregate, datasetShape == "hubs", remainderQuads);
                        childVerification = await QueryWorkloadProcessVerifier.VerifyAsync(request,
                            Path.Combine(verificationDirectory!, "request.json"), resourceSampler.MaximumWorkingSetBytes, deadline.Token,
                            observed: observation => childResourceObservation = observation);
                        Assert.AreEqual((long)(rdf ? expectedRdfQuads : size), childVerification.Verification.Entities);
                        Assert.AreEqual(rdf ? 0L : (long)size * fanOut, childVerification.Verification.Edges);
                    }
                    Phase("reopen");
                    host = await hostOwner.OpenAsync(deadline.Token);
                    runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
                    reopenedBeforeVerification = true;
                    restoredBeforeVerification = restoreAfterLoad;
                }
                finally
                {
                    reopenSeconds = Stopwatch.GetElapsedTime(reopenStarted).TotalSeconds;
                }
            }
            long verifiedDatasetEntities = 0;
            long verifiedDatasetEdges = 0;
            long verificationStarted = Stopwatch.GetTimestamp();
            Phase("parent-dataset-verification");
            var datasetOracle = new QueryWorkloadDatasetOracle(size, fanOut, aggregate, datasetShape == "hubs", remainderQuads);
            try
            {
                if (rdf)
                {
                    await foreach (var page in runtime.RdfDatabase.ReadScanPagesAsync(new(), 1000, expectedRdfQuads + 1, deadline.Token))
                    {
                        foreach (var quad in page.Quads)
                        {
                            datasetOracle.Observe(quad);
                        }
                        verifiedDatasetEntities += page.Quads.Count;
                    }
                    datasetOracle.Complete();
                    Assert.AreEqual((long)expectedRdfQuads, verifiedDatasetEntities, "Stored RDF dataset cardinality differs from its manifest.");
                }
                else
                {
                    using var lease = await runtime.GraphDatabase.AcquireSnapshotAsync(cancellationToken: deadline.Token);
                    var snapshot = await runtime.GraphDatabase.CaptureScanSnapshotAsync(lease, deadline.Token);
                    datasetOracle.VerifyGraph(snapshot.View.Vertices, snapshot.View.Edges, deadline.Token);
                    verifiedDatasetEntities = snapshot.View.Vertices.Count;
                    verifiedDatasetEdges = snapshot.View.Edges.Count;
                }
                storedDatasetFullyVerified = true;
            }
            finally
            {
                datasetVerificationSeconds = Stopwatch.GetElapsedTime(verificationStarted).TotalSeconds;
            }
            string queryTemplate = (rdf, workload) switch
            {
                (true, "incoming-scan") => "SELECT ?value WHERE { ?source <urn:route> <urn:benchmark:KEY> . ?source <urn:key> ?value }",
                (false, "incoming-scan") => "MATCH (hub:Benchmark {key:KEY})<-[:route]-(source) RETURN source.key AS answer",
                (true, "grouped-aggregate") => "SELECT ?bucket (COUNT(*) AS ?total) WHERE { ?s <urn:bucket> ?bucket } GROUP BY ?bucket",
                (false, "grouped-aggregate") => "MATCH (n:Benchmark) RETURN n.bucket AS bucket, COUNT(*) AS total",
                (true, "two-hop-join") => "SELECT ?value WHERE { <urn:benchmark:KEY> <urn:route> ?middle . ?middle <urn:route> ?end . ?end <urn:key> ?value }",
                (false, "two-hop-join") => "MATCH REPEATABLE ELEMENTS (n:Benchmark {key:KEY})-[:route]->(middle) MATCH REPEATABLE ELEMENTS (middle)-[:route]->(m) RETURN m.key AS answer",
                (true, "two-hop-product") => "SELECT ?value WHERE { <urn:benchmark:KEY> <urn:route> ?middle . ?middle <urn:route> ?end . ?end <urn:key> ?value }",
                (false, "two-hop-product") => "MATCH REPEATABLE ELEMENTS p=(n:Benchmark {key:KEY})-[:route]->(middle), q=(middle)-[:route]->(m) RETURN m.key AS answer",
                (true, "two-hop") => "SELECT ?value WHERE { <urn:benchmark:KEY> <urn:route>/<urn:route> ?end . ?end <urn:key> ?value }",
                (false, "two-hop") => "MATCH REPEATABLE ELEMENTS (n:Benchmark {key:KEY})-[:route]->()-[:route]->(m) RETURN m.key AS answer",
                (true, _) => "SELECT ?value WHERE { <urn:benchmark:KEY> <urn:key> ?value }",
                _ => "MATCH (n:Benchmark {key:KEY}) RETURN n.key AS answer"
            };
            using var client = Client(host);
            client.Timeout = TimeSpan.FromSeconds(30);
            async Task Query(int key, CancellationToken token)
            {
                string query = queryTemplate.Replace("KEY", Lexical(key), StringComparison.Ordinal);
                if (incomingScan)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post,
                        rdf ? "/v1/rdf/sparql/scan" : "/v1/graph/gql/query/scan")
                    {
                        Content = JsonContent.Create(new { query })
                    };
                    using var scannedResponse = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    if (scannedResponse.StatusCode != System.Net.HttpStatusCode.OK)
                    {
                        Assert.Fail(await scannedResponse.Content.ReadAsStringAsync(token));
                    }
                    Assert.AreEqual(System.Net.HttpStatusCode.OK, scannedResponse.StatusCode);
                    await using var stream = await scannedResponse.Content.ReadAsStreamAsync(token);
                    using var reader = new StreamReader(stream);
                    // One bounded bit per source key, rather than retaining
                    // JSON rows or trusting a count that could hide duplicates.
                    var seen = new System.Collections.BitArray(size);
                    int count = 0, completed = 0;
                    while (await reader.ReadLineAsync(token) is { } line)
                    {
                        using var frame = JsonDocument.Parse(line);
                        string? type = frame.RootElement.GetProperty("type").GetString();
                        if (type == "complete")
                        {
                            Assert.IsTrue(frame.RootElement.GetProperty("complete").GetBoolean());
                            completed++;
                            continue;
                        }
                        Assert.AreEqual("page", type, line);
                        Assert.AreEqual(0, completed, "Rows followed completion.");
                        foreach (var row in frame.RootElement.GetProperty("rows").EnumerateArray())
                        {
                            int sourceKey = rdf
                                ? Int32.Parse(row.GetProperty("value").GetProperty("lexicalForm").GetString()!, CultureInfo.InvariantCulture)
                                : row.GetProperty("bindings").GetProperty("answer").GetInt32();
                            Assert.IsTrue(sourceKey >= 0 && sourceKey < size, "Unexpected source key.");
                            Assert.IsFalse(seen[sourceKey], "Duplicate incoming neighbour.");
                            if (datasetShape == "ring")
                            {
                                int distance = (key - sourceKey + size) % size;
                                Assert.IsTrue(distance == 0 ? fanOut == size : distance <= fanOut, "Unexpected ring neighbour.");
                            }
                            seen[sourceKey] = true;
                            count++;
                        }
                    }
                    Assert.AreEqual(1, completed, "An incomplete stream is not a successful measurement.");
                    Assert.AreEqual(datasetShape == "hubs" ? size : fanOut, count);
                    return;
                }
                if (aggregate)
                {
                    using var scanned = await client.PostAsJsonAsync(rdf ? "/v1/rdf/sparql/scan" : "/v1/graph/gql/query/scan", new
                    {
                        query
                    }, token);
                    string body = await scanned.Content.ReadAsStringAsync(token);
                    Assert.AreEqual(System.Net.HttpStatusCode.OK, scanned.StatusCode, body);
                    var actualGroups = new List<string>();
                    int completed = 0;
                    foreach (string line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        using var frame = JsonDocument.Parse(line);
                        string? type = frame.RootElement.GetProperty("type").GetString();
                        if (type == "complete")
                        {
                            Assert.IsTrue(frame.RootElement.GetProperty("complete").GetBoolean());
                            completed++;
                            continue;
                        }
                        Assert.AreEqual("page", type);
                        Assert.AreEqual(0, completed, "Rows followed completion.");
                        foreach (var row in frame.RootElement.GetProperty("rows").EnumerateArray())
                        {
                            string bucket = rdf ? row.GetProperty("bucket").GetProperty("lexicalForm").GetString()! :
                                row.GetProperty("bindings").GetProperty("bucket").GetInt64().ToString(CultureInfo.InvariantCulture);
                            string total = rdf ? row.GetProperty("total").GetProperty("lexicalForm").GetString()! :
                                row.GetProperty("bindings").GetProperty("total").GetInt64().ToString(CultureInfo.InvariantCulture);
                            actualGroups.Add(bucket + ":" + total);
                            Assert.IsTrue(actualGroups.Count <= bucketCount, "Aggregate returned extra groups.");
                        }
                    }
                    Assert.AreEqual(1, completed, "An incomplete stream is not a successful measurement.");
                    // Count integers congruent to each bucket without executing a query or grouping stored values.
                    string[] expectedGroups = Enumerable.Range(0, Math.Min(size, bucketCount))
                        .Select(bucket => Lexical(bucket) + ":" + Lexical((size - 1 - bucket) / bucketCount + 1)).ToArray();
                    CollectionAssert.AreEquivalent(expectedGroups, actualGroups);
                    return;
                }
                using HttpResponseMessage response = rdf
                    ? await client.PostAsync("/sparql", new StringContent(query, Encoding.UTF8, "application/sparql-query"), token)
                    : await client.PostAsJsonAsync("/v1/graph/gql/query", new
                    {
                        query
                    }, token);
                Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(token));
                JsonElement result = await response.Content.ReadFromJsonAsync<JsonElement>(token);
                JsonElement rows = rdf ? result.GetProperty("results").GetProperty("bindings") : result.GetProperty("matches");
                // Every source reaches every hub through each of the fanOut
                // intermediate hubs. This closed-form bag does not traverse
                // stored edges or call the query evaluator as an oracle.
                int[] expected = workload == "point-lookup" ? [key] : datasetShape == "hubs" ?
                    Enumerable.Range(0, fanOut).SelectMany(hub => Enumerable.Repeat(hub, fanOut)).ToArray() :
                    (from first in Enumerable.Range(1, fanOut)
                     from second in Enumerable.Range(1, fanOut)
                     select (key + first + second) % size).ToArray();
                int[] actual = rows.EnumerateArray().Select(row => rdf
                    ? Int32.Parse(row.GetProperty("value").GetProperty("value").GetString()!, CultureInfo.InvariantCulture)
                    : row.GetProperty("bindings").GetProperty("answer").GetInt32()).ToArray();
                CollectionAssert.AreEquivalent(expected, actual, "A benchmark query returned an incorrect result bag.");
            }
            var histogram = new WorkloadLatencyHistogram();
            long measuredHotKeyRequests = 0, warmupHotKeyRequests = 0;
            void RecordKey(int key, bool measured)
            {
                if (!aggregate && key < keyDistribution.HotKeyCount)
                {
                    if (measured)
                    {
                        Interlocked.Increment(ref measuredHotKeyRequests);
                    }
                    else
                    {
                        Interlocked.Increment(ref warmupHotKeyRequests);
                    }
                }
            }

            async Task<long> TimedQueries(int duration, bool measured)
            {
                long completed = 0;
                long windowStarted = Stopwatch.GetTimestamp();
                await WorkloadTaskGroup.RunAsync(Enumerable.Range(0, concurrency).Select(worker => (Func<Task>)(async () =>
                {
                    var generator = new Random(seed + worker + (measured ? 100_000 : 0));
                    while (Stopwatch.GetElapsedTime(windowStarted).TotalSeconds < duration)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        int key = keyDistribution.Next(generator);
                        long requestStarted = Stopwatch.GetTimestamp();
                        await Query(key, deadline.Token);
                        if (measured)
                        {
                            histogram.Add(Stopwatch.GetElapsedTime(requestStarted));
                        }
                        RecordKey(key, measured);
                        Interlocked.Increment(ref completed);
                    }
                })).ToArray(), deadline);
                return completed;
            }
            Phase("query-warmup");
            long warmupStarted = Stopwatch.GetTimestamp();
            long verifiedWarmupRequests;
            if (warmupSeconds > 0)
            {
                verifiedWarmupRequests = await TimedQueries(warmupSeconds, measured: false);
            }
            else
            {
                for (int index = 0; index < warmup; index++)
                {
                    await Query(keys[index], deadline.Token);
                    RecordKey(keys[index], measured: false);
                }
                verifiedWarmupRequests = warmup;
            }
            double actualWarmupSeconds = Stopwatch.GetElapsedTime(warmupStarted).TotalSeconds;
            double[] latency = measurementSeconds == 0 ? new double[iterations] : [];
            using var process = Process.GetCurrentProcess();
            TimeSpan cpuBefore = process.TotalProcessorTime;
            long allocations = GC.GetTotalAllocatedBytes();
            Phase("query-measurement");
            long started = Stopwatch.GetTimestamp();
            long verifiedRequests;
            if (measurementSeconds > 0)
            {
                verifiedRequests = await TimedQueries(measurementSeconds, measured: true);
            }
            else
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, iterations), new ParallelOptions
                {
                    MaxDegreeOfParallelism = concurrency,
                    CancellationToken = deadline.Token
                }, async (index, token) =>
                {
                    long requestStarted = Stopwatch.GetTimestamp();
                    await Query(keys[warmup + index], token);
                    latency[index] = Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds;
                    RecordKey(keys[warmup + index], measured: true);
                });
                verifiedRequests = iterations;
            }
            Assert.IsTrue(verifiedRequests > 0, "A measurement must complete verified requests.");
            double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            Phase("query-measurement-complete");
            long allocatedBytes = GC.GetTotalAllocatedBytes() - allocations;
            process.Refresh();
            Array.Sort(latency);
            await samplingStop.CancelAsync();
            await sampling;
            double Percentile(double fraction) => measurementSeconds > 0 ? histogram.Percentile(fraction)!.UpperMilliseconds :
                latency[Math.Clamp((int)Math.Ceiling(fraction * iterations) - 1, 0, iterations - 1)];
            var report = new
            {
                schema = 15,
                status = "completed",
                completedUtc = DateTimeOffset.UtcNow,
                phases,
                loadCheckpointEveryBatches,
                loadBatches,
                loadCheckpoints,
                loadCheckpointSeconds,
                reopenAfterLoad,
                reopenedBeforeVerification,
                finalLoadCheckpointCreated,
                reopenSeconds,
                restoreAfterLoad,
                restoredBeforeVerification,
                backupSeconds,
                restoreSeconds,
                backupSha256,
                backupAdditionalBytesReserved,
                backupFixtureGiB,
                maximumFixtureBytes,
                verifyInChildProcess,
                childVerification,
                childResourceObservation,
                reopenContract = "Optional same-process host disposal and fresh host recovery after a checkpoint covering all load batches. RESTORE_AFTER_LOAD additionally creates and verifies an offline archive and restores to an empty directory before host startup; all dataset and query assertions then run on that restored database. Optional VERIFY_IN_CHILD_PROCESS also opens and verifies the dataset in a separate process before the HTTP host reopens it. Reopen time includes shutdown, optional backup/restore, child verification and startup; those phases and full-content verification are excluded from query timings. This is not process-crash recovery, clean-machine qualification or a cold-cache measurement.",
                workload,
                datasetShape,
                profile,
                size,
                fanOut,
                edgeCount = size * fanOut,
                iterations,
                concurrency,
                warmup,
                seed,
                backgroundRoutes,
                requestedRdfQuads,
                remainderQuads,
                verifiedDatasetEntities,
                verifiedDatasetEdges,
                storedDatasetFullyVerified,
                datasetVerificationSeconds,
                datasetVerificationContract = "Every stored graph vertex/edge identity, label, typed property and endpoint or every RDF term and statement is checked against the generated dataset definition before warmup. RDF duplicate/missing detection uses one bit per expected statement. Verification touches data and is excluded from query timings; it does not establish every physical-index invariant.",
                measurementSeconds,
                warmupSeconds,
                actualWarmupSeconds,
                verifiedWarmupRequests,
                mode = measurementSeconds == 0 ? "fixed-request-count" : "timed",
                timedKeySeeds = measurementSeconds == 0 ? null : Enumerable.Range(0, concurrency).Select(worker => seed + 100_000 + worker).ToArray(),
                timingContract = "Timed warmup replaces request-count warmup. Timed measurement replaces iteration count; workers drain in-flight requests after the window. Actual elapsed time includes draining. Timed percentiles use histogram upper bounds with full intervals and buckets retained; fixed-count percentiles are exact nearest rank.",
                maximumIncomingRoutes = fanOut == 0 ? 0 : datasetShape == "hubs" ? size : fanOut,
                rdfQuadCount = rdf ? expectedRdfQuads : (int?)null,
                distribution = aggregate ? "repeated complete aggregation; closed-loop clients" : keyDistribution.Mode == "hotspot"
                    ? "seeded 80/20 hotspot key selection; closed-loop clients" : "seeded uniform keys; closed-loop clients",
                keyDistribution = aggregate ? "not-applicable" : keyDistribution.Mode,
                keyDomainCount = aggregate ? (int?)null : keyDistribution.KeyCount,
                hotKeyCount = aggregate ? (int?)null : keyDistribution.HotKeyCount,
                measuredHotKeyRequests = aggregate ? (long?)null : measuredHotKeyRequests,
                warmupHotKeyRequests = aggregate ? (long?)null : warmupHotKeyRequests,
                keyDistributionContract = "Hot prefix is keys [0,max(1,floor(keyDomainCount/5))). Hotspot chooses that prefix with80% probability and the remaining keys with20%; a one-key domain always selects0. Uniform retains the original seeded sequence. Counts describe successfully verified requests only; warmup and measurement are separate. Incoming hubs scans choose among hub keys, not all vertices. Aggregation has no request key.",
                transport = aggregate || incomingScan ? "EntityForge NDJSON scan with required completion frame" : rdf ? "SPARQL Protocol JSON results" : "EntityForge GQL HTTP JSON results",
                maximumSortTemporaryBytesPerQuery = aggregate ? aggregateTemporaryBytes : 0,
                scanPageSize = aggregate || incomingScan ? 256 : (int?)null,
                maximumActiveScans = aggregate || incomingScan ? concurrency : (int?)null,
                queryTemplate,
                datasetDefinition = "integer keys 0..size-1; graph label Benchmark/key property, RDF urn:benchmark:key urn:key plain literal; route workloads or BACKGROUND_ROUTES add routes for offsets 1..fanOut: ring targets (key+offset)%size, hubs targets offset-1; grouped-aggregate adds bucket=key%10 as graph property or RDF urn:bucket plain literal. RDF_QUADS overrides entity count and adds at most six isolated urn:remainder triples to reach the exact total. Cardinality is verified before warmup; this touches the dataset and is not a cold-cache run.",
                loadStrategy = "committed batches of at most 1000 vertices (up to 3000 mutations), 2000 quads or 1000 edges; loading excluded from timings",
                generatedDatasetSha256 = Convert.ToHexString(checksum.GetHashAndReset()),
                verifiedRequests,
                verifiedRows = verifiedRequests * (incomingScan ? datasetShape == "hubs" ? size : fanOut : aggregate ? Math.Min(size, bucketCount) : workload == "point-lookup" ? 1 : fanOut * fanOut),
                seconds,
                requestsPerSecond = verifiedRequests / seconds,
                latencyHistogram = measurementSeconds > 0 ? histogram.Summary() : null,
                p50Milliseconds = Percentile(0.50),
                p95Milliseconds = Percentile(0.95),
                p99Milliseconds = Percentile(0.99),
                latencyMilliseconds = latency,
                sampleOrdering = "ascending latency; nearest-rank percentiles; samples include response parsing and correctness validation",
                allocatedBytes,
                cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds,
                process.WorkingSet64,
                process.PeakWorkingSet64,
                databaseBytes = new DirectoryInfo(activeDirectory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length),
                resourceObservations = resourceSampler.Samples,
                sampleIntervalSeconds,
                deadlineSeconds,
                maximumWorkingSetBytes = resourceSampler.MaximumWorkingSetBytes,
                resourcePolicy = "Bounded samples cover loading, warmup and measurement; stop below 15 GiB free, above the explicitly recorded fixture-storage ceiling (8 GiB by default, optional 16 GiB for backup copies only; source stays at 8 GiB) or above the recorded process working-set budget. Deadline allows one hour plus configured timed windows. Sampling is not a hard allocation quota; client/server/sampler costs share this process.",
                os = RuntimeInformation.OSDescription,
                runtime = RuntimeInformation.FrameworkDescription,
                garbageCollector = resourceSampler.GarbageCollector,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                logicalProcessors = Environment.ProcessorCount,
                assemblySha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(EntityForgeDbServer).Assembly.Location))),
                loadedEntityForgeAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(assembly => !assembly.IsDynamic && assembly.GetName().Name?.StartsWith("EntityForge.", StringComparison.Ordinal) == true)
                    .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal)
                    .Select(assembly => new
                    {
                        name = assembly.GetName().Name,
                        sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))
                    }).ToArray(),
                qualification = "Development measurement only; same-process client/server, not target-hardware SLO or endurance qualification. Memory/CPU include the test client."
            };
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            if (reportPath is not null)
            {
                await File.WriteAllTextAsync(reportPath, json);
            }
            TestContext.WriteLine(json);
        }
        finally
        {
            await samplingStop.CancelAsync();
            try
            {
                await sampling;
            }
            finally
            {
                if (reportPath is not null)
                {
                    await File.WriteAllTextAsync(Path.ChangeExtension(reportPath, ".resources.json"),
                        JsonSerializer.Serialize(new
                        {
                            schema = 1,
                            status = "observations-only",
                            profile,
                            loadCheckpointEveryBatches,
                            loadBatches,
                            loadCheckpoints,
                            loadCheckpointSeconds,
                            reopenAfterLoad,
                            reopenedBeforeVerification,
                            finalLoadCheckpointCreated,
                            reopenSeconds,
                            restoreAfterLoad,
                            restoredBeforeVerification,
                            backupSeconds,
                            restoreSeconds,
                            backupSha256,
                            backupAdditionalBytesReserved,
                            backupFixtureGiB,
                            maximumFixtureBytes,
                            verifyInChildProcess,
                            childVerification,
                            childResourceObservation,
                            storedDatasetFullyVerified,
                            datasetVerificationSeconds,
                            maximumWorkingSetBytes = resourceSampler.MaximumWorkingSetBytes,
                            garbageCollector = resourceSampler.GarbageCollector,
                            samples = resourceSampler.Samples,
                            phases
                        }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
    }

    private sealed record QueryWorkloadPhase(string Name, double SampleSeconds);

    private sealed class QueryWorkloadHost(Func<EntityForgeDbHost> build) : IAsyncDisposable
    {
        private EntityForgeDbHost? _current;

        internal async Task<EntityForgeDbHost> OpenAsync(CancellationToken cancellation)
        {
            if (_current is not null)
            {
                await _current.DisposeAsync();
                _current = null;
            }
            _current = build();
            await _current.StartAsync(cancellation);
            return _current;
        }

        public async ValueTask DisposeAsync()
        {
            if (_current is not null)
            {
                await _current.DisposeAsync();
                _current = null;
            }
        }
    }

    private static int Setting(string name, int fallback, int minimum, int maximum)
    {
        string? configured = Environment.GetEnvironmentVariable("ENTITYFORGE_QUERY_BENCHMARK_" + name);
        if (configured is null)
        {
            return fallback;
        }
        if (!Int32.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, $"Benchmark setting must be in {minimum}..{maximum}.");
        }
        return value;
    }
}
