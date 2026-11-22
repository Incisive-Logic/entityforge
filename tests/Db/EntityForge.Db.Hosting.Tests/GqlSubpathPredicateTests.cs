namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH ANY SHORTEST (:Person)-[:knows]->(segment=()->() WHERE VALUE { CALL (segment) { RETURN PATH_LENGTH(segment) AS hops } RETURN hops }=1) RETURN PATH_LENGTH(segment) AS answer", "[1]")]
    [DataRow("MATCH (:Person)-[:knows]->(segment=()->() WHERE EXISTS { FOR item IN ELEMENTS(segment) FILTER item IS TYPED NODE RETURN item }) RETURN PATH_LENGTH(segment) AS answer", "[1]")]
    [DataRow("MATCH (:Person)-[:knows]->(segment=()->() WHERE VALUE { LET segment=9 RETURN segment }=9) RETURN PATH_LENGTH(segment) AS answer", "ERROR")]
    [DataRow("MATCH (:Person)-[:knows]->(outer=(inner=()->() WHERE inner=outer) WHERE PATH_LENGTH(outer)=1) RETURN outer=inner AS answer", "[true]")]
    [DataRow("MATCH (a:Person) CALL (a) { MATCH (a)-[:knows]->(segment=()->() WHERE PATH_LENGTH(segment)=1) RETURN segment } RETURN PATH_LENGTH(segment) AS answer", "[1]")]
    [DataRow("MATCH (:Person)-[:knows]->(segment=()->() WHERE PATH_LENGTH(segment)=1),(:Person) RETURN PATH_LENGTH(segment) AS answer", "[1,1,1]")]
    [DataRow("MATCH (:Person)-[:knows]->(segment=()->() WHERE PATH_LENGTH(segment)=1) WHERE PATH_LENGTH(segment)=2 RETURN COUNT(*) AS answer", "[0]")]
    [DataRow("MATCH (:Person)-[:knows]->(segment=()->() WHERE PATH_LENGTH(segment)=1) RETURN segment NEXT RETURN PATH_LENGTH(segment) AS answer", "[1]")]
    [DataRow("MATCH (:Person)-[:knows]->(segment=()-[:knows]->() WHERE PATH_LENGTH(segment)=1) RETURN PATH_LENGTH(segment) AS answer", "[1]")]
    [DataRow("MATCH (:Person)-[:knows]->(segment=()-[:knows]->() WHERE PATH_LENGTH(segment)=2) RETURN COUNT(*) AS answer", "[0]")]
    [DataRow("OPTIONAL MATCH (:Person)-[:knows]->(segment=()-[:knows]->() WHERE PATH_LENGTH(segment)=2) RETURN segment AS answer", "[null]")]
    [DataRow("MATCH ANY SHORTEST (:Person)-[:knows]->(segment=()-[:knows]->() WHERE PATH_LENGTH(segment)=1) RETURN PATH_LENGTH(segment) AS answer", "[1]")]
    [DataRow("MATCH ANY SHORTEST (:Person)-[:knows]->(segment=()-[:knows]->() WHERE PATH_LENGTH(segment)=2) RETURN COUNT(*) AS answer", "[0]")]
    [DataRow("MATCH (:Person)-[:knows]->(segment=()-[:knows]->() WHERE VALUE { CALL (segment) { RETURN PATH_LENGTH(segment) AS hops } RETURN hops }=1) RETURN PATH_LENGTH(segment) AS answer", "[1]")]
    [DataRow("MATCH (:Person)-[:knows]->(segment=(b WHERE PATH_LENGTH(segment)=1)-[:knows]->()) RETURN PATH_LENGTH(segment) AS answer", "[1]")]
    [DataRow("MATCH (:Person)-[:knows]->(outer=(inner=()->() WHERE inner=outer)) RETURN PATH_LENGTH(outer) AS answer", "[1]")]
    public Task Gql_proper_subpath_predicates_bind_paths_before_selection(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
}
