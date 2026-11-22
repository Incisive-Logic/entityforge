using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class PublicContractOwnershipTests
{
    [TestMethod]
    public void Versions_and_graph_identifiers_reject_invalid_explicit_values()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DatabaseVersion(-1));
        Assert.ThrowsExactly<ArgumentException>(() => new GraphVertexId(Guid.Empty));
        Assert.ThrowsExactly<ArgumentException>(() => new GraphEdgeId(Guid.Empty));
        Assert.IsTrue(new DatabaseVersion(2) > new DatabaseVersion(1));
    }

    [TestMethod]
    public void Graph_results_own_their_collection_state()
    {
        var labels = new HashSet<string>(StringComparer.Ordinal) { "Company" };
        var properties = new Dictionary<string, DatabaseValue>(StringComparer.Ordinal)
        {
            ["name"] = new DatabaseValue.String("Acuity")
        };
        var vertex = new GraphVertex(GraphVertexId.New(), labels, properties);

        labels.Add("ChangedAfterConstruction");
        properties["name"] = new DatabaseValue.String("Changed");

        CollectionAssert.AreEquivalent(new[] { "Company" }, vertex.Labels.ToArray());
        Assert.AreEqual(new DatabaseValue.String("Acuity"), vertex.Properties["name"]);
    }

    [TestMethod]
    public void Rdf_quads_are_construct_valid_and_document_pages_own_their_collection()
    {
        var predicate = new RdfIri("https://example.test/name");
        var quad = new RdfQuad(new RdfIri("https://example.test/company"), predicate,
            new RdfLiteral("Acuity"));
        var quads = new List<RdfQuad> { quad };
        var page = new RdfDocumentPage(0, quads);

        quads.Clear();

        Assert.AreEqual(1, page.Quads.Count);
        Assert.ThrowsExactly<ArgumentException>(() => new RdfQuad(
            new RdfLiteral("invalid subject"), predicate, new RdfLiteral("value")));
        Assert.ThrowsExactly<ArgumentException>(() => quad with { Graph = new RdfLiteral("invalid graph") });
    }
}
