namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ALL SHORTEST ((segment=()-[:knows]->()) |+| (segment=()-[:knows]->())),(c:Person {name:'A'}) RETURN PATH_LENGTH(segment) AS answer", "[1,1,1,1]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ALL SHORTEST ((segment=()-[:knows]->()) | (segment=()-[:knows]->())),(c:Person {name:'A'}) RETURN PATH_LENGTH(segment) AS answer", "[1,1]")]
    [DataRow("MATCH REPEATABLE ELEMENTS (c:Person {name:'A'}),p=ANY SHORTEST ((segment=()-[:knows]->()) |+| (segment=()-[:knows]->())) RETURN PATH_LENGTH(segment) AS answer", "[1,1]")]
    [DataRow("OPTIONAL MATCH REPEATABLE ELEMENTS p=ALL SHORTEST ((segment=()-[:Missing]->()) |+| (segment=()-[:Missing]->())),(c:Person {name:'A'}) RETURN segment AS answer", "[null]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ALL SHORTEST ((segment=()-[:knows]->()) |+| (segment=()-[:knows]->())),q=(segment=()-[:knows]->()) RETURN p AS answer", "ERROR")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ALL SHORTEST ((segment=(segment=()-[:knows]->())) |+| (segment=()-[:knows]->())),(c:Person {name:'A'}) RETURN p AS answer", "ERROR")]
    public Task Gql_selected_products_reconcile_subpath_names_within_alternative_components(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_selected_alternative_subpaths_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH REPEATABLE ELEMENTS p=ANY SHORTEST ((segment=(a:Person {name:'A'})-[:knows]->(b)) |+| " +
            "(segment=(a:Person {name:'A'})-[:knows]->(b))),(c:Person {name:'B'})", "a,b,c,p,segment");
}
