namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=((x)-[:knows]->()){1,2} | (x)-[:knows]->() RETURN x AS answer", "ERROR")]
    [DataRow("MATCH p=(x)-[:knows]->() | ((x)-[:knows]->()){1,2} RETURN x AS answer", "ERROR")]
    [DataRow("MATCH p=((x)-[:knows]->()){1,2} |+| (x)-[:knows]->() RETURN x AS answer", "ERROR")]
    [DataRow("MATCH p=()-[e:knows]->{1,2}() | ()-[e:knows]->() RETURN e AS answer", "ERROR")]
    [DataRow("MATCH p=ALL SHORTEST ((x)-[:knows]->()){1,2} | (x)-[:knows]->() RETURN x AS answer", "ERROR")]
    [DataRow("OPTIONAL MATCH p=((x)-[:Missing]->()){1,2} | (x)-[:Missing]->() RETURN x AS answer", "ERROR")]
    [DataRow("MATCH p=((x)-[:knows]->()){1,2} | ((x)-[:knows]->()){1} RETURN CARDINALITY(x) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=((x)-[:knows]->()){1,2} |+| ((x)-[:knows]->()){1} RETURN CARDINALITY(x) AS answer", "[1,1,1,1,2]")]
    [DataRow("MATCH p=()-[e:knows]->{1,2}() | ()-[e:knows]->{1}() RETURN CARDINALITY(e) AS answer", "[1,1,2]")]
    public Task Gql_path_alternatives_reject_unreconciled_group_exposure(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
}
