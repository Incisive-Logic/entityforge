using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("http://EXAMPLE.test:80/a/", "item", "http://EXAMPLE.test:80/a/item")]
    [DataRow("http://example.test/a/", "http:g", "http:g")]
    [DataRow("http://[v1.a]/a/", "item", "http://[v1.a]/a/item")]
    [DataRow("c:/data/", "item", "c:/data/item")]
    [DataRow("urn:a/b", "item", "urn:a/item")]
    [DataRow("https://example.test/a/b?old#base", "", "https://example.test/a/b?old")]
    [DataRow("https://example.test/a/b?old#base", "?", "https://example.test/a/b?")]
    [DataRow("https://example.test/a/b?old#base", "#", "https://example.test/a/b?old#")]
    [DataRow("https://example.test/a/b", "../item", "https://example.test/item")]
    [DataRow("https://example.test/a/b", "../../../../item", "https://example.test/item")]
    [DataRow("https://example.test/a/b", "./item/../other", "https://example.test/a/other")]
    [DataRow("https://example.test/a/b", "item/.", "https://example.test/a/item/")]
    [DataRow("https://example.test/a/b", "item/..", "https://example.test/a/")]
    [DataRow("https://example.test/a/b", "%2e/%2E%2e/item", "https://example.test/a/%2e/%2E%2e/item")]
    [DataRow("https://example.test/a/b", "//OTHER.test", "https://OTHER.test")]
    [DataRow("x://", "item", "x:///item")]
    [DataRow("http://a/b/c/d;p?q", "g?y/./x", "http://a/b/c/g?y/./x")]
    [DataRow("http://a/b/c/d;p?q", "g?y/../x", "http://a/b/c/g?y/../x")]
    [DataRow("http://a/b/c/d;p?q", "g#s/./x", "http://a/b/c/g#s/./x")]
    [DataRow("http://a/b/c/d;p?q", "g#s/../x", "http://a/b/c/g#s/../x")]
    [DataRow("http://a/b/c/d;p?q", "/./g", "http://a/g")]
    [DataRow("http://a/b/c/d;p?q", "/../g", "http://a/g")]
    [DataRow("http://a/b/c/d;p?q", "g;x=1/../y", "http://a/b/c/y")]
    [DataRow("http://a/b/c/d;p?q", "g;x=1/./y", "http://a/b/c/g;x=1/y")]
    [DataRow("http://a/b/c/d;p?q", "g..", "http://a/b/c/g..")]
    [DataRow("http://a/b/c/d;p?q", "..g", "http://a/b/c/..g")]
    [DataRow("http://a/b/c/d;p?q", "../../../g", "http://a/g")]
    [DataRow("http://a/b/c/d;p?q", ".", "http://a/b/c/")]
    [DataRow("http://a/b/c/d;p?q", "..", "http://a/b/")]
    [DataRow("http://a/b/c/d;p?q", ";x", "http://a/b/c/;x")]
    [DataRow("http://a/b/c/d;p?q", "?y#s", "http://a/b/c/d;p?y#s")]
    [DataRow("https://example.test/a/b", "\u00A0item?q=\uE000", "https://example.test/a/\u00A0item?q=\uE000")]
    public async Task Sparql_base_resolution_uses_generic_components_without_url_normalization(string baseIri, string reference, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string prolog = "BASE <" + baseIri + "> ";
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(prolog + "SELECT (<" + reference + "> AS ?iri) WHERE {}");
        Assert.AreEqual(new RdfIri(expected), result.Bindings.Single()["iri"]);
        var function = (RdfSparqlSelectResult)await database.QuerySparqlAsync(prolog +
            "SELECT (IRI(" + System.Text.Json.JsonSerializer.Serialize(reference) + ") AS ?iri) WHERE {}");
        Assert.AreEqual(new RdfIri(expected), function.Bindings.Single()["iri"]);
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), prolog + "INSERT DATA { <" + reference + "> <urn:p> 1 }");
        Assert.AreEqual(new RdfIri(expected), database.Find(new()).Single().Subject);
    }

    [TestMethod]
    [DataRow("http:g")]
    [DataRow("http://[v1.a]/item")]
    [DataRow("http://example.test:99999/item")]
    public async Task Sparql_load_generic_iri_retrieval_failures_obey_silent_and_atomicity(string source)
    {
        var resources = new RdfSparqlHttpResources([]);
        var silent = await RdfSparqlUpdate.PrepareLoadsAsync("LOAD SILENT <" + source + ">", "scope", resources, default);
        Assert.AreEqual(0, silent["scope"].Count);
        await Assert.ThrowsExactlyAsync<RdfSparqlRemoteExecutionException>(() =>
            RdfSparqlUpdate.PrepareLoadsAsync("LOAD <" + source + ">", "scope", resources, default).AsTask());
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "LOAD SILENT <" + source +
            ">; INSERT DATA { <urn:kept> <urn:p> 1 }");
        Assert.AreEqual(new RdfIri("urn:kept"), database.Find(new()).Single().Subject);
        await Assert.ThrowsExactlyAsync<RdfSparqlRemoteExecutionException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "INSERT DATA { <urn:rejected> <urn:p> 2 }; LOAD <" + source + ">").AsTask());
        Assert.AreEqual(new RdfIri("urn:kept"), database.Find(new()).Single().Subject);
    }

    [TestMethod]
    [DataRow("bad%/../item")]
    [DataRow("bad%GG/../item")]
    [DataRow("\uFFFD/../item")]
    public async Task Sparql_base_resolution_rejects_invalid_segments_before_removal(string reference)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => database.QuerySparqlAsync(
            "BASE <https://example.test/a/> SELECT (<" + reference + "> AS ?iri) WHERE {}").AsTask());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "BASE <https://example.test/a/> LOAD SILENT <" + reference + ">").AsTask());
    }
}
