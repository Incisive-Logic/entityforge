namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (pieces=(:Person)-[:knows]->()){1}(tails=()<-[:knows]-()){1} RETURN COUNT(*) AS answer", "[0]")]
    [DataRow("MATCH REPEATABLE ELEMENTS (pieces=(:Person)-[:knows]->()){1}(tails=()<-[:knows]-()){1} RETURN COUNT(*) AS answer", "[2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS TRAIL (pieces=(:Person)-[:knows]->()){1}(tails=()<-[:knows]-()){1} RETURN COUNT(*) AS answer", "[0]")]
    [DataRow("MATCH (pieces=(:Person)-[:knows]->()){1}(middle=()-[:knows]->()){0}(tails=()-[:knows]->()){1} RETURN CARDINALITY(pieces)+CARDINALITY(middle)+CARDINALITY(tails) AS answer", "[2]")]
    [DataRow("MATCH (pieces=(:Person)-[:knows]->()){1}(joint:Person WHERE joint.name='B')(tails=()-[:knows]->()){1} RETURN CARDINALITY(pieces)+CARDINALITY(tails) AS answer", "[2]")]
    [DataRow("MATCH (pieces=(:Person)-[:knows]->()){1}(tails=()-[:knows]->()){1} WHERE CARDINALITY(pieces)=1 AND CARDINALITY(tails)=1 RETURN COUNT(*) AS answer", "[1]")]
    [DataRow("LET pieces=1 MATCH (pieces=(:Person)-[:knows]->()){1}(tails=()-[:knows]->()){1} RETURN pieces AS answer", "ERROR")]
    [DataRow("MATCH (pieces=(a:Person)-[e:knows]->(b)){1}(tails=(c)-[f:knows]->(d)){1} RETURN CARDINALITY(pieces)+CARDINALITY(tails) AS answer", "[2]")]
    [DataRow("MATCH whole=(pieces=(:Person)-[:knows]->()){1}(tails=()-[:knows]->()){1} RETURN PATH_LENGTH(whole) AS answer", "[2]")]
    [DataRow("MATCH (pieces=(:Person)-[:knows]->()){0}(tails=()-[:knows]->()){1} RETURN CARDINALITY(pieces) AS answer", "[0,0]")]
    [DataRow("MATCH (pieces=(:Person)-[:knows]->()){1}(tails=()-[:knows]->()){0} RETURN CARDINALITY(tails) AS answer", "[0,0]")]
    [DataRow("MATCH (pieces=(:Person)-[:knows]->()){0}(tails=()-[:knows]->()){0} RETURN CARDINALITY(pieces)+CARDINALITY(tails) AS answer", "[0,0,0]")]
    [DataRow("OPTIONAL MATCH (pieces=(:Missing)-[:knows]->()){1}(tails=()-[:knows]->()){1} RETURN pieces AS answer", "[null]")]
    [DataRow("MATCH ANY SHORTEST (pieces=(:Person)-[:knows]->()){1}(tails=()-[:knows]->()){1} RETURN CARDINALITY(pieces)+CARDINALITY(tails) AS answer", "[2]")]
    [DataRow("MATCH (pieces=(:Person)-[:knows]->()){1}(tails=()-[:knows]->()){1} FOR p IN pieces FOR q IN tails RETURN p=q AS answer", "[false]")]
    [DataRow("MATCH (pieces=(:Person)-[:knows]->()){1}(pieces=()-[:knows]->()){1} RETURN pieces AS answer", "ERROR")]
    public Task Gql_named_repeated_segments_preserve_each_list_and_joined_path(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
}
