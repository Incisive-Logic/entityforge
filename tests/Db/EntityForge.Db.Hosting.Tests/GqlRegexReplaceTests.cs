using EntityForge.Db.Graph;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public void Gql_regex_replacement_enforces_output_and_execution_bounds()
    {
        QueryValue Replace(string input, string pattern, string replacement) => GraphScalarEvaluator.Evaluate(
            new GraphScalarExpression(null, null, null, "STRING.REGEXREPLACE",
                new[] { input, pattern, replacement }.Select(value => new GraphScalarExpression(null, null, new LiteralValue(value))).ToArray()),
            (_, _) => UnboundValue.Instance);
        Assert.ThrowsExactly<ArgumentException>(() => Replace(new string('a', 1000), "a", new string('x', 2000)));
        Assert.ThrowsExactly<ArgumentException>(() => Replace(new string('a', 64) + "!", "(a+)+$", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => Replace("a", new string('a', 4097), "x"));
    }
}
