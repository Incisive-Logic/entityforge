using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Hosting.Tests;

// Fixed generated ASCII values only; this is a workload adapter, not a general query builder.
internal sealed class BenchmarkNativeClient(HttpClient http, DatabaseProfileId profile, Guid incarnation)
{
    internal int AbortedAttempts;

    private bool Rdf => profile == DatabaseProfileId.Rdf;

    internal async Task<BenchmarkItem?> FindAsync(string id, CancellationToken token)
    {
        string query = Rdf ? $"SELECT ?value WHERE {{ <urn:bench:{id}> <urn:name> ?value }}"
            : $"MATCH (n:Bench) WHERE n.key='{id}' RETURN n.name AS answer";
        using var request = new HttpRequestMessage(HttpMethod.Post, Rdf ? "/sparql" : "/v1/graph/gql/query");
        request.Content = Rdf ? new StringContent(query, Encoding.UTF8, "application/sparql-query") : JsonContent.Create(new
        {
            query
        });
        if (Rdf)
        {
            request.Headers.Accept.ParseAdd("application/sparql-results+json");
        }
        using var response = await http.SendAsync(request, token);
        string body = await response.Content.ReadAsStringAsync(token);
        Assert.IsTrue(response.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        var rows = Rdf ? json.RootElement.GetProperty("results").GetProperty("bindings") : json.RootElement.GetProperty("matches");
        Assert.IsTrue(rows.GetArrayLength() <= 1, "Duplicate native identity.");
        return rows.GetArrayLength() == 0 ? null : new(id, Rdf ? rows[0].GetProperty("value").GetProperty("value").GetString()!
            : rows[0].GetProperty("bindings").GetProperty("answer").GetString()!);
    }

    internal async Task SaveAsync(BenchmarkItem item, CancellationToken token)
    {
        // The existence read is deliberately inside the measured operation.
        var before = await FindAsync(item.Id, token);
        string statement = Rdf
            ? $"DELETE WHERE {{ <urn:bench:{item.Id}> <urn:name> ?old }}; INSERT DATA {{ <urn:bench:{item.Id}> <urn:name> '{item.Name}' }}"
            : before is null ? $"INSERT (:Bench {{key:'{item.Id}', name:'{item.Name}'}})"
                : $"MATCH (n:Bench) WHERE n.key='{item.Id}' SET n.name='{item.Name}'";
        await UpdateAsync(statement, token);
    }

    internal Task DeleteAsync(string id, CancellationToken token) => UpdateAsync(Rdf
        ? $"DELETE WHERE {{ <urn:bench:{id}> <urn:name> ?old }}"
        : $"MATCH (n:Bench) WHERE n.key='{id}' DELETE n", token);

    internal async Task LoadAsync(IEnumerable<BenchmarkItem> items, CancellationToken token)
    {
        foreach (var batch in items.Chunk(128))
        {
            string statement = Rdf ? "INSERT DATA { " + string.Join(" ", batch.Select(item => $"<urn:bench:{item.Id}> <urn:name> '{item.Name}' .")) + " }"
                : "INSERT " + string.Join(",", batch.Select(item => $"(:Bench {{key:'{item.Id}', name:'{item.Name}'}})"));
            await UpdateAsync(statement, token);
        }
    }

    private async Task UpdateAsync(string statement, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            Guid transactionId = Guid.NewGuid();
            using var response = Rdf ? await http.PostAsync("/sparql/update", new StringContent(statement, Encoding.UTF8, "application/sparql-update"), token)
                : await http.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement,
                    transactionId
                }, token);
            string body = await response.Content.ReadAsStringAsync(token);
            if (!Rdf && response.StatusCode == HttpStatusCode.Conflict)
            {
                using var json = JsonDocument.Parse(body);
                Assert.AreEqual("write_conflict", json.RootElement.GetProperty("code").GetString(), body);
                var client = new HttpGraphEntityClient(http, incarnation);
                Assert.AreEqual(EntityOperationState.Aborted, (await client.AbortAsync(transactionId, token)).State);
                Interlocked.Increment(ref AbortedAttempts);
                Assert.IsTrue(attempt < 15, "Native write retry budget exhausted.");
                await Task.Delay(Math.Min(128, 1 << attempt), token);
                continue;
            }
            Assert.AreEqual(Rdf ? HttpStatusCode.NoContent : HttpStatusCode.OK, response.StatusCode, body);
            return;
        }
    }
}
