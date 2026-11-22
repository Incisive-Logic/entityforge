using EntityForge.Db.Gql;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlCompilationMeasurementTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("QueryCompilationBenchmark")]
    public void Gql_compilation_measurement_separates_syntax_lowering_and_core_compilation()
    {
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_QUERY_COMPILATION_REPORT") is not { Length: > 0 } report)
        {
            Assert.Inconclusive("Opt in with ENTITYFORGE_QUERY_COMPILATION_REPORT set to an output JSON file.");
            return;
        }
        report = Path.GetFullPath(report);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            status = "incomplete",
            startedUtc = DateTimeOffset.UtcNow
        }));
        const int iterations = 256, warmup = 32;
        var measurements = new List<object>();
        foreach (string workload in new[] { "literal-lookup", "parameter-lookup", "two-hop" })
        {
            var requests = Enumerable.Range(0, iterations).Select(index => new GraphGqlQueryRequest(workload switch
            {
                "parameter-lookup" => "MATCH (n:Benchmark {key:$key}) RETURN n.key AS answer",
                "two-hop" => "MATCH REPEATABLE ELEMENTS (n:Benchmark {key:" + index.ToString(CultureInfo.InvariantCulture) +
                    "})-[:route]->()-[:route]->(m) RETURN m.key AS answer",
                _ => "MATCH (n:Benchmark {key:" + index.ToString(CultureInfo.InvariantCulture) + "}) RETURN n.key AS answer"
            }, Parameters: workload == "parameter-lookup"
                ? new Dictionary<string, JsonElement> { ["key"] = JsonSerializer.SerializeToElement(index) } : null)).ToArray();
            var prepared = requests.Select(request => request with
            {
                Query = GraphGqlStandardSyntax.PrepareParameterized(request.Query, request.Parameters)
            }).ToArray();
            Measure("syntax", index => GqlSyntaxParser.Parse(requests[index].Query, out _));
            Measure("standard-lowering-including-syntax", index =>
                GraphGqlStandardSyntax.PrepareParameterized(requests[index].Query, requests[index].Parameters));
            Measure("core-compilation-of-prepared-text", index => new GraphGqlQueryCompiler().Compile(prepared[index]));
            Measure("standard-compilation-total", index => new GraphGqlQueryCompiler().CompileStandard(requests[index]));
            int originalRegexCacheSize = Regex.CacheSize;
            try
            {
                // Diagnostic intervention only: production code must not alter
                // the process-wide regex cache to improve one query engine.
                Regex.CacheSize = 128;
                Measure("core-compilation-expanded-regex-cache-diagnostic", index =>
                    new GraphGqlQueryCompiler().Compile(prepared[index]));
            }
            finally
            {
                Regex.CacheSize = originalRegexCacheSize;
            }

            void Measure(string stage, Func<int, object> operation)
            {
                for (int index = 0; index < warmup; index++)
                {
                    GC.KeepAlive(operation(index));
                }
                long allocations = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                for (int index = 0; index < iterations; index++)
                {
                    GC.KeepAlive(operation(index));
                }
                double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                long bytes = GC.GetAllocatedBytesForCurrentThread() - allocations;
                measurements.Add(new
                {
                    workload,
                    stage,
                    iterations,
                    warmup,
                    seconds,
                    allocatedBytes = bytes,
                    millisecondsPerOperation = seconds * 1000 / iterations,
                    allocatedBytesPerOperation = (double)bytes / iterations
                });
            }
        }
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            schema = 1,
            status = "completed",
            completedUtc = DateTimeOffset.UtcNow,
            os = RuntimeInformation.OSDescription,
            runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            logicalProcessors = Environment.ProcessorCount,
            assemblies = new[] { typeof(GraphGqlQueryRequest).Assembly, typeof(GqlSyntaxParser).Assembly }.Select(assembly => new
            {
                name = assembly.GetName().Name,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))
            }),
            measurements,
            qualification = "Bounded synchronous compilation diagnostic, 256 inputs per stage; no storage, HTTP or result execution. Stages overlap and must not be summed. Thread allocation includes the measured operation; retained memory and production SLOs are not measured. No performance threshold is asserted."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
