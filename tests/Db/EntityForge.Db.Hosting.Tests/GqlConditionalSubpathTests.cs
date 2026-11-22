namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(segment=(x)-[:knows]->(y))? RETURN PATH_LENGTH(segment) AS answer", "[1,1,null,null,null]")]
    [DataRow("MATCH (segment=(x)-[:knows]->(y))? RETURN PATH_LENGTH(segment) AS answer", "[1,1,null,null,null]")]
    [DataRow("MATCH p=(segment=(x)-[:knows]->(y))? WHERE segment IS NULL RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=(leftPath=(x)-[:knows]->(y)) | (rightPath=(x)-[:knows]->(y)) RETURN COALESCE(PATH_LENGTH(leftPath),PATH_LENGTH(rightPath)) AS answer", "[1,1,1,1]")]
    [DataRow("MATCH p=(segment=(x)-[:knows]->(y)) | (segment=(x)-[:knows]->(y)) RETURN PATH_LENGTH(segment) AS answer", "[1,1]")]
    [DataRow("MATCH p=(segment=(x)-[:knows]->(y)) |+| (segment=(x)-[:knows]->(y)) RETURN PATH_LENGTH(segment) AS answer", "[1,1,1,1]")]
    [DataRow("MATCH p=ALL SHORTEST (segment=(x)-[:knows]->(y))? RETURN PATH_LENGTH(segment) AS answer", "[1,1,null,null,null]")]
    [DataRow("OPTIONAL MATCH p=(segment=(x)-[:Missing]->(y))? WHERE segment IS NOT NULL RETURN segment AS answer", "[null]")]
    [DataRow("MATCH p=(x=(x)-[:knows]->(y))? RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=(p=(x)-[:knows]->(y))? RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=(a:Person {name:'A'})(segment=(x)-[:knows]->(y))?(b) RETURN PATH_LENGTH(segment) AS answer", "[1,null]")]
    [DataRow("MATCH p=(segment=(x)-[:knows]->(y) WHERE PATH_LENGTH(segment)=1 AND x.name='A')? RETURN PATH_LENGTH(segment) AS answer", "[1,null,null,null]")]
    [DataRow("MATCH p=(outerPath=(innerPath=(x)-[:knows]->(y))-[:knows]->(z))? RETURN PATH_LENGTH(innerPath) AS answer", "[1,null,null,null]")]
    [DataRow("MATCH p=(segment=(x)-[:knows]->(y))? RETURN VALUE { RETURN PATH_LENGTH(segment) AS hops } AS answer", "[1,1,null,null,null]")]
    public Task Gql_conditional_subpaths_preserve_path_values_nulls_and_multiplicity(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH p=(segment=(c:Person {name:'B'})-[:knows]->(b))? WHERE segment IS NOT NULL", "b,c,p,segment")]
    [DataRow("MATCH (segment=(c:Person {name:'B'})-[:knows]->(b))? WHERE segment IS NOT NULL", "b,c,segment")]
    public Task Gql_conditional_subpaths_preserve_update_scope_rollback_and_replay(string pattern, string columns) =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(pattern, columns);
}
