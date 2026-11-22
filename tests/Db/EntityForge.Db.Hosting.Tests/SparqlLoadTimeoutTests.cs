using System.Diagnostics;
using System.Text;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_load_body_timeout_is_remote_failure_and_silent_preserves_sequence(bool silent)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int fetched = 0;
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] prefix = Encoding.UTF8.GetBytes("<urn:partial> <urn:p> 'never commit' .");
        server.MapGet("/graph", async (HttpContext context) =>
        {
            Interlocked.Increment(ref fetched);
            context.Response.ContentType = "text/turtle";
            context.Response.ContentLength = prefix.Length + 100;
            await context.Response.Body.WriteAsync(prefix, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            sent.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
        });
        await server.StartAsync();
        var source = new Uri(server.Urls.Single() + "/graph");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([source], allowPrivateNetwork: true)
        });
        var sentinel = new RdfQuad(new RdfIri("urn:keep"), new("urn:p"), new RdfLiteral("original"));
        await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(sentinel)]));
        var before = database.CurrentVersion;
        Guid operation = Guid.NewGuid();
        string update = "INSERT DATA { <urn:before> <urn:p> 'before' }; LOAD " +
            (silent ? "SILENT " : "") + $"<{source}>; INSERT DATA {{ <urn:after> <urn:p> 'after' }}";
        // Keep the production thirty-second remote deadline; the caller token is only a hang guard.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var elapsed = Stopwatch.StartNew();
        var pending = database.CommitSparqlUpdateAsync(operation, update, guard.Token).AsTask();
        await sent.Task.WaitAsync(guard.Token);
        if (silent)
        {
            var receipt = await pending;
            Assert.AreEqual(2, receipt.MutationCount);
            CollectionAssert.AreEquivalent(new RdfQuad[]
            {
                sentinel,
                new(new RdfIri("urn:before"), new("urn:p"), new RdfLiteral("before")),
                new(new RdfIri("urn:after"), new("urn:p"), new RdfLiteral("after"))
            }, database.Find(new()).ToArray());
            Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(operation, update, guard.Token));
        }
        else
        {
            await Assert.ThrowsExactlyAsync<RdfSparqlRemoteExecutionException>(() => pending);
            Assert.AreEqual(before, database.CurrentVersion);
            CollectionAssert.AreEquivalent(new[] { sentinel }, database.Find(new()).ToArray());
        }
        Assert.IsFalse(guard.IsCancellationRequested, "The remote deadline must fire before caller cancellation.");
        Assert.IsTrue(elapsed.Elapsed >= TimeSpan.FromSeconds(25), "The endpoint must stall until the real remote deadline.");
        Assert.AreEqual(1, fetched);
        Assert.HasCount(0, database.Find(new(Subject: new RdfIri("urn:partial"))));
        await server.StopAsync();
    }
}
