using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db;
using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GraphPerformanceQualificationTests
{
    private const int DefaultCommitCount = 250;
    private const int DefaultTraversalCount = 1_000;
    private const string CommitCountEnvironmentVariable = "ENTITYFORGE_DB_GRAPH_AUDIT_COMMIT_COUNT";
    private const string TraversalCountEnvironmentVariable = "ENTITYFORGE_DB_GRAPH_AUDIT_TRAVERSAL_COUNT";
    private const double MaximumAcceptedP95CommitMilliseconds = 250;
    private const double MaximumAcceptedP95TraversalMilliseconds = 25;
    private const string WalFileName = "graph.wal";
    private const string CheckpointFileName = "graph.checkpoint";
    private string? _directory;

    public TestContext TestContext { get; set; } = null!;

    [TestCleanup]
    public void Cleanup()
    {
        if (_directory is not null && Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [TestMethod]
    [TestCategory("PerformanceAudit")]
    public async Task Graph_profile_records_repeatable_commit_traversal_checkpoint_and_recovery_baseline()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"entityforge-graph-performance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        int commitCount = Count(CommitCountEnvironmentVariable, DefaultCommitCount);
        int traversalCount = Count(TraversalCountEnvironmentVariable, DefaultTraversalCount);
        var hub = new GraphVertexId(Guid.NewGuid());
        using var measurements = new KernelMetricMeasurements();
        var commitLatency = new double[commitCount];
        double checkpointMilliseconds;

        await using (GraphDatabaseEngine database = await OpenAsync())
        {
            await database.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(hub, ["Hub"])]));
            for (int index = 0; index < commitCount; index++)
            {
                var vertex = new GraphVertexId(Guid.NewGuid());
                long started = Stopwatch.GetTimestamp();
                await database.CommitAsync(new(Guid.NewGuid(),
                    [new CreateVertex(vertex, ["Leaf"]),
                        new CreateEdge(GraphEdgeId.New(), hub, vertex, "contains"),
                        new SetVertexProperty(vertex, "ordinal", new DatabaseValue.Int64(index))]));
                commitLatency[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            long checkpointStarted = Stopwatch.GetTimestamp();
            await database.CreateCheckpointAsync();
            checkpointMilliseconds = Stopwatch.GetElapsedTime(checkpointStarted).TotalMilliseconds;
        }

        long recoveryStarted = Stopwatch.GetTimestamp();
        await using GraphDatabaseEngine reopened = await OpenAsync();
        double recoveryMilliseconds = Stopwatch.GetElapsedTime(recoveryStarted).TotalMilliseconds;
        var traversalLatency = new double[traversalCount];
        for (int index = 0; index < traversalCount; index++)
        {
            long started = Stopwatch.GetTimestamp();
            Assert.AreEqual(commitCount, reopened.Outgoing(hub, "contains").Count);
            traversalLatency[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        double commitP95 = Percentile(commitLatency, 0.95);
        double traversalP95 = Percentile(traversalLatency, 0.95);
        TestContext.WriteLine($"commits={commitCount};traversals={traversalCount};" +
            $"commit_p50_ms={Percentile(commitLatency, 0.50):F3};commit_p95_ms={commitP95:F3};" +
            $"traversal_p50_ms={Percentile(traversalLatency, 0.50):F3};" +
            $"traversal_p95_ms={traversalP95:F3};checkpoint_ms={checkpointMilliseconds:F3};" +
            $"recovery_ms={recoveryMilliseconds:F3};kernel_commit_metric={measurements.CommitCount}");

        Assert.IsTrue(commitP95 < MaximumAcceptedP95CommitMilliseconds,
            $"Graph commit p95 {commitP95:F3} ms exceeded the qualification guardrail.");
        Assert.IsTrue(traversalP95 < MaximumAcceptedP95TraversalMilliseconds,
            $"Graph traversal p95 {traversalP95:F3} ms exceeded the qualification guardrail.");
        Assert.AreEqual(commitCount + 1, measurements.CommitCount);
    }

    private async ValueTask<GraphDatabaseEngine> OpenAsync()
    {
        GroupedRedoDatabaseCommitLog redo = await GroupedRedoDatabaseCommitLog.OpenAsync(
            Path.Combine(_directory!, WalFileName));
        return await GraphDatabaseEngine.OpenAsync(redo,
            new SegmentedDatabaseCommittedChangeStore(Path.Combine(_directory!, "entityforge.changes")),
            new FileDatabaseCheckpointStore(Path.Combine(_directory!, CheckpointFileName)),
            await PhysicalPageFile.OpenAsync(Path.Combine(_directory!, "entityforge.pages")));
    }

    private static int Count(string environmentVariable, int defaultValue) =>
        int.TryParse(Environment.GetEnvironmentVariable(environmentVariable), out int value) && value > 0
            ? value : defaultValue;

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] ordered = values.OrderBy(value => value).ToArray();
        int index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private sealed class KernelMetricMeasurements : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, long> _totals = new(StringComparer.Ordinal);

        public KernelMetricMeasurements()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == DatabaseKernelTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
                _totals.AddOrUpdate(instrument.Name, measurement, (_, current) => current + measurement));
            _listener.Start();
        }

        public long CommitCount => _totals.GetValueOrDefault("entityforge.db.kernel.commits");

        public void Dispose() => _listener.Dispose();
    }
}
