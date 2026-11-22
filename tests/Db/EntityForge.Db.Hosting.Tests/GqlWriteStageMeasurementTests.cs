using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Results;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlWriteStageMeasurementTests
{
    private readonly TemporaryTestDirectories _directories = new("entityforge-gql-write-stages");

    [TestCleanup]
    public void Cleanup() => _directories.Cleanup();

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("QueryBenchmark")]
    public async Task Write_stage_measurement_separates_compilation_preview_and_encoding()
    {
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_WRITE_STAGE_REPORT") is not { Length: > 0 } output)
        {
            Assert.Inconclusive("Opt in with ENTITYFORGE_WRITE_STAGE_REPORT.");
            return;
        }
        var vertices = Enumerable.Range(0, 3000).Select(i => new GraphVertex(GraphVertexId.New(), new HashSet<string> { "Bench" },
            new Dictionary<string, DatabaseValue> { ["key"] = new DatabaseValue.Int64(i), ["score"] = new DatabaseValue.Int64(i) }))
            .ToImmutableDictionary(vertex => vertex.Id);
        var view = GraphReadView.Build(vertices, ImmutableDictionary<GraphEdgeId, GraphEdge>.Empty);
        var statistics = view.StatisticsFor(new(1));
        var reports = new List<object>();
        foreach (bool range in new[] { false, true })
        {
            string source = "MATCH (n:Bench) WHERE " + (range ? "n.key >= $key AND n.key < $upper" : "n.key=$key") +
                " SET n.score=$score RETURN n.key AS key,n.score AS score";
            var parameters = new Dictionary<string, JsonElement>
            {
                ["key"] = JsonSerializer.SerializeToElement(17),
                ["upper"] = JsonSerializer.SerializeToElement(27),
                ["score"] = JsonSerializer.SerializeToElement(-1)
            };
            Guid operation = Guid.NewGuid();
            string prepared = GraphGqlStandardSyntax.PrepareParameterized(source, parameters);
            var compiled = GraphGqlMutationCompiler.Compile(prepared, parameters, operation);
            QueryResultEnvelope? result = null;
            async ValueTask<IReadOnlyList<GraphMutation>> Bind()
            {
                var mutations = await GraphGqlMutationCompiler.BindProgramAsync(compiled.Program!, view, new(1), operation,
                    default, rows => result = rows, statistics);
                Assert.AreEqual(range ? 10 : 1, mutations.Count);
                Assert.AreEqual(range ? 10 : 1, ((GraphPatternResult)result!.Result).Matches.Count);
                return mutations;
            }
            async Task Measure(string stage, Func<ValueTask> run)
            {
                for (int i = 0; i < 30; i++)
                {
                    await run();
                }
                long bytes = GC.GetTotalAllocatedBytes(true);
                long started = Stopwatch.GetTimestamp();
                for (int i = 0; i < 200; i++)
                {
                    await run();
                }
                reports.Add(new
                {
                    range,
                    stage,
                    iterations = 200,
                    millisecondsPerOperation = Stopwatch.GetElapsedTime(started).TotalMilliseconds / 200,
                    bytesPerOperation = (GC.GetTotalAllocatedBytes(true) - bytes) / 200d
                });
            }
            await Measure("lowering", () =>
            {
                GC.KeepAlive(GraphGqlStandardSyntax.PrepareParameterized(source, parameters));
                return ValueTask.CompletedTask;
            });
            await Measure("compilation", () =>
            {
                GC.KeepAlive(GraphGqlMutationCompiler.Compile(prepared, parameters, operation));
                return ValueTask.CompletedTask;
            });
            await Measure("preview-including-result-checks", async () =>
            {
                _ = await Bind();
            });
            var mutations = await Bind();
            await Measure("result-and-transaction-encoding", () =>
            {
                GC.KeepAlive(GraphTransactionCodec.Encode(new(operation, mutations, new(1), [new DatabaseRead()])
                {
                    QueryResult = GqlUpdateResultCodec.CaptureResult(result!)
                }));
                return ValueTask.CompletedTask;
            });
            await using var database = await GraphDatabase.OpenLocalAsync(new(_directories.Create()));
            await database.BulkLoadAsync(Guid.NewGuid(), vertices.Values.ToArray(), []);
            var phases = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            bool recording = false;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Name == "entityforge.db.kernel.commit.phase.duration")
                {
                    owner.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
            {
                string? phase = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "entityforge.db.phase")
                    {
                        phase = tag.Value?.ToString();
                    }
                }
                lock (phases)
                {
                    if (!recording || phase is null)
                    {
                        return;
                    }
                    if (!phases.TryGetValue(phase, out var values))
                    {
                        phases.Add(phase, values = []);
                    }
                    values.Add(value);
                }
            });
            listener.Start();
            long writeStarted = 0;
            for (int i = 0; i < 110; i++)
            {
                if (i == 10)
                {
                    lock (phases)
                    {
                        recording = true;
                    }
                    writeStarted = Stopwatch.GetTimestamp();
                }
                parameters["score"] = JsonSerializer.SerializeToElement(-i - 1);
                var committed = await GraphGqlUpdateExecution.ApplyAsync(database, new(source, Guid.CreateVersion7(), parameters), default);
                Assert.AreEqual(range ? 10 : 1, committed.MutationCount);
            }
            reports.Add(new
            {
                range,
                stage = "embedded-durable-total-with-phase-listener",
                iterations = 100,
                millisecondsPerOperation = Stopwatch.GetElapsedTime(writeStarted).TotalMilliseconds / 100,
                phases = phases.Select(pair => new { phase = pair.Key, samples = pair.Value.Count, millisecondsPerOperation = pair.Value.Sum() / 100 }).ToArray()
            });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            scope = "In-memory stages exclude storage; embedded total includes durable storage and phase listener overhead. No HTTP. Phase timings may overlap; do not sum. Process-wide allocation, no performance assertion.",
            reports
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
