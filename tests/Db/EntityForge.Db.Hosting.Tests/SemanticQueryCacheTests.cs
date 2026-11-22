using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Semantic.Engine;
using System.Text.Json;
using EntityForge.Db.Query;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Semantics;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SemanticQueryCacheTests
{
    [TestMethod]
    public async Task Candidate_cache_is_scoped_to_the_exact_view_even_at_the_same_native_version()
    {
        var concept = new SemanticTerm(new Uri("urn:conflict:item"), SemanticTermRole.Concept);
        var number = new SemanticTerm(new Uri("urn:conflict:number"), SemanticTermRole.Property);
        var scheme = new SemanticIdentityScheme(new Uri("urn:conflict:id"), "id", SemanticValueKind.String);
        var model = new SemanticModelReference(new Uri("urn:conflict:model"), "1", SemanticModelKind.Vocabulary);
        var subject = new SemanticEntityIdentity(concept, scheme, new StringSemanticValue("one"));
        SemanticFieldEvidence Field(int value) => new(Guid.NewGuid(), subject, number, new IntegerSemanticValue(value),
            new(new Uri("urn:conflict:source"), "one", DateTimeOffset.UnixEpoch, model), new(1), null);
        PortableEntityFilterRequest Compare(string op, int n) => new("propertyComparison", number.Identifier.AbsoluteUri,
            op, new(JsonSerializer.SerializeToElement(n), "integer"));
        var filters = new[]
        {
            Compare("equal", 1),
            new PortableEntityFilterRequest("and", Operands: [Compare("greaterThanOrEqual", 1), Compare("lessThanOrEqual", 1)]),
            new PortableEntityFilterRequest("or", Operands: [Compare("equal", 0), Compare("equal", 2)])
        };
        // [0,2] satisfies the two existential range conjuncts using DIFFERENT
        // assertions. A scalar min/max or single-value matcher is unsound here.
        int[][] values = [[], [0], [1], [2], [0, 2]];
        int checkedPairs = 0;
        foreach (var filter in filters)
        {
            foreach (var oldValues in values)
            {
                foreach (var newValues in values)
                {
                    var compiled = new PortableEntityQueryRequestCompiler().Compile(new(concept.Identifier.AbsoluteUri,
                        new(concept.Identifier.AbsoluteUri, Fields: [new(number.Identifier.AbsoluteUri, "number")]),
                        Filter: filter, ConceptMatchMode: "direct"));
                    var before = SemanticDatabaseState.Empty with
                    {
                        Version = new(2),
                        Fields = oldValues.Select(Field).ToArray(),
                        Catalog = new([model], [concept, number], [scheme])
                    };
                    var after = before with
                    {
                        Fields = newValues.Select(Field).Concat(new[]
                        {
                Field(1) with { EndVersion = new(2) }, Field(1) with { BeginVersion = new(3) }
                        }).ToArray()
                    };
                    before = before with
                    {
                        AssertionStatistics = new(before.Fields.Count, 0),
                        QueryStatistics = SemanticQueryStatistics.Empty.Apply(new(before.Fields.ToArray(), []), before.Version)
                    };
                    after = after with
                    {
                        AssertionStatistics = new(newValues.Length, 0),
                        QueryStatistics = SemanticQueryStatistics.Empty.Apply(new(after.Fields.Where(e => e.BeginVersion.Value <= 2 && e.EndVersion is null).ToArray(), []), after.Version)
                    };
                    var executor = new PortableEntityQueryExecutor();
                    var oldResult = await executor.ExecuteAsync(before, DatabaseReadView.Create(before, SemanticTimestampIndex.For(before)).EntityAccessPaths, compiled.Query,
                        compiled.ProjectionOptions, before.Version);
                    var newResult = await executor.ExecuteAsync(after, DatabaseReadView.Create(after, SemanticTimestampIndex.For(after)).EntityAccessPaths, compiled.Query,
                        compiled.ProjectionOptions, after.Version);
                    bool Matches(int[] evidence) => filter == filters[0] ? evidence.Contains(1)
                        : filter == filters[1] ? evidence.Any(v => v >= 1) && evidence.Any(v => v <= 1)
                        : evidence.Any(v => v == 0 || v == 2);
                    Assert.AreEqual(Matches(oldValues), oldResult.Document.Roots.Count != 0);
                    Assert.AreEqual(Matches(newValues), newResult.Document.Roots.Count != 0);
                    checkedPairs++;
                }
            }
        }

        Assert.AreEqual(75, checkedPairs);
    }
}
