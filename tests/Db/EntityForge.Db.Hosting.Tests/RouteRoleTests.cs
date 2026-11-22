using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Bearer_security_checks_native_rdf_update_with_trailing_slash_before_mutation()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var reader = Client(host, ReaderToken);
        using var writer = Client(host, WriterToken);
        const string path = "/v1/rdf/sparql/update/";
        var update = new
        {
            transactionId = Guid.NewGuid(),
            update = "INSERT DATA { <urn:role> <urn:p> 'protected' }"
        };
        using var denied = await reader.PostAsJsonAsync(path, update);
        Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var before = await reader.PostAsJsonAsync("/v1/rdf/sparql", new
        {
            query = "ASK { <urn:role> <urn:p> 'protected' }"
        });
        using var beforeJson = System.Text.Json.JsonDocument.Parse(await before.Content.ReadAsStringAsync());
        Assert.IsFalse(beforeJson.RootElement.GetProperty("value").GetBoolean());
        using var allowed = await writer.PostAsJsonAsync(path, update);
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
        using var after = await reader.PostAsJsonAsync("/v1/rdf/sparql", new
        {
            query = "ASK { <urn:role> <urn:p> 'protected' }"
        });
        using var afterJson = System.Text.Json.JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        Assert.IsTrue(afterJson.RootElement.GetProperty("value").GetBoolean());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Bearer_security_rejects_lower_roles_on_admin_and_mutation_routes(bool rdf)
    {
        _directory = NewDirectory();
        var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory)
            .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"));
        await using var host = (rdf ? builder.UseRdfProfile() : builder.UsePropertyGraphProfile()).Build();
        await host.StartAsync();
        using var reader = Client(host, ReaderToken);
        using var writer = Client(host, WriterToken);
        string id = "11111111-1111-1111-1111-111111111111";
        string[] administration =
        ["GET /v1/administration/target", $"GET /v1/administration/jobs/{id}", $"POST /v1/administration/jobs/{id}",
            "GET /v1/telemetry", "GET /v1/observability/metrics", "GET /v1/storage-profile", "POST /v1/storage-profile/probe",
            "GET /v1/status", "POST /v1/checkpoints", "POST /v1/backups", "POST /v1/archives"];
        string[] mutations = rdf
            ? ["POST /v1/rdf/transactions", "POST /v1/rdf/document-pages", "POST /v1/rdf/import",
                "POST /v1/rdf/sparql/update", "POST /sparql/update", "PUT /sparql/graphs", "POST /sparql/graphs",
                "DELETE /sparql/graphs", "PUT /sparql/graphs/urn:test", "POST /sparql/graphs/urn:test", "DELETE /sparql/graphs/urn:test"]
            : ["POST /v1/graph/batches", "POST /v1/graph/vertices", "POST /v1/graph/edges",
                $"POST /v1/graph/vertices/{id}/properties", $"POST /v1/graph/edges/{id}/properties", "POST /v1/graph/gql/update"];
        foreach (string route in administration.Concat(mutations))
        {
            int separator = route.IndexOf(' ');
            string method = route[..separator], path = route[(separator + 1)..];
            foreach (string variant in new[] { path, path + "/", path.ToUpperInvariant() })
            {
                foreach (var client in administration.Contains(route) ? new[] { reader, writer } : new[] { reader })
                {
                    using var request = new HttpRequestMessage(new HttpMethod(method), variant)
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json")
                    };
                    using var response = await client.SendAsync(request);
                    Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, method + " " + variant);
                }
            }
        }
    }
}
