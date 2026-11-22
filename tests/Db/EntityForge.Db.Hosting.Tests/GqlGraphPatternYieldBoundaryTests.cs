using EntityForge.Db.Gql;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (a)-[:knows]->(b) YIELD b RETURN b")]
    [DataRow("OPTIONAL MATCH p=(a)-[:knows]->(b) YIELD p RETURN p")]
    [DataRow("MATCH p=()-[:knows]->() | ()-[:knows]->() YIELD p RETURN p")]
    [DataRow("INSERT (:Person {name:'Uncommitted'}) RETURN 1 AS marker NEXT MATCH (a) YIELD a RETURN a")]
    public void Gql_graph_pattern_yield_is_explicitly_rejected_before_lowering(string query)
    {
        var validation = Assert.Throws<ArgumentException>(() => GraphGqlStandardSyntax.Validate(query));
        StringAssert.Contains(validation.Message, "Graph-pattern YIELD (GQ19) is not supported");
        var preparation = Assert.Throws<ArgumentException>(() => GraphGqlStandardSyntax.Prepare(query));
        StringAssert.Contains(preparation.Message, "Graph-pattern YIELD (GQ19) is not supported");
    }

    [TestMethod]
    [DataRow("MATCH (a)-[:knows]->(b) RETURN b NEXT RETURN b.name AS answer", "[\"B\",\"C\"]")]
    [DataRow("MATCH (a)-[:knows]->(b) RETURN b NEXT YIELD b AS endpoint RETURN endpoint.name AS answer", "[\"B\",\"C\"]")]
    [DataRow("MATCH (a)-[:knows]->(b) RETURN b NEXT RETURN a AS answer", "ERROR")]
    public Task Gql_next_projection_retains_only_selected_bindings(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
}
