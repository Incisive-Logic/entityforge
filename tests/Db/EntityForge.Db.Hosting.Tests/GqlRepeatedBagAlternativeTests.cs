namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(()-[:knows]->() |+| ()-[:knows]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,2,2,2,2]")]
    [DataRow("MATCH p=((a)-[e:knows]->(b) |+| (a)-[e:knows]->(b)){1,2} RETURN CARDINALITY(e) AS answer", "[1,1,1,1,2,2,2,2]")]
    [DataRow("MATCH p=(()-[e:knows]->() |+| ()-[e:knows]->()){1,2} RETURN CARDINALITY(e) AS answer", "[1,1,1,1,2,2,2,2]")]
    [DataRow("MATCH p=(()-[:knows]->() |+| ()-[:knows]->()){0} RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=(()-[:knows]->() |+| ()-[:Missing]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=((a WHERE a.name='A')-[:knows]->(b) |+| (a WHERE a.name='B')-[:knows]->(b)){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=ALL SHORTEST (()-[:knows]->() |+| ()-[:knows]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,2,2,2,2]")]
    [DataRow("MATCH p=((a)-[e:knows]->(b) |+| (a)<-[e:knows]-(b)){1} RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1]")]
    [DataRow("MATCH p=(()-[:knows]->() |+| ()<-[:Missing]-()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(()-[:Missing]->() |+| ()-[]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(()<-[:knows]-() |+| ()<-[:knows]-()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,2,2,2,2]")]
    [DataRow("MATCH p=((a)-[e:knows]->(b) |+| (a)-[e:knows]->(b)){1,2} RETURN VALUE { FOR n IN a RETURN COLLECT_LIST(n.name) AS names } AS answer", "[[\"A\"],[\"A\"],[\"B\"],[\"B\"],[\"A\",\"B\"],[\"A\",\"B\"],[\"A\",\"B\"],[\"A\",\"B\"]]")]
    [DataRow("MATCH p=(segment=()-[:knows]->() |+| ()-[:knows]->()){1,2} RETURN CARDINALITY(segment) AS answer", "[1,1,1,1,2,2,2,2]")]
    [DataRow("OPTIONAL MATCH p=(()-[:Missing]->() |+| ()-[:Missing]->()){1,2} RETURN p AS answer", "[null]")]
    [DataRow("MATCH p=((a WHERE a.name='A')-[:knows]->(b) |+| (a WHERE a.name='B')-[:knows]->(b)){0} RETURN CARDINALITY(a) AS answer", "[0,0,0]")]
    public Task Gql_repeated_bag_alternatives_preserve_iteration_choices_and_group_bindings(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_repeated_bag_alternatives_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(c:Person {name:'B'})(()-[:knows]->() |+| ()-[:Missing]->()){1}()", "c,p");
}
