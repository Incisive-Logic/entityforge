using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(5000)]
    [DataRow(5001)]
    public async Task Gql_list_concatenation_enforces_cardinality_with_standard_diagnostic(int half)
    {
        _directory = NewDirectory();
        await using (var database = await GraphDatabase.OpenLocalAsync(new(_directory)))
        {
            var id = GraphVertexId.New();
            var items = new DatabaseValue.Array(Enumerable.Repeat<DatabaseValue>(new DatabaseValue.Int64(0), half).ToArray());
            await database.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(id, ["ListSource"]), new SetVertexProperty(id, "items", items)]));
        }
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:ListSource) RETURN CARDINALITY(n.items || n.items) AS answer"
        });
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (half == 5000)
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, result.GetRawText());
            Assert.AreEqual(10000, result.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        }
        else
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, result.GetRawText());
            Assert.IsTrue(result.TryGetProperty("gqlStatus", out var status), result.GetRawText());
            Assert.AreEqual("2202F", status.GetString());
        }
        await host.StopAsync();
    }

    [TestMethod]
    public void Gql_collect_stops_at_first_excess_item_and_disposes_input()
    {
        int consumed = 0;
        bool disposed = false;
        var item = new LiteralValue("1", QueryScalarKind.Integer);
        IEnumerable<QueryValue> Input()
        {
            try
            {
                for (int index = 0; index < 10002; index++)
                {
                    consumed++;
                    yield return item;
                }
            }
            finally
            {
                disposed = true;
            }
        }
        var aggregate = new GraphPatternAggregate(GraphPatternAggregateFunction.Collect, "answer",
            expression: new GraphScalarExpression(null, null, item));
        var error = Assert.ThrowsExactly<ArgumentException>(() => BoundedGraphPatternExecutor.AggregateValues(aggregate, Input(), default));
        Assert.AreEqual(10001, consumed, "Collection must stop at the first item beyond its cardinality limit.");
        Assert.IsTrue(disposed);
        Assert.AreEqual("2202F", error.Data["EntityForge.GqlStatus"]);
        var accepted = (ListValue)BoundedGraphPatternExecutor.AggregateValues(aggregate, Enumerable.Repeat<QueryValue>(item, 10000), default);
        Assert.AreEqual(10000, accepted.Items.Count);
        var distinct = new GraphPatternAggregate(GraphPatternAggregateFunction.Collect, "answer", distinct: true,
            expression: new GraphScalarExpression(null, null, item));
        var reduced = (ListValue)BoundedGraphPatternExecutor.AggregateValues(distinct,
            Enumerable.Repeat<QueryValue>(item, 10002).Prepend(UnboundValue.Instance), default);
        Assert.AreEqual(1, reduced.Items.Count, "The limit applies after null elimination and DISTINCT.");
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void Gql_list_resource_budget_stops_lazy_input(bool aggregate, bool text)
    {
        int consumed = 0;
        bool disposed = false;
        QueryValue item = text ? new LiteralValue(new string('x', 600000))
            : new ListValue(Enumerable.Repeat<QueryValue>(new LiteralValue("1", QueryScalarKind.Integer), 6000).ToArray());
        IEnumerable<QueryValue> Input()
        {
            try
            {
                for (int index = 0; index < 3; index++)
                {
                    consumed++;
                    yield return item;
                }
            }
            finally
            {
                disposed = true;
            }
        }
        var collect = new GraphPatternAggregate(GraphPatternAggregateFunction.Collect, "answer",
            expression: new GraphScalarExpression(null, null, item));
        var error = Assert.ThrowsExactly<ArgumentException>(() =>
        {
            if (aggregate)
            {
                BoundedGraphPatternExecutor.AggregateValues(collect, Input(), default);
            }
            else
            {
                GraphScalarEvaluator.BoundedList(Input());
            }
        });
        Assert.AreEqual(2, consumed, "Stop reading as soon as the resource budget is exceeded.");
        Assert.IsTrue(disposed);
        Assert.IsFalse(error.Data.Contains("EntityForge.GqlStatus"));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void Gql_collection_validation_bounds_deep_programmatic_values_without_recursion(bool records, bool aggregate)
    {
        QueryValue nested = new LiteralValue("1", QueryScalarKind.Integer);
        for (int depth = 0; depth < 9999; depth++)
        {
            nested = records && depth % 2 == 0
                ? new MapValue([new KeyValuePair<string, QueryValue>("item", nested)])
                : new ListValue([nested]);
        }
        var collect = new GraphPatternAggregate(GraphPatternAggregateFunction.Collect, "answer",
            expression: new GraphScalarExpression(null, null, nested));
        ListValue Run(IEnumerable<QueryValue> values) => aggregate
            ? (ListValue)BoundedGraphPatternExecutor.AggregateValues(collect, values, default)
            : GraphScalarEvaluator.BoundedList(values);
        var accepted = Run([nested]);
        Assert.AreSame(nested, accepted.Items.Single());

        var oversized = new ListValue([nested]);
        int consumed = 0;
        bool disposed = false;
        IEnumerable<QueryValue> Input()
        {
            try
            {
                consumed++;
                yield return oversized;
                consumed++;
                yield return NullValue.Instance;
            }
            finally
            {
                disposed = true;
            }
        }
        var error = Assert.ThrowsExactly<ArgumentException>(() => Run(Input()));
        StringAssert.Contains(error.Message, "10,000 total elements");
        Assert.IsFalse(error.Data.Contains("EntityForge.GqlStatus"));
        Assert.AreEqual(1, consumed);
        Assert.IsTrue(disposed);
    }

    [TestMethod]
    public void Gql_nested_collection_work_and_text_limits_do_not_claim_cardinality_truncation()
    {
        var item = new LiteralValue("1", QueryScalarKind.Integer);
        var nested = new ListValue(Enumerable.Repeat<QueryValue>(item, 6000).ToArray());
        var work = Assert.ThrowsExactly<ArgumentException>(() => GraphScalarEvaluator.BoundedList([nested, nested]));
        Assert.IsFalse(work.Data.Contains("EntityForge.GqlStatus"));
        var text = new LiteralValue(new string('x', 600000));
        var characters = Assert.ThrowsExactly<ArgumentException>(() => GraphScalarEvaluator.BoundedList([text, text]));
        Assert.IsFalse(characters.Data.Contains("EntityForge.GqlStatus"));
    }
}
