namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(a:Person {name:'A'})(()-[:knows]->() (()-[:knows]->())?)?(b) RETURN PATH_LENGTH(p) AS answer ORDER BY answer", "[0,1,2]")]
    [DataRow("MATCH p=(a:Person {name:'A'})((x)-[:knows]->(y) ((z)-[:knows]->(w))?)?(b) RETURN CASE WHEN x IS NULL THEN 'empty' WHEN z IS NULL THEN 'one' ELSE 'two' END AS answer ORDER BY answer", "[\"empty\",\"one\",\"two\"]")]
    [DataRow("MATCH p=(a:Person {name:'A'})((x)-[:knows]->(y) ((z WHERE z.name='Missing')-[:knows]->(w))?)?(b) RETURN PATH_LENGTH(p) AS answer ORDER BY answer", "[0,1]")]
    [DataRow("MATCH p=(a:Person {name:'A'})((x WHERE x.name='Missing')-[:knows]->(y) ((z)-[:knows]->(w))?)?(b) RETURN PATH_LENGTH(p) AS answer", "[0]")]
    [DataRow("OPTIONAL MATCH p=(a:Person {name:'Missing'})((x)-[:knows]->(y) ((z)-[:knows]->(w))?)?(b) RETURN p AS answer", "[null]")]
    public Task Gql_nested_questioned_paths_preserve_singleton_exposure_and_branch_predicates(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
}
