using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false, false, "WITH")]
    [DataRow(false, false, "USING")]
    [DataRow(false, false, "USING NAMED")]
    [DataRow(false, true, "WITH")]
    [DataRow(false, true, "USING")]
    [DataRow(false, true, "USING NAMED")]
    [DataRow(true, false, "WITH")]
    [DataRow(true, false, "USING")]
    [DataRow(true, false, "USING NAMED")]
    [DataRow(true, true, "WITH")]
    [DataRow(true, true, "USING")]
    [DataRow(true, true, "USING NAMED")]
    public async Task Sparql11_protocol_dataset_conflicts_reject_entire_sequence(
        bool form, bool named, string clause)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        // Each position must be rejected before either an earlier insertion or a later clear can run.
        for (int position = 0; position < 3; position++)
        {
            using var seed = await client.PostAsync("/sparql/update", new StringContent(
                "INSERT DATA { <urn:sentinel> <urn:p> 1 . GRAPH <urn:g> { <urn:sentinel> <urn:p> 2 } }",
                Encoding.UTF8, "application/sparql-update"));
            Assert.AreEqual(HttpStatusCode.NoContent, seed.StatusCode, await seed.Content.ReadAsStringAsync());
            string conflict = clause == "WITH"
                ? "WITH <urn:g> DELETE { ?s ?p ?o } WHERE { ?s ?p ?o }"
                : "DELETE { ?s ?p ?o } " + clause + " <urn:g> WHERE { ?s ?p ?o }";
            var operations = new List<string>
            {
                "INSERT DATA { <urn:marker> <urn:text> 'WITH; USING WHERE' }",
                "CLEAR ALL"
            };
            operations.Insert(position, "# operation-local prolog and misleading keyword text\nPREFIX using: <urn:using;>\n" + conflict);
            string update = String.Join(";\n", operations);
            string selector = named ? "using-named-graph-uri" : "using-graph-uri";
            using HttpContent content = form
                ? new FormUrlEncodedContent(new Dictionary<string, string> { ["update"] = update, [selector] = "urn:g" })
                : new StringContent(update, Encoding.UTF8, "application/sparql-update");
            using var response = await client.PostAsync("/sparql/update" + (form ? "" : "?" + selector + "=urn%3Ag"), content);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                $"Conflict at position {position}: {await response.Content.ReadAsStringAsync()}");

            using var unchanged = await client.PostAsJsonAsync("/v1/rdf/sparql", new
            {
                query = "ASK { <urn:sentinel> <urn:p> 1 . GRAPH <urn:g> { <urn:sentinel> <urn:p> 2 } " +
                    "FILTER NOT EXISTS { <urn:marker> ?p ?o } }"
            });
            Assert.AreEqual(HttpStatusCode.OK, unchanged.StatusCode, await unchanged.Content.ReadAsStringAsync());
            Assert.IsTrue((await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("value").GetBoolean(),
                $"Rejected operation at position {position} changed the dataset.");

            // Exercise the identical sequence through the real endpoint without dataset parameters.
            // This excludes unrelated syntax errors as the reason for the negative result above.
            using var valid = await client.PostAsync("/sparql/update",
                new StringContent(update, Encoding.UTF8, "application/sparql-update"));
            Assert.AreEqual(HttpStatusCode.NoContent, valid.StatusCode, await valid.Content.ReadAsStringAsync());
            using var cleared = await client.PostAsJsonAsync("/v1/rdf/sparql", new
            {
                query = "ASK { { ?s ?p ?o } UNION { GRAPH ?g { ?s ?p ?o } } }"
            });
            Assert.AreEqual(HttpStatusCode.OK, cleared.StatusCode);
            Assert.IsFalse((await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("value").GetBoolean());
        }
    }
}
