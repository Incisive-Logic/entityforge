using EntityForge.Db.Query.Adapters;
using System.Diagnostics;
using System.Text.Json;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Query;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SemanticQueryScalingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Numeric_window_over_original_size_evidence_preserves_exact_roots()
    {
        const int Count = 100_000, First = 40_000, Width = 128;
        var concept = new SemanticTerm(new Uri("https://query-scaling.test/Item"), SemanticTermRole.Concept);
        var number = new SemanticTerm(new Uri("https://query-scaling.test/number"), SemanticTermRole.Property);
        var scheme = new SemanticIdentityScheme(new Uri("https://query-scaling.test/id"), "ID", SemanticValueKind.String);
        var model = new SemanticModelReference(new Uri("https://query-scaling.test/model"), "1", SemanticModelKind.Vocabulary);
        var fields = Enumerable.Range(0, Count).Select(index => new SemanticFieldEvidence(Guid.NewGuid(),
            new(concept, scheme, new StringSemanticValue(index.ToString())), number, new IntegerSemanticValue(index),
            new(new Uri("https://query-scaling.test/source"), index.ToString(), DateTimeOffset.UnixEpoch, model), new(1), null)).ToArray();
        var state = SemanticDatabaseState.Empty with
        {
            Version = new(1),
            Fields = fields,
            AssertionStatistics = new(Count, 0),
            QueryStatistics = SemanticQueryStatistics.Empty.Apply(new(fields, []), new(1))
        };
        PortableQueryLiteralRequest Integer(int value) => new(JsonSerializer.SerializeToElement(value), "integer");
        var request = new PortableEntityQueryRequest(concept.Identifier.AbsoluteUri,
            new(concept.Identifier.AbsoluteUri, Fields: [new(number.Identifier.AbsoluteUri, "number")]),
            Filter: new("and", Operands: [
                new("propertyComparison", number.Identifier.AbsoluteUri, "greaterThanOrEqual", Integer(First)),
                new("propertyComparison", number.Identifier.AbsoluteUri, "lessThan", Integer(First + Width))]),
            ConceptMatchMode: "direct");
        var compiled = new PortableEntityQueryRequestCompiler().Compile(request);
        var watch = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await new PortableEntityQueryExecutor().ExecuteAsync(state, new SemanticEntityAccessPaths(state),
            compiled.Query, compiled.ProjectionOptions, state.Version, deadline.Token);
        var mapped = PortableEntityQueryResponseMapper.Map(result, 0);
        Assert.AreEqual(Width, mapped.Entities.Count);
        CollectionAssert.AreEquivalent(Enumerable.Range(First, Width).Select(index => index.ToString()).ToArray(),
            mapped.Entities.Select(entity => entity.Fields["number"].Single().Value.ToString()).ToArray());
        TestContext.WriteLine($"100k numeric-window evaluation: {watch.Elapsed.TotalSeconds:F3}s; exact {Width} roots.");
    }
}
