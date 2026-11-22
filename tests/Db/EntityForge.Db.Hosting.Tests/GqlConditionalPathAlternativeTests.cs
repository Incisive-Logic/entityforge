namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(a:Person {name:'A'})-[:knows]->() | (b:Person {name:'B'})-[:knows]->() RETURN COALESCE(a.name,b.name) AS answer", "[\"A\",\"B\"]")]
    [DataRow("MATCH p=(a:Person {name:'A'})-[:knows]->() | (b:Person {name:'B'})-[:knows]->() RETURN a.name AS answer", "[\"A\",null]")]
    [DataRow("MATCH p=(a:Person)-[:knows]->() | ()-[:knows]->(b) WHERE a IS NULL RETURN b.name AS answer", "[\"B\",\"C\"]")]
    [DataRow("MATCH p=((a:Person)-[:knows]->() | ()-[:knows]->(b) WHERE b IS NULL) RETURN a.name AS answer", "[\"A\",\"B\"]")]
    [DataRow("MATCH p=(a:Person)-[:knows]->() |+| ()-[:knows]->(b) RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1]")]
    [DataRow("MATCH p=ALL SHORTEST (a:Person)-[:knows]->() | ()-[:knows]->(b) WHERE a IS NULL RETURN b.name AS answer", "[\"B\",\"C\"]")]
    [DataRow("MATCH p=ANY SHORTEST (a:Person {name:'A'})-[:knows]->() | (:Person {name:'B'})-[:knows]->(b) WHERE a IS NULL RETURN b.name AS answer", "[\"C\"]")]
    [DataRow("OPTIONAL MATCH p=(a:Missing)-[:knows]->() | ()-[:knows]->(b:Missing) RETURN COALESCE(a,b,p) AS answer", "[null]")]
    [DataRow("OPTIONAL MATCH p=(a:Person)-[:knows]->() | ()-[:knows]->(b) WHERE a IS NOT NULL AND b IS NOT NULL RETURN p AS answer", "[null]")]
    [DataRow("MATCH (anchor:Person {name:'A'}) MATCH p=(anchor)-[:knows]->(a) | (anchor)-[:knows]->(b) RETURN COALESCE(a.name,b.name) AS answer", "[\"B\",\"B\"]")]
    [DataRow("MATCH (a:Person)-[:knows]->() | ()-[:knows]->(b) WHERE a IS NULL RETURN b.name AS answer", "[\"B\",\"C\"]")]
    [DataRow("MATCH p=(a:Person)-[:knows]->() | ()-[:knows]->(b) RETURN VALUE { RETURN COALESCE(a.name,b.name) AS valueName } AS answer", "[\"A\",\"B\",\"B\",\"C\"]")]
    [DataRow("MATCH p=(:Person {name:'A'})-[:knows]->() ((a)-[:knows]->() | ()-[:knows]->(b)) RETURN COALESCE(a.name,b.name) AS answer", "[\"B\",\"C\"]")]
    [DataRow("MATCH p=((a)-[:knows]->()){1,2} | ()-[:knows]->(b) RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=(a:Person)((a)-[:knows]->() | (b)-[:knows]->()) RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=((a)-[:knows]->() | (b)-[:knows]->())-[:knows]->(a) RETURN p AS answer", "ERROR")]
    public Task Gql_path_alternatives_preserve_conditional_singleton_bindings(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH p=(a:Person {name:'A'})-[:knows]->() | (c:Person {name:'B'})-[:knows]->() WHERE c IS NOT NULL", "a,c,p")]
    [DataRow("MATCH (a:Person {name:'A'})-[:knows]->() | (c:Person {name:'B'})-[:knows]->() WHERE c IS NOT NULL", "a,c")]
    public Task Gql_conditional_path_alternatives_preserve_update_scope_rollback_and_replay(string pattern, string columns) =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(pattern, columns);
}
