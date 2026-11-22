using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false, false, 1)]
    [DataRow(true, false, 1)]
    [DataRow(false, true, 1)]
    [DataRow(true, true, 1)]
    [DataRow(false, false, 3)]
    [DataRow(true, false, 3)]
    [DataRow(false, true, 3)]
    [DataRow(true, true, 3)]
    public async Task Native_slow_HTTP_scan_releases_admission_and_snapshots(bool rdf, bool disconnect, int cycles)
    {
        _directory = NewDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken token = deadline.Token;
        var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory)
            .UseQueryScans(new()
            {
                MaximumActiveScans = 1,
                PageSize = 1,
                Timeout = disconnect ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(3)
            })
            .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0").ConfigureLogging(log => log.ClearProviders()));
        await using var host = (rdf ? builder.UseRdfProfile() : builder.UsePropertyGraphProfile()).Build();
        await host.StartAsync(token);
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        string payload = new('x', 8192);
        if (rdf)
        {
            await runtime.RdfDatabase.BulkLoadAsync(Guid.CreateVersion7(), Enumerable.Range(0, 1024)
                .Select(i => new RdfQuad(new RdfIri("urn:slow:" + i), new("urn:payload"), new RdfLiteral(payload))).ToArray(), token);
        }
        else
        {
            await runtime.GraphDatabase.BulkLoadAsync(Guid.CreateVersion7(), Enumerable.Range(0, 1024)
                .Select(_ => new GraphVertex(GraphVertexId.New(), new HashSet<string> { "SlowScan" },
                    new Dictionary<string, DatabaseValue> { ["payload"] = new DatabaseValue.String(payload) })).ToArray(), [], token);
        }
        using var writer = Client(host, WriterToken);
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            using var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, cancellation) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = 4096 };
                    try
                    {
                        await socket.ConnectAsync(context.DnsEndPoint, cancellation);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
            using var slow = new HttpClient(handler) { BaseAddress = writer.BaseAddress };
            slow.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ReaderToken);
            string route = rdf ? "/v1/rdf/sparql/scan" : "/v1/graph/gql/query/scan";
            string query = rdf ? "SELECT ?o WHERE { ?s <urn:payload> ?o }" : "MATCH (n:SlowScan) RETURN n.payload AS answer";
            using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(new { query }) };
            using var response = await slow.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var stream = await response.Content.ReadAsStreamAsync(token);
            using var lines = new StreamReader(stream);
            using var first = JsonDocument.Parse((await lines.ReadLineAsync(token))!);
            Assert.AreEqual("page", first.RootElement.GetProperty("type").GetString());
            Assert.AreEqual(1, first.RootElement.GetProperty("rows").GetArrayLength());
            Assert.AreEqual(1, runtime.ActiveQueryScans, "The native producer must still be active for this slow-consumer test.");
            using (var denied = await writer.PostAsJsonAsync(route, new
            {
                query
            }, token))
            {
                Assert.AreEqual(HttpStatusCode.TooManyRequests, denied.StatusCode);
            }
            using (var update = rdf
                ? await writer.PostAsync("/sparql/update", new StringContent($"INSERT DATA {{ <urn:while-slow:{cycle}> <urn:marker> 'ok' }}", Encoding.UTF8, "application/sparql-update"), token)
                : await writer.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = $"INSERT (:DuringSlowScan {{amount:{cycle}}})"
                }, token))
            {
                Assert.IsTrue(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync(token));
            }
            if (disconnect)
            {
                response.Dispose();
                slow.Dispose();
            }
            // Otherwise deliberately stop reading until the server's response-write
            // deadline aborts the real evaluator, rather than a synthetic producer.
            using var released = CancellationTokenSource.CreateLinkedTokenSource(token);
            released.CancelAfter(TimeSpan.FromSeconds(10));
            while (runtime.ActiveQueryScans != 0)
            {
                await Task.Delay(20, released.Token);
            }
            if (rdf)
            {
                Assert.AreEqual(0, runtime.RdfDatabase.RetainedQueryContinuationCount);
            }
            await runtime.CreateCheckpointAsync(token);
            string recoveryQuery = rdf ? $"SELECT ?o WHERE {{ <urn:while-slow:{cycle}> <urn:marker> ?o }}"
                : $"MATCH (n:DuringSlowScan) WHERE n.amount = {cycle} RETURN n.amount AS answer";
            using var recovered = await writer.PostAsJsonAsync(route, new
            {
                query = recoveryQuery
            }, token);
            Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
            string recoveredBody = await recovered.Content.ReadAsStringAsync(token);
            var frames = recoveredBody.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            Assert.AreEqual(1, frames.Count(frame => frame.GetProperty("type").GetString() == "complete"));
            Assert.IsTrue(frames[^1].GetProperty("complete").GetBoolean());
            var rows = frames.Where(frame => frame.GetProperty("type").GetString() == "page")
                .SelectMany(frame => frame.GetProperty("rows").EnumerateArray()).ToArray();
            Assert.AreEqual(1, rows.Length, "Recovery must return the write from this cycle exactly once.");
            if (rdf)
            {
                Assert.AreEqual("ok", rows[0].GetProperty("o").GetProperty("lexicalForm").GetString());
                Assert.AreEqual(0, runtime.RdfDatabase.RetainedQueryContinuationCount);
            }
            else
            {
                Assert.AreEqual(cycle, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            }
            Assert.AreEqual(0, runtime.ActiveQueryScans);
        }
    }
}
