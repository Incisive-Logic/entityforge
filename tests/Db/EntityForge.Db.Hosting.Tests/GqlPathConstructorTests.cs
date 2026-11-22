namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(a:Person)-[e:knows]->(b) RETURN PATH[a,e,b]=p AS answer", "[true,true]")]
    [DataRow("MATCH (a:Person) RETURN PATH_LENGTH(PATH[a]) AS answer", "[0,0,0]")]
    [DataRow("MATCH (a:Person)-[e:knows]->(b) RETURN PATH_LENGTH(PATH[b,e,a]) AS answer", "[1,1]")]
    [DataRow("MATCH (a:Person)-[e:knows]->(b) RETURN PATH_LENGTH(PATH[a,e,b,e,a]) AS answer", "[2,2]")]
    [DataRow("MATCH (a:Person)-[e:knows]->(b)-[f:knows]->(c) RETURN PATH_LENGTH(PATH[a,e,b,f,c]) AS answer", "[2]")]
    [DataRow("MATCH (a:Person)-[e:knows]->(b) LET p=PATH[a,e,b] RETURN p NEXT RETURN PATH_LENGTH(p) AS answer", "[1,1]")]
    [DataRow("MATCH (a:Person)-[e:knows]->(b) RETURN COUNT(DISTINCT PATH[a,e,b]) AS answer", "[2]")]
    [DataRow("MATCH (a:Person)-[e:knows]->(b) RETURN PATH[CASE WHEN TRUE THEN a ELSE b END,e,b] IS TYPED PATH AS answer", "[true,true]")]
    [DataRow("MATCH (a:Person)-[e:knows]->(b) RETURN VALUE { FOR n IN ELEMENTS(PATH[a,e,b]) FILTER n IS TYPED NODE RETURN COLLECT_LIST(n.name) AS names } AS answer", "[[\"A\",\"B\"],[\"B\",\"C\"]]")]
    [DataRow("OPTIONAL MATCH (a:Missing)-[e:knows]->(b) RETURN PATH[a,e,b] AS answer", "[null]")]
    [DataRow("OPTIONAL MATCH (a:Missing) RETURN PATH[a] IS NULL AS answer", "[true]")]
    [DataRow("MATCH (a:Person)-[e:knows]->(b)-[f:knows]->(c) RETURN PATH[a,f,c] AS answer", "ERROR")]
    [DataRow("MATCH (a:Person)-[e:knows]->(b) RETURN PATH[e] AS answer", "ERROR")]
    [DataRow("MATCH (a:Person)-[e:knows]->(b) RETURN PATH[a,a,b] AS answer", "ERROR")]
    [DataRow("MATCH (a:Person)-[e:knows]->(b) RETURN PATH[a,e] AS answer", "ERROR")]
    [DataRow("RETURN PATH[] AS answer", "ERROR")]
    [DataRow("RETURN PATH[1] AS answer", "ERROR")]
    [DataRow("MATCH (a:Person)-[e:knows]->(b) WHERE PATH_LENGTH(PATH[a,e,b])=1 RETURN a.name AS answer", "[\"A\",\"B\"]")]
    [DataRow("MATCH (a:Person)-[e:knows]->(b) WHERE PATH_LENGTH(PATH[a,e,b])=2 RETURN a.name AS answer", "[]")]
    [DataRow("MATCH (a:Person)-[e:knows]->(b) RETURN VALUE { RETURN PATH_LENGTH(PATH[a,e,b]) AS hops } AS answer", "[1,1]")]
    [DataRow("MATCH (a:Person)-[e:knows]->(b) CALL (a,e,b) { RETURN PATH[a,e,b] AS route } RETURN PATH_LENGTH(route) AS answer", "[1,1]")]
    [DataRow("MATCH (a:Person)-[e:knows]->(b) CALL () { RETURN PATH[a,e,b] AS route } RETURN route AS answer", "ERROR")]
    [DataRow("MATCH (a:Person)-[e:knows]->(b) RETURN PATH[missing,e,b] AS answer", "ERROR")]
    [DataRow("MATCH (a:Person)-[e:knows]->(b) RETURN PATH[a,e,b]=PATH[b,e,a] AS answer", "[false,false]")]
    [DataRow("MATCH (a:Person)-[e:knows]->(b) RETURN CAST(PATH[a,e,b] AS PATH) IS TYPED PATH AS answer", "[true,true]")]
    public Task Gql_path_value_construction_preserves_elements_and_rejects_invalid_paths(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
}
