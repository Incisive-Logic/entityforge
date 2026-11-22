using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("/sparql/graphs/metadata")]
    [DataRow("/sparql/graphs?graph=urn%3Ametadata")]
    [DataRow("/sparql/graphs?default")]
    public async Task Sparql_graph_store_HEAD_matches_GET_and_unsupported_methods_preserve_graph(string path)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var writer = Client(host, WriterToken);
        using var reader = Client(host, ReaderToken);
        using var seed = await writer.PutAsync(path,
            new StringContent("<urn:s> <urn:p> \"Zoë 雪\" .", Encoding.UTF8, "text/turtle"));
        Assert.IsTrue(seed.IsSuccessStatusCode, await seed.Content.ReadAsStringAsync());
        string before = await reader.GetStringAsync(path);
        foreach (string media in new[] { "text/turtle", "application/n-triples", "application/rdf+xml", "application/ld+json" })
        {
            using var getRequest = new HttpRequestMessage(HttpMethod.Get, path);
            getRequest.Headers.Accept.ParseAdd(media);
            using var get = await reader.SendAsync(getRequest);
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, path);
            headRequest.Headers.Accept.ParseAdd(media);
            using var head = await reader.SendAsync(headRequest);
            Assert.AreEqual(HttpStatusCode.OK, get.StatusCode, media);
            Assert.AreEqual(HttpStatusCode.OK, head.StatusCode, media);
            byte[] representation = await get.Content.ReadAsByteArrayAsync();
            Assert.IsTrue(representation.Length > 0, media);
            Assert.AreEqual(0, (await head.Content.ReadAsByteArrayAsync()).Length, media);
            Assert.AreEqual(media, get.Content.Headers.ContentType!.MediaType);
            Assert.AreEqual(get.Content.Headers.ContentType.ToString(), head.Content.Headers.ContentType!.ToString());
            Assert.AreEqual((long?)representation.Length, get.Content.Headers.ContentLength, media);
            Assert.AreEqual((long?)representation.Length, head.Content.Headers.ContentLength, media);
        }
        foreach (string method in new[] { "PATCH", "OPTIONS", "TRACE" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path)
            {
                Content = new StringContent("not a supported request", Encoding.UTF8, "text/turtle")
            };
            using var response = await writer.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode, method);
            CollectionAssert.AreEquivalent(new[] { "GET", "HEAD", "PUT", "POST", "DELETE" },
                response.Content.Headers.Allow.ToArray(), method);
            Assert.AreEqual(before, await reader.GetStringAsync(path), method);
        }
        await host.StopAsync();
    }
}
