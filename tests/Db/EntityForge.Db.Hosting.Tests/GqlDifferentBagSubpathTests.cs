namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(segment=() -> () |+| () -> () -> ())+ RETURN CARDINALITY(segment) AS answer", "[1,1,1,2]")]
    [DataRow("MATCH p=(segment=() -> () |+| () -> () -> ())+ RETURN VALUE { FOR q IN segment RETURN COLLECT_LIST(PATH_LENGTH(q)) AS lengths } AS answer", "[[1],[1],[2],[1,1]]")]
    [DataRow("MATCH p=(segment=() -> () |+| () -> () -> ())+ RETURN VALUE { FOR q IN segment FOR n IN ELEMENTS(q) FILTER n IS TYPED NODE RETURN COLLECT_LIST(n.name) AS names } AS answer", "[[\"A\",\"B\"],[\"B\",\"C\"],[\"A\",\"B\",\"C\"],[\"A\",\"B\",\"B\",\"C\"]]")]
    [DataRow("MATCH p=(segment=() <- () |+| () <- () <- ())+ RETURN VALUE { FOR q IN segment FOR n IN ELEMENTS(q) FILTER n IS TYPED NODE RETURN COLLECT_LIST(n.name) AS names } AS answer", "[[\"B\",\"A\"],[\"C\",\"B\"],[\"C\",\"B\",\"A\"],[\"C\",\"B\",\"B\",\"A\"]]")]
    [DataRow("MATCH (segment=(a) -> (b) |+| (a) -> () -> (b))+ RETURN CARDINALITY(segment)=CARDINALITY(a) AND CARDINALITY(a)=CARDINALITY(b) AS answer", "[true,true,true,true]")]
    [DataRow("MATCH p=(segment=() -> () |+| () -> () -> ()){0} RETURN CARDINALITY(segment) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=(segment=() -> () |+| () -> () -> ()){2} RETURN VALUE { FOR q IN segment RETURN COLLECT_LIST(PATH_LENGTH(q)) AS lengths } AS answer", "[[1,1]]")]
    [DataRow("MATCH p=ALL SHORTEST (segment=() -> () |+| () -> () -> ())+ RETURN CARDINALITY(segment) AS answer", "[1,1,1,2]")]
    [DataRow("MATCH p=(segment=() -> () |+| () -> () -> () WHERE PATH_LENGTH(segment)=2)+ RETURN CARDINALITY(segment) AS answer", "[1]")]
    [DataRow("OPTIONAL MATCH (segment=(:Missing) -> () |+| (:Missing) -> () -> ())+ RETURN segment AS answer", "[null]")]
    [DataRow("MATCH p=(s) -> (x)(segment=() -> () |+| () -> () -> ())* (t) RETURN CARDINALITY(segment) AS answer", "[0,0,1]")]
    [DataRow("MATCH p=(a=(a) -> (b) |+| (a) -> () -> (b))+ RETURN p AS answer", "ERROR")]
    public Task Gql_different_shape_bag_subpaths_preserve_iteration_paths_and_scope(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_different_shape_bag_subpaths_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(c:Person {name:'B'})(segment=(a) -> (b) |+| (a) -> () -> (b))+ (t)", "a,b,c,p,segment,t");
}
