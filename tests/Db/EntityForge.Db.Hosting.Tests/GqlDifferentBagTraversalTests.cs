namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> ())* RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2,2]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> ()){2,} RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> ()){3,} RETURN PATH_LENGTH(p) AS answer", "[]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> ()){1,24} RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> ()){2,24} RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> ()){1} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> ()){2} RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> ()){0} RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=(() -> () |+| () -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,2,2,2,2,2]")]
    [DataRow("MATCH p=ALL SHORTEST (() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("OPTIONAL MATCH p=(()-[:Missing]->() |+| ()-[:Missing]->() -> ())+ RETURN p AS answer", "[null]")]
    [DataRow("MATCH p=(() <- () |+| () <- () <- ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=(()-[:Missing]->() |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(() -> () |+| () -> () <- ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=SIMPLE (() -> () |+| () -> () <- ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2,2]")]
    public Task Gql_different_shape_bag_traversal_preserves_decomposition_counts(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_different_shape_bag_traversal_preserves_imports_updates_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH (c:Person {name:'B'}) MATCH p=((:Person {name:'B'}) -> () |+| (:Person {name:'B'}) -> () -> ())+", "c,p");
}
