using EntityForge.Db.Gql;
using System.Diagnostics;
using System.Text.Json;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [TestCategory("QueryBenchmark")]
    public void Gql_compilation_workload_reports_phase_allocations()
    {
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_MIXED_WORKLOAD") != "1")
        {
            Assert.Inconclusive("Opt in with ENTITYFORGE_MIXED_WORKLOAD=1.");
        }
        string[] sources = Enumerable.Range(0, 100)
            .Select(key => $"MATCH (n:Mixed) WHERE n.key={key} RETURN n.amount AS answer").ToArray();
        string[] lowered = sources.Select(source => GraphGqlStandardSyntax.PrepareParameterized(source, null)).ToArray();
        var observations = new List<object>();
        void Measure(string phase, Action<int> action)
        {
            for (int index = 0; index < 10; index++)
            {
                action(index);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            for (int index = 0; index < sources.Length; index++)
            {
                action(index);
            }
            observations.Add(new
            {
                phase,
                iterations = sources.Length,
                seconds = Stopwatch.GetElapsedTime(started).TotalSeconds,
                allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated
            });
        }
        Measure("validate", index => GraphGqlStandardSyntax.Validate(sources[index]));
        Measure("prepare", index => GraphGqlStandardSyntax.PrepareParameterized(sources[index], null));
        Measure("compile-lowered", index => new GraphGqlQueryCompiler().Compile(new(lowered[index])));
        Measure("compile-standard", index => new GraphGqlQueryCompiler().CompileStandard(new(sources[index])));
        string json = JsonSerializer.Serialize(new
        {
            schema = 1,
            observations,
            limitations = "Synchronous current-thread allocations, overlapping phases, warmed parser, 100 distinct integer literals. No execution, database, HTTP, or concurrent requests; not an end-to-end benchmark."
        }, new JsonSerializerOptions { WriteIndented = true });
        TestContext.WriteLine(json);
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_QUERY_BENCHMARK_REPORT_DIRECTORY") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "gql-compilation.json"), json);
        }
    }
}
