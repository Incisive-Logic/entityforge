namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(()-[:knows]->() | ()-[:knows]->()),(c:Person {name:'A'}) RETURN PATH_LENGTH(p) AS answer", "[1,1]")]
    [DataRow("MATCH p=(()-[:knows]->() |+| ()-[:knows]->()),(c:Person {name:'A'}) RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1]")]
    [DataRow("MATCH p=((x WHERE x.name=c.name)-[:knows]->() | (x WHERE x.name='B')-[:knows]->()),(c:Person {name:'A'}) RETURN x.name AS answer", "[\"A\",\"B\"]")]
    [DataRow("MATCH (c:Person {name:'A'}),p=((x WHERE x.name=c.name)-[:knows]->() | (x WHERE x.name='B')-[:knows]->()) RETURN x.name AS answer", "[\"A\",\"B\"]")]
    [DataRow("MATCH p=(a:Person {name:'A'})((x WHERE x.name=c.name)-[:knows]->(y))?(b),(c:Person {name:'C'}) RETURN PATH_LENGTH(p) AS answer", "[0]")]
    [DataRow("MATCH p=(a:Person {name:'A'})((x)-[:knows]->(y))?(b),q=(b)-[:knows]->(c) RETURN PATH_LENGTH(p) AS answer", "[0,1]")]
    [DataRow("MATCH p=(:Person {name:'A'})-[:knows]->?(b),q=(:Person {name:'A'})-[:knows]->() RETURN PATH_LENGTH(p) AS answer", "[0]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(:Person {name:'A'})-[:knows]->?(b),q=(:Person {name:'A'})-[:knows]->() RETURN PATH_LENGTH(p) AS answer", "[0,1]")]
    [DataRow("OPTIONAL MATCH p=(()-[:knows]->() | ()-[:knows]->()),(c:Missing) RETURN p AS answer", "[null]")]
    [DataRow("MATCH p=(segment=()-[:knows]->() | ()-[:knows]->()),(c:Person {name:'A'}) RETURN PATH_LENGTH(segment) AS answer", "[1,1]")]
    [DataRow("MATCH p=((x WHERE x.name=c.name)-[:knows]->() | (x WHERE x.name=c.name)-[:knows]->()),q=((c WHERE c.name=x.name)-[:knows]->() | (c WHERE c.name=x.name)-[:knows]->()) RETURN p AS answer", "[]")]
    [DataRow("MATCH p=(()-[:knows]->() | ()-[:knows]->()),q=(()-[:knows]->() | ()-[:knows]->()) RETURN COUNT(*) AS answer", "[2]")]
    [DataRow("MATCH p=(()-[:knows]->() |+| ()-[:knows]->()),q=(()-[:knows]->() |+| ()-[:knows]->()) RETURN COUNT(*) AS answer", "[8]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(()-[:knows]->() | ()-[:knows]->()),q=(()-[:knows]->() | ()-[:knows]->()) RETURN COUNT(*) AS answer", "[4]")]
    [DataRow("MATCH p=(a:Person {name:'A'})(({name:c.name})-[:knows]->())?(b),(c:Person {name:'C'}) RETURN PATH_LENGTH(p) AS answer", "[0]")]
    public Task Gql_nonselective_alternative_products_preserve_branch_scope_and_uniqueness(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_nonselective_alternative_products_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=((c:Person {name:'B'})-[:knows]->(b) | (c:Person {name:'B'})-[:Missing]->(b)),(a:Person {name:'A'})",
            "a,b,c,p");
}
