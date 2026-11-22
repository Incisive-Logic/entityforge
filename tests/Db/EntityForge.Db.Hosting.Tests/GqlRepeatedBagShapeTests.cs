namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(() -> () |+| () -> () -> ()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> ()){2} RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> ()){0} RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> ()){0,1} RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2]")]
    [DataRow("MATCH p=(() -> () |+| () -> () |+| () -> () -> ()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,2,2,2,2,2]")]
    [DataRow("MATCH p=ALL SHORTEST (() -> () |+| () -> () -> ()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("OPTIONAL MATCH p=(()-[:Missing]->() |+| ()-[:Missing]->() -> ()){1,2} RETURN p AS answer", "[null]")]
    [DataRow("MATCH p=(() -> () |+| () -> () <- ()){1} RETURN PATH_LENGTH(p) AS answer", "[1,1]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(() -> () |+| () -> () <- ()){1} RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    public Task Gql_different_shape_repeated_bags_preserve_path_multiplicity(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_different_shape_repeated_bags_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(c:Person {name:'B'})(() -> () |+| () -> () -> ()){1}()", "c,p");

    [TestMethod]
    public Task Gql_different_shape_repeated_bags_use_traversal_for_large_fixed_counts() =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(
            "MATCH p=(() -> () |+| () -> () -> ()){8} RETURN PATH_LENGTH(p) AS answer", "[]");
}
