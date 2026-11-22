namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH REPEATABLE ELEMENTS p=((SIMPLE () -> () <- ()) |+| (SIMPLE () -> () -> ())){1} RETURN PATH_LENGTH(p) AS answer", "[2,2,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=((q=SIMPLE (r=TRAIL () -> () <- ())) |+| (q=SIMPLE (r=TRAIL () -> () -> ()))){1} RETURN CARDINALITY(q)=CARDINALITY(r) AS answer", "[true]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=((q=ACYCLIC (a)-[e]->(b) WHERE a.name='A') |+| (q=ACYCLIC (a)-[e]->() -> (b) WHERE a.name='A')){1} RETURN PATH_LENGTH(p) AS answer", "[1,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=((q=SIMPLE () -> () <- ()) |+| (q=SIMPLE () -> () -> ())){1} RETURN PATH_LENGTH(p) AS answer", "[2,2,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=((q=ACYCLIC () -> () <- ()) |+| (q=ACYCLIC () -> () -> ())){1} RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=((q=TRAIL () -> () <- ()) |+| (q=TRAIL () -> () -> ())){1} RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=((q=WALK () -> () <- ()) |+| (q=WALK () -> () -> ())){1} RETURN PATH_LENGTH(p) AS answer", "[2,2,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=((q=ACYCLIC () -> ()) <- () |+| (q=ACYCLIC () -> ()) -> ()){1} RETURN PATH_LENGTH(p) AS answer", "[2,2,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=TRAIL ((q=WALK () -> () <- ()) |+| (q=WALK () -> () -> ())){1} RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=((q=ACYCLIC () -> ()) |+| (q=ACYCLIC () -> () -> ()))+ RETURN CARDINALITY(q) AS answer", "[1,1,1,2]")]
    [DataRow("MATCH p=((q=ACYCLIC () -> ()) |+| (q=ACYCLIC () -> () -> ())){0} RETURN CARDINALITY(q) AS answer", "[0,0,0]")]
    [DataRow("OPTIONAL MATCH ((q=ACYCLIC (:Missing) -> ()) |+| (q=ACYCLIC (:Missing) -> () -> ()))+ RETURN q AS answer", "[null]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=((q=TRAIL () -[]- () -[]- ()) |+| (q=TRAIL () -> () -> ())){1} RETURN PATH_LENGTH(p) AS answer", "[2,2,2]")]
    public Task Gql_repeated_bag_nested_modes_preserve_local_scope_and_multiplicity(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_repeated_bag_nested_modes_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(c:Person {name:'B'})((q=ACYCLIC (a) -> (b)) |+| (q=ACYCLIC (a) -> () -> (b)))+ (t)", "a,b,c,p,q,t");
}
