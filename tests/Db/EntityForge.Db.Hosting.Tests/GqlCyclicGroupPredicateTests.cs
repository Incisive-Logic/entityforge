namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(a:Person {name:'A'})((x)-[:knows]->(y) WHERE x.name=c.name){1,2}(b),(c:Person WHERE a.name='A') RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH p=(a:Person {name:'A'})((x)-[:knows]->(y) WHERE x.name=c.name){0,2}(b),(c:Person WHERE a.name='A') RETURN PATH_LENGTH(p) AS answer ORDER BY answer", "[0,0,0,1]")]
    [DataRow("MATCH (c:Person WHERE a.name='A'),p=(a:Person {name:'A'})((x)-[:knows]->(y) WHERE x.name=c.name){1,2}(b) RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH p=(a:Person {name:'A'})(segments=(x)-[:knows]->(y) WHERE PATH_LENGTH(segments)=1 AND x.name=c.name){1,2}(b),(c:Person WHERE a.name='A') RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH p=(a:Person {name:'A'})((x)-[:knows]->(y) WHERE EXISTS { MATCH (x WHERE x.name=c.name) }){1,2}(b),(c:Person WHERE a.name='A') RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("OPTIONAL MATCH p=(a:Person {name:'A'})((x)-[:knows]->(y) WHERE x.name=c.missing){1,2}(b),(c:Person WHERE a.name='A') RETURN p AS answer", "[null]")]
    public Task Gql_cyclic_repeated_group_predicates_preserve_iteration_scope(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
}
