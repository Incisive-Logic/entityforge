using EntityForge.Db.Gql;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (a:Person {name:'A'}) OPTIONAL MATCH (a)-[:knows]->(b)-[:knows]->(c) RETURN [a.name,b.name,c.name] AS answer", "[[\"A\",\"B\",\"C\"]]")]
    [DataRow("MATCH (a:Person {name:'B'}) OPTIONAL MATCH (a)-[:knows]->(b)-[:knows]->(c) RETURN [a.name,b.name,c.name] AS answer", "[[\"B\",null,null]]")]
    [DataRow("MATCH (a:Person {name:'A'}) OPTIONAL MATCH (a)-[:knows]->(b)-[:knows]->(c) WHERE c.name='missing' RETURN [a.name,b.name,c.name] AS answer", "[[\"A\",null,null]]")]
    [DataRow("MATCH (a:Person {name:'A'}) OPTIONAL MATCH (a {name:'missing'})-[:knows]->(b) RETURN [a.name,b.name] AS answer", "[[\"A\",null]]")]
    [DataRow("MATCH (a:Person {name:'A'}) OPTIONAL MATCH (a)-[:knows]->(b), (a)-[:Missing]->(c) RETURN [a.name,b.name,c.name] AS answer", "[[\"A\",null,null]]")]
    public Task Gql_standard_optional_route_preserves_whole_pattern_failure_and_bound_constraints(string query, string expected)
    {
        _ = GraphGqlStandardSyntax.Prepare(query);
        return Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
    }
}
