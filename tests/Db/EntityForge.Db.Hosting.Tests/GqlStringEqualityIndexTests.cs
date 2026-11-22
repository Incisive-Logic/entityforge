using EntityForge.Db.Gql;
using System.Collections.Immutable;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlStringEqualityIndexTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Compiled_string_lookup_uses_the_correct_direct_or_row_index(bool rowStage)
    {
        var vertices = Enumerable.Range(0, 100).Select(i => new GraphVertex(GraphVertexId.New(), new HashSet<string>(),
            new Dictionary<string, DatabaseValue> { ["scalar"] = new DatabaseValue.String("initial-" + i) })).ToImmutableDictionary(v => v.Id);
        var view = GraphReadView.Build(vertices, ImmutableDictionary<GraphEdgeId, GraphEdge>.Empty);
        var snapshot = new GraphQuerySnapshot(new(1), view, view.StatisticsFor(new(1)));
        var executor = new BoundedGraphPatternExecutor(null, capturedView: view);
        var paths = new List<string>();
        const string source = "MATCH (v) WHERE v.scalar = 'initial-17' RETURN v.scalar AS answer";
        var compiler = new GraphGqlQueryCompiler();
        var compiled = rowStage ? compiler.CompileImportedRows(source, new HashSet<string>()) : compiler.CompileStandard(new(source));
        var result = await compiled.ExecuteAsync(branch =>
        {
            paths.AddRange(executor.DescribeCandidates(branch.Query, snapshot).AccessPaths);
            return executor.ExecuteCapturedResultAsync(branch.Query, snapshot, default,
                branch.Projection is null ? null : property => GraphGqlPropertyProjectionShaper.Project(branch.Projection, property),
                false, branch.StartCandidates);
        }, default);
        Assert.IsTrue(paths.Contains(rowStage ? "snapshot.graph-string-equality" : "snapshot.graph-vertex-property-index"),
            "Paths: " + String.Join(",", paths));
        var row = ((GraphPatternResult)result.Result).Matches.Single();
        Assert.AreEqual("initial-17", ((LiteralValue)row.Bindings["answer"]).LexicalValue);
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    public async Task String_candidates_match_full_scan_for_unicode_binary_mixed_values_and_residual_labels(bool reversed, bool conjunction, bool disjunction)
    {
        string[] texts = ["", "A", "a", "A ", "é", "e\u0301", "\U0001f600", "\ud800", "\ufffd", "\0", "AQI=", "missing"];
        DatabaseValue?[] values = [.. texts[..^1].Select(text => (DatabaseValue)new DatabaseValue.String(text)),
            new DatabaseValue.Binary(new byte[] { 1, 2 }), new DatabaseValue.Int64(1), new DatabaseValue.Boolean(true),
            new DatabaseValue.String("A"), null];
        var vertices = values.Select((value, i) => new GraphVertex(GraphVertexId.New(),
            new HashSet<string> { i == values.Length - 2 ? "Other" : "Keep" },
            value is null ? new Dictionary<string, DatabaseValue>() : new()
            {
                ["value"] = value
            })).ToImmutableDictionary(v => v.Id);
        var view = GraphReadView.Build(vertices, ImmutableDictionary<GraphEdgeId, GraphEdge>.Empty);
        var snapshot = new GraphQuerySnapshot(new(1), view, view.StatisticsFor(new(1)));
        var executor = new BoundedGraphPatternExecutor(null);
        foreach (string text in texts)
        {
            var property = new GraphScalarExpression("n", "value", null);
            var literal = new GraphScalarExpression(null, null, new LiteralValue(text));
            var equal = new GraphScalarExpression(null, null, null, "=", reversed ? [literal, property] : [property, literal]);
            GraphScalarExpression expression = conjunction ? new(null, null, null, "AND",
                [equal, new(null, null, new LiteralValue("true", QueryScalarKind.Boolean))]) : equal;
            if (disjunction)
            {
                expression = new(null, null, null, "OR", [equal, new(null, null, new LiteralValue("true", QueryScalarKind.Boolean))]);
            }
            var query = new BoundedGraphPatternQuery(new("n", ["Keep"]), filter: new GraphPatternExpressionFilter(expression));
            async Task<string> Run(bool scan)
            {
                var result = await executor.ExecuteCapturedResultAsync(query, snapshot, default,
                    startCandidates: scan ? vertices.Keys.ToArray() : null);
                return String.Join("|", ((GraphPatternResult)result.Result).Matches.Select(row =>
                    GraphScalarEvaluator.DistinctKey(row.Bindings["n"])).Order(StringComparer.Ordinal));
            }
            Assert.AreEqual(await Run(true), await Run(false), "Indexed and full scan differ.");
            Assert.AreEqual(!disjunction, executor.DescribeCandidates(query, snapshot).AccessPaths.Contains("snapshot.graph-string-equality"));
        }
        Assert.AreEqual(2, view.FindScalarStringEquality(new("value", "AQI="))!.Count,
            "Both a stored string and a binary value project to this string literal.");
    }

    [TestMethod]
    public void String_indexes_keep_old_snapshots_and_invalidate_changed_properties_and_membership()
    {
        var id = GraphVertexId.New();
        var vertex = new GraphVertex(id, new HashSet<string>(), new Dictionary<string, DatabaseValue>
        {
            ["name"] = new DatabaseValue.String("before"),
            ["stable"] = new DatabaseValue.String("same")
        });
        var before = GraphReadView.Build(ImmutableDictionary<GraphVertexId, GraphVertex>.Empty.Add(id, vertex),
            ImmutableDictionary<GraphEdgeId, GraphEdge>.Empty);
        var old = before.FindScalarStringEquality(new("name", "before"));
        var stable = before.FindScalarStringEquality(new("stable", "same"));
        GraphReadView Change(GraphReadView source, params GraphMutation[] mutations)
        {
            var plan = new GraphTransactionPlanner(EntityForge.Values.JsonValueAdmissionPolicy.Default)
                .Plan(new(Guid.NewGuid(), mutations), source.Vertices, source.Edges);
            var changed = new GraphReadView(plan.Vertices, plan.Edges);
            changed.CarryUnchangedIndexes(source, plan.CommittedEffects.Effects);
            return changed;
        }
        var after = Change(before, new SetVertexProperty(id, "name", new DatabaseValue.String("after")));
        Assert.AreSame(stable, after.FindScalarStringEquality(new("stable", "same")));
        Assert.AreEqual(0, after.FindScalarStringEquality(new("name", "before"))!.Count);
        Assert.AreEqual(id, after.FindScalarStringEquality(new("name", "after"))!.Single());
        Assert.AreSame(old, before.FindScalarStringEquality(new("name", "before")));
        var addedId = GraphVertexId.New();
        var inserted = Change(after, new CreateVertex(addedId, []), new SetVertexProperty(addedId, "name", new DatabaseValue.String("after")));
        Assert.AreEqual(2, inserted.FindScalarStringEquality(new("name", "after"))!.Count);
        var deleted = Change(inserted, new DeleteVertex(id));
        Assert.AreEqual(addedId, deleted.FindScalarStringEquality(new("name", "after"))!.Single());
    }

    [TestMethod]
    public void Oversized_string_indexes_fall_back_and_cached_queries_observe_cancellation()
    {
        var id = GraphVertexId.New();
        var view = GraphReadView.Build(new Dictionary<GraphVertexId, GraphVertex>
        {
            [id] = new(id, new HashSet<string>(), new Dictionary<string, DatabaseValue>
            {
                ["large"] = new DatabaseValue.String(new string('x', 1_000_000)),
                ["small"] = new DatabaseValue.String("ok")
            })
        }, new Dictionary<GraphEdgeId, GraphEdge>());
        Assert.IsNull(view.FindScalarStringEquality(new("large", "absent")));
        Assert.AreEqual(id, view.FindScalarStringEquality(new("small", "ok"))!.Single());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => view.FindScalarStringEquality(new("small", "ok"), cancelled.Token));
    }
}
