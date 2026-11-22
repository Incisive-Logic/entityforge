namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("WALK")]
    [DataRow("TRAIL")]
    [DataRow("SIMPLE")]
    [DataRow("ACYCLIC")]
    public Task Gql_quantified_subpath_modes_preserve_selected_candidates(string mode) =>
        Gql_standard_selective_predicates_filter_candidates_before_selection(
            "MATCH p=ALL SHORTEST (" + mode +
            " (a:SelectStart)-[edges:choose]->{1,2}(m) WHERE m.score>1)-[:tailLink]->(d:SelectEnd)" +
            " RETURN PATH_LENGTH(p)+SIZE(edges) AS answer", "[3]");

    [TestMethod]
    [DataRow("WALK", "[3,3]")]
    [DataRow("TRAIL", "[]")]
    [DataRow("SIMPLE", "[3,3]")]
    [DataRow("ACYCLIC", "[]")]
    public Task Gql_quantified_subpath_trail_rejects_reused_relationships(string mode, string expected) =>
        Gql_standard_selective_predicates_filter_candidates_before_selection(
            "MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (" + mode +
            " (a:SelectStart)-[edges:choose]-{2}(b) WHERE b=a)-[:choose]->(m WHERE m.score>1)" +
            " RETURN PATH_LENGTH(p) AS answer", expected);

    [TestMethod]
    [DataRow("(TRAIL (a:SelectStart)-[edges:choose]->{1}(m)<-[back:choose]-(b) WHERE b=a)", "[]")]
    [DataRow("(TRAIL (a:SelectStart)-[edges:choose]->{1}(m) WHERE m.score>1)<-[back:choose]-(b)", "[2]")]
    [DataRow("(TRAIL (a:SelectStart)-[edges:choose]->{0}(b) WHERE b=a)-[:choose]->(m WHERE m.score>1)", "[1]")]
    [DataRow("(TRAIL (a:SelectStart)-[edges:choose]->{1}(m)<-[back:choose]-{1}(b) WHERE b=a)", "[]")]
    [DataRow("(TRAIL (a:SelectStart)-[:choose]->{1}(m)<-[:choose]-(b) WHERE b=a)", "[]")]
    [DataRow("(TRAIL (a:SelectStart)-[:choose]->{1}(m) WHERE m.score>1)<-[:choose]-(b)", "[2]")]
    [DataRow("(TRAIL (a:SelectStart)-[:choose]->{0}(b) WHERE b=a)-[:choose]->(m WHERE m.score>1)", "[1]")]
    public Task Gql_quantified_subpath_trail_scopes_uniqueness_to_all_local_edges(string pattern, string expected) =>
        Gql_standard_selective_predicates_filter_candidates_before_selection(
            "MATCH REPEATABLE ELEMENTS p=ALL SHORTEST " + pattern + " RETURN PATH_LENGTH(p) AS answer", expected);

    [TestMethod]
    [DataRow("SIMPLE", 4, "[]")]
    [DataRow("ACYCLIC", 4, "[]")]
    [DataRow("SIMPLE", 0, "[1]")]
    [DataRow("ACYCLIC", 0, "[1]")]
    public Task Gql_quantified_subpath_node_modes_check_internal_repetition_and_empty_paths(string mode, int length, string expected) =>
        Gql_standard_selective_predicates_filter_candidates_before_selection(
            "MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (" + mode +
            " (a:SelectStart)-[edges:choose]-{" + length + "}(b) WHERE b=a)-[:choose]->(m WHERE m.score>1)" +
            " RETURN PATH_LENGTH(p) AS answer", expected);

    [TestMethod]
    [DataRow("(ACYCLIC ((a:SelectStart)-[edges:choose]->{1,2}(m) WHERE m.score>1))-[:tailLink]->(d:SelectEnd)", "[2]")]
    [DataRow("(SIMPLE ((a:SelectStart)-[edges:choose]-{2}(b) WHERE b=a))-[:choose]->(m WHERE m.score>1)", "[3,3]")]
    [DataRow("(ACYCLIC ((a:SelectStart)-[edges:choose]-{2}(b) WHERE b=a))-[:choose]->(m WHERE m.score>1)", "[]")]
    [DataRow("(ACYCLIC ((a:SelectStart)-[:choose]->{1,2}(m) WHERE m.score>1))-[:tailLink]->(d:SelectEnd)", "[2]")]
    [DataRow("(SIMPLE (TRAIL (a:SelectStart)-[edges:choose]-{2}(b) WHERE b=a))-[:choose]->(m WHERE m.score>1)", "[]")]
    [DataRow("(ACYCLIC (WALK (a:SelectStart)-[edges:choose]-{2}(b) WHERE b=a))-[:choose]->(m WHERE m.score>1)", "[]")]
    public Task Gql_quantified_subpath_modes_preserve_nested_group_predicates(string pattern, string expected) =>
        Gql_standard_selective_predicates_filter_candidates_before_selection(
            "MATCH REPEATABLE ELEMENTS p=ALL SHORTEST " + pattern + " RETURN PATH_LENGTH(p) AS answer", expected);
}
