namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    // These rejection cases pin the current unsupported boundary, not the
    // required semantics of the optional horizontal aggregation feature.
    [TestMethod]
    [DataRow("MATCH ((a:Person)-[e:knows]->(b)){1,2} LET total=COUNT(e) RETURN total AS answer", "ERROR")]
    [DataRow("MATCH ((a:Person)-[e:knows]->(b)){1,2} FILTER COUNT(e)>1 RETURN 1 AS answer", "ERROR")]
    [DataRow("MATCH ((a:Person)-[e:knows]->(b)){1,2} LET total=VALUE { FOR edge IN e RETURN COUNT(edge) AS total } RETURN total AS answer", "[1,1,2]")]
    public Task Gql_horizontal_aggregate_boundary_is_distinct_from_explicit_list_iteration(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
}
