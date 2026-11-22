namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=() -[e WHERE e IS TYPED EDGE]->{2} ()(() -> () |+| () -> () -> ())* RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=() -[e WHERE EXISTS { MATCH (:Person {name:'A'})-[e]->() }]->{2} ()(() -> () |+| () -> () -> ())* RETURN PATH_LENGTH(p) AS answer", "[]")]
    [DataRow("MATCH p=() -[e WHERE EXISTS { MATCH (:Person {name:'A'})-[e]->() }]->{1,2} ()(() -> () |+| () -> () -> ())* RETURN PATH_LENGTH(p) AS answer", "[1,2]")]
    [DataRow("MATCH p=() -[e WHERE e IS TYPED EDGE]->{0,1} ()(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2,2]")]
    [DataRow("MATCH p=() -[e WHERE FALSE]->{0,1} ()(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=() -[e WHERE e.missing=1]->{0,1} ()(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("LET wanted=1 MATCH p=() -[e {missing:wanted}]->{0,1} ()(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("LET wanted=1 MATCH p=() -[{missing:wanted}]->{0,1} ()(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=(a) -[e WHERE a.name='A' AND t.name='C']->{1} ()(() -> () |+| () -> () -> ())+ (t) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=() -[e WHERE EXISTS { MATCH (x)-[e]->(y) }]->{0,1} ()(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2,2]")]
    [DataRow("MATCH p=() -[e WHERE VALUE { RETURN e IS TYPED EDGE AS valid }]->{0,1} ()(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2,2]")]
    [DataRow("MATCH p=ALL SHORTEST () -[e WHERE FALSE]->{0,1} ()(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("OPTIONAL MATCH p=() -[e WHERE FALSE]->{1} ()(() -> () |+| () -> () -> ())+ RETURN e AS answer", "[null]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> ())+ () -[e WHERE e IS TYPED EDGE]->{0,1} () RETURN CARDINALITY(e) AS answer", "[0,0,0,0,1]")]
    [DataRow("LET wanted=1 MATCH p=() -[e {missing:wanted,missing:wanted}]->{0,1} ()(() -> () |+| () -> () -> ())+ RETURN p AS answer", "ERROR")]
    public Task Gql_bag_exterior_predicates_preserve_scalar_iteration_scope_and_selection(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_bag_exterior_predicates_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(c:Person {name:'B'})-[e WHERE e IS TYPED EDGE]->{0,1}()(segment=(a) -> (b) |+| (a) -> () -> (b))+ (t)", "a,b,c,e,p,segment,t");
}
