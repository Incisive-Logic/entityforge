using System.Net;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Sparql_graph_store_and_management_bound_large_reads_and_preserve_unselected_graphs()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        var quads = Enumerable.Range(0, 5002).Select(i => new RdfQuad(new RdfIri("urn:capacity:" + i),
            new("urn:p"), new RdfLiteral("v"), i >= 5000 ? new RdfIri("urn:named") : null)).ToArray();
        await runtime.RdfDatabase.BulkLoadAsync(Guid.NewGuid(), quads);
        using var client = Client(host);
        using var tooLarge = await client.GetAsync("/sparql/graphs?default");
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, tooLarge.StatusCode, await tooLarge.Content.ReadAsStringAsync());
        StringAssert.Contains(await tooLarge.Content.ReadAsStringAsync(), "rdf_sparql_work_limit_exceeded");
        await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(async () =>
            await runtime.RdfDatabase.CommitSparqlUpdateAsync(Guid.NewGuid(), "CLEAR DEFAULT"));
        CollectionAssert.AreEquivalent(quads, runtime.RdfDatabase.Find(new()).ToArray());
        await runtime.RdfDatabase.CommitSparqlUpdateAsync(Guid.NewGuid(), "CLEAR NAMED");
        CollectionAssert.AreEquivalent(quads.Take(5000).ToArray(), runtime.RdfDatabase.Find(new()).ToArray());
    }
}
