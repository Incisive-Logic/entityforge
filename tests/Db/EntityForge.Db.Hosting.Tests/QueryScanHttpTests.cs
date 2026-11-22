using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Runtime.CompilerServices;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Query_scan_HTTP_deadline_and_disconnect_dispose_the_producer(bool disconnect)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseQueryScans(new()
            {
                Timeout = disconnect ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(200)
            })
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<object> Stalled([EnumeratorCancellation] CancellationToken cancellation)
        {
            try
            {
                yield return new
                {
                    type = "page",
                    rows = Array.Empty<object>()
                };
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            }
            finally
            {
                disposed.TrySetResult();
            }
        }
        host.Application.MapGet("/scan-lifecycle-test", (Microsoft.AspNetCore.Http.HttpContext context) =>
            QueryScanEndpoints.WriteAsync(context, runtime, Stalled, context.RequestAborted));
        await host.StartAsync();
        using var client = Client(host);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/scan-lifecycle-test");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var stream = await response.Content.ReadAsStreamAsync();
        using var lines = new StreamReader(stream);
        Assert.IsNotNull(await lines.ReadLineAsync());
        if (disconnect)
        {
            stream.Dispose();
            response.Dispose();
        }
        else
        {
            try
            {
                string? remaining = await lines.ReadLineAsync();
                Assert.IsNull(remaining, "A timed-out query emitted a completion frame.");
            }
            catch (IOException)
            {
                // The endpoint aborts an already started response on deadline.
            }
        }
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (int attempt = 0; runtime.ActiveQueryScans != 0 && attempt < 100; attempt++)
        {
            await Task.Delay(10);
        }
        Assert.AreEqual(0, runtime.ActiveQueryScans);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public async Task Query_scan_HTTP_streams_large_results_and_windows_with_explicit_completion(bool rdf, bool window)
    {
        _directory = NewDirectory();
        var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory)
            .UseBearerTokenSecurity(new(AdministratorToken, readerToken: ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"));
        if (rdf)
        {
            builder.UseRdfProfile();
        }
        else
        {
            builder.UsePropertyGraphProfile();
        }
        await using var host = builder.Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        if (rdf)
        {
            await runtime.RdfDatabase.BulkLoadAsync(Guid.NewGuid(), Enumerable.Range(0, 100_000)
                .Select(i => new RdfQuad(new RdfIri("urn:scan:" + i), new("urn:p"), new RdfLiteral("same"))).ToArray());
        }
        else
        {
            await runtime.GraphDatabase.BulkLoadAsync(Guid.NewGuid(), Enumerable.Range(0, 100_000)
                .Select(_ => new GraphVertex(GraphVertexId.New(), new HashSet<string>(), new Dictionary<string, DatabaseValue>())).ToArray(), []);
        }
        using var client = Client(host, ReaderToken);
        string route = rdf ? "/v1/rdf/sparql/scan" : "/v1/graph/gql/query/scan";
        string query = rdf ? "SELECT ?o WHERE { ?s <urn:p> ?o }" : "MATCH (n) RETURN n.missing AS answer";
        if (window)
        {
            query += " OFFSET 10000 LIMIT 80000";
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(new { query }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await FailureBody(response));
        Assert.AreEqual("application/x-ndjson", response.Content.Headers.ContentType!.MediaType);
        using var lines = new StreamReader(await response.Content.ReadAsStreamAsync());
        int count = 0, completions = 0;
        long? snapshot = null;
        while (await lines.ReadLineAsync() is { } line)
        {
            using var frame = JsonDocument.Parse(line);
            var root = frame.RootElement;
            if (root.GetProperty("type").GetString() == "complete")
            {
                Assert.IsTrue(root.GetProperty("complete").GetBoolean());
                completions++;
                continue;
            }
            Assert.AreEqual(0, completions, "Rows followed completion.");
            snapshot ??= root.GetProperty("snapshotVersion").GetInt64();
            Assert.AreEqual(snapshot.Value, root.GetProperty("snapshotVersion").GetInt64());
            var rows = root.GetProperty("rows");
            Assert.IsTrue(rows.GetArrayLength() <= 256);
            foreach (var row in rows.EnumerateArray())
            {
                if (rdf)
                {
                    Assert.AreEqual("same", row.GetProperty("o").GetProperty("lexicalForm").GetString());
                }
                else
                {
                    Assert.AreEqual(JsonValueKind.Null, row.GetProperty("bindings").GetProperty("answer").ValueKind);
                }
            }
            count += rows.GetArrayLength();
        }
        Assert.AreEqual(window ? 80_000 : 100_000, count);
        Assert.AreEqual(1, completions);
        Assert.AreEqual(0, runtime.ActiveQueryScans);
    }

    [TestMethod]
    public async Task Query_scan_HTTP_enforces_auth_admission_and_rejects_unsupported_plans()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseQueryScans(new()
            {
                MaximumActiveScans = 1
            })
            .UseBearerTokenSecurity(new(AdministratorToken, readerToken: ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        using var anonymous = Client(host);
        anonymous.DefaultRequestHeaders.Authorization = null;
        using var denied = await anonymous.PostAsJsonAsync("/v1/rdf/sparql/scan", new
        {
            query = "SELECT * WHERE { ?s ?p ?o }"
        });
        Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var reader = Client(host, ReaderToken);
        Assert.IsTrue(runtime.TryEnterQueryScan());
        try
        {
            using var full = await reader.PostAsJsonAsync("/v1/rdf/sparql/scan", new
            {
                query = "SELECT * WHERE { ?s ?p ?o }"
            });
            Assert.AreEqual(HttpStatusCode.TooManyRequests, full.StatusCode);
            StringAssert.Contains(await full.Content.ReadAsStringAsync(), "query_scan_capacity_exceeded");
            Assert.AreEqual(1, runtime.ActiveQueryScans);
        }
        finally
        {
            runtime.LeaveQueryScan();
        }
        using var unsupported = await reader.PostAsJsonAsync("/v1/rdf/sparql/scan", new
        {
            query = "SELECT * WHERE { ?s ?p ?o } ORDER BY ?s"
        });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, unsupported.StatusCode);
        Assert.AreEqual(0, runtime.ActiveQueryScans);
        using var recovered = await reader.PostAsJsonAsync("/v1/rdf/sparql/scan", new
        {
            query = "SELECT * WHERE { ?s ?p ?o }"
        });
        Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
        StringAssert.Contains(await recovered.Content.ReadAsStringAsync(), "\"complete\":true");
    }

    [TestMethod]
    public async Task Query_scan_HTTP_budget_failure_has_no_completion_and_recovers()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseQueryScans(new()
            {
                MaximumRows = 2,
                PageSize = 1
            })
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await SeedHttpQueryRetentionAsync(runtime);
        using var client = Client(host);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rdf/sparql/scan")
        {
            Content = JsonContent.Create(new { query = "SELECT * WHERE { ?s ?p ?o }" })
        };
        bool completed = false;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            using var lines = new StreamReader(await response.Content.ReadAsStreamAsync());
            while (await lines.ReadLineAsync() is { } line)
            {
                completed |= line.Contains("\"complete\":true", StringComparison.Ordinal);
            }
        }
        catch (HttpRequestException)
        {
            // The failed stream may terminate before response headers reach the client.
        }
        catch (IOException)
        {
            // A failed stream must not be mistaken for a complete answer.
        }
        Assert.IsFalse(completed);
        Assert.AreEqual(0, runtime.ActiveQueryScans);
        Assert.AreEqual(0, runtime.RdfDatabase.RetainedQueryContinuationCount);
        using var recovered = await client.PostAsJsonAsync("/v1/rdf/sparql/scan", new
        {
            query = "SELECT * WHERE { ?s ?p ?o } LIMIT 1"
        });
        Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
        StringAssert.Contains(await recovered.Content.ReadAsStringAsync(), "\"complete\":true");
    }

    private static async Task<string> FailureBody(HttpResponseMessage response) => response.IsSuccessStatusCode
        ? String.Empty : await response.Content.ReadAsStringAsync();
}
