using EntityForge.Db.Rdf;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("1")]
    [DataRow("<urn:key>")]
    [DataRow("'key'")]
    [DataRow("true")]
    [DataRow("?x+1")]
    [DataRow("STR(?x)='1'")]
    [DataRow("!BOUND(?x)")]
    [DataRow("ASC ?x")]
    public async Task Sparql_order_conditions_reject_unbracketed_general_expressions(string condition)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string query = "SELECT ?x { VALUES ?x {2 1} } ORDER BY " + condition;
        Assert.Throws<Exception>(() => new VDS.RDF.Parsing.SparqlQueryParser().ParseFromString(query));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.QuerySparqlAsync(query).AsTask());
    }

    [TestMethod]
    [DataRow("?x")]
    [DataRow("(?x+1)")]
    [DataRow("STR(?x)")]
    [DataRow("ASC(?x)")]
    [DataRow("(<urn:key>) ?x")]
    [DataRow("(true) ?x")]
    [DataRow("EXISTS {} ?x")]
    [DataRow("NOT EXISTS {} ?x")]
    public async Task Sparql_order_conditions_accept_standard_constraints_and_variables(string condition)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string query = "SELECT ?x { VALUES ?x {2 1} } ORDER BY " + condition;
        // EXISTS and NOT EXISTS are BuiltInCall alternatives in the W3C
        // grammar. This reference parser rejects their unbracketed ORDER use.
        if (!condition.Contains("EXISTS", StringComparison.Ordinal))
        {
            _ = new VDS.RDF.Parsing.SparqlQueryParser().ParseFromString(query);
        }
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
        CollectionAssert.AreEqual(new[] { "1", "2" }, result.Bindings.Select(row => ((EntityForge.Db.Rdf.Contracts.RdfLiteral)row["x"]).LexicalForm).ToArray());
    }
}
