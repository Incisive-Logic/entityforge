using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow('"')]
    [DataRow('`')]
    public async Task Gql_standard_delimited_labels_preserve_literal_names_and_replay(char delimiter)
    {
        _directory = NewDirectory();
        string Quote(string name) => delimiter + name.Replace(delimiter.ToString(), new string(delimiter, 2), StringComparison.Ordinal) + delimiter;
        string label = Quote("a:b|c\"`😀"), edge = Quote("edge:label"), percent = Quote("%"), added = Quote("new label:one"), property = Quote("k:x");
        var seed = new
        {
            statement = "INSERT (a:QuotedLabel & " + label + " {" + property + ":7})-[:" + edge + "]->(:OtherLabel),(:" + percent + "),(:PlainLabel)",
            transactionId = Guid.NewGuid()
        };
        var update = new
        {
            statement = "MATCH (n:QuotedLabel) SET n:" + added + " REMOVE n:" + label + " RETURN COUNT(*) AS answer",
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
            if (restart == 0)
            {
                foreach (var (query, expected) in new[]
                {
                    ("MATCH (n:" + label + ") RETURN COUNT(*) AS answer", 1),
                    ("MATCH ANY (n:" + label + ")-[e:" + edge + "]->(m) RETURN COUNT(*) AS answer", 1),
                    ("MATCH p=(n:" + label + ")-[e:" + edge + " WHERE TRUE]->+(m) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
                    ("MATCH p=(anchorStart:" + label + ")((a)-[e:" + edge + "]->(b))+(anchorEnd) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
                    ("MATCH (n:" + label + ") MATCH ANY (n)-[e:" + edge + " WHERE TRUE]->(m) RETURN COUNT(*) AS answer", 1),
                    ("MATCH (n:" + label + ") OPTIONAL MATCH (n)-[e:" + percent + "]->(m) RETURN COUNT(*) AS answer", 1),
                    ("MATCH (:" + label + " {" + property + ":7})-[:" + edge + "]->() RETURN COUNT(*) AS answer", 1),
                    ("MATCH (n) WHERE n IS LABELED (" + label + " & !MissingLabel) RETURN COUNT(*) AS answer", 1),
                    ("MATCH (n:" + percent + ") RETURN COUNT(*) AS answer", 1),
                    ("MATCH (n) WHERE n IS LABELED " + percent + " RETURN COUNT(*) AS answer", 1),
                    ("MATCH (n) WHERE n IS LABELED % RETURN COUNT(*) AS answer", 4),
                    ("MATCH ()-[e]->() WHERE e IS LABELED " + percent + " RETURN COUNT(*) AS answer", 0),
                    ("MATCH ()-[e]->() WHERE e IS LABELED % RETURN COUNT(*) AS answer", 1)
                })
                {
                    using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                    {
                        query
                    });
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
                    Assert.AreEqual(expected, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0]
                        .GetProperty("bindings").GetProperty("answer").GetInt32(), query);
                }
            }
            using var changed = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
            string body = await changed.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, body);
            receipt ??= body;
            Assert.AreEqual(receipt, body);
            using var verified = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:QuotedLabel) RETURN n IS LABELED " + added + " AND NOT (n IS LABELED " + label + ") AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, verified.StatusCode, await verified.Content.ReadAsStringAsync());
            Assert.IsTrue((await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0]
                .GetProperty("bindings").GetProperty("answer").GetBoolean());
            await host.StopAsync();
        }
    }
}
