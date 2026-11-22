using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("PUT", "text/turtle")]
    [DataRow("POST", "text/turtle")]
    [DataRow("PUT", "application/n-triples")]
    [DataRow("POST", "application/n-triples")]
    [DataRow("PUT", "application/rdf+xml")]
    [DataRow("POST", "application/rdf+xml")]
    public async Task Sparql_graph_store_late_payload_failure_preserves_graph_and_allows_subsequent_write(string method, string media)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        const string path = "/sparql/graphs?graph=urn:atomicPayload";
        using var seed = await client.PutAsync(path, new StringContent("<urn:kept> <urn:p> 'original' .", Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.Created, seed.StatusCode);
        string before = await client.GetStringAsync(path);
        string valid = media == "application/rdf+xml"
            ? "<rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#' xmlns:p='urn:p:'><rdf:Description rdf:about='urn:new'><p:value>accepted</p:value></rdf:Description></rdf:RDF>"
            : "<urn:new> <urn:p> \"accepted\" .\n";
        byte[] prefix = Encoding.UTF8.GetBytes(valid);
        var invalidPayloads = new List<byte[]>
        {
            prefix.Concat(Encoding.UTF8.GetBytes("\n<unfinished")).ToArray(),
            prefix.Concat(new byte[] { 0xFF }).ToArray()
        };
        if (media != "application/rdf+xml")
        {
            invalidPayloads.Add(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(valid)).ToArray());
            invalidPayloads.Add(Encoding.UTF32.GetPreamble().Concat(Encoding.UTF32.GetBytes(valid)).ToArray());
        }
        foreach (byte[] invalid in invalidPayloads)
        {
            using var content = new ByteArrayContent(invalid);
            content.Headers.ContentType = new MediaTypeHeaderValue(media) { CharSet = "utf-8" };
            using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = content };
            using var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(before, await client.GetStringAsync(path), "Rejected payload partially replaced or merged the graph.");
        }
        using var acceptedRequest = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent(valid, Encoding.UTF8, media)
        };
        using var accepted = await client.SendAsync(acceptedRequest);
        Assert.AreEqual(HttpStatusCode.NoContent, accepted.StatusCode, await accepted.Content.ReadAsStringAsync());
        string after = await client.GetStringAsync(path);
        StringAssert.Contains(after, "urn:new");
        Assert.AreEqual(method == "POST", after.Contains("urn:kept", StringComparison.Ordinal));
    }
}
