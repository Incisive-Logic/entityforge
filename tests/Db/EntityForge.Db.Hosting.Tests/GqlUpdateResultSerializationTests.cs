using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using System.Diagnostics;
using System.Text.Json;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlUpdateResultSerializationTests
{
    [TestMethod]
    public void Captured_results_preserve_identity_diagnostics_and_completion_in_parallel()
    {
        Parallel.For(0, 32, index =>
        {
            var envelope = new QueryResultEnvelope(Guid.NewGuid(),
                new RecordResult([new RecordField("value")], [new RecordRow([
                    new KeyValuePair<string, QueryValue>("value", new LiteralValue(index.ToString()))])]),
                diagnostics: [new("01G11", "Null value eliminated in set function.", QueryDiagnosticSeverity.Warning)]);
            byte[] expected = JsonSerializer.SerializeToUtf8Bytes(
                GraphPatternQueryResponseMapper.Project(GraphGqlCompletion.Complete(envelope, false)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            CollectionAssert.AreEqual(expected, GqlUpdateResultCodec.CaptureResult(envelope));
        });
    }

    [TestMethod]
    public void Oversized_update_result_is_still_rejected()
    {
        var envelope = new QueryResultEnvelope(Guid.NewGuid(), new RecordResult([new RecordField("value")],
            [new RecordRow([new KeyValuePair<string, QueryValue>("value", new LiteralValue(new string('x', 1_048_576)))])]));
        Assert.ThrowsExactly<ArgumentException>(() => GqlUpdateResultCodec.CaptureResult(envelope));
    }

    [TestMethod]
    [DoNotParallelize]
    [TestCategory("QueryBenchmark")]
    public void Update_result_metadata_reuse_reports_allocations()
    {
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_WRITE_ALLOCATION_REPORT") is not { Length: > 0 } output)
        {
            Assert.Inconclusive("Opt in with ENTITYFORGE_WRITE_ALLOCATION_REPORT.");
            return;
        }
        var envelope = new QueryResultEnvelope(Guid.Empty, new GraphPatternResult(new GraphResult([], []), []));
        byte[] Previous()
        {
            using var stream = new MemoryStream();
            JsonSerializer.Serialize(stream, GraphPatternQueryResponseMapper.Project(GraphGqlCompletion.Complete(envelope, true)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return stream.ToArray();
        }
        byte[] Current() => GqlUpdateResultCodec.CaptureResult(envelope, true);
        CollectionAssert.AreEqual(Previous(), Current());
        object Measure(Func<byte[]> run)
        {
            for (int i = 0; i < 100; i++)
            {
                run();
            }
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < 1000; i++)
            {
                run();
            }
            return new
            {
                millisecondsPerOperation = Stopwatch.GetElapsedTime(start).TotalMilliseconds / 1000,
                allocatedBytesPerOperation = (GC.GetAllocatedBytesForCurrentThread() - bytes) / 1000d
            };
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            previous = Measure(Previous),
            current = Measure(Current),
            scope = "Serializer metadata reuse only; prior comparison uses an unbounded MemoryStream. No storage, replay or HTTP costs; no threshold."
        },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
