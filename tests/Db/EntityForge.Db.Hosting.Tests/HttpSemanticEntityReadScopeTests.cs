using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class HttpSemanticEntityReadScopeTests
{
    private sealed class Replies(JsonNode? payload, JsonNode? guard = null) : HttpMessageHandler
    {
        internal readonly Guid Database = Guid.NewGuid(), Scope = Guid.NewGuid();
        internal int Releases;
        internal int Validations;
        internal bool ChangeGuard;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/release"))
            {
                Releases++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            bool validation = request.RequestUri.AbsolutePath.EndsWith("/validate");
            if (validation)
            {
                Validations++;
            }

            var actualGuard = ChangeGuard && validation ? null : guard;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    scopeId = Scope,
                    databaseIncarnation = Database,
                    profile = "Semantic",
                    snapshotVersion = 17,
                    contributionGuard = actualGuard,
                    value = payload
                })
            });
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly SemanticEntityIdentity Identity = new(new(new Uri("urn:test:concept"), SemanticTermRole.Concept),
        new(new Uri("urn:test:id"), "Id", SemanticValueKind.String), new StringSemanticValue("one"));

    private static ValueTask<ISemanticEntityReadScope> Open(HttpClient http, Replies replies) => HttpSemanticEntityReadScope.OpenAsync(
        http, replies.Database, [], new("urn:source"), new("urn:partition"), TimeSpan.FromMinutes(1), default);

    [DataTestMethod]
    [DataRow("missing-version")]
    [DataRow("invisible")]
    [DataRow("wrong-subject")]
    [DataRow("missing-provenance")]
    [DataRow("missing-target-value")]
    [DataRow("mismatched-target-kind")]
    public async Task Invalid_relationship_evidence_fails_and_releases_the_scope(string corruption)
    {
        var relationship = new SemanticTerm(new Uri("urn:test:peer"), SemanticTermRole.Relationship);
        var evidence = new SemanticRelationshipEvidence(Guid.NewGuid(), Identity, relationship, Identity,
            new(new("urn:source"), "record", DateTimeOffset.UtcNow, new(new Uri("urn:vocabulary"), "1", SemanticModelKind.Vocabulary)), new(1), null);
        var node = JsonSerializer.SerializeToNode(evidence, Json)!;
        if (corruption is "missing-target-value" or "mismatched-target-kind")
        {
            var target = new SemanticEntityIdentity(Identity.Concept,
                new(new Uri("urn:test:integer-id"), "Id", SemanticValueKind.Integer), new IntegerSemanticValue(42));
            node["target"] = JsonSerializer.SerializeToNode(target, Json);
            if (corruption == "missing-target-value")
            {
                node["target"]!["value"]!.AsObject().Remove("value");
            }
            else
            {
                node["target"]!["value"]!["kind"] = (int)SemanticValueKind.Boolean;
            }
        }
        if (corruption == "missing-version")
        {
            node["beginVersion"]!.AsObject().Remove("value");
        }

        if (corruption == "invisible")
        {
            node["endVersion"] = JsonSerializer.SerializeToNode(new DatabaseVersion(17), Json);
        }

        if (corruption == "wrong-subject")
        {
            node["subject"]!["value"]!["value"] = "different";
        }

        if (corruption == "missing-provenance")
        {
            node["provenance"]!.AsObject().Remove("sourceRecord");
        }

        var replies = new Replies(new JsonArray(node));
        using var http = new HttpClient(replies) { BaseAddress = new("https://example.test") };
        await using var scope = await Open(http, replies);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () => await scope.TraverseAsync(new(Identity, SemanticRelationshipDirection.Outgoing, relationship)));
        Assert.AreEqual(1, replies.Releases);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Opening_uses_its_guard_and_final_validation_rejects_a_changed_guard(bool change)
    {
        var guard = JsonSerializer.SerializeToNode(new SemanticSourcePosition(Guid.NewGuid(), "v1", "p1"), Json)!;
        if (!change)
        {
            guard.AsObject().Remove("generationId");
        }

        var replies = new Replies(null, guard) { ChangeGuard = change };
        using var http = new HttpClient(replies) { BaseAddress = new("https://example.test") };
        if (!change)
        {
            await Assert.ThrowsExceptionAsync<InvalidDataException>(async () => await Open(http, replies));
        }
        else
        {
            await using var scope = await Open(http, replies);
            Assert.AreEqual(0, replies.Validations, "Opening already carries the initial guard.");
            Assert.IsNotNull(scope.ContributionGuard);
            await Assert.ThrowsExceptionAsync<InvalidDataException>(async () => await scope.ValidateAsync());
        }
        Assert.AreEqual(1, replies.Releases);
    }

    [TestMethod]
    public async Task Explicit_zero_identity_is_preserved()
    {
        var target = new SemanticEntityIdentity(Identity.Concept,
            new(new Uri("urn:test:integer-id"), "Id", SemanticValueKind.Integer), new IntegerSemanticValue(0));
        var relationship = new SemanticTerm(new Uri("urn:test:peer"), SemanticTermRole.Relationship);
        var evidence = new SemanticRelationshipEvidence(Guid.NewGuid(), Identity, relationship, target,
            new(new("urn:source"), "record", DateTimeOffset.UtcNow, new(new Uri("urn:vocabulary"), "1", SemanticModelKind.Vocabulary)), new(1), null);
        var replies = new Replies(new JsonArray(JsonSerializer.SerializeToNode(evidence, Json)));
        using var http = new HttpClient(replies) { BaseAddress = new("https://example.test") };
        await using var scope = await Open(http, replies);
        Assert.AreEqual(target, (await scope.TraverseAsync(new(Identity, SemanticRelationshipDirection.Outgoing, relationship))).Single().Target);
    }
}
