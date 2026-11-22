namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (segments=ACYCLIC (:Person)-[:knows]->()){1,20} RETURN CARDINALITY(segments) AS answer", "[1,1,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS (segments=TRAIL (:Person)-[:knows]->()<-[:knows]-()){1} RETURN COUNT(*) AS answer", "[0]")]
    [DataRow("MATCH REPEATABLE ELEMENTS whole=TRAIL (segments=WALK (a:Person)-[e:knows]->(b)<-[f:knows]-(c)){1} RETURN COUNT(*) AS answer", "[0]")]
    public Task Gql_named_repeated_subpath_modes_support_anonymous_elements_and_outer_constraints(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("WALK", "[2]")]
    [DataRow("SIMPLE", "[2]")]
    [DataRow("TRAIL", "[0]")]
    [DataRow("ACYCLIC", "[0]")]
    public Task Gql_named_repeated_subpath_modes_preserve_local_identity_constraints(string mode, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(
            "MATCH REPEATABLE ELEMENTS (segments=" + mode + " (a:Person)-[e:knows]->(b)<-[f:knows]-(c)){1} RETURN COUNT(*) AS answer", expected);

    [TestMethod]
    [DataRow("MATCH (segments=ACYCLIC (a:Person)-[e:knows]->(b)){1,20} RETURN CARDINALITY(segments) AS answer", "[1,1,2]")]
    [DataRow("MATCH (segments=TRAIL (a:Person)-[e:knows]->(b)-[f:knows]->(c)){1,20} FOR p IN segments RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH (segments=(a:Person)-[e:knows]->(b) WHERE VALUE { RETURN PATH_LENGTH(segments) AS hops }=1){1,2} RETURN CARDINALITY(segments) AS answer", "[1,1,2]")]
    [DataRow("MATCH (segments=(a:Person)-[e:knows]->(b) WHERE VALUE { RETURN PATH_LENGTH(segments) AS hops }=1){1,20} RETURN CARDINALITY(segments) AS answer", "[1,1,2]")]
    [DataRow("MATCH (segments=(a:Person)-[e:knows]->(b) WHERE VALUE { CALL (segments) { RETURN PATH_LENGTH(segments) AS hops } RETURN hops }=1){1,2} RETURN CARDINALITY(segments) AS answer", "[1,1,2]")]
    [DataRow("MATCH (segments=(a:Person)-[e:knows]->(b) WHERE VALUE { CALL (segments) { RETURN PATH_LENGTH(segments) AS hops } RETURN hops }=1){1,20} RETURN CARDINALITY(segments) AS answer", "[1,1,2]")]
    [DataRow("MATCH (segments=(a:Person)-[e:knows]->(b)){1,2} RETURN VALUE { FOR p IN segments FOR n IN ELEMENTS(p) FILTER n IS TYPED NODE RETURN COLLECT_LIST(n.name) AS names } AS answer", "[[\"A\",\"B\"],[\"B\",\"C\"],[\"A\",\"B\",\"B\",\"C\"]]")]
    [DataRow("MATCH (segments=(a:Person)-[e:knows]->(b)){1,20} RETURN VALUE { FOR p IN segments FOR n IN ELEMENTS(p) FILTER n IS TYPED NODE RETURN COLLECT_LIST(n.name) AS names } AS answer", "[[\"A\",\"B\"],[\"B\",\"C\"],[\"A\",\"B\",\"B\",\"C\"]]")]
    [DataRow("MATCH (segments=(a:Person)-[e:knows]->(b) WHERE PATH_LENGTH(segments)=1){1,2} RETURN CARDINALITY(segments) AS answer", "[1,1,2]")]
    [DataRow("MATCH (segments=(a:Person)-[e:knows]->(b) WHERE PATH_LENGTH(segments)=1){1,20} RETURN CARDINALITY(segments) AS answer", "[1,1,2]")]
    [DataRow("MATCH (segments=(a:Person)-[e:knows]->(b) WHERE FALSE){0} RETURN CARDINALITY(segments) AS answer", "[0,0,0]")]
    [DataRow("MATCH ANY SHORTEST (segments=(a:Person)-[e:knows]->(b)){1,20} WHERE CARDINALITY(segments)>1 RETURN CARDINALITY(segments) AS answer", "[2]")]
    [DataRow("MATCH whole=(segments=(a:Person)-[e:knows]->(b)-[f:knows]->(c)){1,20} FOR p IN segments RETURN p=whole AS answer", "[true]")]
    [DataRow("LET segments=1 MATCH (segments=(a:Person)-[e:knows]->(b)){1,2} RETURN segments AS answer", "ERROR")]
    [DataRow("MATCH (segments=(a:Person)-[e:knows]->(b)){1,2} RETURN CARDINALITY(segments) AS answer", "[1,1,2]")]
    [DataRow("MATCH (segments=(a:Person)-[e:knows]->(b)){0} RETURN CARDINALITY(segments) AS answer", "[0,0,0]")]
    [DataRow("MATCH (segments=(a:Person)-[e:knows]->(b)){1,20} RETURN CARDINALITY(segments) AS answer", "[1,1,2]")]
    [DataRow("MATCH (segments=()-[:knows]->())+ RETURN CARDINALITY(segments) AS answer", "[1,1,2]")]
    [DataRow("OPTIONAL MATCH (segments=(a:Missing)-[e:knows]->(b)){1,2} RETURN segments AS answer", "[null]")]
    [DataRow("MATCH (segments=(a:Person)-[e:knows]->(b)){1,2} FOR p IN segments RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1]")]
    [DataRow("MATCH (a=(a:Person)-[e:knows]->(b)){1,2} RETURN a AS answer", "ERROR")]
    public Task Gql_quantified_subpaths_collect_one_ordered_path_per_repetition(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
}
