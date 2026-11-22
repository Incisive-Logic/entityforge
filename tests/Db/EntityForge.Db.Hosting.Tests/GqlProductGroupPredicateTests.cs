namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH ((a:Person)-[:knows]->(b) WHERE b.name=c.name),(c:Person WHERE a.name='A')(()-[:knows]->()){1}(d) RETURN c.name AS answer", "[\"B\"]")]
    [DataRow("MATCH (c:Person WHERE a.name='A')(()-[:knows]->()){1}(d),((a:Person)-[:knows]->(b) WHERE b.name=c.name) RETURN c.name AS answer", "[\"B\"]")]
    [DataRow("MATCH ((a:Person)-[:knows]->(b) WHERE b.name=c.missing),(c:Person WHERE a.name='A')(()-[:knows]->()){1}(d) RETURN c.name AS answer", "[]")]
    [DataRow("OPTIONAL MATCH ((a:Person)-[:knows]->(b) WHERE b.name=c.missing),(c:Person WHERE a.name='A')(()-[:knows]->()){1}(d) RETURN c.name AS answer", "[null]")]
    [DataRow("MATCH ((a:Person)-[:knows]->(b) WHERE EXISTS { MATCH (c:Person WHERE c.name=b.name) }),(c:Person WHERE a.name='A')(()-[:knows]->()){1}(d) RETURN c.name AS answer", "[\"B\"]")]
    [DataRow("MATCH ((a:Person)-[:knows]->(b) WHERE VALUE { RETURN c.name AS name }=b.name),(c:Person WHERE a.name='A')(()-[:knows]->()){1}(d) RETURN c.name AS answer", "[\"B\"]")]
    public Task Gql_nonselective_products_preserve_fixed_group_predicate_dependencies(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH p=((a:Person)-[:knows]->(b) |+| (a:Person)-[:knows]->(b) WHERE b.name=c.name),(c:Person WHERE a.name='A')(()-[:knows]->()){1}(d) RETURN c.name AS answer", "[\"B\",\"B\"]")]
    [DataRow("MATCH p=((a:Person)-[:knows]->(b) | (a:Person)-[:knows]->(b) WHERE b.name=c.name),(c:Person WHERE a.name='A')(()-[:knows]->()){1}(d) RETURN c.name AS answer", "[\"B\"]")]
    [DataRow("MATCH p=((a:Person)-[:knows]->(b) |+| (a:Person)-[:knows]->(b) WHERE b.name=c.missing),(c:Person WHERE a.name='A')(()-[:knows]->()){1}(d) RETURN c.name AS answer", "[]")]
    [DataRow("OPTIONAL MATCH p=((a:Person)-[:knows]->(b) |+| (a:Person)-[:knows]->(b) WHERE b.name=c.missing),(c:Person WHERE a.name='A')(()-[:knows]->()){1}(d) RETURN c.name AS answer", "[null]")]
    [DataRow("MATCH (c:Person WHERE a.name='A')(()-[:knows]->()){1}(d),p=((a:Person)-[:knows]->(b) | (a:Person)-[:knows]->(b) WHERE VALUE { RETURN c.name AS name }=b.name) RETURN c.name AS answer", "[\"B\"]")]
    public Task Gql_nonselective_products_preserve_alternative_wrapper_predicate_dependencies(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_fixed_group_product_predicates_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH ((a:Person)-[:knows]->(b) WHERE b.name=c.name),(c:Person WHERE a.name='A')(()-[:knows]->()){1}(d)",
            "a,b,c,d");
}
