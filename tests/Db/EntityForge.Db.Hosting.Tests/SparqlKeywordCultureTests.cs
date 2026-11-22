using System.Globalization;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("en-GB")]
    [DataRow("tr-TR")]
    [DataRow("az-Latn-AZ")]
    [DataRow("fr-FR")]
    public async Task Sparql_query_keyword_case_is_independent_of_execution_culture(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            await using var database = await OpenAsync(CreateDirectory());
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
                "INSERT DATA { GRAPH <urn:G> { <urn:I> <urn:p> 3,1,2 . <urn:i> <urn:p> 2 } }");
            foreach (string query in new[]
            {
                "SELECT DISTINCT ?o FROM <urn:G> WHERE { ?s <urn:p> ?o } ORDER BY ?o LIMIT 2",
                "select distinct ?o from <urn:G> where { ?s <urn:p> ?o } order by ?o limit 2",
                "SeLeCt DiStInCt ?o FrOm <urn:G> WhErE { ?s <urn:p> ?o } OrDeR By ?o LiMiT 2"
            })
            {
                Assert.AreEqual(culture, CultureInfo.CurrentCulture.Name);
                var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
                CollectionAssert.AreEqual(new[] { "1", "2" }, result.Bindings.Select(row => ((RdfLiteral)row["o"]).LexicalForm).ToArray(), query);
            }
            var distinctNames = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
                "select ?Item ?item where { values (?Item ?item) { ('İıIi' 'different') } }");
            Assert.AreEqual("İıIi", ((RdfLiteral)distinctNames.Bindings.Single()["Item"]).LexicalForm);
            Assert.AreEqual("different", ((RdfLiteral)distinctNames.Bindings.Single()["item"]).LexicalForm);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await database.QuerySparqlAsync("SELECT ?s WHERE { ?s A <urn:Type> }"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestMethod]
    [DataRow("en-GB")]
    [DataRow("tr-TR")]
    [DataRow("az-Latn-AZ")]
    [DataRow("fr-FR")]
    public async Task Sparql_update_keyword_case_is_independent_of_execution_culture(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            await using var database = await OpenAsync(CreateDirectory());
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
                "prefix ex:<urn:> insert data { graph ex:G { ex:I ex:p 1 . ex:i ex:p 2 } }");
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
                "prefix ex:<urn:> with ex:G delete { ?s ex:p ?v } insert { ?s ex:p ?n } " +
                "where { ?s ex:p ?v bind(?v + 10 as ?n) }");
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
                "prefix ex:<urn:> copy graph ex:G to graph ex:H; clear graph ex:G; " +
                "add graph ex:H to graph ex:G; drop graph ex:H");
            Assert.AreEqual(culture, CultureInfo.CurrentCulture.Name);
            var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
                "SELECT ?s ?v WHERE { GRAPH <urn:G> { ?s <urn:p> ?v } } ORDER BY ?v");
            CollectionAssert.AreEqual(new[] { "11", "12" }, result.Bindings.Select(row => ((RdfLiteral)row["v"]).LexicalForm).ToArray());
            CollectionAssert.AreEqual(new[] { "urn:I", "urn:i" }, result.Bindings.Select(row => ((RdfIri)row["s"]).Value).ToArray());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "insert data { <urn:x> <urn:p> }"));
            Assert.AreEqual(culture, CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
