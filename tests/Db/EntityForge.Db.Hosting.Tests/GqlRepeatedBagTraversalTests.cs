namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(()-[:knows]->() |+| ()-[:knows]->())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,2,2,2,2]")]
    [DataRow("MATCH p=(()-[:knows]->() |+| ()-[:knows]->())* RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,1,1,2,2,2,2]")]
    [DataRow("MATCH p=(()-[:knows]->() |+| ()-[:Missing]->()){1,20} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=((a)-[e:knows]->(b) |+| (a)-[e:knows]->(b))+ RETURN CARDINALITY(e) AS answer", "[1,1,1,1,2,2,2,2]")]
    [DataRow("MATCH p=((a WHERE a.name='A')-[:knows]->(b) |+| (a WHERE a.name='B')-[:knows]->(b))+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=ALL SHORTEST (()-[:knows]->() |+| ()-[:knows]->())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,2,2,2,2]")]
    [DataRow("OPTIONAL MATCH p=(()-[:Missing]->() |+| ()-[:Missing]->())+ RETURN p AS answer", "[null]")]
    [DataRow("MATCH p=((a)-[e:knows]->(b) |+| (a)-[e:knows]->(b))+ RETURN VALUE { FOR n IN a RETURN COLLECT_LIST(n.name) AS names } AS answer", "[[\"A\"],[\"A\"],[\"B\"],[\"B\"],[\"A\",\"B\"],[\"A\",\"B\"],[\"A\",\"B\"],[\"A\",\"B\"]]")]
    [DataRow("MATCH p=(segment=()-[:knows]->() |+| ()-[:knows]->())* RETURN CARDINALITY(segment) AS answer", "[0,0,0,1,1,1,1,2,2,2,2]")]
    [DataRow("MATCH p=(()-[:knows]->()-[:knows]->() |+| ()-[:knows]->()-[:knows]->())+ RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    [DataRow("MATCH p=((a)-[:knows]->(b) |+| (a)-[:knows]->(b) WHERE b.name='B')+ RETURN PATH_LENGTH(p) AS answer", "[1,1]")]
    [DataRow("MATCH p=((a:Missing)-[:knows]->(b) |+| (a:Missing)-[:knows]->(b) WHERE 1/0=1)+ RETURN p AS answer", "[]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(()-[:knows]-()-[:knows]-() |+| ()-[:knows]-()-[:knows]-()){1} RETURN PATH_LENGTH(p) AS answer", "[2,2,2,2,2,2,2,2,2,2,2,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=SIMPLE (()-[:knows]-()-[:knows]-() |+| ()-[:knows]-()-[:knows]-())+ RETURN PATH_LENGTH(p) AS answer", "[2,2,2,2,2,2,2,2,2,2,2,2]")]
    public Task Gql_repeated_bag_traversal_preserves_iteration_multiplicity(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH ((a)-[e:knows]->(b) |+| (a)-[e:knows]->(b))+ RETURN CARDINALITY(e) AS answer", "[1,1,1,1,2,2,2,2]")]
    [DataRow("MATCH (segment=()-[:knows]->() |+| ()-[:knows]->())* RETURN CARDINALITY(segment) AS answer", "[0,0,0,1,1,1,1,2,2,2,2]")]
    [DataRow("MATCH ALL SHORTEST ((a)-[e:knows]->(b) |+| (a)-[e:knows]->(b))+ RETURN CARDINALITY(a) AS answer", "[1,1,1,1,2,2,2,2]")]
    public Task Gql_repeated_bag_without_path_variable_preserves_group_exports(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_repeated_bag_traversal_preserves_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(c:Person {name:'B'})(()-[:knows]->() |+| ()-[:Missing]->())+()", "c,p");
}
