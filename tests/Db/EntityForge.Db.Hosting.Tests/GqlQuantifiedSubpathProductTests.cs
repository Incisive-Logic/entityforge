namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH REPEATABLE ELEMENTS (segments=(a:Person)-[e:knows]->(b)){1},(other=(c:Person)-[f:knows]->(d)){1} FOR p IN segments FOR q IN other RETURN p=q AS answer", "[true,false,false,true]")]
    [DataRow("MATCH REPEATABLE ELEMENTS ANY SHORTEST (segments=(a:Person)-[e:knows]->(b)){1},ANY SHORTEST (other=(c:Person)-[f:knows]->(d)){1} RETURN COUNT(*) AS answer", "[4]")]
    [DataRow("LET segments=1 MATCH (segments=(a:Person)-[e:knows]->(b)){1},(n:Person) RETURN segments AS answer", "ERROR")]
    [DataRow("MATCH REPEATABLE ELEMENTS ANY SHORTEST (segments=(a:Person)-[e:knows]->(b)){1},(segments=(c:Person)-[f:knows]->(d)){1} RETURN segments AS answer", "ERROR")]
    [DataRow("MATCH (segments=(a:Person)-[e:knows]->(b)){1,2},(n:Person) RETURN SUM(CARDINALITY(segments)) AS answer", "[12]")]
    [DataRow("MATCH (segments=(a:Person)-[e:knows]->(b)){1},(other=(c:Person)-[f:knows]->(d)){1} RETURN CARDINALITY(segments)+CARDINALITY(other) AS answer", "[2,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS (segments=(a:Person)-[e:knows]->(b)){1},(other=(c:Person)-[f:knows]->(d)){1} RETURN CARDINALITY(segments)+CARDINALITY(other) AS answer", "[2,2,2,2]")]
    [DataRow("MATCH (segments=(a:Person)-[e:knows]->(b)){1,2},(n:Person) WHERE CARDINALITY(segments)=2 AND n.name='A' RETURN CARDINALITY(segments) AS answer", "[2]")]
    [DataRow("OPTIONAL MATCH (segments=(a:Person)-[e:knows]->(b)){1,2},(:Missing) RETURN segments AS answer", "[null]")]
    [DataRow("MATCH REPEATABLE ELEMENTS ANY SHORTEST (segments=(a:Person)-[e:knows]->(b)){1,2},(n:Person) RETURN SUM(CARDINALITY(segments)) AS answer", "[12]")]
    [DataRow("MATCH REPEATABLE ELEMENTS (other=(x:Person WHERE CARDINALITY(segments)=2)-[:knows]->()){1},(segments=(a:Person)-[e:knows]->(b)){1,2} RETURN CARDINALITY(segments) AS answer", "[2,2]")]
    [DataRow("MATCH (segments=(a:Person)-[e:knows]->(b)){1},(segments=(c:Person)-[f:knows]->(d)){1} RETURN segments AS answer", "ERROR")]
    public Task Gql_named_repeated_path_products_preserve_lists_dependencies_and_uniqueness(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
}
