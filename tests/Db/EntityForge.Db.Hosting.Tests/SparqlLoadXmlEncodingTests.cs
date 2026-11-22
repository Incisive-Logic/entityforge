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
    [DataRow("latin1", false, null, "iso-8859-1")]
    [DataRow("latin1", false, "iso-8859-1", "UTF-8")]
    [DataRow("latin1", false, "\"iso-8859-1\"", "UTF-8")]
    [DataRow("utf16le", true, "iso-8859-1", "iso-8859-1")]
    [DataRow("utf16be", true, null, "UTF-16")]
    [DataRow("utf8", true, "iso-8859-1", "iso-8859-1")]
    [DataRow("utf8", false, "UTF-8", "iso-8859-1")]
    [DataRow("utf8", false, null, "UTF-8")]
    [DataRow("utf32le", true, "iso-8859-1", "UTF-8")]
    [DataRow("utf32be", true, null, "UTF-32")]
    [DataRow("utf16le", false, "UTF-16LE", "UTF-8")]
    [DataRow("utf16be", false, "UTF-16BE", "UTF-8")]
    public async Task Sparql_load_xml_resolves_byte_encoding_before_rdf_parsing(
        string encoding, bool bom, string? charset, string declaration)
    {
        Encoding codec = encoding switch
        {
            "latin1" => Encoding.Latin1,
            "utf16le" => new UnicodeEncoding(false, true, true),
            "utf16be" => new UnicodeEncoding(true, true, true),
            "utf32le" => new UTF32Encoding(false, true, true),
            "utf32be" => new UTF32Encoding(true, true, true),
            _ => new UTF8Encoding(true, true)
        };
        string xml = $"<?xml version='1.0' encoding='{declaration}'?>" +
            "<rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#' xmlns:x='urn:'>" +
            "<rdf:Description rdf:about='item'><x:value>café £</x:value></rdf:Description></rdf:RDF>";
        byte[] bytes = (bom ? codec.GetPreamble() : []).Concat(codec.GetBytes(xml)).ToArray();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapGet("/graph", () => Results.Bytes(bytes, "application/rdf+xml" + (charset is null ? "" : "; charset=" + charset)));
        await server.StartAsync();
        var source = new Uri(server.Urls.Single() + "/graph");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([source], allowPrivateNetwork: true)
        });
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), $"LOAD <{source}> INTO GRAPH <urn:g>");
        CollectionAssert.AreEquivalent(new RdfQuad[]
        {
            new(new RdfIri(new Uri(source, "item").AbsoluteUri), new("urn:value"), new RdfLiteral("café £"), new RdfIri("urn:g"))
        }, database.Find(new()).ToArray());
        await server.StopAsync();
    }

    [TestMethod]
    [DataRow("invalid-utf8")]
    [DataRow("unknown-charset")]
    [DataRow("disabled-charset")]
    [DataRow("doctype")]
    [DataRow("depth")]
    public async Task Sparql_load_xml_encoding_keeps_failure_atomicity_and_resource_guards(string mode)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int entitiesFetched = 0;
        server.MapGet("/entity", () =>
        {
            Interlocked.Increment(ref entitiesFetched);
            return Results.Text("external");
        });
        string charset = mode switch
        {
            "invalid-utf8" => "UTF-8",
            "unknown-charset" => "x-entityforge-unknown",
            "disabled-charset" => "utf-7",
            _ => "iso-8859-1"
        };
        server.MapGet("/graph", () =>
        {
            string xml = "<rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#' xmlns:x='urn:'>" +
                "<rdf:Description rdf:about='urn:s'><x:value>café</x:value></rdf:Description></rdf:RDF>";
            if (mode == "doctype")
            {
                xml = $"<!DOCTYPE rdf:RDF [<!ENTITY ext SYSTEM '{server.Urls.Single()}/entity'>]>" + xml.Replace("café", "&ext;", StringComparison.Ordinal);
            }
            if (mode == "depth")
            {
                xml = String.Concat(Enumerable.Repeat("<e>", 66)) + "café" + String.Concat(Enumerable.Repeat("</e>", 66));
            }
            return Results.Bytes(Encoding.Latin1.GetBytes(xml), "application/rdf+xml; charset=" + charset);
        });
        await server.StartAsync();
        var source = new Uri(server.Urls.Single() + "/graph");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([source], allowPrivateNetwork: true)
        });
        foreach (bool silent in new[] { false, true })
        {
            string update = "INSERT DATA { <urn:marker> <urn:p> 'keep' }; LOAD " + (silent ? "SILENT " : "") + $"<{source}>";
            var before = database.CurrentVersion;
            if (mode == "depth")
            {
                await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(), update).AsTask());
            }
            else if (!silent)
            {
                await Assert.ThrowsExactlyAsync<RdfSparqlRemoteExecutionException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(), update).AsTask());
            }
            else
            {
                await database.CommitSparqlUpdateAsync(Guid.NewGuid(), update);
            }
            if (mode == "depth" || !silent)
            {
                Assert.AreEqual(before, database.CurrentVersion);
                Assert.HasCount(0, database.Find(new()));
            }
            else
            {
                CollectionAssert.AreEquivalent(new RdfQuad[] { new(new RdfIri("urn:marker"), new("urn:p"), new RdfLiteral("keep")) }, database.Find(new()).ToArray());
            }
        }
        Assert.AreEqual(0, entitiesFetched);
        await server.StopAsync();
    }
}
