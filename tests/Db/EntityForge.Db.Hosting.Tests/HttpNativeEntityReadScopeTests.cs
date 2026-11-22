using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntityForge.Db.Client;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class HttpNativeEntityReadScopeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Whole_materialization_validates_once_after_all_fragments_and_rejects_expiry(bool expire)
    {
        var identity = GraphVertexId.New();
        var vertex = new GraphVertex(identity, new HashSet<string> { "item" }, new Dictionary<string, DatabaseValue>());
        using var replies = new Replies(DatabaseProfileId.PropertyGraph, vertex);
        using var http = Client(replies);
        var scope = new HttpGraphEntityReadScope(await Open(http, replies, DatabaseProfileId.PropertyGraph));
        Task<GraphVertex?> read = EntityReadScopeLifetime.RunAsync(scope, async () =>
        {
            await scope.GetVertexAsync(identity);
            var result = await scope.GetVertexAsync(identity);
            Assert.AreEqual(0, replies.Validations, "Fragments remain private until final validation.");
            replies.ExpireOnValidation = expire;
            return result;
        }, () => scope.ValidateAsync()).AsTask();
        if (expire)
        {
            await Assert.ThrowsExceptionAsync<HttpRequestException>(async () => await read);
        }
        else
        {
            Assert.AreEqual(identity, (await read)!.Id);
        }

        Assert.AreEqual(1, replies.Validations);
        Assert.AreEqual(1, replies.Releases);
    }

    [TestMethod]
    public async Task Deferred_validation_is_local_to_the_materialization_execution_context()
    {
        var identity = GraphVertexId.New();
        using var replies = new Replies(DatabaseProfileId.PropertyGraph,
            new GraphVertex(identity, new HashSet<string> { "item" }, new Dictionary<string, DatabaseValue>()));
        using var http = Client(replies);
        var scope = new HttpGraphEntityReadScope(await Open(http, replies, DatabaseProfileId.PropertyGraph));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<GraphVertex?> materialization = EntityReadScopeLifetime.RunAsync(scope, async () =>
        {
            var value = await scope.GetVertexAsync(identity);
            entered.SetResult();
            await finish.Task;
            return value;
        }, () => scope.ValidateAsync()).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await scope.GetVertexAsync(identity);
            Assert.AreEqual(1, replies.Validations, "An unrelated direct read still validates after decoding.");
        }
        finally
        {
            finish.TrySetResult();
        }
        await materialization;
        Assert.AreEqual(2, replies.Validations);
        Assert.AreEqual(1, replies.Releases);
    }

    [TestMethod]
    public async Task Conversion_failure_discards_materialization_and_releases_without_final_success()
    {
        var identity = GraphVertexId.New();
        using var replies = new Replies(DatabaseProfileId.PropertyGraph,
            new GraphVertex(identity, new HashSet<string> { "item" }, new Dictionary<string, DatabaseValue>()));
        using var http = Client(replies);
        var scope = new HttpGraphEntityReadScope(await Open(http, replies, DatabaseProfileId.PropertyGraph));
        await Assert.ThrowsExceptionAsync<FormatException>(async () =>
            await EntityReadScopeLifetime.RunAsync<GraphVertex?>(scope, async () =>
            {
                await scope.GetVertexAsync(identity);
                throw new FormatException("Conversion failed.");
            }, () => scope.ValidateAsync()));
        Assert.AreEqual(0, replies.Validations);
        Assert.AreEqual(1, replies.Releases);
    }

    [TestMethod]
    public async Task Captured_execution_context_cannot_defer_validation_after_materialization_ends()
    {
        var identity = GraphVertexId.New();
        using var replies = new Replies(DatabaseProfileId.PropertyGraph,
            new GraphVertex(identity, new HashSet<string> { "item" }, new Dictionary<string, DatabaseValue>()));
        using var http = Client(replies);
        var raw = await Open(http, replies, DatabaseProfileId.PropertyGraph);
        await using var scope = new HttpGraphEntityReadScope(raw);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? captured = null;
        await raw.MaterializeAsync(async () =>
        {
            captured = Task.Run(async () =>
            {
                await finish.Task;
                await scope.GetVertexAsync(identity);
            });
            await scope.ValidateAsync();
            return true;
        });
        finish.SetResult();
        await captured!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, replies.Validations);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed class Replies(DatabaseProfileId profile, object? payload) : HttpMessageHandler
    {
        internal readonly Guid Database = Guid.NewGuid(), Id = Guid.NewGuid();
        internal int Releases, Validations;
        internal bool ExpireOnValidation;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/release"))
            {
                Releases++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/validate"))
            {
                Validations++;
                if (ExpireOnValidation)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone)
                    {
                        Content = JsonContent.Create(new { code = "read_scope_unavailable" })
                    });
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    scopeId = Id,
                    databaseIncarnation = Database,
                    profile = profile.ToString(),
                    snapshotVersion = 17,
                    contributionGuard = (object?)null,
                    value = payload
                })
            });
        }
    }

    private static HttpClient Client(Replies replies) => new(replies) { BaseAddress = new("https://example.test") };

    private static async Task<HttpEntityReadScope> Open(HttpClient http, Replies replies, DatabaseProfileId profile) =>
        await new HttpEntityReadTransport(http, replies.Database, profile switch
        {
            DatabaseProfileId.PropertyGraph => (IEntityReadRecipePolicy)EntityForge.Db.Graph.GraphReadRecipePolicy.Instance,
            DatabaseProfileId.Rdf => EntityForge.Db.Rdf.RdfReadRecipePolicy.Instance,
            DatabaseProfileId.Semantic => EntityForge.Db.Semantic.SemanticReadRecipePolicy.Instance,
            _ => throw new ArgumentException()
        }).OpenAsync([], TimeSpan.FromMinutes(1));

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Non_finite_native_values_survive_effect_encoding_and_recovery(bool checkpoint)
    {
        var vertex = new GraphVertex(GraphVertexId.New(), new HashSet<string> { "special" }, new Dictionary<string, DatabaseValue>
        {
            ["nan"] = new DatabaseValue.Double(double.NaN),
            ["positive"] = new DatabaseValue.Double(double.PositiveInfinity),
            ["negative"] = new DatabaseValue.Double(double.NegativeInfinity)
        });
        var effects = GraphCommittedEffectsCodec.Decode(GraphCommittedEffectsCodec.Encode(new([new GraphVertexCreated(vertex)])));
        var decoded = ((GraphVertexCreated)effects.Effects.Single()).Vertex;
        foreach (var item in vertex.Properties)
        {
            Assert.AreEqual(item.Value, decoded.Properties[item.Key]);
        }

        string path = Path.Combine(Path.GetTempPath(), "non-finite-graph-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var database = await GraphDatabase.OpenLocalAsync(new(path)))
            {
                await database.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(vertex.Id, vertex.Labels.ToArray()),
                    .. vertex.Properties.Select(p => new SetVertexProperty(vertex.Id, p.Key, p.Value))]));
                if (checkpoint)
                {
                    await database.CreateCheckpointAsync();
                }
            }
            await using var reopened = await GraphDatabase.OpenLocalAsync(new(path));
            var actual = reopened.GetVertex(vertex.Id)!;
            foreach (var item in vertex.Properties)
            {
                Assert.AreEqual(item.Value, actual.Properties[item.Key]);
            }
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [TestMethod]
    public async Task Graph_decodes_every_native_value_and_validates_after_decoding()
    {
        var vertex = new GraphVertex(GraphVertexId.New(), new HashSet<string> { "item" }, new Dictionary<string, DatabaseValue>
        {
            ["string"] = new DatabaseValue.String("escaped\nvalue"),
            ["integer"] = new DatabaseValue.Int64(long.MaxValue),
            ["double"] = new DatabaseValue.Double(1.25),
            ["boolean"] = new DatabaseValue.Boolean(false),
            ["time"] = new DatabaseValue.DateTimeOffset(DateTimeOffset.Parse("2026-09-11T12:00:00+02:00")),
            ["uri"] = new DatabaseValue.Uri(new("urn:read:value")),
            ["binary"] = new DatabaseValue.Binary(new byte[] { 0, 255 }),
            ["json"] = new DatabaseValue.Json("{\"b\":2,\"a\":[null,true]}")
        });
        var replies = new Replies(DatabaseProfileId.PropertyGraph, vertex);
        using var http = Client(replies);
        await using var scope = new HttpGraphEntityReadScope(await Open(http, replies, DatabaseProfileId.PropertyGraph));
        var actual = await scope.GetVertexAsync(vertex.Id);
        Assert.AreEqual(JsonSerializer.Serialize(vertex, Json), JsonSerializer.Serialize(actual, Json));
        Assert.AreEqual(1, replies.Validations);
    }

    [TestMethod]
    public async Task Rdf_preserves_lexical_terms_and_quoted_triples()
    {
        var subject = new RdfIri("urn:subject");
        var predicate = new RdfIri("urn:predicate");
        RdfQuad[] quads = [new(subject, predicate, new RdfIri("urn:object")),
            new(subject, predicate, new RdfBlankNode("b1")),
            new(subject, predicate, new RdfLiteral("001", "http://www.w3.org/2001/XMLSchema#integer")),
            new(subject, predicate, new RdfLiteral("bonjour", language: "fr")),
            new(subject, predicate, new RdfTripleTerm(new RdfBlankNode("b2"), predicate, new RdfLiteral("quoted")))];
        var replies = new Replies(DatabaseProfileId.Rdf, quads);
        using var http = Client(replies);
        await using var scope = new HttpRdfEntityReadScope(await Open(http, replies, DatabaseProfileId.Rdf));
        CollectionAssert.AreEqual(quads, (await scope.ReadDefaultSubjectAsync(subject)).ToArray());
        Assert.AreEqual(1, replies.Validations);
    }

    [DataTestMethod]
    [DataRow("identity")]
    [DataRow("missing-properties")]
    [DataRow("value-kind")]
    public async Task Malformed_graph_payload_retires_and_releases(string fault)
    {
        var identity = GraphVertexId.New();
        var value = JsonSerializer.SerializeToNode(new GraphVertex(identity, new HashSet<string> { "item" },
            new Dictionary<string, DatabaseValue> { ["n"] = new DatabaseValue.Int64(1) }), Json)!;
        if (fault == "identity")
        {
            value["id"]!["value"] = Guid.NewGuid();
        }

        if (fault == "missing-properties")
        {
            value.AsObject().Remove("properties");
        }

        if (fault == "value-kind")
        {
            value["properties"]!["n"]!["kind"] = (int)DatabaseValueKind.String;
        }

        var replies = new Replies(DatabaseProfileId.PropertyGraph, value);
        using var http = Client(replies);
        var scope = new HttpGraphEntityReadScope(await Open(http, replies, DatabaseProfileId.PropertyGraph));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () => await scope.GetVertexAsync(identity));
        Assert.AreEqual(1, replies.Releases);
        Assert.AreEqual(0, replies.Validations);
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () => await scope.ValidateAsync());
    }

    [DataTestMethod]
    [DataRow("subject")]
    [DataRow("graph")]
    [DataRow("term")]
    [DataRow("null")]
    public async Task Malformed_rdf_payload_is_not_absence_and_releases(string fault)
    {
        var subject = new RdfIri("urn:subject");
        var value = JsonSerializer.SerializeToNode(new[] { new RdfQuad(subject, new("urn:p"), new RdfLiteral("text")) }, Json)!;
        if (fault == "subject")
        {
            value[0]!["subject"]!["value"] = "urn:wrong";
        }

        if (fault == "graph")
        {
            value[0]!["graph"] = JsonSerializer.SerializeToNode<RdfTerm>(new RdfIri("urn:named"), Json);
        }

        if (fault == "term")
        {
            value[0]!["object"]!["$term"] = "unknown";
        }

        var replies = new Replies(DatabaseProfileId.Rdf, fault == "null" ? null : value);
        using var http = Client(replies);
        var scope = new HttpRdfEntityReadScope(await Open(http, replies, DatabaseProfileId.Rdf));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () => await scope.ReadDefaultSubjectAsync(subject));
        Assert.AreEqual(1, replies.Releases);
    }

    [TestMethod]
    public async Task Outgoing_reply_cannot_substitute_another_predicate()
    {
        var identity = GraphVertexId.New();
        var edge = new GraphEdge(GraphEdgeId.New(), GraphVertexId.New(), GraphVertexId.New(), "RELATED",
            new Dictionary<string, DatabaseValue>());
        var replies = new Replies(DatabaseProfileId.PropertyGraph, new[] { edge });
        using var http = Client(replies);
        var scope = new HttpGraphEntityReadScope(await Open(http, replies, DatabaseProfileId.PropertyGraph));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () => await scope.OutgoingAsync(identity, "RELATED"));
        Assert.AreEqual(1, replies.Releases);
    }

    [TestMethod]
    public async Task Duplicate_native_members_are_not_interpreted_using_last_property_wins()
    {
        var identity = GraphVertexId.New();
        var vertex = new GraphVertex(identity, new HashSet<string> { "item" }, new Dictionary<string, DatabaseValue>());
        string encoded = JsonSerializer.Serialize(vertex, Json);
        using var duplicate = JsonDocument.Parse(encoded.Insert(1, "\"properties\":{},"));
        var replies = new Replies(DatabaseProfileId.PropertyGraph, duplicate.RootElement);
        using var http = Client(replies);
        var scope = new HttpGraphEntityReadScope(await Open(http, replies, DatabaseProfileId.PropertyGraph));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () => await scope.GetVertexAsync(identity));
        Assert.AreEqual(1, replies.Releases);
    }

    [DataTestMethod]
    [DataRow("\"nan\"")]
    [DataRow("\"1.5\"")]
    [DataRow("1e999")]
    public async Task Unknown_or_overflowing_double_is_rejected(string encoded)
    {
        var identity = GraphVertexId.New();
        var value = JsonSerializer.SerializeToNode(new GraphVertex(identity, new HashSet<string> { "item" },
            new Dictionary<string, DatabaseValue> { ["number"] = new DatabaseValue.Double(1) }), Json)!;
        value["properties"]!["number"]!["value"] = JsonNode.Parse(encoded);
        var replies = new Replies(DatabaseProfileId.PropertyGraph, value);
        using var http = Client(replies);
        var scope = new HttpGraphEntityReadScope(await Open(http, replies, DatabaseProfileId.PropertyGraph));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () => await scope.GetVertexAsync(identity));
        Assert.AreEqual(1, replies.Releases);
    }

    [DataTestMethod]
    [DataRow(false, "literal")]
    [DataRow(true, "literal")]
    [DataRow(false, "iri")]
    [DataRow(true, "iri")]
    public async Task Predicate_discriminators_must_agree_with_iri_type(bool quoted, string tag)
    {
        var subject = new RdfIri("urn:subject");
        var predicate = new RdfIri("urn:predicate");
        RdfTerm target = quoted ? new RdfTripleTerm(subject, predicate, new RdfLiteral("text")) : new RdfLiteral("text");
        var value = JsonSerializer.SerializeToNode(new[] { new RdfQuad(subject, predicate, target) }, Json)!;
        (quoted ? value[0]!["object"]!["predicate"]! : value[0]!["predicate"]!)["$term"] = tag;
        var replies = new Replies(DatabaseProfileId.Rdf, value);
        using var http = Client(replies);
        await using var scope = new HttpRdfEntityReadScope(await Open(http, replies, DatabaseProfileId.Rdf));
        if (tag == "iri")
        {
            Assert.AreEqual(predicate, (await scope.ReadDefaultSubjectAsync(subject)).Single().Predicate);
        }
        else
        {
            await Assert.ThrowsExceptionAsync<InvalidDataException>(async () => await scope.ReadDefaultSubjectAsync(subject));
            Assert.AreEqual(1, replies.Releases);
        }
    }

    [TestMethod]
    public async Task Expiry_after_decoding_discards_the_typed_result()
    {
        var replies = new Replies(DatabaseProfileId.Rdf, Array.Empty<RdfQuad>()) { ExpireOnValidation = true };
        using var http = Client(replies);
        var scope = new HttpRdfEntityReadScope(await Open(http, replies, DatabaseProfileId.Rdf));
        var failure = await Assert.ThrowsExceptionAsync<HttpRequestException>(async () => await scope.ReadDefaultSubjectAsync(new("urn:s")));
        Assert.AreEqual(HttpStatusCode.Gone, failure.StatusCode);
        Assert.AreEqual(1, replies.Validations);
        Assert.AreEqual(1, replies.Releases);
    }
}
