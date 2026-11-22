namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public Task Gql_anonymous_product_edge_properties_preserve_correlation_and_scope() =>
        Gql_anonymous_repeated_label_unions_hide_internal_bindings(
            "MATCH REPEATABLE ELEMENTS p=(a:Person)(()-[:knows]->()){1}(b WHERE d.name='B'),q=(c)-[:knows {weight:a.rank}]->(d) RETURN *",
            "a,b,c,d,p,q", 1,
            "INSERT (:Person {name:'A',rank:1})-[:knows {weight:1}]->(:Person {name:'B',rank:2})-[:knows {weight:3}]->(:Person {name:'C',rank:4})");

    [TestMethod]
    [DataRow("MATCH p=(a:Person)(()-[:knows]->()){1}({name:c.name}),(c:Person WHERE a.name='A') RETURN *", "a,c,p", 1)]
    [DataRow("MATCH p=(a:Person {name:'A'})((WHERE c.name='A')-[:knows]->()){1,2}(b),(c:Person WHERE a.name='A') RETURN *", "a,b,c,p", 2)]
    [DataRow("LET __pathProduct=7 MATCH p=(a:Person)(()-[:knows]->()){1}(:Person {name:c.name}),(c:Person WHERE a.name='A') RETURN *", "__pathProduct,a,c,p", 1)]
    [DataRow("OPTIONAL MATCH p=(a:Person)(()-[:knows]->()){1}({name:c.missing}),(c:Person WHERE a.name='A') RETURN *", "a,c,p", 1)]
    public Task Gql_anonymous_product_properties_hide_generated_bindings(string query, string columns, int count) =>
        Gql_anonymous_repeated_label_unions_hide_internal_bindings(query, columns, count,
            "INSERT (:Person {name:'A'})-[:knows]->(:Person {name:'B'})-[:knows]->(:Person {name:'C'})");
}
