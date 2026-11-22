namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=() ->{0,1} ()(()-[:knows]->() |+| ()-[:Missing]->()->())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=() ->{0,1} ()(()-[:knows|Missing]->() |+| ()-[:knows]->()-[:knows]->())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2,2]")]
    [DataRow("MATCH p=() ->{0,1} ()(()-[:knows]->() |+| ()->()->())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2,2]")]
    [DataRow("MATCH () -[e]->{0,1} ()((q=() -> ()) |+| (q=() -> () -> ()))+ RETURN CARDINALITY(e)+CARDINALITY(q) AS answer", "[1,1,1,2,2]")]
    [DataRow("MATCH p=() ->{0,1} ()(() -> () |+| () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,2,2,2,2,2,2]")]
    [DataRow("MATCH p=() ->{0} ()(() -> () |+| () -> () -> ()){0} () ->{0} () RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=() <-{0,1} ()((q=() <- ()) |+| (q=() <- () <- ()))+ RETURN VALUE { FOR x IN q FOR n IN ELEMENTS(x) FILTER n IS TYPED NODE RETURN COLLECT_LIST(n.name) AS names } AS answer", "[[\"B\",\"A\"],[\"C\",\"B\"],[\"C\",\"B\",\"A\"],[\"C\",\"B\",\"B\",\"A\"],[\"B\",\"A\"]]")]
    [DataRow("MATCH p=() -[e {missing:1}]->{0,1} ()(() -> () |+| () -> () -> ())+ RETURN CARDINALITY(e) AS answer", "[0,0,0,0]")]
    [DataRow("MATCH p=() ->{0,1} ()(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2,2]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> ())+ () ->{0,1} () RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2,2]")]
    [DataRow("MATCH p=() ->{0,1} ()(() -> () |+| () -> () -> ())+ () ->{0,1} () RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2,2,2]")]
    [DataRow("MATCH p=() -[e]->{0,1} ()(() -> () |+| () -> () -> ())+ RETURN CARDINALITY(e)*10+PATH_LENGTH(p) AS answer", "[1,1,2,2,12]")]
    [DataRow("MATCH p=() ->{1,2} ()(() -> () |+| () -> () -> ())* RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=() ->+ ()(() -> () |+| () -> () -> ())* RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=() -[:Missing]->{0,1} ()(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=() ->{0,1} ()((q=() -> ()) |+| (q=() -> () -> ()))+ RETURN VALUE { FOR x IN q RETURN COLLECT_LIST(PATH_LENGTH(x)) AS lengths } AS answer", "[[1],[1],[2],[1,1],[1]]")]
    [DataRow("OPTIONAL MATCH p=(:Missing) ->{0,1} ()(() -> () |+| () -> () -> ())+ RETURN p AS answer", "[null]")]
    [DataRow("MATCH p=ALL SHORTEST () ->{0,1} ()(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2,2]")]
    public Task Gql_different_shape_bag_variable_exteriors_preserve_decomposition_boundaries(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_different_shape_bag_variable_exteriors_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(c:Person {name:'B'})-[e]->{0,1}()(segment=(a) -> (b) |+| (a) -> () -> (b))+ (t)", "a,b,c,e,p,segment,t");
}
