namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=((a)-[e:knows]->(b)((x)-[f:knows]->(y))?)? RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2]")]
    [DataRow("MATCH p=((a)-[e:knows]->(b)((x)-[f:knows]->(y))?)? RETURN y.name AS answer", "[null,null,null,null,null,\"C\"]")]
    [DataRow("MATCH p=(((x)-[f:knows]->(y))?(a)-[e:knows]->(b))? RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2]")]
    [DataRow("MATCH p=(((x)-[:knows]->(y))?)? RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=((a)-[:knows]->(b)((x)-[:knows]->(y))? WHERE y IS NOT NULL)? RETURN PATH_LENGTH(p) AS answer", "[0,0,0,2]")]
    [DataRow("MATCH p=((a)-[:knows]->(b)((x)-[:knows]->(y) |+| (x)-[:knows]->(y))?)? RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2,2]")]
    [DataRow("MATCH p=((a)-[:knows]->(b)((x)-[:knows]->(y) | (x)-[:knows]->(y))?)? RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2]")]
    public Task Gql_nested_questioned_paths_preserve_positive_body_and_conditional_bindings(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_nested_questioned_paths_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=((a)-[:knows]->(c)((x)-[:knows]->(y))? WHERE y IS NOT NULL)? WHERE c IS NOT NULL", "a,c,p,x,y");
}
