namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (segments=(inner=(:Person)-[:knows]->())){1}(tails=(other=()-[:knows]->())){1} RETURN segments=inner AND tails=other AS answer", "[true]")]
    [DataRow("MATCH REPEATABLE ELEMENTS ANY SHORTEST (segments=(inner=(:Person)-[:knows]->())){1},ANY SHORTEST (tails=(other=(:Person)-[:knows]->())){1} RETURN COUNT(*) AS answer", "[4]")]
    [DataRow("MATCH (segments=(inner=(:Person)-[:knows]->())){1},(tails=(other=(:Person)-[:knows]->())){1} RETURN COUNT(*) AS answer", "[2]")]
    [DataRow("MATCH (segments=(inner=(:Person)-[:knows]->()))+ RETURN segments=inner AS answer", "[true,true,true]")]
    [DataRow("MATCH (segments=(:Person)-[:knows]->(tail=()-[:knows]->())){1,20} FOR p IN tail RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH (segments=(:Person)-[:knows]->(tail=())){1,2} FOR p IN tail RETURN PATH_LENGTH(p) AS answer", "[0,0,0,0]")]
    [DataRow("MATCH REPEATABLE ELEMENTS (segments=(inner=TRAIL (:Person)-[:knows]->()<-[:knows]-())){1} RETURN COUNT(*) AS answer", "[0]")]
    [DataRow("MATCH REPEATABLE ELEMENTS (segments=(inner=SIMPLE (:Person)-[:knows]->()<-[:knows]-())){1} RETURN CARDINALITY(inner) AS answer", "[1,1]")]
    [DataRow("MATCH REPEATABLE ELEMENTS TRAIL (segments=(inner=WALK (:Person)-[:knows]->()<-[:knows]-())){1} RETURN COUNT(*) AS answer", "[0]")]
    [DataRow("MATCH (segments=(inner=ACYCLIC (:Person)-[:knows]->())){1,20} RETURN CARDINALITY(inner) AS answer", "[1,1,2]")]
    [DataRow("MATCH (segments=(segments=(:Person)-[:knows]->())){1} RETURN segments AS answer", "ERROR")]
    [DataRow("MATCH (segments=(inner=(:Person)-[:knows]->() WHERE inner=segments)){1,20} RETURN segments=inner AS answer", "[true,true,true]")]
    [DataRow("MATCH (segments=(inner=(a:Person)-[e:knows]->(b))){1,2} RETURN segments=inner AS answer", "[true,true,true]")]
    [DataRow("MATCH ((inner=(a:Person)-[e:knows]->(b))){1,20} RETURN CARDINALITY(inner) AS answer", "[1,1,2]")]
    [DataRow("MATCH (segments=(a:Person)-[e:knows]->(tail=(b)-[f:knows]->(c))){1} FOR p IN tail RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH (segments=(inner=(:Person)-[:knows]->())){0} RETURN CARDINALITY(inner)+CARDINALITY(segments) AS answer", "[0,0,0]")]
    [DataRow("OPTIONAL MATCH (segments=(inner=(:Missing)-[:knows]->())){1} RETURN inner AS answer", "[null]")]
    [DataRow("MATCH ANY SHORTEST (segments=(inner=(:Person)-[:knows]->() WHERE PATH_LENGTH(inner)=1)){1,2} RETURN segments=inner AS answer", "[true,true,true]")]
    [DataRow("MATCH (segments=(inner=(:Person)-[:knows]->() WHERE PATH_LENGTH(inner)=2)){1,20} RETURN COUNT(*) AS answer", "[0]")]
    [DataRow("MATCH (segments=(inner=(:Person)-[:knows]->() WHERE VALUE { CALL (inner) { RETURN PATH_LENGTH(inner) AS hops } RETURN hops }=1)){1,20} RETURN CARDINALITY(inner) AS answer", "[1,1,2]")]
    public Task Gql_nested_named_repeated_subpaths_preserve_lists_and_iteration_scope(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
}
