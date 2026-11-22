using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Server_generated_query_operation_ids_work_after_local_retention(bool rdf)
    {
        _directory = NewDirectory();
        var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory)
            .ConfigureServices(services => services.AddSingleton<IEntityForgeDbAuthorizationPolicy, AllowEntityForgeDbAuthorizationPolicy>())
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"));
        await using var host = (rdf ? builder.UseRdfProfile() : builder.UsePropertyGraphProfile()).Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        IDatabaseOperationRetention retention = rdf ? runtime.RdfDatabase : runtime.GraphDatabase;
        await retention.RetainOperationsAsync(DateTimeOffset.UnixEpoch.AddMilliseconds(1));
        using var http = Client(host);
        using var response = rdf
            ? await http.PostAsync("/sparql/update", new StringContent("INSERT { <urn:retained> <urn:value> '1' } WHERE {}", Encoding.UTF8, "application/sparql-update"))
            : await http.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = "INSERT (:Retained {key:1})"
            });
        Assert.AreEqual(rdf ? HttpStatusCode.NoContent : HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var query = rdf
            ? await http.PostAsync("/sparql", new StringContent("SELECT ?v WHERE { <urn:retained> <urn:value> ?v }", Encoding.UTF8, "application/sparql-query"))
            : await http.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:Retained) RETURN n.key AS answer"
            });
        Assert.AreEqual(HttpStatusCode.OK, query.StatusCode);
        using var body = System.Text.Json.JsonDocument.Parse(await query.Content.ReadAsStringAsync());
        var rows = rdf ? body.RootElement.GetProperty("results").GetProperty("bindings") : body.RootElement.GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
    }
}
