namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    // ISO's published BNF <non-reserved word>, exercised as bindings rather
    // than merely accepted by a standalone grammar parse.
    public static IEnumerable<object[]> GqlNonReservedNames =>
        ("ACYCLIC BINDING BINDINGS CONNECTING DESTINATION DIFFERENT DIRECTED EDGE EDGES ELEMENT ELEMENTS " +
         "FIRST GRAPH GROUPS KEEP LABEL LABELED LABELS LAST NFC NFD NFKC NFKD NO NODE NORMALIZED ONLY " +
         "ORDINALITY PROPERTY READ RELATIONSHIP RELATIONSHIPS REPEATABLE SHORTEST SIMPLE SOURCE TABLE TEMP " +
         "TO TRAIL TRANSACTION TYPE UNDIRECTED VERTEX WALK WITHOUT WRITE ZONE")
        .Split(' ').Select(name => new object[] { name });

    [TestMethod]
    [DynamicData(nameof(GqlNonReservedNames), DynamicDataSourceType.Property)]
    public Task Gql_non_reserved_words_preserve_bindings_across_query_and_update(string name) =>
        Gql_standard_Unicode_identifiers_preserve_bindings_labels_and_parameters(name);

    [TestMethod]
    [DataRow("LET GRAPHLIMIT=3 RETURN GRAPHLIMIT UNION LET GRAPHLIMIT=3 RETURN GRAPHLIMIT NEXT RETURN GRAPHLIMIT AS answer", "[3]")]
    [DataRow("MATCH (a:Person {name:'A'}) OPTIONAL MATCH (GRAPH:Person {name:'B'}) RETURN GRAPH.name AS answer", "[\"B\"]")]
    [DataRow("MATCH (GRAPH:Person {name:'A'}) RETURN GRAPH.name AS answer UNION MATCH (GRAPH:Person {name:'A'}) RETURN GRAPH.name AS answer", "[\"A\"]")]
    public Task Gql_graph_identifier_projection_preserves_expression_boundaries(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
}
