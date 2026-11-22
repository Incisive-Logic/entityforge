namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=((x)-[:knows]->(y) WHERE EXISTS { MATCH (y)(()-[:knows]->()){1}(z) })? RETURN x.name AS answer", "[\"A\",null,null,null]")]
    [DataRow("MATCH p=((x)-[:knows]->(y) WHERE NOT EXISTS { MATCH (y)(()-[:knows]->()){1}(z) })? RETURN x.name AS answer", "[\"B\",null,null,null]")]
    [DataRow("MATCH p=((x)-[:knows]->(y) WHERE VALUE { MATCH (y)(()-[:knows]->()){1}(z) RETURN z.name AS valueName }='C')? RETURN x.name AS answer", "[\"A\",null,null,null]")]
    [DataRow("MATCH p=((x)-[:knows]->(y) WHERE EXISTS { MATCH (y)(()-[:Missing]->()){1}(z) })? RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=((x WHERE EXISTS { MATCH (x)(()-[:knows]->()){2}(z) })-[:knows]->(y))? RETURN x.name AS answer", "[\"A\",null,null,null]")]
    [DataRow("MATCH p=((x)-[:knows]->(y) WHERE EXISTS { MATCH (y)(()-[:knows]->()){1}(z) })? RETURN z AS answer", "ERROR")]
    public Task Gql_questioned_paths_keep_nested_query_repetitions_in_their_own_scope(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_questioned_nested_queries_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=((c)-[:knows]->(b) WHERE NOT EXISTS { MATCH (b)(()-[:knows]->()){1}(z) })? WHERE c IS NOT NULL", "b,c,p");
}
