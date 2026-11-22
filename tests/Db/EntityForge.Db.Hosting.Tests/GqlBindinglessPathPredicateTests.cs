using EntityForge.Db.Gql;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("OPTIONAL MATCH ((()-[:knows]->()){1,2} WHERE FALSE) RETURN 1 AS answer", "[1]")]
    [DataRow("OPTIONAL MATCH ((()-[:knows]->()){1,2} WHERE TRUE) RETURN 1 AS answer", "[1,1,1]")]
    [DataRow("LET wanted='B' MATCH (c:Person {name:wanted}) OPTIONAL MATCH ((c)(()-[:Missing]->()){1,2}() WHERE TRUE) RETURN c.name AS answer", "[\"B\"]")]
    [DataRow("MATCH (c:Person {name:'A'}) OPTIONAL MATCH ((c)(()-[:knows]->()){1,2}() WHERE TRUE) RETURN c.name AS answer", "[\"A\",\"A\"]")]
    [DataRow("LET __pathPredicateResult=7 OPTIONAL MATCH ((()-[:knows]->()){1,2} WHERE FALSE) RETURN __pathPredicateResult AS answer", "[7]")]
    [DataRow("MATCH (c:Person {name:'A'}) OPTIONAL MATCH (c)(()-[:knows]->()){1,2}() RETURN c.name AS answer", "[\"A\",\"A\"]")]
    [DataRow("MATCH (c:Person {name:'B'}) OPTIONAL MATCH (c)(()-[:Missing]->()){1,2}() RETURN c.name AS answer", "[\"B\"]")]
    public Task Gql_bindingless_path_predicates_preserve_optional_rows_and_multiplicity(string query, string expected)
    {
        _ = GraphGqlStandardSyntax.Prepare(query);
        return Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
    }

    [TestMethod]
    public Task Gql_bindingless_path_predicates_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH (c:Person {name:'B'}) OPTIONAL MATCH ((c)(()-[:Missing]->()){1,2}() WHERE TRUE)", "c");
}
