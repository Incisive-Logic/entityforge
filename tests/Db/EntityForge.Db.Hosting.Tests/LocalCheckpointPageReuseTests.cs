using System.Reflection;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Ordinary_native_checkpoint_reuses_retired_pages_and_recovers_later_writes(bool rdf)
    {
        _directory = NewDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var vertex = GraphVertexId.New();
        RdfQuad Quad(int value) => new(new RdfIri("urn:reuse"), new("urn:value"),
            new RdfLiteral(value.ToString(System.Globalization.CultureInfo.InvariantCulture).PadRight(512, 'x')));
        EntityForgeDbHostBuilder Builder()
        {
            var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory)
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0").ConfigureLogging(log => log.ClearProviders()));
            return rdf ? builder.UseRdfProfile() : builder.UsePropertyGraphProfile();
        }
        int last = 0;
        await using (var host = Builder().Build())
        {
            await host.StartAsync(deadline.Token);
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            var kernel = rdf ? runtime.RdfDatabase.PartitionKernel : runtime.GraphDatabase.PartitionKernel;
            var profile = (IDatabaseProfileCheckpointMaintenance)typeof(DatabaseKernel)
                .GetField("_profile", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
            if (rdf)
            {
                await runtime.RdfDatabase.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(Quad(0))]), deadline.Token);
            }
            else
            {
                await runtime.GraphDatabase.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(vertex, ["Reuse"])]), deadline.Token);
            }
            GraphVertexId[] previousVertices = [];
            async Task Write(int value)
            {
                if (rdf)
                {
                    await runtime.RdfDatabase.CommitAsync(new(Guid.NewGuid(),
                        [new RetractRdfQuad(Quad(value - 1)), new AssertRdfQuad(Quad(value))]), deadline.Token);
                }
                else
                {
                    var mutations = previousVertices.Select(id => (GraphMutation)new DeleteVertex(id)).ToList();
                    previousVertices = Enumerable.Range(0, 64).Select(_ => GraphVertexId.New()).ToArray();
                    mutations.AddRange(previousVertices.Select(id => new CreateVertex(id, ["Churn"])));
                    mutations.Add(new SetVertexProperty(vertex, "value", new DatabaseValue.String(Quad(value).Object.ToString()!)));
                    await runtime.GraphDatabase.CommitAsync(new(Guid.NewGuid(), mutations), deadline.Token);
                }
            }
            while (!profile.RequiresCheckpointMaintenance && last < 8192)
            {
                await Write(++last);
            }
            Assert.IsTrue(profile.RequiresCheckpointMaintenance, "Fixture must create real retired-page debt.");
            await runtime.CreateCheckpointAsync(deadline.Token);
            Assert.IsFalse(profile.RequiresCheckpointMaintenance, "Ordinary checkpoint left free pages quarantined.");
            for (int i = 0; i < 100; i++)
            {
                await Write(++last);
            }
        }
        await using var recovered = Builder().Build();
        await recovered.StartAsync(deadline.Token);
        var restored = recovered.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        if (rdf)
        {
            CollectionAssert.AreEqual(new[] { Quad(last) }, restored.RdfDatabase.Find(new()).ToArray());
        }
        else
        {
            Assert.AreEqual(new DatabaseValue.String(Quad(last).Object.ToString()!), restored.GraphDatabase.GetVertex(vertex)!.Properties["value"]);
        }
    }
}
