namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(segment=()-[:knows]->()){1,2} | (segment=()-[:knows]->()){1,2} RETURN CARDINALITY(segment) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(segment=()-[:knows]->()){1,2} |+| (segment=()-[:knows]->()){1,2} RETURN CARDINALITY(segment) AS answer", "[1,1,2,1,1,2]")]
    [DataRow("MATCH p=(segment=()-[:knows]->()){0,1} | (segment=()-[:knows]->()){0,1} RETURN CARDINALITY(segment) AS answer", "[0,0,0,1,1]")]
    [DataRow("MATCH p=(segment=()-[:knows]->()){1} | (segment=()-[:knows]->()) RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=(segment=()-[:knows]->()) | (segment=()-[:knows]->()){1} RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=(segment=()-[:knows]->()){1} | ()-[:knows]->() RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=(segment=()-[:knows]->()){1,2} | (segment=()-[:knows]->()){1,2} RETURN VALUE { FOR s IN segment LET name=VALUE { FOR n IN ELEMENTS(s) WITH OFFSET i FILTER i=0 RETURN n.name AS name } RETURN COLLECT_LIST(name) AS names } AS answer", "[[\"A\"],[\"B\"],[\"A\",\"B\"]]")]
    [DataRow("MATCH p=ALL SHORTEST (segment=()-[:knows]->()){1,2} | (segment=()-[:knows]->()){1,2} RETURN CARDINALITY(segment) AS answer", "[1,1,2]")]
    [DataRow("OPTIONAL MATCH p=(segment=()-[:Missing]->()){1} | (segment=()-[:Missing]->()){1} RETURN segment AS answer", "[null]")]
    public Task Gql_repeated_subpath_alternatives_preserve_group_exposure_and_multiplicity(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_repeated_subpath_alternatives_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(c:Person {name:'B'})(segment=()-[:knows]->()){1}(b) | (c:Person {name:'B'})(segment=()-[:knows]->()){1}(b)", "b,c,p,segment");
}
