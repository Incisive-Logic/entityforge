using EntityForge.Db.Gql;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(() -> () |+| () -> ()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,2,2,2,2]")]
    [DataRow("MATCH p=(() <- () |+| () <- ()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,2,2,2,2]")]
    [DataRow("MATCH p=(() -> () |+| ()-[:Missing]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(()-[:Missing]->() |+| () -> ()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(() -> () |+| () <- ()){1} RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1]")]
    [DataRow("MATCH p=(() ~> () |+| () -> ()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,2,2,2,2]")]
    [DataRow("MATCH p=(() <~ () |+| () <- ()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,2,2,2,2]")]
    [DataRow("MATCH p=(() <-> () |+| () - ()){1} RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,1,1,1,1]")]
    [DataRow("MATCH p=(() -> () |+| () -> ())+ RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,2,2,2,2]")]
    [DataRow("MATCH p=(() <- () |+| () <- ()){1,24} RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,2,2,2,2]")]
    [DataRow("MATCH p=((a) -> (b) |+| (a) -> (b)){0} RETURN CARDINALITY(a) AS answer", "[0,0,0]")]
    public Task Gql_repeated_bags_accept_standard_abbreviated_edges(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("~")]
    [DataRow("~[]~")]
    public void Gql_repeated_bags_report_unsupported_exclusively_undirected_edges(string edge)
    {
        var error = Assert.Throws<ArgumentException>(() => GraphGqlStandardSyntax.Prepare(
            "MATCH p=(() " + edge + " () |+| () -> ()){1,2} RETURN p"));
        StringAssert.Contains(error.Message, "exclusively undirected");
    }

    [TestMethod]
    public Task Gql_repeated_abbreviated_bags_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(c:Person {name:'B'})(() -> () |+| ()-[:Missing]->()){1}()", "c,p");
}
