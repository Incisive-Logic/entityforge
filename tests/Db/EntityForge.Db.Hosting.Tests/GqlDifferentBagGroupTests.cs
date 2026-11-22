namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=((a) -> (b) |+| (a) -> () -> (b))+ RETURN CARDINALITY(a) AS answer", "[1,1,1,2]")]
    [DataRow("MATCH p=((a) -> (b) |+| (a) -> () -> (b))+ RETURN VALUE { FOR n IN a RETURN COLLECT_LIST(n.name) AS names } AS answer", "[[\"A\"],[\"B\"],[\"A\"],[\"A\",\"B\"]]")]
    [DataRow("MATCH ((a) -> (b) |+| (a) -> () -> (b))+ RETURN VALUE { FOR n IN b RETURN COLLECT_LIST(n.name) AS names } AS answer", "[[\"B\"],[\"C\"],[\"C\"],[\"B\",\"C\"]]")]
    [DataRow("MATCH p=((a) -> (b) |+| (a) -> () -> (b)){0} RETURN CARDINALITY(a) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=((a) -> (b) |+| (a) -> () -> (b)){2} RETURN CARDINALITY(b) AS answer", "[2]")]
    [DataRow("MATCH p=ALL SHORTEST ((a) -> (b) |+| (a) -> () -> (b))+ RETURN CARDINALITY(a) AS answer", "[1,1,1,2]")]
    [DataRow("MATCH p=((a)-[e:knows]->(b) |+| (a)-[e:knows]->()-[:knows]->(b))+ RETURN CARDINALITY(e) AS answer", "[1,1,1,2]")]
    [DataRow("MATCH p=((a) -> (b) |+| (a) -> () -> (b) WHERE a.name='A')+ RETURN CARDINALITY(a) AS answer", "[1,1]")]
    [DataRow("MATCH p=((a) -> (b) |+| (a) -> (b) |+| (a) -> () -> (b))+ RETURN CARDINALITY(a) AS answer", "[1,1,1,1,1,2,2,2,2]")]
    [DataRow("MATCH p=((a) -> (b) |+| (a) -> () -> (b) WHERE b.name='C')+ RETURN CARDINALITY(b) AS answer", "[1,1]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=SIMPLE ((a) -> (a) |+| (a) -> () <- (a))+ RETURN CARDINALITY(a) AS answer", "[1,1]")]
    [DataRow("OPTIONAL MATCH ((a:Missing) -> (b) |+| (a:Missing) -> () -> (b))+ RETURN a AS answer", "[null]")]
    [DataRow("MATCH p=(s:Person {name:'A'}) -> (x)((a) -> (b) |+| (a) -> () -> (b))+ (t) RETURN CARDINALITY(a) AS answer", "[1]")]
    [DataRow("MATCH p=((a) -> (b) |+| (a) -> () -> (c))+ RETURN p AS answer", "ERROR")]
    public Task Gql_different_shape_bag_groups_preserve_each_decomposition_and_order(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_different_shape_bag_groups_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(c:Person {name:'B'})((a) -> (b) |+| (a) -> () -> (b))+ (t)", "a,b,c,p,t");
}
