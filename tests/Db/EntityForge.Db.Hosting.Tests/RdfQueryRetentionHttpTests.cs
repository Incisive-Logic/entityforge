using System.Net;
using System.Net.Http.Json;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RDF_oversized_quad_page_releases_advanced_cursor_and_allows_restart(bool resumed)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseRdfQueryPaging(new()
            {
                MaximumActiveContinuations = 1
            })
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        string literal = new('x', 250_000);
        for (int batch = 0; batch < 8; batch++)
        {
            await runtime.RdfDatabase.CommitAsync(new(Guid.NewGuid(), Enumerable.Range(batch * 10, 10)
                .Select(index => (RdfMutation)new AssertRdfQuad(new(new RdfIri($"urn:large:{index:D3}"),
                    new("urn:p"), new RdfLiteral(literal)))).ToArray()));
        }
        using var client = Client(host);
        string? continuation = null;
        if (resumed)
        {
            using var first = await client.PostAsJsonAsync("/v1/rdf/quads", new RdfQuadQueryRequest(new(), 1));
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            continuation = (await first.Content.ReadFromJsonAsync<RdfQueryPage>())!.NextContinuation;
            Assert.IsNotNull(continuation);
        }
        using var rejected = await client.PostAsJsonAsync("/v1/rdf/quads", new RdfQuadQueryRequest(new(), 70, continuation));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        string problem = await rejected.Content.ReadAsStringAsync();
        Assert.IsTrue(problem.Length < 4096);
        StringAssert.Contains(problem, "query_response_size_exceeded");
        Assert.AreEqual(0, ((RdfDatabaseEngine)runtime.RdfDatabase).RetainedQueryContinuationCount);
        if (continuation is not null)
        {
            await Assert.ThrowsExactlyAsync<RdfQueryContinuationException>(async () =>
                await runtime.RdfDatabase.QueryPageAsync(new(), 1, continuation));
        }
        var subjects = new HashSet<RdfTerm>();
        continuation = null;
        do
        {
            using var response = await client.PostAsJsonAsync("/v1/rdf/quads", new RdfQuadQueryRequest(new(), 8, continuation));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var page = (await response.Content.ReadFromJsonAsync<RdfQueryPage>())!;
            foreach (var quad in page.Quads)
            {
                Assert.IsTrue(subjects.Add(quad.Subject), "A restarted read must not duplicate a quad.");
                Assert.AreEqual(literal, ((RdfLiteral)quad.Object).LexicalForm);
            }
            continuation = page.NextContinuation;
        } while (continuation is not null);
        Assert.AreEqual(80, subjects.Count);
        Assert.AreEqual(0, ((RdfDatabaseEngine)runtime.RdfDatabase).RetainedQueryContinuationCount);
    }

    [TestMethod]
    public async Task RDF_query_capacity_is_429_and_reader_can_release_a_continuation()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseRdfQueryPaging(new()
            {
                MaximumActiveContinuations = 1
            })
            .UseBearerTokenSecurity(new(AdministratorToken, readerToken: ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await SeedHttpQueryRetentionAsync(runtime);
        using var reader = Client(host, ReaderToken);
        using var first = await reader.PostAsJsonAsync("/v1/rdf/quads", new RdfQuadQueryRequest(new(), 1));
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        var page = (await first.Content.ReadFromJsonAsync<RdfQueryPage>())!;
        foreach (string route in new[] { "/v1/rdf/quads", "/v1/rdf/quads/stream" })
        {
            using var rejected = await reader.PostAsJsonAsync(route, new RdfQuadQueryRequest(new(), 1));
            Assert.AreEqual(HttpStatusCode.TooManyRequests, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
            StringAssert.Contains(await rejected.Content.ReadAsStringAsync(), "rdf_query_capacity_exceeded");
        }
        string release = "/v1/rdf/quads/continuations/" + page.NextContinuation;
        using var anonymous = Client(host);
        anonymous.DefaultRequestHeaders.Authorization = null;
        using var denied = await anonymous.DeleteAsync(release);
        Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var released = await reader.DeleteAsync(release);
        Assert.AreEqual(HttpStatusCode.NoContent, released.StatusCode);
        using var repeated = await reader.DeleteAsync(release);
        Assert.AreEqual(HttpStatusCode.NoContent, repeated.StatusCode);
        using var complete = await reader.PostAsJsonAsync("/v1/rdf/quads", new RdfQuadQueryRequest(new(), 100));
        Assert.AreEqual(HttpStatusCode.OK, complete.StatusCode);
        Assert.AreEqual(0, ((RdfDatabaseEngine)runtime.RdfDatabase).RetainedQueryContinuationCount);
    }

    [TestMethod]
    [DataRow(false, false, null)]
    [DataRow(true, false, null)]
    [DataRow(false, true, null)]
    [DataRow(true, true, null)]
    [DataRow(false, false, "turtle")]
    [DataRow(true, false, "turtle")]
    [DataRow(false, false, "nquads")]
    [DataRow(true, false, "nquads")]
    [DataRow(false, false, "jsonld")]
    [DataRow(true, false, "jsonld")]
    public async Task RDF_quad_page_failed_delivery_releases_advanced_cursor(bool resumed, bool silentCancellation, string? format)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseRdfQueryPaging(new()
            {
                MaximumActiveContinuations = 1
            })
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await SeedHttpQueryRetentionAsync(runtime);
        string? continuation = resumed
            ? (await runtime.RdfDatabase.QueryPageAsync(new(MatchDefaultGraphOnly: format == "turtle"), 1)).NextContinuation : null;
        using var disconnected = new CancellationTokenSource();
        using var output = new PageDisconnectStream(disconnected, silentCancellation);
        var context = new DefaultHttpContext { RequestServices = host.Application.Services };
        context.RequestAborted = disconnected.Token;
        context.Response.Body = output;
        IResult result = format is null
            ? await RdfEntityForgeDbEndpoints.QuadPageAsync(context,
                new(new(), 1, continuation), runtime, disconnected.Token)
            : await RdfEntityForgeDbEndpoints.ExportAsync(context, runtime, format, 1, continuation, disconnected.Token);
        Assert.AreEqual(1, ((RdfDatabaseEngine)runtime.RdfDatabase).RetainedQueryContinuationCount);
        if (silentCancellation)
        {
            await result.ExecuteAsync(context);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => result.ExecuteAsync(context));
        }
        Assert.IsTrue(disconnected.IsCancellationRequested);
        Assert.AreEqual(0, ((RdfDatabaseEngine)runtime.RdfDatabase).RetainedQueryContinuationCount);
        var recovered = await runtime.RdfDatabase.QueryPageAsync(new(), 100);
        Assert.AreEqual(4, recovered.Quads.Count);
        Assert.IsNull(recovered.NextContinuation);
    }

    private sealed class PageDisconnectStream(CancellationTokenSource cancellation, bool silent) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return silent ? ValueTask.CompletedTask : ValueTask.FromException(new IOException("Simulated page delivery failure."));
        }
    }

    [TestMethod]
    public async Task RDF_quad_stream_disconnect_releases_the_new_page_cursor()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await SeedHttpQueryRetentionAsync(runtime);
        using var disconnected = new CancellationTokenSource();
        using var output = new QueryDisconnectStream(disconnected);
        var context = new DefaultHttpContext { RequestServices = host.Application.Services };
        context.RequestAborted = disconnected.Token;
        context.Response.Body = output;
        await Assert.ThrowsExactlyAsync<IOException>(() => RdfEntityForgeDbEndpoints.StreamQuadsAsync(context,
            new(new(), 1), runtime, disconnected.Token));
        Assert.AreEqual(0, ((RdfDatabaseEngine)runtime.RdfDatabase).RetainedQueryContinuationCount);
    }

    private static async Task SeedHttpQueryRetentionAsync(EntityForgeDbRuntime runtime)
    {
        await runtime.RdfDatabase.CommitAsync(new(Guid.NewGuid(), Enumerable.Range(0, 4).Select(index =>
            (RdfMutation)new AssertRdfQuad(new(new RdfIri("urn:retention:" + index), new("urn:p"), new RdfLiteral("value")))).ToArray()));
    }

    private sealed class QueryDisconnectStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromException(new IOException("Simulated client disconnect."));
        }
    }
}
