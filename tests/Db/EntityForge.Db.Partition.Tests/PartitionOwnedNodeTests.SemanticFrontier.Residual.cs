using EntityForge.Query.Querying;
using EntityForge.Query.Values;
using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Query;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Semantics;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Semantic_frontier_residual_nonmatch_still_changes_evidence_and_bound_behavior()
    {
        var concept = new SemanticTerm(new Uri("urn:residual:item"), SemanticTermRole.Concept);
        var property = new SemanticTerm(new Uri("urn:residual:text"), SemanticTermRole.Property);
        var scheme = new SemanticIdentityScheme(new Uri("urn:residual:id"), "id", SemanticValueKind.String);
        var model = new SemanticModelReference(new Uri("urn:residual:model"), "1", SemanticModelKind.Vocabulary);
        var subject = new SemanticEntityIdentity(concept, scheme, new StringSemanticValue("one"));
        SemanticFieldEvidence Field(string value) => new(Guid.NewGuid(), subject, property, new StringSemanticValue(value),
            new(new Uri("urn:residual:source"), "one", DateTimeOffset.UnixEpoch, model), new(1), null);
        var request = new PortableEntityQueryRequest(concept.Identifier.AbsoluteUri, new(concept.Identifier.AbsoluteUri),
            Filter: new("propertyComparison", property.Identifier.AbsoluteUri, "contains", new(JsonSerializer.SerializeToElement("needle"))), ConceptMatchMode: "direct");
        var query = new EntityQueryDefinition(concept, new(concept), new PropertyComparisonEntityQueryFilter(property, EntityQueryComparisonOperator.Contains, new LiteralValue("needle")), conceptMatchMode: EntityConceptMatchMode.Direct);
        var before = SemanticDatabaseState.Empty with
        {
            Version = new(1),
            Fields = [Field("hay")],
            Catalog = new([model], [concept, property], [scheme])
        };
        var after = before with
        {
            Fields = [before.Fields[0], Field("straw")]
        };
        var selection = new PartitionQueryExchangeRequest("entity", EntityQuery: request, SemanticFrontier: new(0));
        string? previousDocument = null;
        foreach (var state in new[] { before, after })
        {
            var paths = DatabaseReadView.Create(state, SemanticTimestampIndex.For(state)).EntityAccessPaths;
            var relation = await PartitionOwnedNode.CaptureSemanticFrontierEvidenceAsync(state, paths, selection, default);
            Assert.AreEqual(state.Fields.Count, relation.Fields!.Length, "Residual nonmatches remain observable in the frontier.");
            var full = await new PortableEntityQueryExecutor().ExecuteAsync(state, paths, query, null, state.Version);
            Assert.AreEqual(0, full.Document.Roots.Count);
            string document = JsonSerializer.Serialize(new
            {
                full.Document.Roots,
                full.Document.Evidence,
                full.Document.SemanticModels
            });
            if (previousDocument is not null)
            {
                Assert.AreEqual(previousDocument, document, "Final document including models/evidence is unchanged.");
            }

            previousDocument = document;
        }
        var oversized = before with
        {
            Fields = Enumerable.Range(0, PartitionQueryLimits.MaximumFacts + 1).Select(_ => Field("hay")).ToArray()
        };
        var oversizedPaths = DatabaseReadView.Create(oversized, SemanticTimestampIndex.For(oversized)).EntityAccessPaths;
        var empty = await new PortableEntityQueryExecutor().ExecuteAsync(oversized, oversizedPaths, query, null, oversized.Version);
        Assert.AreEqual(0, empty.Document.Roots.Count);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await PartitionOwnedNode.CaptureSemanticFrontierEvidenceAsync(oversized, oversizedPaths, selection, default),
            "A nonmatching subject can change bounded frontier success into failure.");
    }
}
