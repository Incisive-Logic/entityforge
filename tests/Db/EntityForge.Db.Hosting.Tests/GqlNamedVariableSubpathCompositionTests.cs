namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (a:Person {name:'A'}) CALL (a) { MATCH (s=(a)-[edges:knows]->{0,2}(b)) RETURN s } RETURN PATH_LENGTH(s) AS answer", "[0,1,2]")]
    [DataRow("MATCH (s=(a:Person {name:'A'})-[edges:knows]->{0,2}(b)) RETURN s NEXT RETURN PATH_LENGTH(s) AS answer", "[0,1,2]")]
    [DataRow("MATCH (s=(a:Person {name:'A'})-[edges:knows]->{0,2}(b)) RETURN SUM(PATH_LENGTH(s)) AS answer", "[3]")]
    [DataRow("MATCH (s=(a:Person {name:'A'})-[edges:knows]->{0,2}(b)) RETURN VALUE { CALL (s) { RETURN PATH_LENGTH(s) AS length } RETURN length } AS answer", "[0,1,2]")]
    [DataRow("MATCH (s=(a:Person {name:'A'})(alias)-[edges:knows]->{0,2}(b)) RETURN PATH_LENGTH(s) AS answer", "[0,1,2]")]
    [DataRow("MATCH (s=(a:Person {name:'A'})-[edges:knows]->{0,2}(b)(alias)) RETURN PATH_LENGTH(s) AS answer", "[0,1,2]")]
    [DataRow("MATCH (s=(a:Person {name:'A'})-[edges:knows]->{0,2}(b) WHERE VALUE { CALL (s) { RETURN PATH_LENGTH(s) AS length } RETURN length=2 AS selected }) RETURN PATH_LENGTH(s) AS answer", "[2]")]
    [DataRow("MATCH (a:Person {name:'C'}) OPTIONAL MATCH (s=(a)-[edges:knows]->{1,2}(b)) CALL (s) { RETURN s IS NULL AS absent } RETURN absent AS answer", "[true]")]
    [DataRow("MATCH (s=(a:Person {name:'A'})-[edges:knows]->{0,2}(b)) RETURN s NEXT YIELD s AS renamed CALL (renamed) { RETURN PATH_LENGTH(renamed) AS length } RETURN length AS answer", "[0,1,2]")]
    public Task Gql_named_variable_subpaths_compose_with_scope_and_adjacent_nodes(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
}
