namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("\u001c")]
    [DataRow("\u001d")]
    [DataRow("\u001e")]
    [DataRow("\u001f")]
    [DataRow("\u180e")]
    [DataRow("\u2007")]
    [DataRow("\t\r\n")]
    public Task Gql_lexer_separators_reach_execution_without_changing_tokens(string separator) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(
            string.Join(separator, new[] { "LET", "amount=3", "RETURN", "amount", "AS", "answer" }), "[3]");

    [TestMethod]
    public Task Gql_separator_normalization_preserves_literal_and_identifier_contents() =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(
            "RETURN\u180e'left\u180eright'\u180eAS\u180e\"column\u180ename\" NEXT YIELD \"column\u180ename\" AS item RETURN item AS answer",
            "[\"left\\u180eright\"]");

    [TestMethod]
    public Task Gql_separator_normalization_preserves_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH\u180e(a:Person {name:'A'})-[:knows]->(b),(c:Person {name:'B'})", "a,b,c");
}
