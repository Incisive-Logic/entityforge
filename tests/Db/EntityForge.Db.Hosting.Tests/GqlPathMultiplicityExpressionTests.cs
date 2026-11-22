using EntityForge.Db.Gql;
using EntityForge.Db.Graph;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlPathMultiplicityExpressionTests
{
    private static LiteralValue Number(int number) => new(number.ToString(System.Globalization.CultureInfo.InvariantCulture), QueryScalarKind.Integer);

    private static GraphScalarExpression Expression() => new(null, null, null, "__GQL_PATH_MULTIPLICITY",
    [
        new(null, null, Number(2)), new(null, null, Number(1)), new(null, null, NullValue.Instance),
        new(null, null, new ListValue([
            new ListValue([Number(0), Number(1), Number(2)]),
            new ListValue([Number(1), Number(2), Number(3)]),
            new ListValue([Number(0), Number(2), Number(1)]),
            new ListValue([Number(0), Number(2), Number(1)])]))
    ]);

    [TestMethod]
    public void Gql_lowered_path_multiplicity_evaluates_with_shared_work_and_cancellation()
    {
        var shared = new GqlExecutionBudget();
        QueryValue Run(CancellationToken token = default) => GraphScalarEvaluator.Evaluate(Expression(),
            (_, _) => UnboundValue.Instance, new GraphScalarEvaluator.IterationBudget
            {
                ChargePathWork = shared.SpendPathWork,
                Cancellation = token
            });
        Assert.AreEqual(Number(8), Run());
        shared.SpendPathWork(99_960);
        var limit = Assert.Throws<ArgumentException>(() => Run());
        StringAssert.Contains(limit.Message, "path decomposition work");
        Assert.Throws<OperationCanceledException>(() => Run(new CancellationToken(canceled: true)));
        Assert.AreEqual(Number(8), GraphScalarEvaluator.Evaluate(Expression(), (_, _) => UnboundValue.Instance));
    }

    [TestMethod]
    public void Gql_lowered_path_decompositions_bound_materialized_binding_cells()
    {
        var expression = new GraphScalarExpression(null, null, null, "__GQL_PATH_DECOMPOSITIONS",
        [
            new(null, null, Number(1)), new(null, null, Number(1)), new(null, null, Number(1)),
            new(null, null, new ListValue([
                new ListValue([Number(0), Number(1), Number(1000),
                    new ListValue(Enumerable.Repeat<QueryValue>(Number(7), 200).ToArray())])]))
        ]);
        var error = Assert.Throws<ArgumentException>(() => GraphScalarEvaluator.Evaluate(expression, (_, _) => UnboundValue.Instance));
        StringAssert.Contains(error.Message, "binding materialization budget");
        Assert.AreEqual(Number(8), GraphScalarEvaluator.Evaluate(Expression(), (_, _) => UnboundValue.Instance));
    }

    [TestMethod]
    public void Gql_internal_path_multiplicity_is_not_a_public_language_extension()
    {
        Assert.Throws<ArgumentException>(() => GraphGqlStandardSyntax.Prepare(
            "RETURN __GQL_PATH_MULTIPLICITY(2,1,NULL,[[0,2,1]]) AS answer"));
        Assert.Throws<ArgumentException>(() => GraphGqlStandardSyntax.Prepare(
            "RETURN __GQL_PATH_DECOMPOSITIONS(2,1,NULL,[[0,2,1,[]]]) AS answer"));
    }
}
