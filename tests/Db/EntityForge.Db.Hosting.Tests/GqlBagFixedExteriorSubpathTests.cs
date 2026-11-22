namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(q=() -> ())(() -> () |+| () -> () -> ())* RETURN PATH_LENGTH(q)*10+PATH_LENGTH(p) AS answer", "[11,11,12]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> ())*(q=() -> ()) RETURN PATH_LENGTH(q)*10+PATH_LENGTH(p) AS answer", "[11,11,12]")]
    [DataRow("MATCH p=(q=() -> ())(() -> () |+| () -> () -> ())*(r=() -> ()) RETURN PATH_LENGTH(q)+PATH_LENGTH(r)+PATH_LENGTH(p) AS answer", "[4]")]
    [DataRow("MATCH p=(q=() -> () WHERE PATH_LENGTH(q)=1)(() -> () |+| () -> () -> ())* RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("OPTIONAL MATCH p=(q=() -> () WHERE FALSE)(() -> () |+| () -> () -> ())* RETURN q AS answer", "[null]")]
    [DataRow("MATCH p=(q=(r=() -> ()))(() -> () |+| () -> () -> ())* RETURN PATH_LENGTH(q)+PATH_LENGTH(r)+PATH_LENGTH(p) AS answer", "[3,3,4]")]
    [DataRow("MATCH p=(q=() <- ())(() <- () |+| () <- () <- ())* RETURN VALUE { FOR n IN ELEMENTS(q) FILTER n IS TYPED NODE RETURN COLLECT_LIST(n.name) AS names } AS answer", "[[\"B\",\"A\"],[\"C\",\"B\"],[\"C\",\"B\"]]")]
    [DataRow("MATCH p=(q=() -> () WHERE PATH_LENGTH(q)=1)(() -> () |+| () -> ())* RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> ())*(q=() -> () WHERE PATH_LENGTH(q)=1) RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(q=(r=() -> () WHERE PATH_LENGTH(r)=1) WHERE PATH_LENGTH(q)=1)(() -> () |+| () -> () -> ())* RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(q=TRAIL () -> () WHERE PATH_LENGTH(q)=1)(() -> () |+| () -> () -> ())* RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=ALL SHORTEST (q=() -> () WHERE PATH_LENGTH(q)=1)(() -> () |+| () -> () -> ())* RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("LET wanted=1 MATCH p=(q=() -> () WHERE PATH_LENGTH(q)=wanted)(() -> () |+| () -> () -> ())* RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    public Task Gql_bag_fixed_exterior_subpaths_preserve_path_values_and_predicates(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH p=(q=() -> () WHERE PATH_LENGTH(q)=1)(segment=() -> () |+| () -> () -> ())* RETURN PATH_LENGTH(q)+CARDINALITY(segment) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(segment=() -> () |+| () -> () -> ())*(q=() -> () WHERE PATH_LENGTH(q)=1) RETURN PATH_LENGTH(q)+CARDINALITY(segment) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(q=() -> () WHERE PATH_LENGTH(q)=1)((segment=() -> ()) |+| (segment=() -> () -> ()))* RETURN PATH_LENGTH(q)+CARDINALITY(segment) AS answer", "[1,1,2]")]
    [DataRow("OPTIONAL MATCH p=(q=() -> () WHERE FALSE)(segment=() -> () |+| () -> () -> ())* RETURN segment AS answer", "[null]")]
    [DataRow("MATCH p=(q=() -> ())(segment=() -> () |+| () -> () -> () WHERE PATH_LENGTH(q)=1)* RETURN PATH_LENGTH(q)+CARDINALITY(segment) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(q=() -> () WHERE PATH_LENGTH(q)=1)(segment=() -> () |+| () -> ())* RETURN PATH_LENGTH(q)+CARDINALITY(segment) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=(q=() -> () WHERE PATH_LENGTH(q)=1)(segment=() -> () |+| () -> () -> ())* RETURN q,segment NEXT RETURN PATH_LENGTH(q)+CARDINALITY(segment) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(q=() -> () WHERE PATH_LENGTH(q)=1)(segment=() -> () |+| () -> () -> ()){0} RETURN PATH_LENGTH(q)+CARDINALITY(segment) AS answer", "[1,1]")]
    public Task Gql_bag_fixed_and_repeated_subpaths_preserve_scalar_and_list_bindings(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH p=(q=() -> () WHERE PATH_LENGTH(q)=1)()-[e]->{0,1}()(segment=() -> () |+| () -> () -> ())* RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=(segment=() -> () |+| () -> () -> ())*()-[e]->{0,1}()(q=() -> () WHERE PATH_LENGTH(q)=1) RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=(q=() -> () WHERE PATH_LENGTH(q)=1)()-[e WHERE FALSE]->{0,1}()(segment=() -> () |+| () -> () -> ())* RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(q=() -> () WHERE PATH_LENGTH(q)=1)()-[e IS knows|Missing]->{0,1}()(segment=() -> () |+| () -> () -> ())* RETURN PATH_LENGTH(q)*100+CARDINALITY(e)*10+CARDINALITY(segment) AS answer", "[100,100,101,110]")]
    [DataRow("MATCH p=(q=() -> () WHERE PATH_LENGTH(q)=1)()-[e]->{0,1}()(segment=() -> () |+| () -> () -> ())*()-[f]->{0,1}() RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2,2]")]
    [DataRow("MATCH p=(q=() -> () WHERE PATH_LENGTH(q)=1)()-[e]->{1}()(segment=() -> () |+| () -> () -> ())* RETURN CARDINALITY(segment) AS answer", "[0]")]
    [DataRow("MATCH p=(q=() <- () WHERE PATH_LENGTH(q)=1)()<-[e]-{0,1}()(segment=() <- () |+| () <- () <- ())* RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("OPTIONAL MATCH p=(q=() -> () WHERE FALSE)()-[e]->{0,1}()(segment=() -> () |+| () -> () -> ())* RETURN e AS answer", "[null]")]
    public Task Gql_bag_mixed_fixed_and_quantified_exteriors_preserve_offsets(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_bag_mixed_fixed_and_quantified_exteriors_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(q=(c:Person {name:'B'}) -> (t) WHERE PATH_LENGTH(q)=1)()-[e]->{0,1}()(segment=() -> () |+| () -> () -> ())*", "c,e,p,q,segment,t");

    [TestMethod]
    public Task Gql_bag_fixed_and_repeated_subpaths_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(q=(c:Person {name:'B'}) -> (t) WHERE PATH_LENGTH(q)=1)(segment=() -> () |+| () -> () -> ())*", "c,p,q,segment,t");

    [TestMethod]
    public Task Gql_bag_fixed_exterior_subpaths_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(q=(c:Person {name:'B'}) -> (t) WHERE PATH_LENGTH(q)=1)(() -> () |+| () -> () -> ())*", "c,p,q,t");
}
