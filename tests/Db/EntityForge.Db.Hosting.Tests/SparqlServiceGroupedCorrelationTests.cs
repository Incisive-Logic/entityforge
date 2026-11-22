namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("GROUP BY (STR(?item))", "?n=2")]
    [DataRow("GROUP BY (STR(?item)) HAVING(COUNT(*)>1)", "?n=2")]
    [DataRow("GROUP BY (STR(?item)) ORDER BY DESC(COUNT(*)) LIMIT 1", "?n=2")]
    [DataRow("GROUP BY (STR(?item)) VALUES ?__entityforge_group_0 {9}", "?n=2")]
    public Task Sparql_correlated_SERVICE_group_expressions_preserve_filtered_bag_counts(string modifiers, string filter) =>
        Sparql11_correlated_SERVICE_expressions_use_substituted_solutions(
            "SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> {" +
            " { SELECT (COUNT(*) AS ?n) { VALUES (?x ?item) {(1 'a') (1 'a') (2 'b')} } " + modifiers + " }" +
            " FILTER(" + filter + ") } } }", "1", 2);
}
