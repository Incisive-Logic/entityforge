using EntityForge.Db.Semantic.Query;
using EntityForge.Db.Gql;
using System.Diagnostics;
using System.Text.Json;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SemanticViewMeasurementTests
{
    [TestMethod]
    [TestCategory("QueryCompilationBenchmark")]
    public async Task Cold_semantic_view_measurement_separates_capture_and_projection()
    {
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_SEMANTIC_VIEW_REPORT") is not { Length: > 0 } report)
        {
            Assert.Inconclusive("Opt in with ENTITYFORGE_SEMANTIC_VIEW_REPORT.");
            return;
        }
        string path = Path.Combine(Path.GetTempPath(), "entityforge-view-measurement-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await BenchmarkDatabase.OpenAsync(path, DatabaseProfileId.Semantic, "embedded-api", default);
            var database = host.Runtime!.Database;
            var store = database.EntityStore<BenchmarkItem>();
            for (int i = 0; i < 1000; i++)
            {
                await store.SaveAsync(new(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "initial-" + i));
            }
            using var retained = await database.AcquireCurrentDistributedStateAsync(default);
            var relation = await retained.CaptureQueryRelationAsync(SemanticNativeQueryView.MaximumEvidence, default);
            var cached = await SemanticNativeQueryEndpoints.CaptureAsync(database, default);
            const string point = "MATCH (v) WHERE v.scalar = 'initial-17' RETURN v.scalar AS answer";
            var compiled = new GraphGqlQueryCompiler().CompileStandard(new(point));
            var observations = new List<object>();
            const int iterations = 12, warmup = 2;
            foreach (string stage in new[] { "capture", "projection", "reused-projection", "cold-capture-and-projection", "hot-capture", "compile-point", "execute-point" })
            {
                async Task Run()
                {
                    if (stage == "hot-capture")
                    {
                        var current = await SemanticNativeQueryEndpoints.CaptureAsync(database, default);
                        Assert.AreSame(cached.View, current.View);
                        return;
                    }
                    if (stage == "compile-point")
                    {
                        GC.KeepAlive(new GraphGqlQueryCompiler().CompileStandard(new(point)));
                        return;
                    }
                    if (stage == "execute-point")
                    {
                        var result = await SemanticNativeQueryEndpoints.GqlAsync(cached.View, cached.Version, compiled, false, default);
                        Assert.AreEqual(1, ((EntityForge.Query.Results.GraphPatternResult)result.Result).Matches.Count);
                        return;
                    }
                    var input = stage is "projection" or "reused-projection" ? relation : await retained.CaptureQueryRelationAsync(SemanticNativeQueryView.MaximumEvidence, default);
                    if (stage != "capture")
                    {
                        var view = SemanticNativeQueryView.Create(input.Fields, input.Relationships, default,
                            stage == "reused-projection" ? cached.View : null);
                        Assert.IsTrue(view.Quads.Count > 1000);
                        GC.KeepAlive(view);
                    }
                }
                for (int i = 0; i < warmup; i++)
                {
                    await Run();
                }
                long bytes = GC.GetTotalAllocatedBytes(true);
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; i++)
                {
                    await Run();
                }
                observations.Add(new
                {
                    stage,
                    iterations,
                    millisecondsPerOperation = Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations,
                    allocatedBytesPerOperation = (GC.GetTotalAllocatedBytes(true) - bytes) / (double)iterations
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                rows = 1000,
                warmup,
                fields = relation.Fields.Length,
                relationships = relation.Relationships.Length,
                viewPayloadBytes = cached.View.PayloadBytes,
                observations,
                qualification = "Cold projection reuse disabled deliberately; capture uses one retained state. Process allocations include background work. Stages overlap and must not be added."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }
}
