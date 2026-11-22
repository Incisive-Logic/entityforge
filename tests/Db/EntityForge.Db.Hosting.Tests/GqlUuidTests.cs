using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_rejects_uuid_extensions_without_persisting_writes()
    {
        _directory = NewDirectory();
        Guid transactionId = Guid.NewGuid();
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            using var written = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                transactionId,
                statement = "FOR i IN [1,2] INSERT (n:UuidCheck {token:uuid(),items:[uuid(0,0),uuid(-1,-1)]}) RETURN n.token AS answer"
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, written.StatusCode, await written.Content.ReadAsStringAsync());
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:UuidCheck) RETURN COUNT(*) AS total"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(0, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt32());
        }
    }
}
