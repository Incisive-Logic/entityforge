using System.Text;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("UTF-8")]
    [DataRow("x-entityforge-unknown")]
    [DataRow("utf-7")]
    public async Task Sparql_SERVICE_XML_encoding_failure_rolls_back_and_corrected_retry_replays_after_restart(string invalidCharset)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        bool valid = false;
        int fetched = 0;
        byte[] bytes = Encoding.Latin1.GetBytes(
            "<sparql xmlns='http://www.w3.org/2005/sparql-results#'><head><variable name='x'/></head>" +
            "<results><result><binding name='x'><literal>café £</literal></binding></result></results></sparql>");
        server.MapPost("/sparql", () =>
        {
            Interlocked.Increment(ref fetched);
            return Results.Bytes(bytes, "application/sparql-results+xml; charset=" + (valid ? "iso-8859-1" : invalidCharset));
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        string directory = CreateDirectory();
        Guid operation = Guid.NewGuid();
        string update = "INSERT DATA { <urn:marker> <urn:p> 'staged' }; " +
            $"INSERT {{ <urn:remote> <urn:p> ?x }} WHERE {{ SERVICE <{endpoint}> {{ ?s ?p ?x }} }}; " +
            "INSERT { <urn:copy> <urn:p> ?x } WHERE { <urn:remote> <urn:p> ?x }";
        RdfSparqlUpdateCommit? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await RdfDatabase.OpenLocalAsync(new(directory)
            {
                HttpResources = new([endpoint], allowPrivateNetwork: true)
            });
            if (restart == 0)
            {
                var before = database.CurrentVersion;
                await Assert.ThrowsExactlyAsync<RdfSparqlRemoteExecutionException>(() =>
                    database.CommitSparqlUpdateAsync(operation, update).AsTask());
                Assert.AreEqual(before, database.CurrentVersion);
                Assert.AreEqual(0, database.Find(new()).Count);
                valid = true;
            }
            var committed = await database.CommitSparqlUpdateAsync(operation, update);
            receipt ??= committed;
            Assert.AreEqual(receipt, committed);
            Assert.AreEqual(committed, await database.CommitSparqlUpdateAsync(operation, update));
            Assert.AreEqual(2, fetched, "Only the failed attempt and corrected first commit may fetch remote data.");
            CollectionAssert.AreEquivalent(new RdfQuad[]
            {
                new(new RdfIri("urn:marker"), new("urn:p"), new RdfLiteral("staged")),
                new(new RdfIri("urn:remote"), new("urn:p"), new RdfLiteral("café £")),
                new(new RdfIri("urn:copy"), new("urn:p"), new RdfLiteral("café £"))
            }, database.Find(new()).ToArray());
            // A replay must not depend on the remote endpoint still returning valid data.
            valid = false;
        }
        await server.StopAsync();
    }

    [TestMethod]
    [DataRow("latin1", false, "iso-8859-1", null)]
    [DataRow("latin1", false, "\"iso-8859-1\"", "UTF-8")]
    [DataRow("latin1", false, null, "iso-8859-1")]
    [DataRow("utf8", false, "UTF-8", "iso-8859-1")]
    [DataRow("utf8", true, "iso-8859-1", "iso-8859-1")]
    [DataRow("utf16le", false, "UTF-16LE", "UTF-8")]
    [DataRow("utf16be", false, "UTF-16BE", "UTF-8")]
    [DataRow("utf16le", true, "iso-8859-1", "iso-8859-1")]
    [DataRow("utf16be", true, null, "UTF-16")]
    public async Task Sparql_SERVICE_XML_honors_transport_encoding(
        string encoding, bool bom, string? charset, string? declaration)
    {
        Encoding codec = encoding switch
        {
            "latin1" => Encoding.Latin1,
            "utf16le" => new UnicodeEncoding(false, true, true),
            "utf16be" => new UnicodeEncoding(true, true, true),
            _ => new UTF8Encoding(true, true)
        };
        string xml = (declaration is null ? "" : $"<?xml version='1.0' encoding='{declaration}'?>") +
            "<sparql xmlns='http://www.w3.org/2005/sparql-results#'><head><variable name='x'/></head>" +
            "<results><result><binding name='x'><literal>café £</literal></binding></result></results></sparql>";
        byte[] bytes = (bom ? codec.GetPreamble() : []).Concat(codec.GetBytes(xml)).ToArray();
        await WithEncodedXmlService(bytes, charset, async (database, endpoint) =>
        {
            var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync($"SELECT ?x {{ SERVICE <{endpoint}> {{ ?s ?p ?x }} }}");
            Assert.AreEqual(1, result.Bindings.Count);
            Assert.AreEqual(new RdfLiteral("café £"), result.Bindings[0]["x"]);
        });
    }

    [TestMethod]
    [DataRow("UTF-8")]
    [DataRow("x-entityforge-unknown")]
    [DataRow("utf-7")]
    public async Task Sparql_SERVICE_XML_invalid_encoding_obeys_SILENT(string charset)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(
            "<sparql xmlns='http://www.w3.org/2005/sparql-results#'><head><variable name='x'/></head>" +
            "<results><result><binding name='x'><literal>café</literal></binding></result></results></sparql>");
        await WithEncodedXmlService(bytes, charset, async (database, endpoint) =>
        {
            await Assert.ThrowsExactlyAsync<RdfSparqlRemoteExecutionException>(() => database.QuerySparqlAsync(
                $"SELECT * {{ SERVICE <{endpoint}> {{ ?s ?p ?x }} }}").AsTask());
            var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
                $"SELECT * {{ SERVICE SILENT <{endpoint}> {{ ?s ?p ?x }} }}");
            Assert.AreEqual(1, result.Bindings.Count);
            Assert.AreEqual(0, result.Bindings[0].Count);
        });
    }

    private async Task WithEncodedXmlService(byte[] bytes, string? charset, Func<IRdfDatabase, Uri, Task> check)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapPost("/sparql", () => Results.Bytes(bytes,
            "application/sparql-results+xml" + (charset is null ? "" : "; charset=" + charset)));
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([endpoint], allowPrivateNetwork: true)
        });
        await check(database, endpoint);
        await server.StopAsync();
    }
}
