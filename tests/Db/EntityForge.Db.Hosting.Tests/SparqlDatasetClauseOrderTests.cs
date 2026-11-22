using EntityForge.Db.Rdf;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("DESCRIBE <urn:a> LIMIT 1 FROM <urn:g>")]
    [DataRow("DESCRIBE <urn:a> OFFSET 0 FROM <urn:g>")]
    [DataRow("DESCRIBE <urn:a> ORDER BY (<urn:a>) FROM <urn:g>")]
    [DataRow("DESCRIBE * LIMIT 1 FROM NAMED <urn:g>")]
    public async Task Sparql_describe_dataset_clauses_cannot_follow_solution_modifiers(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.Throws<Exception>(() => new VDS.RDF.Parsing.SparqlQueryParser().ParseFromString(query));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.QuerySparqlAsync(query).AsTask());
    }
}
