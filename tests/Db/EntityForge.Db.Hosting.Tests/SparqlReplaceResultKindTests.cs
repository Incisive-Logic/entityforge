namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("REPLACE('abc'@en,'z','x')", "abc", "en")]
    [DataRow("REPLACE('abc'@en,'(b)','$1$1')", "abbc", "en")]
    [DataRow("REPLACE('abc'@en,'abc','')", "", "en")]
    [DataRow("REPLACE('a😀b'@en,'(.)','$1$1')", "aa😀😀bb", "en")]
    [DataRow("REPLACE('abc','(z)?b','$1X')", "aXc", null)]
    [DataRow("REPLACE('abc','z','$')", null, null)]
    [DataRow("REPLACE('abc','z','\\\\q')", null, null)]
    [DataRow("REPLACE('abc','a*','x')", null, null)]
    [DataRow("REPLACE('abc','z','x','q')", null, null)]
    [DataRow("REPLACE('abc','z'@en,'x')", null, null)]
    [DataRow("REPLACE('abc','z','x'@en)", null, null)]
    [DataRow("REPLACE('abc','z','x','i'@en)", null, null)]
    public Task Sparql_replace_preserves_term_kind_and_errors_in_query_and_scan(string expression, string? lexical, string? language) =>
        Sparql_string_results_preserve_kind_and_error_in_query_and_scan(expression, lexical, language);
}
