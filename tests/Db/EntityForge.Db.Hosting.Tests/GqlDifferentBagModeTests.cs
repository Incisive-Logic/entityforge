namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(SIMPLE () -> () |+| () -> () <- ()){1} RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(TRAIL () -> () |+| () -> () <- ()){1} RETURN PATH_LENGTH(p) AS answer", "[1,1]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(ACYCLIC () -> () |+| () -> () <- ()){1} RETURN PATH_LENGTH(p) AS answer", "[1,1]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(WALK () -> () |+| () -> () <- ()){1} RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=(WALK () -> () |+| () -> () <- ()){1} RETURN PATH_LENGTH(p) AS answer", "[1,1]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=SIMPLE (ACYCLIC () -> () |+| () -> () <- ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(segment=SIMPLE (a) -> (b) |+| (a) -> () <- (b)){1} RETURN CARDINALITY(segment) AS answer", "[1,1,1,1]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(ACYCLIC () -> () |+| () -> () <- ()){0} RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("OPTIONAL MATCH p=(TRAIL (:Missing) -> () |+| (:Missing) -> () <- ()){1} RETURN p AS answer", "[null]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(TRAIL () -> () <- () |+| () -> () <- ()){1} RETURN PATH_LENGTH(p) AS answer", "[]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(SIMPLE () -> () <- () |+| () -> () <- ()){1} RETURN PATH_LENGTH(p) AS answer", "[2,2,2,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(ACYCLIC () - () |+| () -> () -> ()){2} RETURN PATH_LENGTH(p) AS answer", "[2,2,2,2,2,2,3,3]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=TRAIL (ACYCLIC () - () |+| () -> () -> ()){2} RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    public Task Gql_repeated_bag_iteration_modes_preserve_local_and_whole_path_constraints(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_repeated_bag_iteration_modes_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(c:Person {name:'B'})(segment=ACYCLIC (a) -> (b) |+| (a) -> () -> (b))+ (t)", "a,b,c,p,segment,t");
}
