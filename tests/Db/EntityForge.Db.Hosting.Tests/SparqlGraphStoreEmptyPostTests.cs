using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("", "text/turtle")]
    [DataRow("?default", "text/turtle")]
    [DataRow("?graph=urn%3Aabsent", "text/turtle")]
    [DataRow("", "application/n-triples")]
    [DataRow("?default", "application/n-triples")]
    [DataRow("?graph=urn%3Aabsent", "application/n-triples")]
    public async Task Sparql11_graph_store_empty_post_is_no_content_and_preserves_preconditions(string selector, string mediaType)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        const string existing = "/sparql/graphs?graph=urn%3Aexisting";
        using var seed = await client.PutAsync(existing, new StringContent("<urn:s> <urn:p> 1 .", Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.Created, seed.StatusCode);
        string before = await client.GetStringAsync(existing);
        foreach (string path in new[] { "/sparql/graphs" + selector, existing })
        {
            using var response = await client.PostAsync(path, new StringContent("", Encoding.UTF8, mediaType));
            Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.IsNull(response.Headers.Location);
            Assert.AreEqual(0, (await response.Content.ReadAsByteArrayAsync()).Length);
        }
        using var absent = await client.GetAsync("/sparql/graphs?graph=urn%3Aabsent");
        Assert.AreEqual(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.AreEqual(before, await client.GetStringAsync(existing));
        using var conditional = new HttpRequestMessage(HttpMethod.Post, existing)
        {
            Content = new StringContent("", Encoding.UTF8, mediaType)
        };
        conditional.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var rejected = await client.SendAsync(conditional);
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, rejected.StatusCode);
        Assert.AreEqual(before, await client.GetStringAsync(existing));
    }
}
