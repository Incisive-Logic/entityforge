namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(a:Person {name:'A'})(() -> () |+| () -> () -> ())+ (b) RETURN b.name AS answer", "[\"B\",\"C\",\"C\"]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> ())+ (b:Person {name:'C'}) RETURN PATH_LENGTH(p) AS answer", "[1,2,2]")]
    [DataRow("MATCH (a:Person {name:'B'}) MATCH p=(a)(() -> () |+| () -> () -> ())+ (b) RETURN b.name AS answer", "[\"C\"]")]
    [DataRow("MATCH p=(a)(() -> () |+| () -> () -> ())* (a) RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=(a:Person {name:'A'})(() -> () |+| () -> () -> ()){0} (b:Person {name:'C'}) RETURN PATH_LENGTH(p) AS answer", "[]")]
    [DataRow("MATCH p=(a WHERE a.name='A')(() -> () |+| () -> () -> ())+ (b WHERE b.name='C') RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    [DataRow("MATCH p=ALL SHORTEST (a:Person {name:'A'})(() -> () |+| () -> () -> ())+ (b) RETURN b.name AS answer", "[\"B\",\"C\",\"C\"]")]
    [DataRow("OPTIONAL MATCH p=(a:Missing)(() -> () |+| () -> () -> ())+ (b) RETURN b AS answer", "[null]")]
    [DataRow("MATCH p=(a:Person {name:'A'})(() -> () |+| () -> () -> ())+ (b) WHERE b.name='C' RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    public Task Gql_different_shape_bag_anchors_preserve_identity_constraints_and_copies(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH (() -> () |+| () -> () -> ())+ RETURN COUNT(*) AS answer", "[4]")]
    [DataRow("MATCH (a:Person {name:'A'})(() -> () |+| () -> () -> ())+ (b) RETURN b.name AS answer", "[\"B\",\"C\",\"C\"]")]
    [DataRow("MATCH (a:Person {name:'A'}) MATCH (a)(() -> () |+| () -> () -> ())+ (b) RETURN b.name AS answer", "[\"B\",\"C\",\"C\"]")]
    [DataRow("MATCH ALL SHORTEST (a:Person {name:'A'})(() -> () |+| () -> () -> ())+ (b) RETURN b.name AS answer", "[\"B\",\"C\",\"C\"]")]
    [DataRow("OPTIONAL MATCH (a:Missing)(() -> () |+| () -> () -> ())+ (b) RETURN b AS answer", "[null]")]
    [DataRow("MATCH (a)(() -> () |+| () -> () -> ())* (a) RETURN a.name AS answer", "[\"A\",\"B\",\"C\"]")]
    [DataRow("LET marker=7 MATCH (() -> () |+| () -> () -> ())+ RETURN marker AS answer", "[7,7,7,7]")]
    public Task Gql_different_shape_bag_without_path_variable_preserves_multiplicity_and_scope(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH p=(c:Person {name:'B'})(() -> () |+| () -> () -> ())+ (b)", "b,c,p")]
    [DataRow("MATCH (c:Person {name:'B'}) MATCH p=(c)(() -> () |+| () -> () -> ())+ (b)", "b,c,p")]
    [DataRow("MATCH (c:Person {name:'B'})(() -> () |+| () -> () -> ())+ (b)", "b,c")]
    [DataRow("MATCH (c:Person {name:'B'}) MATCH (c)(() -> () |+| () -> () -> ())+ (b)", "b,c")]
    [DataRow("MATCH (c:Person {name:'B'})(() -> () |+| () -> () -> () WHERE c.name='B')+ (b)", "b,c")]
    public Task Gql_different_shape_bag_anchors_preserve_update_scope_rollback_and_replay(string pattern, string columns) =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(pattern, columns);
}
