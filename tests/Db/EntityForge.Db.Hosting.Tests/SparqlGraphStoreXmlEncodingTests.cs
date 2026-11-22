using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("PUT", "latin1", true)]
    [DataRow("POST", "latin1", true)]
    [DataRow("MULTIPART", "latin1", true)]
    [DataRow("PUT", "utf16", true)]
    [DataRow("POST", "utf16", true)]
    [DataRow("MULTIPART", "utf16", true)]
    [DataRow("PUT", "utf8", true)]
    [DataRow("POST", "utf8", true)]
    [DataRow("MULTIPART", "utf8", true)]
    [DataRow("PUT", "latin1", false)]
    [DataRow("POST", "utf16", false)]
    public async Task Sparql_graph_store_xml_upload_honors_transport_encoding(string method, string encoding, bool contentType)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        const string path = "/sparql/graphs?graph=urn:encoded";
        using var seed = await client.PutAsync(path, new StringContent("<urn:keep> <urn:p> 'original' .", Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.Created, seed.StatusCode);
        Encoding codec = encoding switch
        {
            "latin1" => Encoding.Latin1,
            "utf16" => new UnicodeEncoding(false, true, true),
            _ => new UTF8Encoding(false, true)
        };
        string declaration = encoding switch
        {
            "latin1" => "iso-8859-1",
            "utf16" => "UTF-16",
            _ => "UTF-8"
        };
        string xml = $"<?xml version='1.0' encoding='{declaration}'?>" +
            "<rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#' xmlns:x='urn:'>" +
            "<rdf:Description rdf:about='urn:loaded'><x:value>café £</x:value></rdf:Description></rdf:RDF>";
        using var document = new ByteArrayContent(codec.GetPreamble().Concat(codec.GetBytes(xml)).ToArray());
        if (contentType)
        {
            // The UTF-16 BOM must override a conflicting transport charset.
            document.Headers.ContentType = new MediaTypeHeaderValue("application/rdf+xml") { CharSet = encoding == "latin1" ? "iso-8859-1" : "utf-8" };
        }
        using var multipart = new MultipartFormDataContent();
        HttpContent payload = document;
        if (method == "MULTIPART")
        {
            multipart.Add(document, "xml", "graph.rdf");
            multipart.Add(new StringContent("<urn:part> <urn:p> 'second' .", Encoding.UTF8, "text/turtle"), "turtle", "graph.ttl");
            payload = multipart;
        }
        using var request = new HttpRequestMessage(method == "PUT" ? HttpMethod.Put : HttpMethod.Post, path) { Content = payload };
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode, await response.Content.ReadAsStringAsync());
        var expected = new List<RdfQuad>
        {
            new(new RdfIri("urn:loaded"), new("urn:value"), new RdfLiteral("café £"), new RdfIri("urn:encoded"))
        };
        if (method != "PUT")
        {
            expected.Add(new(new RdfIri("urn:keep"), new("urn:p"), new RdfLiteral("original"), new RdfIri("urn:encoded")));
        }
        if (method == "MULTIPART")
        {
            expected.Add(new(new RdfIri("urn:part"), new("urn:p"), new RdfLiteral("second"), new RdfIri("urn:encoded")));
        }
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        CollectionAssert.AreEquivalent(expected, runtime.RdfDatabase.Find(new()).ToArray());
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("text/turtle")]
    [DataRow("application/n-triples")]
    public async Task Sparql_graph_store_multipart_rejects_non_utf8_rdf_text_after_valid_xml(string media)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        const string path = "/sparql/graphs?graph=urn:encoded";
        using var seed = await client.PutAsync(path, new StringContent("<urn:keep> <urn:p> 'original' .", Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.Created, seed.StatusCode);
        string before = await client.GetStringAsync(path);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("<rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#' xmlns:x='urn:'><rdf:Description rdf:about='urn:new'><x:p>new</x:p></rdf:Description></rdf:RDF>", Encoding.Unicode, "application/rdf+xml"), "xml", "graph.rdf");
        var invalid = new ByteArrayContent(Encoding.Latin1.GetBytes("<urn:bad> <urn:p> \"café\" ."));
        invalid.Headers.ContentType = new MediaTypeHeaderValue(media) { CharSet = "utf-8" };
        multipart.Add(invalid, "invalid", "graph.txt");
        using var response = await client.PostAsync(path, multipart);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(before, await client.GetStringAsync(path));
        await host.StopAsync();
    }
}
