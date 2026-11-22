using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(2)]
    [DataRow(20)]
    [DataRow(2, true)]
    [DataRow(20, true)]
    [DataRow(20, true, true)]
    public async Task Gql_nested_subpath_updates_preserve_atomicity_receipts_and_restart(int upperBound,
        bool mixedModes = false, bool named = false)
    {
        _directory = NewDirectory();
        var seed = new
        {
            statement = "INSERT (:NestedWrite {name:'A'})-[:step]->(:NestedWrite {name:'B'})-[:step]->(:NestedWrite {name:'C'})",
            transactionId = Guid.NewGuid()
        };
        string match = "MATCH (:NestedWrite {name:'A'})(segments=(inner=()-[:step]->() WHERE PATH_LENGTH(inner)=1)){1," + upperBound + "}";
        string hops = "CARDINALITY(inner)";
        if (mixedModes)
        {
            match = "MATCH " + (named ? "p=" : "") + "SIMPLE (TRAIL (:NestedWrite {name:'A'})-[edges:step]->{1," + upperBound + "}())";
            hops = named ? "PATH_LENGTH(p)" : "CARDINALITY(edges)";
        }
        var update = new
        {
            statement = match + " INSERT (:PathProof {hops:" + hops + "}) RETURN SUM(" + hops + ") AS answer",
            transactionId = Guid.NewGuid()
        };
        string? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", seed);
            Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
            for (int replay = 0; replay < 2; replay++)
            {
                using var written = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
                string body = await written.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, written.StatusCode, body);
                receipt ??= body;
                Assert.AreEqual(receipt, body);
                Assert.AreEqual(3, (await written.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result")
                    .GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
                await AssertScalar("MATCH (n:PathProof) RETURN COUNT(*) AS answer", 2);
                await AssertScalar("MATCH (n:PathProof) RETURN SUM(n.hops) AS answer", 3);
            }
            using var failed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = match + " INSERT (:RejectedPathProof) RETURN 1/0 AS answer",
                transactionId = Guid.NewGuid()
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, failed.StatusCode, await failed.Content.ReadAsStringAsync());
            await AssertScalar("MATCH (n:RejectedPathProof) RETURN COUNT(*) AS answer", 0);
            await AssertScalar("MATCH (n:NestedWrite) RETURN COUNT(*) AS answer", 3);
            await host.StopAsync();

            async Task AssertScalar(string query, int expected)
            {
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query
                });
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
                Assert.AreEqual(1, rows.GetArrayLength());
                Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            }
        }
    }
}
