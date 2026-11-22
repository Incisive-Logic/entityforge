using EntityForge.Db.Rdf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Sparql11_SERVICE_cancelled_in_flight_preserves_state_and_allows_retry(bool update, bool silent)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int fetched = 0;
        server.MapPost("/sparql", async (HttpContext context) =>
        {
            if (Interlocked.Increment(ref fetched) == 1)
            {
                entered.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(context.RequestAborted);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                }
            }
            return Results.Text(ServiceRows, "application/sparql-results+json");
        });
        await server.StartAsync();
        try
        {
            var endpoint = new Uri(server.Urls.Single() + "/sparql");
            await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
            {
                HttpResources = new([endpoint], allowPrivateNetwork: true)
            });
            var version = database.CurrentVersion;
            Guid transaction = Guid.NewGuid();
            string body = $"SERVICE {(silent ? "SILENT " : "")}<{endpoint}> {{ ?s <urn:p> ?v }}";
            string query = "SELECT ?s ?v WHERE { " + body + " }";
            string statement = "INSERT DATA { <urn:staged> <urn:p> 9 }; INSERT { ?s <urn:copy> ?v } WHERE { " + body + " }";
            using var cancellation = new CancellationTokenSource();
            Task pending = update
                ? database.CommitSparqlUpdateAsync(transaction, statement, cancellation.Token).AsTask()
                : database.QuerySparqlAsync(query, cancellation.Token).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(version, database.CurrentVersion);
            Assert.AreEqual(0, database.Find(new()).Count);
            release.TrySetResult();

            if (update)
            {
                await database.CommitSparqlUpdateAsync(transaction, statement);
                Assert.AreEqual(3, database.Find(new()).Count);
                Assert.AreNotEqual(version, database.CurrentVersion);
                await database.CommitSparqlUpdateAsync(transaction, statement);
                Assert.AreEqual(2, fetched, "A successful retry receipt must prevent another remote fetch.");
            }
            else
            {
                var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
                Assert.AreEqual(2, result.Bindings.Count);
                Assert.AreEqual(version, database.CurrentVersion);
                Assert.AreEqual(2, fetched);
            }
        }
        finally
        {
            release.TrySetResult();
            await server.StopAsync();
        }
    }
}
