namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=((a)(()-[:knows]->()){1,2}(b))? RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2]")]
    [DataRow("MATCH p=((a)(()-[:knows]->()){1,2}(b))? RETURN a.name AS answer", "[null,null,null,\"A\",\"A\",\"B\"]")]
    [DataRow("MATCH p=((a)(()-[:knows]->()){1,2}(b))? WHERE a IS NULL RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=((a)(()-[:knows]->()){1,2}(b) WHERE b.name='C')? RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,2]")]
    [DataRow("MATCH p=((a)(()-[:knows]->()){0,2}(b))? RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=((a)(()-[e:knows]->()){1,2}(b))? RETURN e AS answer", "ERROR")]
    [DataRow("MATCH p=((a)(segment=()-[:knows]->()){1,2}(b))? RETURN segment AS answer", "ERROR")]
    [DataRow("MATCH p=((a)(()-[:Missing]->()){1,2}(b))? RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=((a)(()-[:knows]->()){1,2}(b) WHERE b.name='C') WHERE a.name='A' RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=ALL PATHS ((a)(()-[:knows]->()){1,2}(b) WHERE b.name='C') RETURN PATH_LENGTH(p) AS answer", "[1,2]")]
    [DataRow("OPTIONAL MATCH p=((a)(()-[:knows]->()){1,2}(b) WHERE b.name='Missing') RETURN p AS answer", "[null]")]
    [DataRow("MATCH (a:Person {name:'A'}) MATCH p=((a)(()-[:knows]->()){1,2}(b) WHERE b.name='C') RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=((a)(()-[:knows]->())+(b))? RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2]")]
    [DataRow("MATCH p=ALL SHORTEST ((a)(()-[:knows]->()){1,2}(b))? RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2]")]
    [DataRow("MATCH p=ANY SHORTEST ((a)(()-[:knows]->()){1,2}(b) WHERE b.name='C')? WHERE b IS NOT NULL RETURN PATH_LENGTH(p) AS answer", "[1,2]")]
    [DataRow("MATCH p=(segment=(a)(()-[:knows]->()){1,2}(b))? RETURN PATH_LENGTH(segment) AS answer", "[null,null,null,1,1,2]")]
    [DataRow("MATCH p=(segment=(a)(()-[:knows]->()){1,2}(b) WHERE PATH_LENGTH(segment)=2)? RETURN PATH_LENGTH(p) AS answer", "[0,0,0,2]")]
    [DataRow("MATCH p=((a)(()-[:knows]->()){1,2}(b) |+| (a)(()-[:knows]->()){1,2}(b))? RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2,1,1,2]")]
    [DataRow("MATCH p=((a)(()-[:knows]->()){1,2}(b) | (a)(()-[:knows]->()){1,2}(b))? RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2]")]
    [DataRow("MATCH p=ANY SHORTEST (segment=(a)(()-[:knows]->()){1,2}(b) WHERE PATH_LENGTH(segment)=2) RETURN PATH_LENGTH(segment) AS answer", "[2]")]
    [DataRow("MATCH p=(outer=(inner=(a)(()-[:knows]->()){1,2}(b))) RETURN PATH_LENGTH(outer)-PATH_LENGTH(inner) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=(p=(a)(()-[:knows]->()){1,2}(b)) RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=(a=(a)(()-[:knows]->()){1,2}(b)) RETURN p AS answer", "ERROR")]
    public Task Gql_questioned_anonymous_repetitions_preserve_singletons_and_positive_minimum(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_questioned_anonymous_repetitions_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=((a:Person {name:'A'})(()-[:knows]->()){1,2}(c:Person {name:'B'}))? WHERE c IS NOT NULL", "a,c,p");

    [TestMethod]
    public Task Gql_questioned_repeated_subpath_alias_preserves_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(segment=(a:Person {name:'A'})(()-[:knows]->()){1,2}(c:Person {name:'B'}))? WHERE c IS NOT NULL", "a,c,p,segment");
}
