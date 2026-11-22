namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=()-[:knows]->()(segment=()-[:knows]->() | ()-[:knows]->()) RETURN PATH_LENGTH(segment) AS answer", "[1]")]
    [DataRow("MATCH p=()-[:knows]->()(segment=()-[:knows]->() |+| ()-[:knows]->()) RETURN PATH_LENGTH(segment) AS answer", "[1,1]")]
    [DataRow("MATCH ()-[:knows]->()(segment=()-[:knows]->() | ()-[:knows]->()) RETURN PATH_LENGTH(segment) AS answer", "[1]")]
    [DataRow("MATCH p=(a:Person {name:'A'})(segment=() | ()-[:knows]->())-[:knows]->(z) RETURN PATH_LENGTH(p)*10+PATH_LENGTH(segment) AS answer", "[10,21]")]
    [DataRow("MATCH p=(a:Person {name:'A'})(segment=() | ()-[:knows]->() WHERE PATH_LENGTH(segment)=0)-[:knows]->(z) RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH p=ALL SHORTEST (a:Person {name:'A'})(segment=() | ()-[:knows]->() WHERE PATH_LENGTH(segment)=1)-[:knows]->(z) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=()-[:knows]->()(segment=TRAIL ()-[:knows]->() | ()-[:knows]->()) RETURN PATH_LENGTH(segment) AS answer", "[1]")]
    [DataRow("OPTIONAL MATCH p=()-[:knows]->()(segment=()-[:Missing]->() | ()-[:Missing]->()) RETURN segment AS answer", "[null]")]
    [DataRow("MATCH p=()-[:knows]->()(segment=(a)-[:knows]->() | ()-[:knows]->(b)) WHERE a IS NULL RETURN b.name AS answer", "[\"C\"]")]
    [DataRow("MATCH p=(a)-[:knows]->()(a=()-[:knows]->() | ()-[:knows]->()) RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=(leftSlice=()-[:knows]->() | ()-[:knows]->())(rightSlice=()-[:knows]->() | ()-[:knows]->()) RETURN PATH_LENGTH(leftSlice)+PATH_LENGTH(rightSlice) AS answer", "[2]")]
    [DataRow("MATCH p=(leftSlice=()-[:knows]->() |+| ()-[:knows]->())(rightSlice=()-[:knows]->() |+| ()-[:knows]->()) RETURN PATH_LENGTH(leftSlice)+PATH_LENGTH(rightSlice) AS answer", "[2,2,2,2]")]
    [DataRow("MATCH p=(leftSlice=()-[:knows]->() |+| ()-[:knows]->())(rightSlice=()-[:knows]->() | ()-[:knows]->()) RETURN PATH_LENGTH(leftSlice)+PATH_LENGTH(rightSlice) AS answer", "[2,2]")]
    [DataRow("MATCH p=(segment=()-[:knows]->() | ()-[:knows]->())(segment=()-[:knows]->() | ()-[:knows]->()) RETURN p AS answer", "ERROR")]
    public Task Gql_named_proper_alternatives_preserve_slice_bindings_and_selection(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_named_proper_alternatives_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(:Person {name:'A'})-[:knows]->()(segment=(c:Person {name:'B'})-[:knows]->(b) | (c:Person {name:'B'})-[:Missing]->(b))",
            "b,c,p,segment");
}
