namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=()-[e IS knows|Missing]->{0,1}()(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2,2]")]
    [DataRow("MATCH p=()-[IS knows|Missing]->{0,1}()(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2,2]")]
    [DataRow("MATCH p=()-[e IS knows&!Missing]->{0,1}()(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2,2]")]
    [DataRow("MATCH p=()-[e IS !knows]->{0,1}()(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=()-[e IS knows&Missing]->{0,1}()(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=()-[e IS knows|Missing WHERE e IS TYPED EDGE]->{0,1}()(() -> () |+| () -> () -> ())+ RETURN CARDINALITY(e) AS answer", "[0,0,0,0,1]")]
    [DataRow("MATCH p=()-[e IS knows|Missing WHERE FALSE]->{0,1}()(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=()-[e IS knows|Missing {missing:1}]->{0,1}()(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("LET wanted=1 MATCH p=()-[e IS knows|Missing {missing:wanted}]->{0,1}()(() -> () |+| () -> () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=()-[e IS knows|Missing]->{2}()(() -> () |+| () -> () -> ())* RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("OPTIONAL MATCH p=()-[e IS !knows]->{1}()(() -> () |+| () -> () -> ())+ RETURN e AS answer", "[null]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> ())+()-[e IS knows|Missing]->{0,1}() RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2,2]")]
    public Task Gql_bag_exterior_boolean_labels_preserve_iteration_predicates(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_bag_exterior_boolean_labels_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(c:Person {name:'B'})-[e IS knows|Missing WHERE e IS TYPED EDGE]->{0,1}()(segment=(a) -> (b) |+| (a) -> () -> (b))+ (t)", "a,b,c,e,p,segment,t");
}
