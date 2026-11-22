using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_standard_property_maps_resolve_row_variables_before_path_selection()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:MapPerson {name:'Ada',age:37})-[:mapLink {active:FALSE}]->(b:MapPerson {name:'Bob',age:40}), (a)-[:mapLink {active:TRUE}]->(:MapPerson {name:'Cal',age:41})-[:mapLink {active:TRUE}]->(b)",
            transactionId = Guid.NewGuid()
        });
        Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
        foreach (var (query, expected) in new[]
        {
            ("LET requested='Ada' MATCH (n:MapPerson {name:requested}) RETURN n.age AS answer", 37),
            ("FOR requested IN ['Ada','Bob'] MATCH (n:MapPerson {name:requested}) RETURN SUM(n.age) AS answer", 77),
            ("LET requested='Missing' OPTIONAL MATCH (n:MapPerson {name:requested}) RETURN CASE WHEN COUNT(*)=1 AND COUNT(n)=0 THEN 1 ELSE 0 END AS answer", 1),
            ("LET requested=NULL MATCH (n:MapPerson {name:requested}) RETURN COUNT(*) AS answer", 0),
            ("LET wanted=TRUE MATCH p=ALL SHORTEST (a:MapPerson {name:'Ada'})-[e:mapLink {active:wanted}]->+(b:MapPerson {name:'Bob'}) RETURN PATH_LENGTH(p) AS answer", 2),
            ("LET wanted=TRUE MATCH p=(a:MapPerson {name:'Ada'}) ((x)-[e:mapLink {active:wanted}]->(y))+(b:MapPerson {name:'Bob'}) RETURN PATH_LENGTH(p) AS answer", 2),
            ("LET requested='Ada',expectedAge=37 MATCH (n:MapPerson {name:requested,age:expectedAge}) RETURN COUNT(*) AS answer", 1),
            ("MATCH (n:MapPerson {age:n.age}) RETURN COUNT(*) AS answer", 3),
            ("LET requested='Ada' MATCH (n:MapPerson {age:VALUE { MATCH (m:MapPerson {name:requested}) RETURN m.age AS \"m.age\" }}) RETURN n.age AS answer", 37)
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength(), query);
            Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32(), query);
        }
        await host.StopAsync();
    }
}
