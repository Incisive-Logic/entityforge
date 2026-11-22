namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=((x)-[e:knows]->(y))? RETURN x.name AS answer", "[\"A\",\"B\",null,null,null]")]
    [DataRow("MATCH p=((x)-[e:knows]->(y))? RETURN PATH_LENGTH(p) AS answer", "[1,1,0,0,0]")]
    [DataRow("MATCH p=((x)-[e:knows]->(y))? WHERE x IS NOT NULL RETURN y.name AS answer", "[\"B\",\"C\"]")]
    [DataRow("MATCH p=((x)-[e:knows]->(y))? WHERE e IS NULL RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=(a:Person {name:'A'})((x)-[e:knows]->(y))?(b) RETURN b.name AS answer", "[\"A\",\"B\"]")]
    [DataRow("MATCH (a:Person {name:'A'})((x)-[e:knows]->(y))?(b) RETURN b.name AS answer", "[\"A\",\"B\"]")]
    [DataRow("MATCH ((x)-[:knows]->(y))? RETURN x.name AS answer", "[\"A\",\"B\",null,null,null]")]
    [DataRow("MATCH p=((x)-[:knows]->(y) WHERE x.name='A')? RETURN x.name AS answer", "[\"A\",null,null,null]")]
    [DataRow("OPTIONAL MATCH p=((x)-[:Missing]->(y))? WHERE x IS NOT NULL RETURN p AS answer", "[null]")]
    [DataRow("MATCH p=ALL SHORTEST ((x)-[:knows]->(y))? RETURN PATH_LENGTH(p) AS answer", "[1,1,0,0,0]")]
    [DataRow("MATCH p=(a:Person)((a)-[:knows]->(b))? RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=((x))? RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=(a:Person {name:'A'})-[e:knows]->?(b) RETURN b.name AS answer", "[\"A\",\"B\"]")]
    [DataRow("MATCH (a:Person {name:'A'})-[e:knows]->?(b) RETURN e IS NULL AS answer", "[true,false]")]
    [DataRow("MATCH p=((x)-[:knows]->(y) |+| (x)-[:knows]->(y))? RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,0,0,0]")]
    [DataRow("MATCH p=((x)-[:knows]->(y) | (x)-[:knows]->(y))? RETURN PATH_LENGTH(p) AS answer", "[1,1,0,0,0]")]
    [DataRow("MATCH p=((x)-[:knows]->(y))? RETURN VALUE { RETURN y.name AS nameValue } AS answer", "[\"B\",\"C\",null,null,null]")]
    [DataRow("LET wanted='A' MATCH p=((x)-[:knows]->(y) WHERE x.name=wanted)? RETURN x.name AS answer", "[\"A\",null,null,null]")]
    public Task Gql_questioned_paths_preserve_conditional_singletons_and_empty_paths(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH p=((c:Person {name:'B'})-[:knows]->(b))? WHERE c IS NOT NULL", "b,c,p")]
    [DataRow("MATCH ((c:Person {name:'B'})-[:knows]->(b))? WHERE c IS NOT NULL", "b,c")]
    public Task Gql_questioned_paths_preserve_update_scope_rollback_and_replay(string pattern, string columns) =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(pattern, columns);
}
