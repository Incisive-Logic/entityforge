namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(segment=()-[:knows]->() | ()-[:knows]->()) RETURN PATH_LENGTH(segment) AS answer", "[1,1]")]
    [DataRow("MATCH p=(segment=()-[:knows]->() |+| ()-[:knows]->()) RETURN PATH_LENGTH(segment) AS answer", "[1,1,1,1]")]
    [DataRow("MATCH p=(outerPath=(innerPath=()-[:knows]->() | ()-[:knows]->())) RETURN PATH_LENGTH(outerPath)+PATH_LENGTH(innerPath) AS answer", "[2,2]")]
    [DataRow("MATCH p=(segment=()-[:knows]->() | ()-[:knows]->{2}()) WHERE PATH_LENGTH(segment)=2 RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=ALL SHORTEST (segment=()-[:knows]->() | ()-[:knows]->{2}() WHERE PATH_LENGTH(segment)=2) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("OPTIONAL MATCH p=(segment=()-[:Missing]->() | ()-[:Missing]->()) RETURN segment AS answer", "[null]")]
    [DataRow("MATCH p=(segment=(a)-[:knows]->() | ()-[:knows]->(b)) WHERE a IS NULL RETURN PATH_LENGTH(segment) AS answer", "[1,1]")]
    [DataRow("MATCH p=(segment=()-[:knows]->() | ()-[:knows]->()) RETURN VALUE { RETURN PATH_LENGTH(segment) AS lengthValue } AS answer", "[1,1]")]
    [DataRow("MATCH p=(p=()-[:knows]->() | ()-[:knows]->()) RETURN p AS answer", "ERROR")]
    [DataRow("LET segment=1 MATCH p=(segment=()-[:knows]->() | ()-[:knows]->()) RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=(segment=(()-[:knows]->()){1,2} | (()-[:knows]->()){1,2}) RETURN PATH_LENGTH(segment) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(outerPath=(innerPath=()-[:knows]->() | ()-[:knows]->{2}() WHERE PATH_LENGTH(innerPath)>1) WHERE PATH_LENGTH(outerPath)=2) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    public Task Gql_whole_alternative_bindings_preserve_path_aliases_and_predicate_scope(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_whole_alternative_bindings_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(segment=(c:Person {name:'B'})-[:knows]->(b) | (c:Person {name:'B'})-[:Missing]->(b)) WHERE PATH_LENGTH(segment)=1",
            "b,c,p,segment");
}
