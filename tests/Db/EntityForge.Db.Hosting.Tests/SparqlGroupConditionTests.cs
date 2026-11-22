using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("STR(?x)='1'")]
    [DataRow("STRLEN(STR(?x))+1")]
    [DataRow("?x+1")]
    [DataRow("1")]
    [DataRow("<urn:key>")]
    public async Task Sparql_group_conditions_reject_unbracketed_general_expressions(string condition)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string query = "SELECT (COUNT(*) AS ?n) { VALUES ?x {1 2} } GROUP BY " + condition;
        Assert.Throws<Exception>(() => new VDS.RDF.Parsing.SparqlQueryParser().ParseFromString(query));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.QuerySparqlAsync(query).AsTask());
    }

    [TestMethod]
    [DataRow("STR(?x)", "1,1")]
    [DataRow("(STRLEN(STR(?x))+1)", "2")]
    [DataRow("EXISTS {}", "2")]
    [DataRow("NOT EXISTS {}", "2")]
    [DataRow("EXISTS { FILTER(?x=1) }", "1,1")]
    [DataRow("(EXISTS { FILTER(?x=1) } AS ?present)", "1,1")]
    public async Task Sparql_group_conditions_accept_functions_and_exists(string condition, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string query = "SELECT (COUNT(*) AS ?n) { VALUES ?x {1 2} } GROUP BY " + condition;
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
        Assert.AreEqual(expected, String.Join(',', result.Bindings.Select(row => ((RdfLiteral)row["n"]).LexicalForm).Order()));
    }
}
