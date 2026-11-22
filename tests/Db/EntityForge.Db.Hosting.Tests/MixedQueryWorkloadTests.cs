using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [TestCategory("QueryBenchmark")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Mixed_native_queries_verify_acknowledged_updates_after_restart(bool rdf)
    {
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_MIXED_WORKLOAD") != "1")
        {
            Assert.Inconclusive("Opt in with ENTITYFORGE_MIXED_WORKLOAD=1; development measurement, not endurance qualification.");
        }
        int clients = Setting("CLIENTS", 2, 1, 32);
        int rows = Setting("ROWS", 100, clients, 100_000);
        int steps = Setting("MIXED_STEPS", 100, 10, 10_000);
        int durationSeconds = Setting("MIXED_SECONDS", 0, 0, 96 * 3600);
        int warmupSeconds = Setting("WARMUP_SECONDS", 0, 0, 3600);
        int windowSeconds = Setting("WINDOW_SECONDS", 300, 1, 3600);
        int backupEveryWindows = Setting("BACKUP_EVERY_WINDOWS", 0, 0, 4096);
        if (backupEveryWindows > 0 && durationSeconds == 0)
        {
            throw new ArgumentException("Scheduled mixed-workload backups require timed measurement windows.");
        }
        var changeHistory = new DatabaseChangeJournalRetentionOptions(
            Setting("CHANGE_HISTORY_BYTES", (int)DatabaseChangeJournalRetentionOptions.DefaultMaximumRetainedBytes, 1, 1024 * 1024 * 1024),
            Setting("CHANGE_HISTORY_MIN_CHANGES", DatabaseChangeJournalRetentionOptions.DefaultMinimumRetainedChangeCount, 0, 1_000_000),
            TimeSpan.FromSeconds(Setting("CHANGE_HISTORY_MIN_SECONDS", 86400, 0, 604800)));
        if (durationSeconds > 0 && Math.Ceiling(durationSeconds / (double)windowSeconds) > 4096)
        {
            throw new ArgumentException("Choose a window duration requiring at most 4,096 windows.");
        }
        if (rows % clients != 0 || steps % 10 != 0)
        {
            throw new ArgumentException("Mixed workload rows must divide evenly among clients and steps must be a multiple of ten.");
        }
        string profile = rdf ? "rdf" : "graph";
        string? report = Environment.GetEnvironmentVariable("ENTITYFORGE_QUERY_BENCHMARK_REPORT_DIRECTORY") is { Length: > 0 } output
            ? Path.Combine(output, profile + "-mixed-read-write.json") : null;
        if (report is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            await WorkloadReport.WriteAsync(report, JsonSerializer.Serialize(new
            {
                status = "incomplete",
                profile,
                rows,
                clients,
                steps
            }));
        }
        _directory = NewDirectory();
        string oracleDirectory = NewDirectory();
        string? backupDirectory = backupEveryWindows == 0 ? null : NewDirectory();
        string? restoreDirectory = backupDirectory is null ? null : Path.Combine(backupDirectory, "restored");
        string? backupPath = backupDirectory is null ? null : Path.Combine(backupDirectory, "current.efbackup");
        int backupCount = 0;
        double backupSeconds = 0;
        int[]? backupMinimum = null, backupMaximum = null;
        LocalDatabaseBackupInfo? lastBackup = null;
        Directory.CreateDirectory(oracleDirectory);
        using var initialOracle = new WorkloadAcknowledgementOracle(oracleDirectory, rows, clients);
        WorkloadAcknowledgementOracle oracle = initialOracle;
        const int seed = 39075;
        int[] expected = new int[rows];
        var readLatency = new WorkloadLatencyHistogram();
        var updateLatency = new WorkloadLatencyHistogram();
        var rejectedLatency = new WorkloadLatencyHistogram();
        long rejectedUpdates = 0;
        long measuredRequests = 0;
        var windows = new List<object>();
        int resourceInterval = Math.Max(1, (int)Math.Ceiling((durationSeconds + warmupSeconds + 3600) / 4090D));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds + warmupSeconds + 3600));
        CancellationToken token = deadline.Token;
        string Number(int number) => number.ToString(CultureInfo.InvariantCulture);
        Guid Identity(int key)
        {
            byte[] bytes = new byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, key + 1);
            return new(bytes);
        }
        EntityForgeDbHost OpenHost(string? directory = null)
        {
            var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(directory ?? _directory)
                .UseChangeJournalRetention(changeHistory)
                .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0").ConfigureLogging(log => log.ClearProviders()));
            return (rdf ? builder.UseRdfProfile() : builder.UsePropertyGraphProfile()).Build();
        }
        async Task<int> ReadValue(HttpClient http, int key)
        {
            string query = rdf ? $"SELECT ?value WHERE {{ <urn:mixed:{Number(key)}> <urn:value> ?value }}"
                : $"MATCH (n:Mixed) WHERE n.key={Number(key)} RETURN n.amount AS answer";
            using var response = rdf
                ? await http.PostAsync("/sparql", new StringContent(query, Encoding.UTF8, "application/sparql-query"), token)
                : await http.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query
                }, token);
            string body = await response.Content.ReadAsStringAsync(token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
            using var document = JsonDocument.Parse(body);
            var bindings = rdf ? document.RootElement.GetProperty("results").GetProperty("bindings") : document.RootElement.GetProperty("matches");
            Assert.AreEqual(1, bindings.GetArrayLength());
            int actual = rdf ? Int32.Parse(bindings[0].GetProperty("value").GetProperty("value").GetString()!, CultureInfo.InvariantCulture)
                : bindings[0].GetProperty("bindings").GetProperty("answer").GetInt32();
            return actual;
        }
        async Task Read(HttpClient http, int key, int value)
        {
            Assert.AreEqual(value, await ReadValue(http, key), $"Incorrect mixed-workload value at key {key}.");
        }
        using var process = Process.GetCurrentProcess();
        double elapsed;
        double cpuSeconds;
        long allocated;
        long databaseBytes;
        var resourceSampler = new WorkloadResourceSampler(backupDirectory is null
            ? [_directory, oracleDirectory] : [_directory, oracleDirectory, backupDirectory], intervalSeconds: resourceInterval);
        long acknowledged;
        double startupSeconds;
        int backupVerifiedRows = 0;
        double restoreSeconds = 0, restoreVerificationSeconds = 0;
        double? measurementStartedAtSampleSeconds = null, measurementEndedAtSampleSeconds = null;
        async Task SaveResourceObservations()
        {
            if (report is not null)
            {
                await WorkloadReport.WriteAsync(Path.Combine(Path.GetDirectoryName(report)!, profile + "-mixed-resources.json"),
                    JsonSerializer.Serialize(new
                    {
                        schema = 2,
                        status = "observations-only",
                        profile,
                        measurementStartedAtSampleSeconds,
                        measurementEndedAtSampleSeconds,
                        maximumWorkingSetBytes = resourceSampler.MaximumWorkingSetBytes,
                        garbageCollector = resourceSampler.GarbageCollector,
                        samples = resourceSampler.Samples
                    }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        using var samplingStop = new CancellationTokenSource();
        Task sampling = resourceSampler.RunAsync(samplingStop.Token, deadline);
        try
        {
            await using (var host = OpenHost())
            {
                await host.StartAsync(token);
                var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
                foreach (int[] batch in Enumerable.Range(0, rows).Chunk(500))
                {
                    if (rdf)
                    {
                        await runtime.RdfDatabase.CommitAsync(new(Guid.CreateVersion7(), batch.Select(key =>
                            (RdfMutation)new AssertRdfQuad(new(new RdfIri("urn:mixed:" + Number(key)), new("urn:value"), new RdfLiteral("0")))).ToArray()), token);
                    }
                    else
                    {
                        await runtime.GraphDatabase.CommitAsync(new(Guid.CreateVersion7(), batch.SelectMany(key => new GraphMutation[]
                        {
                            new CreateVertex(new(Identity(key)), ["Mixed"]),
                            new SetVertexProperty(new(Identity(key)), "key", new DatabaseValue.Int64(key)),
                            new SetVertexProperty(new(Identity(key)), "amount", new DatabaseValue.Int64(0))
                        }).ToArray()), token);
                    }
                }
                using (var warmup = Client(host, ReaderToken))
                {
                    for (int key = 0; key < clients; key++)
                    {
                        await Read(warmup, key, 0);
                    }
                }
                if (warmupSeconds > 0)
                {
                    long warmupStarted = Stopwatch.GetTimestamp();
                    await WorkloadTaskGroup.RunAsync(Enumerable.Range(0, clients).Select(actor => (Func<Task>)(async () =>
                    {
                        using var http = Client(host, ReaderToken);
                        var random = new Random(seed + actor);
                        while (Stopwatch.GetElapsedTime(warmupStarted).TotalSeconds < warmupSeconds)
                        {
                            await Read(http, actor + clients * random.Next(rows / clients), 0);
                        }
                    })).ToArray(), deadline);
                }
                TimeSpan cpu = process.TotalProcessorTime;
                long allocationStart = GC.GetTotalAllocatedBytes();
                measurementStartedAtSampleSeconds = resourceSampler.ElapsedSeconds;
                long started = Stopwatch.GetTimestamp();
                try
                {
                    var randoms = Enumerable.Range(0, clients).Select(actor => new Random(seed + actor)).ToArray();
                    do
                    {
                        long windowStarted = Stopwatch.GetTimestamp();
                        DateTimeOffset retentionCutoff = DateTimeOffset.UtcNow;
                        double windowLimit = durationSeconds == 0 ? 0 : Math.Min(windowSeconds,
                            durationSeconds - Stopwatch.GetElapsedTime(started).TotalSeconds);
                        long requestsBefore = measuredRequests;
                        long rejectionsBefore = rejectedUpdates;
                        var windowReads = new WorkloadLatencyHistogram();
                        var windowUpdates = new WorkloadLatencyHistogram();
                        var windowRejections = new WorkloadLatencyHistogram();
                        if (windows.Count >= 4096)
                        {
                            throw new InvalidOperationException("Measurement window budget exhausted.");
                        }
                        bool scheduleBackup = backupEveryWindows > 0 && (windows.Count + 1) % backupEveryWindows == 0;
                        int[]? minimumAtWindowStart = scheduleBackup ? (int[])expected.Clone() : null;
                        Task writers = WorkloadTaskGroup.RunAsync(Enumerable.Range(0, clients).Select(actor => (Func<Task>)(async () =>
                        {
                            using var http = Client(host, WriterToken);
                            var operations = rdf ? null : new HttpGraphEntityClient(http,
                                ((IEntityMappingReplayHost)runtime.GraphDatabase).DatabaseIncarnation);
                            var random = randoms[actor];
                            for (long step = 0; durationSeconds == 0 ? step < steps :
                                step % 10 != 0 || Stopwatch.GetElapsedTime(windowStarted).TotalSeconds < windowLimit; step++)
                            {
                                int key = actor + clients * random.Next(rows / clients);
                                long requestStarted = Stopwatch.GetTimestamp();
                                if (step % 10 != 9)
                                {
                                    await Read(http, key, expected[key]);
                                    TimeSpan readElapsed = Stopwatch.GetElapsedTime(requestStarted);
                                    readLatency.Add(readElapsed);
                                    windowReads.Add(readElapsed);
                                    Interlocked.Increment(ref measuredRequests);
                                    continue;
                                }
                                int value = expected[key] + 1;
                                Guid operation = Guid.CreateVersion7();
                                string statement = rdf
                                    ? $"DELETE {{ <urn:mixed:{Number(key)}> <urn:value> ?old }} INSERT {{ <urn:mixed:{Number(key)}> <urn:value> '{Number(value)}' }} WHERE {{ <urn:mixed:{Number(key)}> <urn:value> ?old }}"
                                    : $"MATCH (n:Mixed) WHERE n.key={Number(key)} SET n.amount={Number(value)}";
                                using var response = rdf
                                    ? await http.PostAsync("/sparql/update", new StringContent(statement, Encoding.UTF8, "application/sparql-update"), token)
                                    : await http.PostAsJsonAsync("/v1/graph/gql/update", new
                                    {
                                        statement,
                                        transactionId = operation
                                    }, token);
                                string responseBody = await response.Content.ReadAsStringAsync(token);
                                TimeSpan requestElapsed = Stopwatch.GetElapsedTime(requestStarted);
                                if (!rdf && response.StatusCode == HttpStatusCode.Conflict)
                                {
                                    using var problem = JsonDocument.Parse(responseBody);
                                    Assert.AreEqual("write_conflict", problem.RootElement.GetProperty("code").GetString());
                                    rejectedLatency.Add(requestElapsed);
                                    windowRejections.Add(requestElapsed);
                                    Interlocked.Increment(ref rejectedUpdates);
                                    Interlocked.Increment(ref measuredRequests);
                                    // Separate rejection validation from timed requests. This
                                    // actor owns the key, so an explicit conflict must not alter it.
                                    await Read(http, key, expected[key]);
                                    Assert.AreEqual(EntityOperationState.Aborted, (await operations!.AbortAsync(operation)).State);
                                    continue;
                                }
                                Assert.AreEqual(rdf ? HttpStatusCode.NoContent : HttpStatusCode.OK, response.StatusCode, responseBody);
                                updateLatency.Add(requestElapsed);
                                windowUpdates.Add(requestElapsed);
                                oracle.Append(actor, key, value, operation);
                                Volatile.Write(ref expected[key], value);
                                Interlocked.Increment(ref measuredRequests);
                            }
                        })).ToArray(), deadline);
                        await Task.WhenAll(writers, CaptureBackupDuringWindow());

                        async Task CaptureBackupDuringWindow()
                        {
                            if (!scheduleBackup)
                            {
                                return;
                            }
                            try
                            {
                                await Task.Delay(50, token);
                                if (writers.IsCompleted)
                                {
                                    return; // A short final window need not create a post-load backup.
                                }
                                long backupStarted = Stopwatch.GetTimestamp();
                                var captured = await runtime.CreateBackupAsync(backupPath!, token);
                                int[] maximum = new int[rows];
                                for (int key = 0; key < rows; key++)
                                {
                                    // Each key has one writer and at most one update
                                    // visible before its HTTP acknowledgement is recorded.
                                    maximum[key] = checked(Volatile.Read(ref expected[key]) + 1);
                                }
                                lastBackup = captured;
                                backupMinimum = minimumAtWindowStart;
                                backupMaximum = maximum;
                                backupSeconds += Stopwatch.GetElapsedTime(backupStarted).TotalSeconds;
                                backupCount++;
                            }
                            catch
                            {
                                await deadline.CancelAsync();
                                throw;
                            }
                        }
                        double requestSeconds = Stopwatch.GetElapsedTime(windowStarted).TotalSeconds;
                        long maintenanceStarted = Stopwatch.GetTimestamp();
                        if (durationSeconds > 0)
                        {
                            oracle.Checkpoint();
                            IDatabaseOperationRetention retention = rdf ? runtime.RdfDatabase : runtime.GraphDatabase;
                            await retention.RetainOperationsAsync(retentionCutoff, token);
                            oracle = new WorkloadAcknowledgementOracle(oracleDirectory, rows, clients);
                        }
                        windows.Add(new
                        {
                            startSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds - Stopwatch.GetElapsedTime(windowStarted).TotalSeconds,
                            requestSeconds,
                            maintenanceSeconds = durationSeconds == 0 ? 0 : Stopwatch.GetElapsedTime(maintenanceStarted).TotalSeconds,
                            requests = measuredRequests - requestsBefore,
                            rejectedUpdates = rejectedUpdates - rejectionsBefore,
                            reads = windowReads.Summary(false),
                            updates = windowUpdates.Summary(false),
                            rejections = windowRejections.Summary(false)
                        });
                        if (report is not null)
                        {
                            await WorkloadReport.WriteAsync(report, JsonSerializer.Serialize(new
                            {
                                schema = 6,
                                status = "incomplete",
                                profile,
                                rows,
                                clients,
                                durationSeconds,
                                warmupSeconds,
                                windowSeconds,
                                measurementStartedAtSampleSeconds,
                                measurementEndedAtSampleSeconds,
                                verifiedMeasuredRequests = measuredRequests,
                                windows
                            }), token);
                            // Keep the last completed window's bounded observations
                            // even if the test process dies before its finally block.
                            await SaveResourceObservations();
                        }
                    }
                    while (durationSeconds > 0 && Stopwatch.GetElapsedTime(started).TotalSeconds < durationSeconds);
                }
                finally
                {
                    oracle.Dispose();
                    measurementEndedAtSampleSeconds = resourceSampler.ElapsedSeconds;
                }
                elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
                cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds;
                allocated = GC.GetTotalAllocatedBytes() - allocationStart;
                databaseBytes = new DirectoryInfo(_directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
            }
            // Reconstruct expected state from separately flushed input records, never
            // from query results or database state. Every acknowledged update must survive.
            oracle.Checkpoint();
            var durableOracle = WorkloadAcknowledgementOracle.Recover(oracleDirectory);
            int[] durableExpected = durableOracle.Values;
            acknowledged = durableOracle.Acknowledged;
            Assert.AreEqual(measuredRequests / 10 - rejectedUpdates, acknowledged);
            Assert.AreEqual(0L, measuredRequests % 10);
            Assert.IsTrue(measuredRequests > 0, "The measurement period did not execute any requests.");
            CollectionAssert.AreEqual(expected, durableExpected);
            long restartStarted = Stopwatch.GetTimestamp();

            await using (var host = OpenHost())
            {
                await host.StartAsync(token);
                startupSeconds = Stopwatch.GetElapsedTime(restartStarted).TotalSeconds;
                using var http = Client(host, ReaderToken);
                for (int key = 0; key < rows; key++)
                {
                    await Read(http, key, durableExpected[key]);
                }
            }
            if (backupEveryWindows > 0)
            {
                Assert.IsNotNull(lastBackup, "No backup overlapped the measured workload; choose a longer run or more frequent backups.");
                long restoreBytes = checked(lastBackup.CheckpointBytes + lastBackup.PageBytes + lastBackup.ChangeJournalBytes + 16L * 1024 * 1024);
                long fixtureBytes = new[] { _directory, oracleDirectory, backupDirectory! }.Sum(directory =>
                    new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length));
                if (checked(fixtureBytes + restoreBytes) > 8L * 1024 * 1024 * 1024 ||
                    WorkloadVolume.AvailableFreeBytes(restoreDirectory!) < 15L * 1024 * 1024 * 1024 + restoreBytes)
                {
                    throw new IOException("Workload backup restore exceeds the fixture or free-space budget.");
                }
                long restoreStarted = Stopwatch.GetTimestamp();
                var restoredInfo = await new LocalDatabaseBackupManager().RestoreAsync(backupPath!, restoreDirectory!,
                    EntityForgeDbHostOptions.DefaultCheckpointFileName, EntityForgeDbHostOptions.DefaultCommitLogFileName,
                    EntityForgeDbHostOptions.DefaultManifestFileName, EntityForgeDbHostOptions.DefaultChangeJournalFileName,
                    EntityForgeDbHostOptions.DefaultPageFileName, new GroupedRedoRestoreLogInitializer(), token);
                restoreSeconds = Stopwatch.GetElapsedTime(restoreStarted).TotalSeconds;
                Assert.AreEqual(lastBackup, restoredInfo);
                long restoreVerificationStarted = Stopwatch.GetTimestamp();
                await using var restoredHost = OpenHost(restoreDirectory);
                await restoredHost.StartAsync(token);
                using var reader = Client(restoredHost, ReaderToken);
                for (int key = 0; key < rows; key++)
                {
                    int actual = await ReadValue(reader, key);
                    Assert.IsTrue(actual >= backupMinimum![key] && actual <= backupMaximum![key],
                        $"Backup value {actual} at key {key} outside independently tracked acknowledgement bounds.");
                    backupVerifiedRows++;
                }
                restoreVerificationSeconds = Stopwatch.GetElapsedTime(restoreVerificationStarted).TotalSeconds;
            }
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
                // Preserve failure observations too; this is never a pass verdict.
                await SaveResourceObservations();
            }
        }
        process.Refresh();
        string json = JsonSerializer.Serialize(new
        {
            schema = 6,
            status = rejectedUpdates == 0 ? "completed" : "completed-with-conflicts",
            profile,
            rows,
            clients,
            stepsPerClient = durationSeconds == 0 ? (int?)steps : null,
            durationSeconds,
            warmupSeconds,
            windowSeconds,
            windows,
            seed,
            workload = "mixed-read-write",
            readPercent = 90,
            writePercent = 10,
            backups = new
            {
                everyWindows = backupEveryWindows,
                count = backupCount,
                seconds = backupSeconds,
                verifiedRestoreRows = backupVerifiedRows,
                restoreSeconds,
                restoreVerificationSeconds,
                lastSha256 = lastBackup?.Sha256
            },
            changeJournalRetention = new
            {
                maximumRetainedBytes = changeHistory.MaximumRetainedBytes,
                minimumRetainedChangeCount = changeHistory.MinimumRetainedChangeCount,
                minimumRetainedSeconds = changeHistory.MinimumRetainedDuration.TotalSeconds
            },
            readTemplate = rdf ? "SELECT ?value WHERE { <urn:mixed:{key}> <urn:value> ?value }"
                : "MATCH (n:Mixed) WHERE n.key={key} RETURN n.amount AS answer",
            updateTemplate = rdf ? "DELETE { <urn:mixed:{key}> <urn:value> ?old } INSERT { <urn:mixed:{key}> <urn:value> '{value}' } WHERE { <urn:mixed:{key}> <urn:value> ?old }"
                : "MATCH (n:Mixed) WHERE n.key={key} SET n.amount={value}",
            datasetDefinition = "Keys0..rows-1;initialvalue0;GraphlabelMixed,key/amountintegerproperties;RDFurn:mixed:key urn:value plainintegerlexicalliteral;onevalueperkey",
            distribution = "seeded uniform disjoint key ownership per client; closed-loop; database-wide GQL conflicts reported separately without retries",
            verifiedMeasuredRequests = measuredRequests,
            acknowledgedUpdates = acknowledged,
            verifiedRestartRows = rows,
            rejectedUpdates,
            rejectionVerificationReads = rejectedUpdates,
            abortedRejectedOperations = rejectedUpdates,
            seconds = elapsed,
            attemptedRequestsPerSecond = measuredRequests / elapsed,
            successfulRequestsPerSecond = (measuredRequests - rejectedUpdates) / elapsed,
            reads = readLatency.Summary(),
            updates = updateLatency.Summary(),
            rejectedUpdateTimings = rejectedLatency.Summary(),
            cpuSeconds,
            allocatedBytes = allocated,
            databaseBytes,
            process.WorkingSet64,
            startupSeconds,
            resourceSampling = new
            {
                coverage = "loading, warmup, measurement, restart and backup restore verification",
                measurementStartedAtSampleSeconds,
                measurementEndedAtSampleSeconds,
                intervalSeconds = resourceInterval,
                maximumSamples = 4096,
                minimumFreeBytes = 15L * 1024 * 1024 * 1024,
                maximumFixtureBytes = 8L * 1024 * 1024 * 1024,
                maximumWorkingSetBytes = resourceSampler.MaximumWorkingSetBytes,
                garbageCollector = resourceSampler.GarbageCollector,
                samples = resourceSampler.Samples,
                limitations = "Same-process client/server/oracle and sampler overhead included. Storage is sampled, not a hard allocation quota. ManagedBytes is an estimate without forced GC; LastGcHeapBytes describes the last collection, not retained live memory. No memory-drift verdict."
            },
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EntityForgeDbServer).Assembly.Location))),
            loadedEntityForgeAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic && assembly.GetName().Name?.StartsWith("EntityForge.", StringComparison.Ordinal) == true)
                .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal)
                .Select(assembly => new
                {
                    name = assembly.GetName().Name,
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))
                }).ToArray(),
            qualification = "Development same-process HTTP measurement. Read timings include validation; update timings exclude independent oracle fsync, conflict verification reads and explicit abort requests, which are included in throughput/CPU. Explicit GQL write_conflict responses are verified, their retained plans explicitly aborted, and reported separately, not counted as successful updates; other errors fail the run. Warmup is one read per client plus optional timed concurrent reads. Timed windows drain complete 90/10 request batches before oracle compaction and local retention; elapsed throughput includes maintenance and report-writing pauses. Restart verification is outside measured workload. Duration alone does not establish same-key conflict, power-loss, endurance or target-hardware qualification."
        }, new JsonSerializerOptions { WriteIndented = true });
        if (report is not null)
        {
            await WorkloadReport.WriteAsync(report, json, token);
        }
        TestContext.WriteLine(json);
    }
}
