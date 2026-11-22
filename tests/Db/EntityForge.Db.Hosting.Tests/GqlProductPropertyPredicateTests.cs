namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (a:Person)(()-[:knows]->()){1}({name:c.name}),(c:Person {name:a.name}) RETURN c.name AS answer", "[]")]
    [DataRow("MATCH (a:Person)(()-[:knows]->()){1}(:Person {name:c.name}),(c:Person WHERE a.name='A') RETURN c.name AS answer", "[\"B\"]")]
    [DataRow("MATCH (c:Person WHERE a.name='A'),(a:Person)(()-[:knows]->()){1}({name:c.name}) RETURN c.name AS answer", "[\"B\"]")]
    [DataRow("OPTIONAL MATCH p=(a:Person)(()-[:knows]->()){1}({name:c.missing}),(c:Person WHERE a.name='A') RETURN p AS answer", "[null]")]
    [DataRow("MATCH (a:Person {name:c.name})(()-[:knows]->()){1}(b),(c:Person {name:a.name}) RETURN a.name AS answer ORDER BY answer", "[\"A\",\"B\"]")]
    [DataRow("MATCH (c:Person {name:a.name}),(a:Person {name:c.name})(()-[:knows]->()){1}(b) RETURN a.name AS answer ORDER BY answer", "[\"A\",\"B\"]")]
    [DataRow("MATCH (a:Person {name:VALUE { RETURN c.name AS name }})(()-[:knows]->()){1}(b),(c:Person WHERE a.name=c.name) RETURN a.name AS answer ORDER BY answer", "[\"A\",\"B\"]")]
    [DataRow("MATCH (a:Person {name:c.missing})(()-[:knows]->()){1}(b),(c:Person {name:a.name}) RETURN a.name AS answer", "[]")]
    [DataRow("OPTIONAL MATCH (a:Person {name:c.missing})(()-[:knows]->()){1}(b),(c:Person {name:a.name}) RETURN a.name AS answer", "[null]")]
    [DataRow("MATCH (a:Person {name:c.name,name:'A'})(()-[:knows]->()){1}(b),(c:Person {name:a.name}) RETURN a.name AS answer", "ERROR")]
    [DataRow("MATCH (a:Person {`name`:c.name,name:'A'})(()-[:knows]->()){1}(b),(c:Person {name:a.name}) RETURN a.name AS answer", "ERROR")]
    public Task Gql_nonselective_products_preserve_cyclic_property_predicates(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
}
