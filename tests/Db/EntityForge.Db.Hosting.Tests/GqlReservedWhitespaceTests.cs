namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("LET WHITESPACE=3 RETURN WHITESPACE AS answer", "ERROR")]
    [DataRow("LET whitespace=3 RETURN whitespace AS answer", "ERROR")]
    [DataRow("RETURN 1 AS Whitespace", "ERROR")]
    [DataRow("MATCH (WHITESPACE:Person) RETURN COUNT(*) AS answer", "ERROR")]
    [DataRow("RETURN 3 AS \"WHITESPACE\" NEXT YIELD \"WHITESPACE\" AS item RETURN item AS answer", "[3]")]
    public Task Gql_pre_reserved_whitespace_word_requires_delimited_identifier_context(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
}
