using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("ask", "", "application/sparql-results+json")]
    [DataRow("store", "*/*", "text/turtle")]
    [DataRow("ask", "application/sparql-results+json;q=0., application/sparql-results+xml;q=1.", "application/sparql-results+xml")]
    [DataRow("ask", "application/*;q=0.8, application/sparql-results+json;q=0", "application/sparql-results+xml")]
    [DataRow("select", "text/*;q=0.8, text/csv;q=0", "text/tab-separated-values")]
    [DataRow("construct", "text/*;q=0, application/n-triples;q=0.4, */*;q=0.1", "application/n-triples")]
    [DataRow("store", "text/turtle; charset=UTF-8;q=0, text/turtle;q=1, application/n-triples;q=0.2", "application/n-triples")]
    [DataRow("description", "text/turtle;charset=\"UTF-8\";q=0.001", "text/turtle")]
    [DataRow("ask", "application/sparql-results+json;profile=other, application/sparql-results+xml;q=0.1", "application/sparql-results+xml")]
    [DataRow("store", "text/turtle;charset=iso-8859-1, application/rdf+xml;q=0.2", "application/rdf+xml")]
    [DataRow("select", "text/csv;q=1.000, */*;q=0", "text/csv")]
    [DataRow("select", "text/csv;header=present", "text/csv")]
    [DataRow("select", "text/csv;header=\"present\";charset=UTF-8", "text/csv")]
    [DataRow("select", "text/csv;header=absent", "")]
    [DataRow("select", "text/csv;header=absent, text/tab-separated-values;q=0.2", "text/tab-separated-values")]
    [DataRow("select", "text/csv;header=present;q=0, text/csv;q=1, text/tab-separated-values;q=0.2", "text/tab-separated-values")]
    [DataRow("select", "text/tab-separated-values;header=present", "")]
    [DataRow("ask", "*/*;q=0", "")]
    [DataRow("description", "text/*;q=0, */*;q=1", "")]
    public async Task Sparql11_negotiation_obeys_specificity_parameters_and_zero_quality(string form, string accept, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string path = form switch
        {
            "store" => "/sparql/graphs?default",
            "description" => "/sparql",
            _ => "/sparql?query=" + Uri.EscapeDataString(form switch
            {
                "ask" => "ASK {}",
                "select" => "SELECT (1 AS ?n) {}",
                _ => "CONSTRUCT { <urn:s> <urn:p> 1 } WHERE {}"
            })
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Accept", accept);
        using var response = await client.SendAsync(request);
        Assert.AreEqual(expected.Length == 0 ? HttpStatusCode.NotAcceptable : HttpStatusCode.OK,
            response.StatusCode, await response.Content.ReadAsStringAsync());
        if (expected.Length != 0)
        {
            Assert.AreEqual(expected, response.Content.Headers.ContentType?.MediaType);
            if (expected == "text/csv")
            {
                Assert.AreEqual("present", response.Content.Headers.ContentType!.Parameters.Single(parameter => parameter.Name == "header").Value);
                Assert.AreEqual("utf-8", response.Content.Headers.ContentType.CharSet);
                Assert.AreEqual("n\r\n1\r\n", await response.Content.ReadAsStringAsync());
            }
        }
        Assert.IsTrue(response.Headers.Vary.Contains("Accept"));
    }

    [TestMethod]
    [DataRow("application/sparql-results+json;q=2")]
    [DataRow("application/sparql-results+json;q=-1")]
    [DataRow("application/sparql-results+json;q=invalid")]
    [DataRow("application/sparql-results+json;q=1.001")]
    [DataRow("application/sparql-results+json;q=0.0001")]
    [DataRow("application/sparql-results+json;q=\"0.5\"")]
    [DataRow("application/sparql-results+json;q=0;q=1")]
    [DataRow("application/sparql-results+json, not-a-media-type")]
    public async Task Sparql11_negotiation_rejects_malformed_accept_instead_of_silently_choosing_a_format(string accept)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (string path in new[] { "/sparql?query=ASK%7B%7D", "/sparql/graphs?default", "/sparql" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation("Accept", accept);
            using var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, path + ": " + await response.Content.ReadAsStringAsync());
        }
    }
}
