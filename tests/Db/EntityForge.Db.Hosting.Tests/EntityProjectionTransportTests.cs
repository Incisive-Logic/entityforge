using EntityForge.Db.Client;
using EntityForge.Query.Projection;
using EntityForge.Query.Results;
using EntityForge.Query.Values;
using EntityForge.Semantics;
using EntityForge.Db.Transport.Contracts;
using System.Net;
using System.Text;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class EntityProjectionTransportTests
{
    [TestMethod]
    public void Transport_round_trip_preserves_dynamic_entity_identity_types_values_relationships_and_evidence()
    {
        Guid blankScope = Guid.NewGuid();
        var type = new SemanticTerm(new Uri("https://example.test/Company"), SemanticTermRole.Concept);
        var name = new SemanticTerm(new Uri("https://example.test/name"), SemanticTermRole.Property);
        var evidenceReference = new EvidenceReference(new Uri("https://evidence.example/1"));
        var evidence = new EvidenceRecord(evidenceReference, new Uri("https://provider.example/hr"),
            new Uri("https://graph.example/source"), "row-1", "snapshot-7",
            DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2027-01-01T00:00:00Z"));
        var node = new EntityProjectionNode(new IdentifierValue("COMPANY000001"), [type],
            [new EntityProjectionField("name", [new EntityProjectionValue(
                new LiteralValue("Société", QueryScalarKind.String,
                    new Uri("http://www.w3.org/1999/02/22-rdf-syntax-ns#langString"), "fr"),
                name, name, [evidenceReference])])],
            [new EntityProjectionRelationship("related",
                [new EntityProjectionReference(new BlankNodeValue("related-1", blankScope))])]);
        var source = new EntityProjectionQueryResult(new EntityProjection(Guid.NewGuid(), [node], [evidence]), new(42), "next");

        EntityProjectionQueryResult restored = EntityProjectionTransport.Decode(EntityProjectionTransport.Encode(source));

        Assert.AreEqual(source.SnapshotVersion, restored.SnapshotVersion);
        Assert.AreEqual(source.NextContinuation, restored.NextContinuation);
        EntityProjectionNode restoredNode = restored.Document.Roots.Single();
        Assert.AreEqual(node.Identity, restoredNode.Identity);
        Assert.AreEqual(type, restoredNode.Types.Single());
        LiteralValue restoredName = (LiteralValue)restoredNode.Fields.Single().Values.Single().Value;
        Assert.AreEqual("Société", restoredName.LexicalValue);
        Assert.AreEqual("fr", restoredName.Language);
        Assert.AreEqual(blankScope, ((BlankNodeValue)((EntityProjectionReference)restored.Document.Roots.Single()
            .Relationships.Single().Items.Single()).Identity).ResultScopeId);
        EvidenceRecord restoredEvidence = restored.Document.Evidence.Single();
        Assert.AreEqual(evidence.SourceGraph, restoredEvidence.SourceGraph);
        Assert.AreEqual(evidence.SnapshotIdentifier, restoredEvidence.SnapshotIdentifier);
    }

    [TestMethod]
    public async Task Http_client_preserves_stable_remote_error_code_and_retryability()
    {
        using var http = new HttpClient(new ResponseHandler(new(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"contractVersion":1,"code":"database_unavailable","detail":"Try later","retryable":true}""",
                Encoding.UTF8, "application/problem+json")
        }))
        {
            BaseAddress = new Uri("https://entityforge.test")
        };

        EntityForgeRemoteException exception = await Assert.ThrowsExactlyAsync<EntityForgeRemoteException>(async () =>
            await new HttpEntityProjectionClient(http).QueryAsync(new("https://example.test/Company")));

        Assert.AreEqual("database_unavailable", exception.Code);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.IsTrue(exception.IsTransient);
    }

    private sealed class ResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }
}
