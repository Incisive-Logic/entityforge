namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(a)(()-[:knows]->()){1,2}() | ()(()-[:knows]->()){1,2}(b) RETURN a.name AS answer", "[\"A\",\"A\",\"B\",null,null,null]")]
    [DataRow("MATCH p=(a)(()-[:knows]->()){1,2}() | ()(()-[:knows]->()){1,2}(b) WHERE a IS NULL RETURN b.name AS answer", "[\"B\",\"C\",\"C\"]")]
    [DataRow("MATCH p=(a)(()-[e:knows]->()){1,2}() | ()(()-[e:knows]->()){1,2}(b) RETURN CARDINALITY(e) AS answer", "[1,1,2,1,1,2]")]
    [DataRow("MATCH p=(a)(segment=()-[:knows]->()){1,2}() | ()(segment=()-[:knows]->()){1,2}(b) RETURN CARDINALITY(segment) AS answer", "[1,1,2,1,1,2]")]
    [DataRow("MATCH p=(a)(()-[:knows]->()){0}() | ()(()-[:knows]->()){0}(b) RETURN a.name AS answer", "[\"A\",\"B\",\"C\",null,null,null]")]
    [DataRow("MATCH p=(a)(()-[e:knows]->()){1}() | ()(()-[:knows]->()){1}(b) RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=(a)(()-[:knows]->()){1}() |+| (a)(()-[:knows]->()){1}() |+| ()(()-[:knows]->()){1}(b) RETURN a.name AS answer", "[\"A\",\"B\",\"A\",\"B\",null,null]")]
    [DataRow("MATCH p=(a)(()-[:knows]->()){1}() | (a)(()-[:knows]->()){1}() | ()(()-[:knows]->()){1}(b) RETURN a.name AS answer", "[\"A\",\"B\",null,null]")]
    [DataRow("OPTIONAL MATCH p=(a)(()-[:Missing]->()){1}() | ()(()-[:Missing]->()){1}(b) RETURN a AS answer", "[null]")]
    [DataRow("MATCH p=ALL SHORTEST (a)(()-[:knows]->()){1,2}() | ()(()-[:knows]->()){1,2}(b) WHERE a IS NULL RETURN b.name AS answer", "[\"B\",\"C\",\"C\"]")]
    public Task Gql_repeated_alternatives_null_extend_only_conditional_singletons(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_conditional_repeated_alternatives_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(c:Person {name:'B'})(()-[:knows]->()){1}(b) | (a:Person {name:'Missing'})(()-[:knows]->()){1}(b)", "a,b,c,p");
}
