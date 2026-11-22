using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlGroupingKeyTests
{
    [TestMethod]
    public void Dense_candidates_preserve_order_and_do_not_leak_vertices_between_views()
    {
        var a = new GraphVertex(GraphVertexId.New(), new HashSet<string>(), new Dictionary<string, DatabaseValue>());
        var b = new GraphVertex(GraphVertexId.New(), new HashSet<string>(), new Dictionary<string, DatabaseValue>());
        var original = GraphReadView.Build(new Dictionary<GraphVertexId, GraphVertex> { [a.Id] = a, [b.Id] = b },
            new Dictionary<GraphEdgeId, GraphEdge>());
        Assert.IsNotNull(original.TryDenseCandidates([a.Id, b.Id], default));
        Assert.IsNull(original.TryDenseCandidates([b.Id, a.Id], default));
        Assert.IsNull(original.TryDenseCandidates([a.Id], default));
        var changed = new GraphVertex(a.Id, new HashSet<string>(), new Dictionary<string, DatabaseValue> { ["amount"] = new DatabaseValue.Int64(7) });
        var updated = GraphReadView.Build(new Dictionary<GraphVertexId, GraphVertex> { [a.Id] = changed, [b.Id] = b }, original.Edges);
        Assert.AreSame(changed, updated.TryDenseCandidates([a.Id, b.Id], default)![0]);
        Assert.AreSame(a, original.TryDenseCandidates([a.Id, b.Id], default)![0]);
    }

    [TestMethod]
    public async Task Grouped_root_accumulators_match_general_execution_with_nulls_mixed_numbers_and_paging()
    {
        DatabaseValue?[] amounts = [new DatabaseValue.Int64(Int64.MaxValue), new DatabaseValue.Int64(1),
            new DatabaseValue.Decimal(.5m), new DatabaseValue.Double(-0d), null];
        var vertices = Enumerable.Range(0, 100).Select(i =>
        {
            var values = new Dictionary<string, DatabaseValue> { ["bucket"] = new DatabaseValue.Int64(i % 7) };
            if (amounts[i % amounts.Length] is { } amount)
            {
                values["amount"] = amount;
            }
            return new GraphVertex(GraphVertexId.New(), new HashSet<string>(), values);
        }).ToDictionary(v => v.Id);
        var view = GraphReadView.Build(vertices, new Dictionary<GraphEdgeId, GraphEdge>());
        var snapshot = new GraphQuerySnapshot(new(1), view, view.StatisticsFor(new(1)));
        var plan = new GraphPatternAggregation([new(GraphPatternAggregateFunction.Count, "total"),
            new(GraphPatternAggregateFunction.Sum, "sum", "n", "amount"), new(GraphPatternAggregateFunction.Average, "average", "n", "amount")],
            groupBy: [new("bucket", expression: new("n", "bucket", null))],
            orderBy: [new("bucket", GraphPatternSortDirection.Descending)], offset: 1, limit: 4);
        async Task<(string Rows, string Warnings)> Run(bool general)
        {
            var query = new BoundedGraphPatternQuery(new("n"), [], aggregation: plan,
                filter: general ? new GraphPatternExpressionFilter(new(null, null, new LiteralValue("true", QueryScalarKind.Boolean))) : null);
            var result = await new BoundedGraphPatternExecutor(null).ExecuteCapturedResultAsync(query, snapshot, default);
            return (String.Join(";", ((RecordResult)result.Result).Rows.Select(row => String.Join("|", row.Values.Select(pair =>
                pair.Key + "=" + GraphScalarEvaluator.DistinctKey(pair.Value))))),
                String.Join(";", result.Diagnostics.Select(diagnostic => diagnostic.Code)));
        }
        Assert.AreEqual(await Run(true), await Run(false));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(100)]
    [DataRow(3000)]
    public async Task Property_group_keys_agree_with_general_expression_groups(int count)
    {
        DatabaseValue?[] values = [new DatabaseValue.Int64(1), new DatabaseValue.Decimal(1m), new DatabaseValue.Double(1d),
            new DatabaseValue.Int64(0), new DatabaseValue.Double(-0d), new DatabaseValue.Double(Double.NaN),
            new DatabaseValue.Decimal(.1m), new DatabaseValue.Double(.1d), new DatabaseValue.String("1"), null];
        var vertices = Enumerable.Range(0, count).Select(i =>
        {
            var properties = new Dictionary<string, DatabaseValue> { ["amount"] = new DatabaseValue.Int64(i) };
            var value = i < 1000 ? values[i % values.Length] : new DatabaseValue.Int64(i);
            if (value is not null)
            {
                properties.Add("bucket", value);
            }
            return new GraphVertex(GraphVertexId.New(), new HashSet<string>(), properties);
        }).ToDictionary(v => v.Id);
        var view = GraphReadView.Build(vertices, new Dictionary<GraphEdgeId, GraphEdge>());
        var snapshot = new GraphQuerySnapshot(new(1), view, view.StatisticsFor(new(1)));
        GraphScalarExpression property = new("n", "bucket", null);
        GraphScalarExpression general = new(null, null, null, "COALESCE", [property, new(null, null, UnboundValue.Instance)]);
        async Task<string[]> Rows(GraphScalarExpression expression)
        {
            var plan = new GraphPatternAggregation([new(GraphPatternAggregateFunction.Count, "total"),
                new(GraphPatternAggregateFunction.Sum, "sum", "n", "amount")],
                groupBy: [new("bucket", expression: expression)]);
            var query = new BoundedGraphPatternQuery(new("n"), [], aggregation: plan);
            var result = await new BoundedGraphPatternExecutor(null).ExecuteCapturedResultAsync(query, snapshot, default);
            return ((RecordResult)result.Result).Rows.Select(row => String.Join("|", row.Values.Select(pair =>
                pair.Key + "=" + GraphScalarEvaluator.DistinctKey(pair.Value)))).Order().ToArray();
        }
        var actual = await Rows(property);
        CollectionAssert.AreEqual(await Rows(general), actual);
        if (count == 100)
        {
            Assert.AreEqual(7, actual.Length);
        }
    }
}
