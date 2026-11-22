using EntityForge.Db.Gql;
using EntityForge.Db.Graph;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlRowPredicateTests
{
    [TestMethod]
    public void Generated_identity_projection_preserves_import_order_values_and_scope_limit()
    {
        var scope = new HashSet<string>(StringComparer.Ordinal) { "second", "first" };
        var compiler = new GraphGqlQueryCompiler();
        var identity = compiler.CompileImportedRows("RETURN *", scope);
        var general = compiler.CompileImportedRows("RETURN second AS second,first AS first", scope);
        var row = new GraphPatternMatch(new Dictionary<string, QueryValue>
        {
            ["second"] = new LiteralValue("2", QueryScalarKind.Integer),
            ["first"] = UnboundValue.Instance
        }, []);
        var input = new QueryResultEnvelope(Guid.NewGuid(), new GraphPatternResult(new GraphResult([], []), [row]));
        string Apply(CompiledGraphGqlQuery plan) => String.Join("|", ((GraphPatternResult)new GqlRowPipeline(plan.Pipeline!.Stages)
            .Apply(input).Result).Matches.Single().Bindings.Select(pair => pair.Key + "=" + GraphScalarEvaluator.DistinctKey(pair.Value)));
        Assert.AreEqual(Apply(general), Apply(identity));
        var oldSeed = compiler.Compile(new("MATCH (__seed) RETURN __seed LIMIT 0"));
        Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(oldSeed.Query), System.Text.Json.JsonSerializer.Serialize(identity.Query));
        Assert.ThrowsExactly<ArgumentException>(() => compiler.CompileImportedRows("RETURN *",
            Enumerable.Range(0, 33).Select(i => "value" + i).ToHashSet(StringComparer.Ordinal)));
    }

    [TestMethod]
    [DataRow("MATCH (n) WHERE n.key=1 RETURN *", true)]
    [DataRow("MATCH (n) WHERE n.key>=1 AND n.key<11 RETURN *", true)]
    [DataRow("MATCH (n) WHERE 1<n.key RETURN *", true)]
    [DataRow("MATCH (n) WHERE n.name='value' RETURN *", true)]
    [DataRow("MATCH (n) WHERE 'value'=n.name RETURN *", true)]
    [DataRow("MATCH (n) WHERE n.name<'value' RETURN *", false)]
    [DataRow("MATCH (n) WHERE n.name='value' OR n.name='other' RETURN *", false)]
    [DataRow("MATCH (n) WHERE n.key=1 OR n.key=2 RETURN *", false)]
    [DataRow("MATCH (n) WHERE n.key+1=2 RETURN *", false)]
    [DataRow("OPTIONAL MATCH (n) WHERE n.key=1 RETURN *", false)]
    [DataRow("MATCH (n)-[e]->(m) WHERE n.key=1 RETURN *", false)]
    public async Task Only_uncorrelated_simple_root_predicates_are_pushed_before_materialization(string source, bool expected)
    {
        var plan = new GraphGqlQueryCompiler().CompileImportedRows(source, new HashSet<string>());
        GraphPatternFilter? observed = null;
        int calls = 0;
        var empty = new QueryResultEnvelope(Guid.NewGuid(), new GraphPatternResult(new GraphResult([], []), []));
        var seed = new QueryResultEnvelope(Guid.NewGuid(), new GraphPatternResult(new GraphResult([], []), [new([], [])]));
        await plan.Pipeline!.ApplyAsync(seed, compiled =>
        {
            calls++;
            observed = compiled.Query.Filter;
            return ValueTask.FromResult(empty);
        }, default, new GqlExecutionBudget());
        Assert.AreEqual(1, calls);
        Assert.AreEqual(expected, observed is GraphPatternExpressionFilter);
        if (observed is GraphPatternExpressionFilter expression)
        {
            Assert.IsTrue(expression.Expression.Bindings.All(binding => binding == "n"));
        }
    }
}
