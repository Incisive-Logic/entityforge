using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("FROM <urn:g>")]
    public async Task Sparql_name_characters_are_preserved_at_projection_trim_boundaries(string dataset)
    {
        await using var database = await OpenAsync(CreateDirectory());
        // U+1680 is in PN_CHARS_BASE despite its Unicode whitespace category.
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
            "SELECT ?\u1680 " + dataset + " WHERE {VALUES ?\u1680 {<urn:item>}}");
        Assert.AreEqual(new RdfIri("urn:item"), result.Bindings.Single()["\u1680"]);
    }
}
