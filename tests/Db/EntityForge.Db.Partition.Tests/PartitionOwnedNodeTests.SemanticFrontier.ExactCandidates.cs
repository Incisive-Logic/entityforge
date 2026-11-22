using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Semantics;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Semantic_frontier_indexed_scalar_candidates_exclude_large_nonmatching_subjects()
    {
        var concept = new SemanticTerm(new Uri("urn:exact:item"), SemanticTermRole.Concept);
        var property = new SemanticTerm(new Uri("urn:exact:number"), SemanticTermRole.Property);
        var scheme = new SemanticIdentityScheme(new Uri("urn:exact:id"), "id", SemanticValueKind.String);
        var model = new SemanticModelReference(new Uri("urn:exact:model"), "1", SemanticModelKind.Vocabulary);
        SemanticEntityIdentity Identity(string key) => new(concept, scheme, new StringSemanticValue(key));
        var selected = Identity("selected");
        var outside = Identity("outside");
        var multi = Identity("multi");
        SemanticFieldEvidence Field(SemanticEntityIdentity subject, int value) => new(Guid.NewGuid(), subject, property,
            new IntegerSemanticValue(value), new(new Uri("urn:exact:source"), "one", DateTimeOffset.UnixEpoch, model), new(1), null);
        PortableEntityFilterRequest Compare(string op, int value) => new("propertyComparison", property.Identifier.AbsoluteUri, op,
            new(JsonSerializer.SerializeToElement(value), "integer"));
        var fields = new[] { Field(selected, 1), Field(multi, 0), Field(multi, 2) };
        var before = SemanticDatabaseState.Empty with
        {
            Version = new(1),
            Fields = fields,
            Catalog = new([model], [concept, property], [scheme])
        };
        var after = before with
        {
            Fields = fields.Concat(Enumerable.Range(0, PartitionQueryLimits.MaximumFacts + 1).Select(_ => Field(outside, 9))).ToArray()
        };
        var filters = new[] { Compare("equal", 1), new PortableEntityFilterRequest("and", Operands: [Compare("greaterThanOrEqual", 1), Compare("lessThanOrEqual", 1)]) };
        foreach (var filter in filters)
        {
            var query = new PortableEntityQueryRequest(concept.Identifier.AbsoluteUri, new(concept.Identifier.AbsoluteUri), Filter: filter, ConceptMatchMode: "direct");
            var request = new PartitionQueryExchangeRequest("entity", EntityQuery: query, SemanticFrontier: new(0));
            SemanticFieldEvidence[]? previous = null;
            foreach (var state in new[] { before, after })
            {
                var paths = DatabaseReadView.Create(state, SemanticTimestampIndex.For(state)).EntityAccessPaths;
                var relation = await PartitionOwnedNode.CaptureSemanticFrontierEvidenceAsync(state, paths, request, default);
                var capturedFields = relation.Fields;
                Assert.IsNotNull(capturedFields);
                Assert.IsFalse(capturedFields.Any(field => field.Subject == outside));
                Assert.AreEqual(filter.Kind == "and" ? 3 : 1, capturedFields.Length,
                    "Range conjunctions must preserve existential matches on separate values 0 and 2.");
                if (previous is not null)
                {
                    CollectionAssert.AreEquivalent(previous, capturedFields);
                }

                previous = capturedFields;
            }
        }
        // Without the actual timestamp predicate provider, the same cold property
        // may fall back to concept candidates: never certify from syntax alone.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await PartitionOwnedNode.CaptureSemanticFrontierEvidenceAsync(after, new SemanticEntityAccessPaths(after),
                new("entity", EntityQuery: new(concept.Identifier.AbsoluteUri, new(concept.Identifier.AbsoluteUri), Filter: filters[0], ConceptMatchMode: "direct"), SemanticFrontier: new(0)), default));
    }
}
