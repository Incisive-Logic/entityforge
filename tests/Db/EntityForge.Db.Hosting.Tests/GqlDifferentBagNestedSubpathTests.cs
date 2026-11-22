namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=((q=() -> ()) |+| (q=() -> ()) -> ())+ RETURN VALUE { FOR x IN q RETURN COLLECT_LIST(PATH_LENGTH(x)) AS lengths } AS answer", "[[1],[1],[1],[1,1]]")]
    [DataRow("MATCH p=((q=() -> ()) |+| () -> (q=() -> ()))+ RETURN VALUE { FOR x IN q FOR n IN ELEMENTS(x) FILTER n IS TYPED NODE RETURN COLLECT_LIST(n.name) AS names } AS answer", "[[\"A\",\"B\"],[\"B\",\"C\"],[\"B\",\"C\"],[\"A\",\"B\",\"B\",\"C\"]]")]
    [DataRow("MATCH p=((q=(r=() -> ())) |+| (q=(r=() -> ()) -> ()))+ RETURN CARDINALITY(q)=CARDINALITY(r) AS answer", "[true,true,true,true]")]
    [DataRow("MATCH p=((q=() -> ()) |+| (q=() -> ()))+ RETURN CARDINALITY(q) AS answer", "[1,1,1,1,2,2,2,2]")]
    [DataRow("MATCH p=(ACYCLIC (q=() -> ()) |+| (q=() -> () -> ()))+ RETURN CARDINALITY(q) AS answer", "[1,1,1,2]")]
    [DataRow("MATCH p=(segment=(q=() -> () WHERE PATH_LENGTH(segment)=2) |+| (q=() -> () -> () WHERE PATH_LENGTH(segment)=2))+ RETURN CARDINALITY(q) AS answer", "[1]")]
    [DataRow("MATCH p=((q=() -> ()) |+| (q=() -> () -> ()))+ RETURN VALUE { FOR x IN q RETURN COLLECT_LIST(PATH_LENGTH(x)) AS lengths } AS answer", "[[1],[1],[2],[1,1]]")]
    [DataRow("MATCH ((q=(a) -> (b)) |+| (q=(a) -> () -> (b)))+ RETURN CARDINALITY(q)=CARDINALITY(a) AND CARDINALITY(a)=CARDINALITY(b) AS answer", "[true,true,true,true]")]
    [DataRow("MATCH p=((q=() <- ()) |+| (q=() <- () <- ()))+ RETURN VALUE { FOR x IN q FOR n IN ELEMENTS(x) FILTER n IS TYPED NODE RETURN COLLECT_LIST(n.name) AS names } AS answer", "[[\"B\",\"A\"],[\"C\",\"B\"],[\"C\",\"B\",\"A\"],[\"C\",\"B\",\"B\",\"A\"]]")]
    [DataRow("MATCH p=(segment=(q=() -> ()) |+| (q=() -> () -> ()))+ RETURN CARDINALITY(segment)=CARDINALITY(q) AS answer", "[true,true,true,true]")]
    [DataRow("MATCH p=((q=() -> () WHERE PATH_LENGTH(q)=2) |+| (q=() -> () -> () WHERE PATH_LENGTH(q)=2))+ RETURN CARDINALITY(q) AS answer", "[1]")]
    [DataRow("MATCH p=((q=() -> ()) |+| (q=() -> () -> ()) WHERE PATH_LENGTH(q)=2)+ RETURN CARDINALITY(q) AS answer", "[1]")]
    [DataRow("MATCH p=((q=() -> ()) |+| (q=() -> () -> ())){0} RETURN CARDINALITY(q) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=((q=() -> ()) |+| (q=() -> () -> ())){2} RETURN VALUE { FOR x IN q RETURN COLLECT_LIST(PATH_LENGTH(x)) AS lengths } AS answer", "[[1,1]]")]
    [DataRow("OPTIONAL MATCH ((q=(:Missing) -> ()) |+| (q=(:Missing) -> () -> ()))+ RETURN q AS answer", "[null]")]
    [DataRow("MATCH p=ALL SHORTEST ((q=() -> ()) |+| (q=() -> () -> ()))+ RETURN CARDINALITY(q) AS answer", "[1,1,1,2]")]
    [DataRow("MATCH p=((q=(q) -> ()) |+| (q=(q) -> () -> ()))+ RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=((q=() -> ()) |+| (r=() -> () -> ()))+ RETURN p AS answer", "ERROR")]
    public Task Gql_different_shape_bag_nested_subpaths_preserve_paths_predicates_and_scope(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_different_shape_bag_nested_subpaths_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(c:Person {name:'B'})((q=(a) -> (b)) |+| (q=(a) -> () -> (b)))+ (t)", "a,b,c,p,q,t");
}
