namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("WALK", "[\"bad\",\"good\"]")]
    [DataRow("SIMPLE", "[\"bad\",\"good\"]")]
    [DataRow("TRAIL", "[]")]
    [DataRow("ACYCLIC", "[]")]
    public Task Gql_local_quantified_path_modes_enforce_identity_after_an_exterior_edge(string mode, string expected) =>
        Gql_standard_selective_predicates_filter_candidates_before_selection(
            "MATCH REPEATABLE ELEMENTS (a:SelectStart)-[:choose]->(" + mode +
            " (m)-[:choose]-{2}(b) WHERE b=m) RETURN b.name AS answer ORDER BY answer", expected);

    [TestMethod]
    [DataRow("CALL () { MATCH (a:Person {name:'A'}) RETURN a } MATCH p=(a)(()-[:knows]->() |+| ()-[:knows]->()-[:knows]->()){1,2}(b) RETURN *", "a,p,b", 3)]
    [DataRow("CALL () { MATCH (a:Person {name:'C'}) RETURN a } MATCH p=(a)(()-[:knows]->() |+| ()-[:knows]->()-[:knows]->()){1,2}(b) RETURN *", "a,p,b", 0)]
    [DataRow("CALL () { MATCH (a:Person {name:'A'}) RETURN a } MATCH (a)-[:knows]->(ACYCLIC ()-[:knows]->()) RETURN *", "a", 1)]
    [DataRow("CALL () { MATCH (a:Person {name:'C'}) RETURN a } MATCH (a)-[:knows]->(ACYCLIC ()-[:knows]->()) RETURN *", "a", 0)]
    public Task Gql_alternative_and_subpath_rewrites_preserve_call_outputs(string query, string columns, int count) =>
        Gql_anonymous_repeated_label_unions_hide_internal_bindings(query, columns, count,
            "INSERT (:Person {name:'A'})-[:knows]->(:Person {name:'B'})-[:knows]->(:Person {name:'C'})");

    [TestMethod]
    [DataRow("CALL () { MATCH (a:Person {name:'A'}) RETURN a } MATCH (a)(alias)-[e:knows]->(b) RETURN *", "a,alias,e,b", 1)]
    [DataRow("CALL () { MATCH (a:Person {name:'C'}) RETURN a } MATCH (a)(alias)-[e:knows]->(b) RETURN *", "a,alias,e,b", 0)]
    [DataRow("CALL () { MATCH (a:Person {name:'C'}) RETURN a } OPTIONAL MATCH (a)(alias)-[e:knows]->(b) RETURN *", "a,alias,e,b", 1)]
    [DataRow("CALL () { MATCH (a:Person {name:'A'}) RETURN a } LET wanted='A' MATCH ANY (a:Person {name:wanted})-[e:knows WHERE TRUE]->(b) RETURN *", "a,wanted,e,b", 1)]
    [DataRow("CALL () { MATCH (a:Person {name:'A'}) RETURN a } LET wanted='Missing' OPTIONAL MATCH ANY (a:Person {name:wanted})-[e:knows WHERE TRUE]->(b) RETURN *", "a,wanted,e,b", 1)]
    [DataRow("CALL () { MATCH (a:Person {name:'A'}) RETURN a } MATCH (a)(()-[e:knows]->()){1}(middle)(()-[f:knows]->()){1}(b) RETURN *", "a,e,middle,f,b", 1)]
    [DataRow("CALL () { MATCH (a:Person {name:'C'}) RETURN a } MATCH (a)(()-[e:knows]->()){1}(middle)(()-[f:knows]->()){1}(b) RETURN *", "a,e,middle,f,b", 0)]
    [DataRow("CALL () { MATCH (a:Person {name:'A'}) RETURN a } MATCH ANY SHORTEST (a)(()-[e:knows]->()){1}(middle)(()-[f:knows]->()){1}(b) RETURN *", "a,e,middle,f,b", 1)]
    [DataRow("CALL () { FOR duplicate IN [1,1] MATCH (a:Person {name:'A'}) RETURN a } MATCH (a)(()-[e:knows]->()){1}(middle)(()-[f:knows]->()){1}(b) RETURN *", "a,e,middle,f,b", 2)]
    [DataRow("CALL () { MATCH (a:Person {name:'A'}) RETURN a } MATCH (a)((:Person)-[:knows|Absent]->()){1,2}(b) RETURN *", "a,b", 2)]
    public Task Gql_path_rewrites_preserve_call_output_bindings(string query, string columns, int count) =>
        Gql_anonymous_repeated_label_unions_hide_internal_bindings(query, columns, count,
            "INSERT (:Person {name:'A'})-[:knows]->(:Person {name:'B'})-[:knows]->(:Person {name:'C'})");

    [TestMethod]
    [DataRow("A", 1)]
    [DataRow("C", 0)]
    public Task Gql_graph_exists_preserves_call_endpoint_identity(string name, int count) =>
        Gql_anonymous_repeated_label_unions_hide_internal_bindings(
            "CALL () { MATCH (a:Person {name:'" + name + "'}) RETURN a } " +
            "FILTER EXISTS { MATCH (a)(()-[:knows]->()){1,2}(b) } RETURN *", "a", count,
            "INSERT (:Person {name:'A'})-[:knows]->(:Person {name:'B'})-[:knows]->(:Person {name:'C'})");

    [TestMethod]
    [DataRow("CALL () { MATCH (a:Person {name:'A'}) RETURN a } MATCH (a)-[e:knows WHERE TRUE]->{1,2}(b) RETURN *", 2)]
    [DataRow("CALL () { MATCH (a:Person {name:'A'}),(b:Person {name:'C'}) RETURN a,b } MATCH (a)-[e:knows WHERE TRUE]->{1,2}(b) RETURN *", 1)]
    [DataRow("CALL () { MATCH (a:Person {name:'C'}) RETURN a } OPTIONAL MATCH (a)-[e:knows WHERE TRUE]->{1,2}(b) RETURN *", 1)]
    public Task Gql_quantified_predicates_correlate_call_endpoint_results(string query, int count) =>
        Gql_anonymous_repeated_label_unions_hide_internal_bindings(query, "a,e,b", count,
            "INSERT (:Person {name:'A'})-[:knows]->(:Person {name:'B'})-[:knows]->(:Person {name:'C'})");

    [TestMethod]
    [DataRow("CALL () { FOR duplicate IN [1,1] MATCH (a)-[e:knows]->(b) RETURN a,e,b } MATCH ANY (a)-[e WHERE TRUE]->(b) RETURN *", "a,e,b", 4)]
    [DataRow("CALL () { FOR duplicate IN [1,1] MATCH (a)-[e:knows]->(b) RETURN a,e,b } OPTIONAL MATCH ANY (a)-[e WHERE FALSE]->(b) RETURN *", "a,e,b", 4)]
    [DataRow("CALL () { RETURN NULL AS a,NULL AS e,NULL AS b } MATCH ANY (a)-[e WHERE TRUE]->(b) RETURN *", "a,e,b", 0)]
    [DataRow("CALL () { RETURN NULL AS a,NULL AS e,NULL AS b } OPTIONAL MATCH ANY (a)-[e WHERE TRUE]->(b) RETURN *", "a,e,b", 1)]
    [DataRow("LET __anonymousPath=7 CALL () { MATCH (a)-[e:knows]->(b) RETURN a,e,b } MATCH ANY (a)-[e WHERE TRUE]->(b) RETURN *", "__anonymousPath,a,e,b", 2)]
    [DataRow("CALL () { MATCH (a)-[e:knows]->(b) RETURN a,e,b } MATCH ANY (a)-[e WHERE TRUE]->(b) WHERE FALSE RETURN *", "a,e,b", 0)]
    public Task Gql_fully_bound_call_matches_preserve_bags_and_visible_columns(string query, string columns, int count) =>
        Gql_anonymous_repeated_label_unions_hide_internal_bindings(query, columns, count);

    [TestMethod]
    [DataRow("", "[\"good\"]")]
    [DataRow("OPTIONAL ", "[\"bad\",\"good\"]")]
    public Task Gql_selective_predicates_accept_fully_bound_call_results(string optional, string expected) =>
        Gql_standard_selective_predicates_filter_candidates_before_selection(
            "CALL () { MATCH (a:SelectStart)-[e:choose]->(m)-[f:tailLink]->(d:SelectEnd) RETURN a,e,m,f,d } " +
            optional + "MATCH ANY (a)-[e WHERE e.weight>1]->(m)-[f]->(d) RETURN m.name AS answer ORDER BY answer",
            expected);

    [TestMethod]
    [DataRow("CALL () { MATCH (origin:SelectStart) RETURN origin AS a } MATCH ANY (a)-[e:choose WHERE e.weight>1]->(m)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("CALL () { MATCH (middle:SelectMiddle {name:'good'}) RETURN middle AS m } MATCH ANY (a:SelectStart)-[e:choose WHERE e.weight>1]->(m)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("CALL () { MATCH (middle:SelectMiddle {name:'bad'}) RETURN middle AS m } MATCH ANY (a:SelectStart)-[e:choose WHERE e.weight>1]->(m)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[]")]
    [DataRow("CALL () { MATCH (middle:SelectMiddle {name:'bad'}) RETURN middle AS m } OPTIONAL MATCH ANY (a:SelectStart)-[e:choose WHERE e.weight>1]->(m)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[\"bad\"]")]
    [DataRow("CALL () { RETURN NULL AS a } MATCH ANY (a)-[e:choose WHERE e.weight>1]->(m)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[]")]
    [DataRow("CALL () { FOR duplicate IN [1,1] MATCH (origin:SelectStart) RETURN origin AS a } MATCH ANY (a)-[e:choose WHERE e.weight>1]->(m)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[\"good\",\"good\"]")]
    [DataRow("CALL () { MATCH ()-[edge:choose]->() WHERE edge.weight=2 RETURN edge AS e } MATCH ANY (a:SelectStart)-[e WHERE e.weight>1]->(m)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("CALL () { MATCH (origin:SelectStart) RETURN origin AS a } OPTIONAL MATCH ANY (a)-[e:choose WHERE e.weight>10]->(m)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[null]")]
    public Task Gql_selective_predicates_correlate_elements_returned_by_inline_calls(string query, string expected) =>
        Gql_standard_selective_predicates_filter_candidates_before_selection(query, expected);

    [TestMethod]
    [DataRow("WALK")]
    [DataRow("TRAIL")]
    [DataRow("SIMPLE")]
    [DataRow("ACYCLIC")]
    public Task Gql_bound_selective_predicates_preserve_inner_path_modes(string mode) =>
        Gql_standard_selective_predicates_filter_candidates_before_selection(
            "MATCH (a:SelectStart) MATCH ANY (" + mode +
            " (a)-[e:choose]->(m)-[f:tailLink]->(d:SelectEnd) WHERE m.score>1) RETURN m.name AS answer",
            "[\"good\"]");

    [TestMethod]
    [DataRow("WALK", "[\"good\"]")]
    [DataRow("TRAIL", "[]")]
    [DataRow("SIMPLE", "[\"good\"]")]
    [DataRow("ACYCLIC", "[]")]
    public Task Gql_bound_selective_predicates_enforce_inner_path_modes(string mode, string expected) =>
        Gql_standard_selective_predicates_filter_candidates_before_selection(
            "MATCH (a:SelectStart) MATCH REPEATABLE ELEMENTS ANY (" + mode +
            " (a)-[e:choose]->(m)<-[f:choose]-(b) WHERE b=a AND m.score>1) RETURN m.name AS answer",
            expected);

    [TestMethod]
    [DataRow("WALK", "[2]")]
    [DataRow("TRAIL", "[]")]
    [DataRow("SIMPLE", "[2]")]
    [DataRow("ACYCLIC", "[]")]
    public Task Gql_bound_named_selective_predicates_enforce_inner_path_modes(string mode, string expected) =>
        Gql_standard_selective_predicates_filter_candidates_before_selection(
            "MATCH (a:SelectStart) MATCH REPEATABLE ELEMENTS p=ANY (" + mode +
            " (a)-[e:choose]->(m)<-[f:choose]-(b) WHERE b=a AND m.score>1) RETURN PATH_LENGTH(p) AS answer",
            expected);
}
