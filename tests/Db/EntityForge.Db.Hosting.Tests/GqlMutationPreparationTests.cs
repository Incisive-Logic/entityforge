using EntityForge.Db.Gql;
using System.Text.Json;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlMutationPreparationTests
{
    [TestMethod]
    [DataRow("MATCH (n) SET n.key=2", "n")]
    [DataRow("MATCH (n) SET n.key=n.key+1", "n")]
    [DataRow("MATCH (n)-[e]->(m) SET m.key=n.key", "m,n")]
    [DataRow("MATCH (n)-[e]->(m) SET e.key=1", "e")]
    public void Mutation_selection_retains_exact_required_binding_order(string source, string expected)
    {
        var compiled = GraphGqlMutationCompiler.Compile(GraphGqlStandardSyntax.Prepare(source));
        Assert.IsNotNull(compiled.Match);
        Assert.AreEqual(expected, String.Join(",", compiled.Match.Query.ReturnedBindings));
        Assert.AreEqual(10_000, compiled.Match.Query.MaximumMatches);
    }

    [TestMethod]
    public void Mutation_preparation_rebinds_values_and_rejects_unknown_targets()
    {
        const string source = "MATCH (n) SET n.key=$value";
        foreach (int value in new[] { 1, 2 })
        {
            var parameters = new Dictionary<string, JsonElement> { ["value"] = JsonSerializer.SerializeToElement(value) };
            var compiled = GraphGqlMutationCompiler.Compile(GraphGqlStandardSyntax.PrepareParameterized(source, parameters), parameters);
            Assert.AreEqual(value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ((LiteralValue)compiled.MatchedMutations.Single().Expression!.Literal!).LexicalValue);
        }
        Assert.ThrowsExactly<ArgumentException>(() => GraphGqlMutationCompiler.Compile("MATCH (n) SET missing.key=1"));
    }
}
