using EntityForge.Db.Query.Adapters;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Malformed_native_iri_or_datatype_rejects_the_whole_batch_and_allows_correction(bool datatype)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        var valid = new RdfMutationWriteRequest("assert", new(new("iri", Value: "urn:valid"),
            new("iri", Value: "urn:p"), new("literal", LexicalForm: "kept")));
        var malformed = datatype
            ? new RdfTermWriteRequest("literal", LexicalForm: "bad", Datatype: "urn:bad type")
            : new RdfTermWriteRequest("iri", Value: "urn:bad%ZZ");
        Guid operation = Guid.NewGuid();
        using var rejected = await client.PostAsJsonAsync("/v1/rdf/transactions", new RdfMutationBatchRequest(operation,
            [valid, new("assert", new(new("iri", Value: "urn:bad"), new("iri", Value: "urn:p"), malformed))]));
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
        using (var empty = await client.GetAsync("/sparql?query=ASK%20%7B%20%3Fs%20%3Fp%20%3Fo%20%7D"))
        {
            Assert.AreEqual(HttpStatusCode.OK, empty.StatusCode);
            using var document = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
            Assert.IsFalse(document.RootElement.GetProperty("boolean").GetBoolean());
        }
        using var corrected = await client.PostAsJsonAsync("/v1/rdf/transactions", new RdfMutationBatchRequest(operation, [valid]));
        Assert.AreEqual(HttpStatusCode.OK, corrected.StatusCode, await corrected.Content.ReadAsStringAsync());
        using var populated = await client.GetAsync("/sparql?query=ASK%20%7B%20%3Fs%20%3Fp%20%3Fo%20%7D");
        Assert.AreEqual(HttpStatusCode.OK, populated.StatusCode);
        using var result = JsonDocument.Parse(await populated.Content.ReadAsStringAsync());
        Assert.IsTrue(result.RootElement.GetProperty("boolean").GetBoolean());
    }
}
