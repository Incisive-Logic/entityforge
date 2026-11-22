using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("PUT", "valid", 204)]
    [DataRow("POST", "valid", 204)]
    [DataRow("PUT", "invalid", 400)]
    [DataRow("POST", "invalid", 400)]
    [DataRow("PUT", "doctype", 400)]
    [DataRow("POST", "doctype", 400)]
    public async Task Sparql_graph_store_without_content_type_defaults_to_safe_RdfXml(string method, string kind, int status)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        const string path = "/sparql/graphs?graph=urn:defaultFormat";
        using var seed = await client.PutAsync(path, new StringContent("<urn:kept> <urn:p> 1 .", Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.Created, seed.StatusCode);
        string xml = kind switch
        {
            "valid" => "<rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#' xmlns:p='urn:p:'><rdf:Description rdf:about='urn:new'><p:value>new</p:value></rdf:Description></rdf:RDF>",
            "doctype" => "<!DOCTYPE rdf [<!ENTITY x SYSTEM 'file:///nonexistent'>]><rdf/>",
            _ => "<rdf:RDF"
        };
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(xml))
        };
        Assert.IsNull(request.Content.Headers.ContentType);
        using var response = await client.SendAsync(request);
        Assert.AreEqual((HttpStatusCode)status, response.StatusCode, await response.Content.ReadAsStringAsync());
        string stored = await client.GetStringAsync(path);
        Assert.AreEqual(kind == "valid", stored.Contains("urn:new", StringComparison.Ordinal));
        Assert.AreEqual(kind != "valid" || method == "POST", stored.Contains("urn:kept", StringComparison.Ordinal));
    }
}
