using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Kernel;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public void Root_lookup_capture_owns_every_selection_and_validates_complete_batch()
    {
        var query = new PortableEntityQueryRequest("urn:contract:item", new("urn:contract:item"),
            Filter: new("propertyComparison", "urn:contract:name", "equal", new(JsonSerializer.SerializeToElement("selected"))),
            ConceptMatchMode: "direct");
        var selection = new PartitionQueryExchangeRequest("entity", EntityQuery: query, ProfileFragments: true);
        var column = new DatabaseRootIndexColumn("semantic-concept-v1", "");
        PartitionRootIndexDemand Demand() => new(Guid.NewGuid(), column, "urn:contract:item", selection);
        var inputs = new[] { Demand(), Demand() };
        var request = new PartitionRootIndexRequest(Guid.NewGuid(), column, inputs);
        var owned = PartitionRootIndexRouting.Capture(request, default);
        var fingerprint = owned.Fingerprint;
        Assert.AreNotSame(inputs, owned.Lookups);
        for (int i = 0; i < inputs.Length; i++)
        {
            Assert.AreEqual(inputs[i].Id, owned.Lookups![i].Id);
            Assert.AreNotSame(inputs[i].Selection.EntityQuery, owned.Lookups[i].Selection.EntityQuery);
        }
        inputs[1] = Demand() with
        {
            Value = "urn:changed"
        };
        Assert.AreEqual("urn:contract:item", owned.Lookups![1].Value);
        Assert.AreEqual(fingerprint, owned.Fingerprint);
        foreach (var invalid in new[]
        {
            Demand() with { Id = Guid.Empty }, Demand() with { Column = new("other", "") },
            Demand() with { Value = "urn:other" }, Demand() with { Selection = selection with { Offset = 1 } },
            Demand() with { Selection = selection with { CompleteQuery = true } }
        })
        {
            Assert.ThrowsExactly<InvalidDataException>(() => PartitionRootIndexRouting.Capture(request with { Lookups = [Demand(), invalid] }, default));
        }

        var duplicate = Demand();
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionRootIndexRouting.Capture(request with { Lookups = [duplicate, duplicate] }, default));
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionRootIndexRouting.Capture(request with { Lookups = [] }, default));
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionRootIndexRouting.Capture(request with { Lookups = Enumerable.Range(0, 33).Select(_ => Demand()).ToArray() }, default));
        Assert.AreEqual(32, PartitionRootIndexRouting.Capture(request with
        {
            Lookups = Enumerable.Range(0, 32).Select(_ => Demand()).ToArray()
        }, default).Lookups!.Length);
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => PartitionRootIndexRouting.Capture(request, stopped.Token));
    }
}
