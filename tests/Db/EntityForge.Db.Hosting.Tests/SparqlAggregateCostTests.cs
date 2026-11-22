using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [TestCategory("QueryBenchmarkDiagnostics")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Aggregate_diagnostic_separates_source_page_time_from_reduction(bool persisted)
    {
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_QUERY_BENCHMARK_DIAGNOSTICS") != "1")
        {
            Assert.Inconclusive("Opt in to the bounded aggregation cost diagnostic.");
        }
        int size = Setting("ENTITYFORGE_AGGREGATE_DIAGNOSTIC_ROWS", 100003, 150000);
        int routes = Setting("ENTITYFORGE_AGGREGATE_DIAGNOSTIC_ROUTES", 0, 5);
        await using var database = await OpenAsync(CreateDirectory());
        await using var source = new RdfQueryScanReader(database);
        string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
        RdfQuad Bucket(int key) => new(new RdfIri("urn:benchmark:" + Text(key)), new("urn:bucket"), new RdfLiteral(Text(key % 10)));
        if (persisted)
        {
            foreach (int[] batch in Enumerable.Range(0, size).Chunk(1000))
            {
                var mutations = new List<RdfMutation>();
                foreach (int key in batch)
                {
                    mutations.Add(new AssertRdfQuad(Bucket(key)));
                    mutations.Add(new AssertRdfQuad(new(new RdfIri("urn:benchmark:" + Text(key)), new("urn:key"), new RdfLiteral(Text(key)))));
                    for (int route = 0; route < routes; route++)
                    {
                        mutations.Add(new AssertRdfQuad(new(new RdfIri("urn:benchmark:" + Text(key)),
                            new("urn:route:" + Text(route)), new RdfIri("urn:benchmark:" + Text((key + route + 1) % size)))));
                    }
                }
                await database.CommitAsync(new(Guid.NewGuid(), mutations));
            }
        }
        long sourceTicks = 0;
        var work = new Dictionary<string, long>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, consumer) =>
        {
            if (instrument.Name == "entityforge.db.lookup.work")
            {
                consumer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? stage = null, name = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "entityforge.db.stage")
                {
                    stage = tag.Value?.ToString();
                }
                if (tag.Key == "entityforge.db.lookup.work")
                {
                    name = tag.Value?.ToString();
                }
            }
            if (stage == "rdf_aggregate_diagnostic" && name is not null)
            {
                lock (work)
                {
                    work[name] = work.GetValueOrDefault(name) + value;
                }
            }
        });
        listener.Start();
        var role = AppendMergeLookupRole.Register("rdf_aggregate_diagnostic");
        int pages = 0, inputs = 0, position = 0;
        async ValueTask<RdfQueryPage> Read(RdfQuadPattern pattern, int pageSize, string? cursor, CancellationToken cancellation)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                RdfQueryPage page;
                if (persisted)
                {
                    using var scope = AppendMergeLookupTelemetry.Begin(role, pageSize);
                    page = await source.ReadAsync(pattern, pageSize, cursor, cancellation);
                    scope?.Complete();
                }
                else
                {
                    cancellation.ThrowIfCancellationRequested();
                    int count = Math.Min(pageSize, size - position);
                    var quads = Enumerable.Range(position, count).Select(Bucket).ToArray();
                    position += count;
                    page = new(1, quads, position < size ? "next" : null, null);
                }
                pages++;
                inputs += page.Quads.Count;
                return page;
            }
            finally
            {
                sourceTicks += Stopwatch.GetTimestamp() - started;
            }
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var timer = Stopwatch.StartNew();
        var groups = new List<string>();
        bool completed = false;
        try
        {
            await foreach (var page in RdfSparql.ScanAsync(
                "SELECT ?bucket (COUNT(*) AS ?total) WHERE { ?s <urn:bucket> ?bucket } GROUP BY ?bucket",
                Read, persisted ? source.ReleaseAsync : (_, _) => ValueTask.FromResult(true),
                new(MaximumSortTemporaryBytes: 64 * 1024 * 1024), deadline.Token))
            {
                groups.AddRange(page.Rows.Select(row => ((RdfLiteral)row["bucket"]).LexicalForm + ":" + ((RdfLiteral)row["total"]).LexicalForm));
                completed |= page.Complete;
            }
            CollectionAssert.AreEquivalent(Enumerable.Range(0, 10).Select(bucket => Text(bucket) + ":" + Text((size - 1 - bucket) / 10 + 1)).ToArray(), groups);
            Assert.IsTrue(completed);
        }
        finally
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                persisted,
                size,
                routes,
                seededQuads = persisted ? size * (2 + routes) : 0,
                pages,
                inputs,
                completed,
                seconds = timer.Elapsed.TotalSeconds,
                work,
                sourceSeconds = (double)sourceTicks / Stopwatch.Frequency,
                retainedContinuations = database.RetainedQueryContinuationCount
            }));
        }

        static int Setting(string name, int fallback, int maximum)
        {
            string? text = Environment.GetEnvironmentVariable(name);
            if (text is null)
            {
                return fallback;
            }
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ||
                value < (name.EndsWith("ROWS", StringComparison.Ordinal) ? 1 : 0) || value > maximum)
            {
                throw new ArgumentOutOfRangeException(name, $"Diagnostic setting exceeds its bounded range (maximum {maximum}).");
            }
            return value;
        }
    }
}
