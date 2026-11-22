using EntityForge.Db.Graph;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlTypeOperandValidationTests
{
    [TestMethod]
    [DataRow("ISTYPED")]
    [DataRow("ISNOTTYPED")]
    [DataRow("CAST")]
    [DataRow("ISTYPED", true)]
    [DataRow("ISNOTTYPED", true)]
    [DataRow("CAST", true)]
    public void Programmatic_type_operations_bound_recursive_value_inspection(string function, bool record = false)
    {
        var item = new LiteralValue("1", QueryScalarKind.Integer);
        var inner = new ListValue(Enumerable.Repeat<QueryValue>(item, 1000));
        QueryValue Wrap(QueryValue value) => record ? new MapValue([new KeyValuePair<string, QueryValue>("x", value)]) : value;
        var oversized = new ListValue(Enumerable.Repeat(Wrap(inner), 101));
        var type = new LiteralValue(record ? "LIST<RECORD{eA==:LIST<INTEGER>}>" : "LIST<LIST<INTEGER>>");
        var required = new LiteralValue("false", QueryScalarKind.Boolean);
        QueryValue Run(QueryValue value) => function == "CAST" ? Evaluate(function, value, type) :
            Evaluate(function, value, type, required);
        var error = Assert.ThrowsExactly<ArgumentException>(() => Run(oversized));
        StringAssert.Contains(error.Message, "100,000");
        var small = new ListValue([Wrap(new ListValue([item]))]);
        var result = Run(small);
        if (function == "CAST")
        {
            Assert.AreSame(small, result);
        }
        else
        {
            Assert.AreEqual(new LiteralValue(function == "ISTYPED" ? "true" : "false", QueryScalarKind.Boolean), result);
        }
    }

    [TestMethod]
    public void Programmatic_type_predicates_share_one_expression_work_budget()
    {
        var value = new ListValue(Enumerable.Repeat<QueryValue>(new LiteralValue("1", QueryScalarKind.Integer), 60_000));
        var predicate = new GraphScalarExpression(null, null, null, "ISTYPED",
            [Constant(value), Constant(new LiteralValue("LIST<INTEGER>")), Constant(new LiteralValue("false", QueryScalarKind.Boolean))]);
        var conjunction = new GraphScalarExpression(null, null, null, "AND", [predicate, predicate]);
        var error = Assert.ThrowsExactly<ArgumentException>(() => GraphScalarEvaluator.Evaluate(conjunction,
            (_, _) => throw new AssertFailedException("No property reads expected.")));
        StringAssert.Contains(error.Message, "100,000");
    }

    private static GraphScalarExpression Constant(QueryValue value) => new(null, null, value);

    [TestMethod]
    public void Programmatic_cast_bounds_length_validation_after_an_early_type_mismatch()
    {
        var inner = new ListValue(Enumerable.Repeat<QueryValue>(new LiteralValue("1", QueryScalarKind.Integer), 1000));
        var oversized = new ListValue(Enumerable.Repeat<QueryValue>(inner, 101));
        var target = new LiteralValue("LIST<LIST<DECIMAL>>");
        var error = Assert.ThrowsExactly<ArgumentException>(() => Evaluate("CAST", oversized, target));
        StringAssert.Contains(error.Message, "100,000");
        var small = new ListValue([new ListValue([new LiteralValue("1", QueryScalarKind.Integer)])]);
        var converted = (ListValue)Evaluate("CAST", small, target);
        var convertedInner = (ListValue)converted.Items.Single();
        Assert.AreEqual(new LiteralValue("1", QueryScalarKind.Decimal), convertedInner.Items.Single());
    }

    private static QueryValue Evaluate(string function, params QueryValue[] values) =>
        GraphScalarEvaluator.Evaluate(new(null, null, null, function, values.Select(Constant).ToArray()),
            (_, _) => throw new AssertFailedException("A constant expression must not read properties."));

    [TestMethod]
    [DataRow("ISTYPED")]
    [DataRow("ISNOTTYPED")]
    public void Programmatic_type_predicates_reject_invalid_argument_shapes(string function)
    {
        var value = new LiteralValue("1", QueryScalarKind.Integer);
        var type = new LiteralValue("INTEGER");
        var required = new LiteralValue("false", QueryScalarKind.Boolean);
        QueryValue[][] invalid = [[], [value], [value, type], [value, type, required, required],
            [value, value, required], [value, type, new LiteralValue("false")],
            [value, type, UnboundValue.Instance], [value, type, new LiteralValue("invalid", QueryScalarKind.Boolean)]];
        foreach (var arguments in invalid)
        {
            Assert.ThrowsExactly<ArgumentException>(() => Evaluate(function, arguments));
        }
        Assert.AreEqual(new LiteralValue(function == "ISTYPED" ? "true" : "false", QueryScalarKind.Boolean),
            Evaluate(function, value, type, required));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("LIST<INTEGER")]
    [DataRow("(INTEGER>")]
    [DataRow("INTEGER]")]
    public void Programmatic_type_operations_reject_unbalanced_type_descriptions(string type)
    {
        RejectType(type);
    }

    [TestMethod]
    [DataRow("UNKNOWN")]
    [DataRow("LIST<>")]
    [DataRow("(INTEGER|)")]
    [DataRow("(|INTEGER)")]
    [DataRow("INTEGER!!")]
    [DataRow("LIST<INTEGER>[x]")]
    [DataRow("LIST<INTEGER>[01]")]
    [DataRow("INTEGER[3]")]
    [DataRow("LIST<INTEGER>trailing")]
    [DataRow("(INTEGER)trailing")]
    [DataRow("RECORD{YWdl:UNKNOWN}")]
    [DataRow("RECORD{YWdl:INTEGER;YWdl:STRING}")]
    [DataRow("RECORD{YWdl:INTEGER;}")]
    [DataRow("RECORD{YWdl}")]
    [DataRow("RECORD{%%:INTEGER}")]
    [DataRow("RECORD{/w==:INTEGER}")]
    [DataRow("RECORD{YW dl:INTEGER}")]
    [DataRow("RECORD{YWdl:LIST<>}")]
    [DataRow("RECORD{YWdl:INTEGER}trailing")]
    public void Programmatic_type_operations_reject_invalid_canonical_descriptions(string type) => RejectType(type);

    [TestMethod]
    [DataRow("LIST<INTEGER>", true)]
    [DataRow("LIST<INTEGER!>[1]!", true)]
    [DataRow("LIST<(INTEGER!|STRING)>[2]", true)]
    [DataRow("(LIST<INTEGER>[1]|INTEGER)", true)]
    [DataRow("LIST<INTEGER>[999999999999999999999999999999]", true)]
    [DataRow("LIST<INTEGER>[0]", false)]
    [DataRow("LIST<STRING>", false)]
    public void Programmatic_canonical_list_types_preserve_bounds_and_membership(string type, bool expected)
    {
        var value = new ListValue([new LiteralValue("1", QueryScalarKind.Integer)]);
        var target = new LiteralValue(type);
        var required = new LiteralValue("false", QueryScalarKind.Boolean);
        Assert.AreEqual(new LiteralValue(expected ? "true" : "false", QueryScalarKind.Boolean),
            Evaluate("ISTYPED", value, target, required));
        Assert.AreEqual(new LiteralValue(expected ? "false" : "true", QueryScalarKind.Boolean),
            Evaluate("ISNOTTYPED", value, target, required));
        if (expected)
        {
            Assert.AreEqual(value, Evaluate("CAST", value, target));
        }
    }

    [TestMethod]
    public void Programmatic_type_operations_bound_recursive_type_descriptions()
    {
        // Eighty levels reproduce the missing guard safely without risking a
        // stack-overflow process crash if this regression is reintroduced.
        RejectType(new string('(', 80) + "INTEGER" + new string(')', 80));
        RejectType(String.Concat(Enumerable.Repeat("RECORD{YQ==:", 80)) + "INTEGER" + new string('}', 80));
        var value = new LiteralValue("1", QueryScalarKind.Integer);
        var bounded = new LiteralValue(new string('(', 64) + "INTEGER" + new string(')', 64));
        Assert.AreEqual(value, Evaluate("CAST", value, bounded));
    }

    private static void RejectType(string type)
    {
        var target = new LiteralValue(type);
        foreach (QueryValue value in new QueryValue[] { new LiteralValue("1", QueryScalarKind.Integer), UnboundValue.Instance, new ListValue([]) })
        {
            Assert.ThrowsExactly<ArgumentException>(() => Evaluate("CAST", value, target));
            foreach (string function in new[] { "ISTYPED", "ISNOTTYPED" })
            {
                Assert.ThrowsExactly<ArgumentException>(() =>
                    Evaluate(function, value, target, new LiteralValue("false", QueryScalarKind.Boolean)));
            }
        }
    }
}
