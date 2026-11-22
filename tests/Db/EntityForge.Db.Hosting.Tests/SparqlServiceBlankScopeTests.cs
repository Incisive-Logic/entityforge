using EntityForge.Db.Rdf;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SERVICE")]
    [DataRow("SERVICE SILENT")]
    public async Task Sparql_service_separates_surrounding_blank_label_scopes(string service)
    {
        await using var database = await OpenAsync(CreateDirectory());
        // No HTTP resources are configured. Invalid scope must fail during
        // preparation, even when an empty local relation would skip the call.
        string query = "SELECT ?value { _:x <urn:p> ?value " + service +
            " <https://example.invalid/sparql> {} _:x <urn:q> ?q }";
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await database.QuerySparqlAsync(query));
    }

    [TestMethod]
    [DataRow("_:x <urn:p> ?value SERVICE SILENT <https://example.invalid/sparql> { _:x <urn:q> ?q }")]
    [DataRow("SERVICE SILENT <https://example.invalid/sparql> { _:x <urn:p> ?value } _:x <urn:q> ?q")]
    public async Task Sparql_service_body_cannot_reuse_an_outer_blank_label(string body)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await database.QuerySparqlAsync("SELECT * { " + body + " }"));
    }

    [TestMethod]
    [DataRow("_:x <urn:p> ?value . _:x <urn:q> ?q")]
    [DataRow("_:x <urn:p>/<urn:r> ?value . _:x <urn:q> ?q")]
    [DataRow("_:x <urn:p> ?value FILTER(?value = 1) _:x <urn:q> ?q")]
    [DataRow("_:x <urn:p> ?value SERVICE SILENT <https://example.invalid/sparql> {} _:y <urn:q> ?q")]
    public async Task Sparql_blank_label_scope_controls_remain_valid(string body)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync("SELECT * { " + body + " }");
        Assert.AreEqual(0, result.Bindings.Count);
    }
}
