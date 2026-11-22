namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH REPEATABLE ELEMENTS (a:Person WHERE EXISTS { MATCH (c:Person WHERE c.name='A') })(()-[:knows]->()){1}(b),(c:Person WHERE EXISTS { MATCH (a:Person WHERE a.name='A') }) RETURN c.name AS answer", "[\"A\"]")]
    [DataRow("MATCH REPEATABLE ELEMENTS (a:Person WHERE EXISTS { MATCH (c:Person {name:'A'}) })(()-[:knows]->()){1}(b),(c:Person WHERE EXISTS { MATCH (a:Person {name:'A'}) }) RETURN c.name AS answer", "[\"A\"]")]
    [DataRow("MATCH REPEATABLE ELEMENTS (c:Person WHERE EXISTS { MATCH (a:Person {name:'A'}) }),(a:Person WHERE EXISTS { MATCH (c:Person {name:'A'}) })(()-[:knows]->()){1}(b) RETURN c.name AS answer", "[\"A\"]")]
    [DataRow("MATCH REPEATABLE ELEMENTS (a:Person WHERE VALUE { RETURN c.name AS name }='A')(()-[:knows]->()){1}(b),(c:Person WHERE VALUE { RETURN a.name AS name }='A') RETURN c.name AS answer", "[\"A\"]")]
    [DataRow("MATCH REPEATABLE ELEMENTS (a:Person WHERE NOT EXISTS { MATCH (c:Person {name:'A'}) })(()-[:knows]->()){1}(b),(c:Person WHERE EXISTS { MATCH (a:Person {name:'A'}) }) RETURN c.name AS answer ORDER BY answer", "[\"B\",\"C\"]")]
    [DataRow("OPTIONAL MATCH (a:Person WHERE EXISTS { MATCH (c:Person {name:'Missing'}) })(()-[:knows]->()){1}(b),(c:Person WHERE EXISTS { MATCH (a:Person {name:'A'}) }) RETURN a.name AS answer", "[null]")]
    public Task Gql_nonselective_products_preserve_cyclic_nested_predicate_correlation(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
}
