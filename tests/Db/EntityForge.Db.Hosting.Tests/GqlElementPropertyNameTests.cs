namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (a:Person {name:'A',`name`:'A'}) RETURN a.name AS answer", "ERROR")]
    [DataRow("MATCH p=((a:Person {name:'A',`name`:'A'})-[:knows]->(b)){1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=((a:Person {name:'A',`name`:'A'})-[:knows]->(b)){0} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(()-[e:knows {weight:1,`weight`:1}]->()){0} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH (:Missing) RETURN EXISTS { MATCH (a {name:'A',`name`:'A'}) } AS answer", "ERROR")]
    [DataRow("MATCH p=((a:Person {name:'A',`na\\u006de`:'A'})-[:knows]->(b)){0} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH (a:Person {name:'A'})(b:Person {name:'A'}) RETURN a.name AS answer", "[\"A\"]")]
    [DataRow("MATCH p=((a:Person {name:'A',Name:'Other'})-[:knows]->(b)){0} RETURN COUNT(*) AS answer", "[3]")]
    public Task Gql_element_property_names_are_validated_before_pattern_expansion(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
}
