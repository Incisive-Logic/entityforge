using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("DESCRIBE ?s FROM <urn:g> VALUES ?s { <urn:a> }", "urn:a")]
    [DataRow("DESCRIBE ?s FROM <urn:g> VALUES ?s { <urn:a> <urn:a> UNDEF <urn:b> }", "urn:a")]
    [DataRow("DESCRIBE * FROM <urn:g> VALUES (?s ?t) { (<urn:a> <urn:shared>) }", "urn:a,urn:shared")]
    [DataRow("DESCRIBE ?s FROM NAMED <urn:g> VALUES ?s { <urn:a> }", "urn:a")]
    [DataRow("DESCRIBE ?s FROM <urn:g> FROM <urn:h> ORDER BY ?s LIMIT 1 OFFSET 1 VALUES ?s { <urn:b> <urn:a> }", "urn:b")]
    [DataRow("DESCRIBE ?s FROM <urn:g> VALUES ?s {}", "")]
    [DataRow("DESCRIBE <urn:a> FROM <urn:g> VALUES () { () }", "urn:a")]
    [DataRow("BASE <urn:> PREFIX ex: <urn:> DESCRIBE ?s FROM ex:g VALUES ?s { <a> }", "urn:a")]
    public async Task Sparql_describe_selected_dataset_accepts_values_without_where(string query, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), DatasetSeed);
        var version = database.CurrentVersion;
        var result = (RdfSparqlGraphResult)await database.QuerySparqlAsync(query);
        Assert.AreEqual(expected, string.Join(',', result.Quads.Select(quad => ((RdfIri)quad.Subject).Value)
            .Order(StringComparer.Ordinal)));
        Assert.IsTrue(result.Quads.All(quad => quad.Graph is null));
        Assert.AreEqual(version, result.SnapshotVersion);
        Assert.AreEqual(version, database.CurrentVersion);
        Assert.AreEqual(5, database.Find(new()).Count);
    }
}
