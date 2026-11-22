using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("text/turtle", "_:b <urn:p> <relative> .")]
    [DataRow("application/n-triples", "_:b <urn:p> <urn:target> .")]
    [DataRow("application/rdf+xml", "<rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#' xmlns:x='urn:'><rdf:Description rdf:nodeID='b'><x:p rdf:resource='relative'/></rdf:Description></rdf:RDF>")]
    public async Task Sparql11_load_is_atomic_resolves_document_base_and_replays_after_restart(string contentType, string body)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int fetched = 0;
        server.MapGet("/graph", () =>
        {
            Interlocked.Increment(ref fetched);
            return Results.Text(body, contentType);
        });
        await server.StartAsync();
        var source = new Uri(server.Urls.Single() + "/graph");
        string directory = CreateDirectory();
        Guid transaction = Guid.NewGuid();
        string update = $"LOAD <{source}> INTO GRAPH <urn:g>; INSERT {{ ?s <urn:copied> ?v }} WHERE {{ GRAPH <urn:g> {{ ?s <urn:p> ?v }} }}";
        RdfSparqlUpdateCommit? first = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await RdfDatabase.OpenLocalAsync(new(directory)
            {
                HttpResources = new([source], allowPrivateNetwork: true)
            });
            var receipt = await database.CommitSparqlUpdateAsync(transaction, update);
            first ??= receipt;
            Assert.AreEqual(first, receipt);
            Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(transaction, update));
            var quads = database.Find(new());
            Assert.AreEqual(2, quads.Count);
            Assert.IsInstanceOfType<RdfBlankNode>(quads[0].Subject);
            Assert.AreEqual(quads[0].Subject, quads[1].Subject);
            Assert.AreEqual(new RdfIri(contentType == "application/n-triples" ? "urn:target" : new Uri(source, "relative").AbsoluteUri), quads[0].Object);
            Assert.AreEqual(1, fetched, "A replay must not fetch the document again.");
        }
        await using (var separate = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([source], allowPrivateNetwork: true)
        }))
        {
            await separate.CommitSparqlUpdateAsync(Guid.NewGuid(), $"LOAD <{source}>; LOAD <{source}>");
            var quads = separate.Find(new());
            Assert.AreEqual(2, quads.Count);
            Assert.AreEqual(2, quads.Select(quad => quad.Subject).Distinct().Count());
            Assert.IsTrue(quads.All(quad => quad.Graph is null));
        }
        await server.StopAsync();
    }

    [TestMethod]
    public async Task Sparql11_load_allowlist_errors_and_silent_failures_preserve_atomicity()
    {
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory()));
        string command = "INSERT DATA { <urn:new> <urn:p> 1 }; LOAD <http://127.0.0.1:1/denied>";
        await Assert.ThrowsExactlyAsync<RdfSparqlRemoteExecutionException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(), command).AsTask());
        Assert.AreEqual(0, database.Find(new()).Count);
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), command.Replace("; LOAD ", "; LOAD SILENT ", StringComparison.Ordinal));
        Assert.AreEqual(1, database.Find(new()).Count);
    }

    [TestMethod]
    public async Task Sparql11_load_cancellation_is_not_silenced()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapGet("/slow", async (HttpContext context) => await Task.Delay(Timeout.Infinite, context.RequestAborted));
        await server.StartAsync();
        var source = new Uri(server.Urls.Single() + "/slow");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([source], allowPrivateNetwork: true)
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAsync<OperationCanceledException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            $"INSERT DATA {{ <urn:new> <urn:p> 1 }}; LOAD SILENT <{source}>", cancellation.Token).AsTask());
        Assert.AreEqual(0, database.Find(new()).Count);
        await server.StopAsync();
    }

    [TestMethod]
    [DataRow("redirect")]
    [DataRow("missing")]
    [DataRow("doctype")]
    [DataRow("media")]
    public async Task Sparql11_load_rejects_redirects_invalid_documents_and_missing_resources(string mode)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int followed = 0;
        server.MapGet("/other", () =>
        {
            Interlocked.Increment(ref followed);
            return Results.Text("<urn:s> <urn:p> 1 .", "text/turtle");
        });
        server.MapGet("/graph", () => mode switch
        {
            "redirect" => (IResult)Results.Redirect("/other"),
            "missing" => Results.NotFound(),
            "doctype" => Results.Text("<!DOCTYPE rdf:RDF [<!ENTITY x SYSTEM 'file:///unreadable'>]><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'/>", "application/rdf+xml"),
            _ => Results.Text("not RDF", "application/octet-stream")
        });
        await server.StartAsync();
        var source = new Uri(server.Urls.Single() + "/graph");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([source], allowPrivateNetwork: true)
        });
        string update = $"INSERT DATA {{ <urn:new> <urn:p> 1 }}; LOAD <{source}>";
        await Assert.ThrowsExactlyAsync<RdfSparqlRemoteExecutionException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(), update).AsTask());
        Assert.AreEqual(0, database.Find(new()).Count);
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), update.Replace("; LOAD ", "; LOAD SILENT ", StringComparison.Ordinal));
        Assert.AreEqual(1, database.Find(new()).Count);
        Assert.AreEqual(0, followed);
        await server.StopAsync();
    }

    [TestMethod]
    [DataRow("bytes")]
    [DataRow("triples")]
    [DataRow("depth")]
    public async Task Sparql11_load_work_limits_abort_even_silent_sequences(string mode)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        string body = mode switch
        {
            "bytes" => "#" + new string('x', 4 * 1024 * 1024),
            "triples" => String.Join('\n', Enumerable.Range(0, 4097).Select(index => $"<urn:s{index}> <urn:p> 1 .")),
            _ => "<urn:s> <urn:p> " + String.Concat(Enumerable.Repeat("[ <urn:p> ", 65)) + "<urn:o> " + new string(']', 65) + " ."
        };
        server.MapGet("/graph", () => Results.Text(body, "text/turtle"));
        await server.StartAsync();
        var source = new Uri(server.Urls.Single() + "/graph");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([source], allowPrivateNetwork: true)
        });
        await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            $"INSERT DATA {{ <urn:new> <urn:p> 1 }}; LOAD SILENT <{source}>").AsTask());
        Assert.AreEqual(0, database.Find(new()).Count);
        await server.StopAsync();
    }
}
