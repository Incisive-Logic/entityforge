namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("TRAIL")]
    [DataRow("SIMPLE")]
    [DataRow("ACYCLIC")]
    public Task Gql_named_variable_subpaths_preserve_values_and_prefilter_scope(string mode) =>
        Gql_standard_selective_predicates_filter_candidates_before_selection(
            "MATCH p=ALL SHORTEST (s=" + mode +
            " (a:SelectStart)-[edges:choose]->{1,2}(m) WHERE m.score>1 AND PATH_LENGTH(s)=1)-[:tailLink]->(d:SelectEnd)" +
            " RETURN PATH_LENGTH(s)+PATH_LENGTH(p) AS answer", "[3]");

    [TestMethod]
    [DataRow("MATCH p=ALL SHORTEST (s=(a:SelectStart)-[edges:choose]->{0}(b))-[:choose]->(m WHERE m.score>1) RETURN PATH_LENGTH(s) AS answer", "[0]")]
    [DataRow("OPTIONAL MATCH p=ALL SHORTEST (s=(a:SelectStart)-[edges:choose]->{1,2}(m) WHERE m.score>99)-[:tailLink]->(d) RETURN s IS NULL AS answer", "[true]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (s=(m WHERE m.score>1)<-[edges:choose]-{1,2}(a:SelectStart))-[back:choose]->(n WHERE n.score>1) RETURN s=PATH[m,back,a] AS answer", "[true]")]
    [DataRow("MATCH p=ALL SHORTEST (s=(a:SelectStart)-[:choose]->{1,2}(m) WHERE m.score>1)-[:tailLink]->(d) CALL (s) { RETURN PATH_LENGTH(s) AS n } RETURN n AS answer", "[1]")]
    public Task Gql_named_variable_subpaths_preserve_empty_optional_reverse_and_imports(string query, string expected) =>
        Gql_standard_selective_predicates_filter_candidates_before_selection(query, expected);

    [TestMethod]
    public Task Gql_path_assembly_remains_compiler_internal() =>
        Gql_path_value_construction_preserves_elements_and_rejects_invalid_paths("RETURN __GQL_PATH_FROM_EDGES(NULL,[]) AS answer", "ERROR");

    [TestMethod]
    [DataRow("départ", "liens", "chemin")]
    [DataRow("__namedSubpathElement0", "__namedSubpathElementAssembly0", "__namedSubpathElement_Assembly0")]
    public Task Gql_named_variable_subpaths_preserve_unicode_and_generated_name_collisions(string start, string edges, string path)
    {
        return Gql_standard_selective_predicates_filter_candidates_before_selection(
            "MATCH p=ALL SHORTEST (" + path + "=(" + start + ":SelectStart)-[" + edges + ":choose]->{1,2}(m)" +
            " WHERE m.score>1 AND PATH_LENGTH(" + path + ")=1)-[:tailLink]->(d:SelectEnd)" +
            " RETURN PATH_LENGTH(" + path + ")+SIZE(" + edges + ") AS answer", "[2]");
    }

    [TestMethod]
    [DataRow("(s=(a:SelectStart)-[firstEdges:choose]->{1,2}(m)-[secondEdges:tailLink]->{1,2}(d) WHERE m.score>1 AND PATH_LENGTH(s)=2)", "PATH_LENGTH(s)+SIZE(firstEdges)+SIZE(secondEdges)", "[4]")]
    [DataRow("(s=(t=(a:SelectStart)-[firstEdges:choose]->{1,2}(m) WHERE m.score>1 AND PATH_LENGTH(t)=1)-[secondEdges:tailLink]->{1,2}(d) WHERE PATH_LENGTH(s)=2)", "PATH_LENGTH(s)+PATH_LENGTH(t)", "[3]")]
    [DataRow("(s=(t=(a:SelectStart)-[:choose]->{1,2}(m) WHERE m.score>1 AND PATH_LENGTH(t)=1)-[:tailLink]->{1,2}(d) WHERE PATH_LENGTH(s)=2)", "PATH_LENGTH(s)+PATH_LENGTH(t)", "[3]")]
    public Task Gql_named_variable_subpaths_preserve_multiple_segments_and_nested_names(string pattern, string projection, string expected) =>
        Gql_standard_selective_predicates_filter_candidates_before_selection(
            "MATCH REPEATABLE ELEMENTS p=ALL SHORTEST " + pattern +
            "<-[:tailLink]-(lastNode WHERE lastNode.score>1) RETURN " + projection + " AS answer", expected);

    [TestMethod]
    [DataRow("|", "[0,1]")]
    [DataRow("|+|", "[0,0,1,1]")]
    public Task Gql_named_variable_subpaths_preserve_alternative_bags(string alternative, string expected)
    {
        const string branch = "((segment=(a:Person {name:'A'})-[edges:knows]->{0,1}(b))-[:knows]->(c))";
        return Gql_standard_named_paths_preserve_order_identity_and_nulls(
            "MATCH p=" + branch + " " + alternative + " " + branch +
            " RETURN PATH_LENGTH(segment) AS answer", expected);
    }
}
