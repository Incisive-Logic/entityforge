using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public void Scalar_frontier_job_contract_rejects_alternate_languages_flags_and_continuations()
    {
        var query = new PortableEntityQueryRequest("urn:contract:item", new("urn:contract:item"),
            Filter: new("propertyComparison", "urn:contract:name", "equal", new(JsonSerializer.SerializeToElement("selected"))),
            ConceptMatchMode: "direct");
        var request = new PartitionQueryExchangeRequest("entity", EntityQuery: query, SemanticFrontier: new(0));
        var owned = PartitionQueryOwnedSelection.Capture(request);
        var interpretation = new PartitionQueryInterpretation(owned);
        interpretation.Require(PartitionQueryOwnedSelection.Capture(request with
        {
            SemanticFrontier = new(1, [])
        }));
        foreach (var invalid in new[]
        {
            request with { Language = "gql" }, request with { Statement = "other" },
            request with { CompleteQuery = true }, request with { GraphFragments = true },
            request with { ProfileFragments = true }, request with { IncludeSupportingGraph = true },
            request with { ContinueLocal = true }, request with { SemanticFrontier = null },
            request with { SemanticFrontier = new(2, []) },
            request with { GraphFrontier = new(0) },
            request with { EntityQuery = query with { First = 1 } }
        })
        {
            Assert.ThrowsExactly<InvalidDataException>(() => interpretation.Require(PartitionQueryOwnedSelection.Capture(invalid)));
        }
    }

    [TestMethod]
    public void Scalar_fragment_job_contract_binds_complete_projection_and_rejects_frontiers()
    {
        var query = new PortableEntityQueryRequest("urn:contract:item", new("urn:contract:item"),
            Filter: new("propertyComparison", "urn:contract:name", "equal", new(JsonSerializer.SerializeToElement("selected"))),
            ConceptMatchMode: "direct");
        var request = new PartitionQueryExchangeRequest("entity", EntityQuery: query, ProfileFragments: true);
        var interpretation = new PartitionQueryInterpretation(PartitionQueryOwnedSelection.Capture(request));
        interpretation.Require(PartitionQueryOwnedSelection.Capture(request with
        {
            Offset = 1
        }));
        foreach (var invalid in new[]
        {
            request with { SemanticFrontier = new(0) },
            request with { GraphFrontier = new(0) }, request with { ProfileFragments = false },
            request with { CompleteQuery = true }, request with { EntityQuery = query with { First = 1 } }
        })
        {
            Assert.ThrowsExactly<InvalidDataException>(() => interpretation.Require(PartitionQueryOwnedSelection.Capture(invalid)));
        }
    }
}
