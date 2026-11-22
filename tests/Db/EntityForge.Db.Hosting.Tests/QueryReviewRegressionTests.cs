using EntityForge.Db.Query.Adapters;
using EntityForge.Query.Projection;
using System.Reflection;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Query;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Semantics;
using EntityForge.Query.Querying;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class QueryReviewRegressionTests
{
    [TestMethod]
    public async Task Nested_relationship_filters_share_the_target_budget()
    {
        var concept = new SemanticTerm(new Uri("urn:budget:Item"), SemanticTermRole.Concept);
        var relation = new SemanticTerm(new Uri("urn:budget:link"), SemanticTermRole.Relationship);
        var scheme = new SemanticIdentityScheme(new Uri("urn:budget:id"), "id", SemanticValueKind.String);
        var ids = Enumerable.Range(0, 5).Select(i => new SemanticEntityIdentity(concept, scheme, new StringSemanticValue(i.ToString()))).ToArray();
        var provenance = new SemanticProvenance(new Uri("urn:budget:source"), "row", DateTimeOffset.UnixEpoch,
            new SemanticModelReference(new Uri("urn:budget:model"), "1", SemanticModelKind.Vocabulary));
        SemanticRelationshipEvidence Edge(int from, int to) => new(Guid.NewGuid(), ids[from], relation, ids[to], provenance, new(1), null);
        SemanticRelationshipEvidence[] edges = [Edge(0, 1), Edge(0, 2), Edge(1, 3), Edge(2, 4)];
        var state = SemanticDatabaseState.Empty with
        {
            Version = new(1),
            Relationships = edges
        };
        var paths = new SemanticEntityAccessPaths(state);
        var filter = new RelationshipExistsEntityQueryFilter(relation, EntityRelationshipDirection.Outgoing, concept,
            new RelationshipExistsEntityQueryFilter(relation, EntityRelationshipDirection.Outgoing, concept));
        var method = typeof(PortableEntityQueryExecutor).GetMethod("ReadFilterTargetEvidenceAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        async Task Run(int limit)
        {
            var operation = (ValueTask<SemanticEvidenceSlice>)method.Invoke(null, new object?[]
            {
                paths, new[] { ids[0] }, filter, Array.Empty<SemanticFieldEvidence>(), edges,
                new DatabaseVersion(1), Array.Empty<EntityQueryScope>(), null, new SemanticFilterWorkBudget(limit), CancellationToken.None
            })!;
            await operation;
        }
        await Run(4);
        await Assert.ThrowsExactlyAsync<EntityForgeQueryCapabilityException>(() => Run(3));
    }

    [TestMethod]
    public void Graph_lookup_counters_are_bounded_under_concurrency_and_generation_carry()
    {
        var view = GraphReadView.Build(new Dictionary<GraphVertexId, GraphVertex>(), new Dictionary<GraphEdgeId, GraphEdge>());
        Parallel.For(0, 20_000, i => view.FindByProperty("missing-" + i, new DatabaseValue.String("value")));
        var member = typeof(GraphReadView).GetField("_propertyLookupCounts", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var counts = (System.Collections.Concurrent.ConcurrentDictionary<string, int>)member.GetValue(view)!;
        Assert.IsTrue(counts.Count <= 4096);
        for (int i = 0; i < 100; i++)
        {
            view.FindByProperty("hot", new DatabaseValue.Int64(i));
        }

        Assert.AreEqual(8, counts["hot"]);
        var next = GraphReadView.Build(view.Vertices, view.Edges);
        next.CarryUnchangedIndexes(view, []);
        var carried = (System.Collections.Concurrent.ConcurrentDictionary<string, int>)member.GetValue(next)!;
        Assert.IsTrue(carried.Count <= 4096);
        Assert.AreEqual(8, carried["hot"]);
    }

    [TestMethod]
    public void Filter_work_budget_counts_across_batches_and_rejects_before_over_limit_enumeration()
    {
        var concept = new SemanticTerm(new Uri("urn:budget:Item"), SemanticTermRole.Concept);
        var scheme = new SemanticIdentityScheme(new Uri("urn:budget:id"), "id", SemanticValueKind.String);
        var ids = Enumerable.Range(0, 4).Select(i => new SemanticEntityIdentity(concept, scheme, new StringSemanticValue(i.ToString()))).ToArray();
        var budget = new SemanticFilterWorkBudget(3);
        Assert.AreEqual(2, budget.AdmitTargets([ids[0], ids[0], ids[1]], default).Length);
        Assert.AreEqual(1, budget.AdmitTargets([ids[0]], default).Length);
        Assert.ThrowsExactly<EntityForgeQueryCapabilityException>(() => budget.AdmitTargets([ids[2]], default));
        Assert.ThrowsExactly<OperationCanceledException>(() => new SemanticFilterWorkBudget().AdmitTargets(ids, new(true)));
    }

    [TestMethod]
    public void Graph_ordering_cache_bounds_retention_and_shares_absent_property_order()
    {
        var vertices = Enumerable.Range(0, 2000).Select(_ => new GraphVertex(GraphVertexId.New(),
            new HashSet<string>(), new Dictionary<string, DatabaseValue>())).ToDictionary(vertex => vertex.Id);
        var view = GraphReadView.Build(vertices, new Dictionary<GraphEdgeId, GraphEdge>());
        var first = view.OrderedByProperty("absent-0", GraphPatternSortDirection.Ascending);
        for (int i = 1; i < 1000; i++)
        {
            Assert.AreSame(first, view.OrderedByProperty("absent-" + i, GraphPatternSortDirection.Descending));
        }

        var cache = (GraphQueryCache<(string, GraphPatternSortDirection), IReadOnlyList<GraphVertexId>>)
            typeof(GraphReadView).GetField("_orderedProperties", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        Assert.IsTrue(cache.Count <= 32);
        Assert.IsTrue(cache.RetainedEntries <= 1_000_000);
        Assert.ThrowsExactly<OperationCanceledException>(() => view.OrderedByProperty("new", GraphPatternSortDirection.Ascending, new(true)));
    }

    [TestMethod]
    public async Task Graph_cache_builds_a_shared_value_once_and_does_not_retain_oversized_results()
    {
        var cache = new GraphQueryCache<int, int[]>(array => array.Length);
        int builds = 0;
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            cache.GetOrAdd(1, _ =>
            {
                Interlocked.Increment(ref builds);
                return new int[100];
            }))));
        Assert.AreEqual(1, builds);
        Assert.IsTrue(results.All(result => ReferenceEquals(results[0], result)));
        cache.GetOrAdd(2, _ => new int[1_000_001]);
        Assert.AreEqual(1, cache.Count);
    }

    [DataTestMethod]
    [DataRow("ascending", "minimum", "first", 1L)]
    [DataRow("descending", "maximum", "last", 1L)]
    [DataRow("ascending", "maximum", "last", 2L)]
    [DataRow("descending", "minimum", "first", 2L)]
    public async Task Batched_pages_match_full_sort_with_history_missing_and_multiple_values(
        string direction, string selection, string missing, long version)
    {
        var concept = new SemanticTerm(new Uri("urn:review:Item"), SemanticTermRole.Concept);
        var number = new SemanticTerm(new Uri("urn:review:number"), SemanticTermRole.Property);
        var name = new SemanticTerm(new Uri("urn:review:name"), SemanticTermRole.Property);
        var scheme = new SemanticIdentityScheme(new Uri("urn:review:id"), "id", SemanticValueKind.String);
        var model = new SemanticModelReference(new Uri("urn:review:model"), "1", SemanticModelKind.Vocabulary);
        var fields = new List<SemanticFieldEvidence>();
        for (int i = 0; i < 1050; i++)
        {
            var subject = new SemanticEntityIdentity(concept, scheme, new StringSemanticValue(i.ToString()));
            var provenance = new SemanticProvenance(new Uri("urn:review:source"), i.ToString(), DateTimeOffset.UnixEpoch, model);
            fields.Add(new(Guid.NewGuid(), subject, name, new StringSemanticValue(i.ToString("D4")), provenance, new(1), null));
            if (i % 7 == 0)
            {
                continue;
            }

            fields.Add(new(Guid.NewGuid(), subject, number, new IntegerSemanticValue(i % 23), provenance, new(1), new(2)));
            fields.Add(new(Guid.NewGuid(), subject, number, new IntegerSemanticValue(50 - i % 23), provenance, new(2), null));
            if (i % 3 == 0)
            {
                fields.Add(new(Guid.NewGuid(), subject, number, new IntegerSemanticValue(-i), provenance, new(1), null));
            }
        }
        var state = SemanticDatabaseState.Empty with
        {
            Version = new(2),
            Fields = fields.ToArray()
        };
        var access = new SemanticEntityAccessPaths(state);
        var executor = new PortableEntityQueryExecutor();
        var request = new PortableEntityQueryRequest(concept.Identifier.AbsoluteUri,
            new(concept.Identifier.AbsoluteUri, Fields: [new(name.Identifier.AbsoluteUri, "name")]),
            OrderBy: [new(number.Identifier.AbsoluteUri, direction, selection, missing), new(name.Identifier.AbsoluteUri, "descending")],
            ConceptMatchMode: "direct");
        async Task<EntityForge.Query.Projection.EntityProjectionProjectionResult> Execute(PortableEntityQueryRequest input)
        {
            var compiled = new PortableEntityQueryRequestCompiler().Compile(input);
            return await executor.ExecuteAsync(state, access, compiled.Query, compiled.ProjectionOptions, new(version));
        }
        var expected = (await Execute(request)).Document.Roots.Select(root => root.Identity).ToArray();
        var actual = new List<EntityForge.Query.Values.GraphElementIdentity>();
        string? after = null;
        do
        {
            var page = await Execute(request with
            {
                First = 37,
                After = after
            });
            actual.AddRange(page.Document.Roots.Select(root => root.Identity));
            Assert.IsTrue(actual.Count <= expected.Length);
            after = page.NextContinuation;
        } while (after is not null);
        CollectionAssert.AreEqual(expected, actual.ToArray());
    }
}
