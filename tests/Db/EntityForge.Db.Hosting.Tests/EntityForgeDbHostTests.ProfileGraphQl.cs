using EntityForge.Db.Query.Adapters;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntityForge.Db.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Profile_GraphQl_documented_mutation_and_selected_document_aliases_execute(bool rdf)
    {
        _directory = NewDirectory();
        await using var host = ProfileHost(rdf);
        await host.StartAsync();
        using var client = Client(host);
        string design = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GraphQlDesign", "PROFILE_GRAPHQL_DESIGN.md"));
        string example = System.Text.RegularExpressions.Regex.Matches(design, "```graphql\\s*([\\s\\S]*?)```")
            .Select(m => m.Groups[1].Value).Single(s => s.Contains(rdf ? "applyRdfChanges" : "applyGraphChanges", StringComparison.Ordinal));
        using var documented = await client.PostAsJsonAsync("/graphql", new EntityForgeGraphQlRequest(example));
        using var result = JsonDocument.Parse(await documented.Content.ReadAsStringAsync());
        Assert.IsFalse(result.RootElement.TryGetProperty("errors", out _), result.RootElement.GetRawText());
        Commit(await Mutate(client, ProfileBatch(rdf), ProfileMutation(rdf)));
        using var response = await client.PostAsJsonAsync("/graphql", new EntityForgeGraphQlRequest("""
            query($type:String!, $property:String!, $show:Boolean! = false) {
              entities(type:$type) { nodes {
                identity display:field(source:$property) { values }
                hidden:field(source:"deliberately absent") @include(if:$show) { values }
              } }
            }
            """, new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement(rdf ? "urn:Company" : "Company"),
            ["property"] = JsonSerializer.SerializeToElement(rdf ? "urn:name" : "name")
        }, Extensions: new Dictionary<string, JsonElement> { ["entityDocument"] = JsonSerializer.SerializeToElement(true) }));
        using var selected = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = selected.RootElement;
        Assert.IsFalse(root.TryGetProperty("errors", out _), root.GetRawText());
        var node = root.GetProperty("data").GetProperty("entities").GetProperty("nodes")[0];
        Assert.IsTrue(node.TryGetProperty("display", out _));
        Assert.IsFalse(node.TryGetProperty("hidden", out _));
        var fields = root.GetProperty("extensions").GetProperty("entityDocument").GetProperty("entities")[0].GetProperty("fields");
        Assert.IsTrue(fields.TryGetProperty("display", out _), fields.GetRawText());
        Assert.IsFalse(fields.EnumerateObject().Any(p => p.Name.StartsWith("selection_", StringComparison.Ordinal)));
    }

    private EntityForgeDbHost ProfileHost(bool rdf, bool secure = false)
    {
        var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory!);
        if (rdf)
        {
            builder.UseRdfProfile();
        }
        else
        {
            builder.UsePropertyGraphProfile();
        }

        if (secure)
        {
            builder.UseBearerTokenSecurity(new("profile-graphql-admin-00000000001", "profile-graphql-writer-0000000001", "profile-graphql-reader-0000000001", requireHttps: false));
        }

        return builder.ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
    }

    private static string ProfileMutation(bool rdf) => $$"""
        mutation Write($input: {{(rdf ? "Rdf" : "Graph")}}ChangeBatchInput!) {
          ...WriteFields
        }
        fragment WriteFields on Mutation {
          commit: apply{{(rdf ? "Rdf" : "Graph")}}Changes(input:$input) {
            transactionId databaseIncarnation version outcome mutationCount effectCount changeCursor
          }
        }
        """;

    private static JsonObject ProfileBatch(bool rdf, string name = "Alpha") => JsonSerializer.SerializeToNode(new
    {
        transactionId = Guid.NewGuid(),
        changes = rdf ? new object[]
        {
            new
            {
                kind = "assert", quad = new
            {
                subject = new {kind = "iri", value = "urn:company:1"},
                predicate = new {kind = "iri", value = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type"}, @object = new {kind = "iri", value = "urn:Company"}
            }
            },
            new
            {
                kind = "assert", quad = new
            {
                subject = new {kind = "iri", value = "urn:company:1"},
                predicate = new {kind = "iri", value = "urn:name"}, @object = new {kind = "literal", lexicalForm = name}
            }
            }
        }
        : new object[]
        {
            new {kind = "createVertex", id = "11111111-1111-1111-1111-111111111111", labels = new[]{"Company"}},
            new {kind = "setVertexProperty", id = "11111111-1111-1111-1111-111111111111", name = "name", value = new { kind = "string", lexical = name }}
        }
    })!.AsObject();

    private static async Task<JsonElement> ProfileRead(HttpClient client, bool rdf, long? version = null)
    {
        using var response = await client.PostAsJsonAsync("/graphql", new EntityForgeGraphQlRequest("""
            query Read($type:String!, $source:String!, $show:Boolean! = true, $version:Long) {
              result: entities(type:$type, first:10, snapshotVersion:$version) {
                nodes { ...Fields } pageInfo { snapshotVersion }
              }
            }
            fragment Fields on Entity { identity name: field(source:$source) @include(if:$show) { values } }
            """, new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement(rdf ? "urn:Company" : "Company"),
            ["source"] = JsonSerializer.SerializeToElement(rdf ? "urn:name" : "name"),
            ["version"] = JsonSerializer.SerializeToElement(version)
        }));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.IsFalse(json.RootElement.TryGetProperty("errors", out _), json.RootElement.GetRawText());
        return json.RootElement.GetProperty("data").GetProperty("result").Clone();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Profile_GraphQl_native_atomic_receipt_replay_history_and_reopen(bool rdf)
    {
        _directory = NewDirectory();
        var input = ProfileBatch(rdf);
        long version;
        await using (var host = ProfileHost(rdf))
        {
            await host.StartAsync();
            using var client = Client(host);
            var receipt = Commit(await Mutate(client, input, ProfileMutation(rdf)));
            version = receipt.GetProperty("version").GetInt64();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            using IDisposable history = rdf ? runtime.RdfDatabase.AcquireSnapshot(new(version)) : runtime.GraphDatabase.AcquireSnapshot(new(version));
            Assert.AreEqual("AppliedWithChanges", receipt.GetProperty("outcome").GetString());
            Assert.AreEqual(2, receipt.GetProperty("mutationCount").GetInt32());
            Assert.AreEqual("Alpha", (await ProfileRead(client, rdf)).GetProperty("nodes")[0].GetProperty("name").GetProperty("values")[0].GetString());
            var retry = Commit(await Mutate(client, input, ProfileMutation(rdf)));
            Assert.AreEqual(version, retry.GetProperty("version").GetInt64());
            Assert.AreEqual("IdempotentReplay", retry.GetProperty("outcome").GetString());
            var conflict = ProfileBatch(rdf, "Wrong");
            conflict["transactionId"] = input["transactionId"]!.DeepClone();
            var rejected = await Mutate(client, conflict, ProfileMutation(rdf));
            Assert.AreEqual("IDEMPOTENCY_CONFLICT", rejected.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString());
            var change = ProfileBatch(rdf, "Beta");
            if (rdf)
            {
                var retract = input["changes"]![1]!.DeepClone();
                retract["kind"] = "retract";
                change["changes"]![0] = retract;
            }
            else
            {
                change["changes"]!.AsArray().RemoveAt(0);
            }

            Commit(await Mutate(client, change, ProfileMutation(rdf)));
            Assert.AreEqual("Beta", (await ProfileRead(client, rdf)).GetProperty("nodes")[0].GetProperty("name").GetProperty("values")[0].GetString());
            Assert.AreEqual("Alpha", (await ProfileRead(client, rdf, version)).GetProperty("nodes")[0].GetProperty("name").GetProperty("values")[0].GetString());
            await host.StopAsync();
        }
        await using var reopened = ProfileHost(rdf);
        await reopened.StartAsync();
        using var recovered = Client(reopened);
        Assert.AreEqual("Beta", (await ProfileRead(recovered, rdf)).GetProperty("nodes")[0].GetProperty("name").GetProperty("values")[0].GetString());
        Assert.AreEqual(version, Commit(await Mutate(recovered, input, ProfileMutation(rdf))).GetProperty("version").GetInt64());
    }

    [TestMethod]
    public async Task Profile_GraphQl_graph_edges_labels_properties_and_native_failure_are_atomic()
    {
        _directory = NewDirectory();
        await using var host = ProfileHost(false);
        await host.StartAsync();
        using var client = Client(host);
        var graph = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>().GraphDatabase;
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), edge = Guid.NewGuid();
        async Task<JsonElement> Apply(params object[] changes) => await Mutate(client,
            JsonSerializer.SerializeToNode(new
            {
                transactionId = Guid.NewGuid(),
                changes
            })!.AsObject(), ProfileMutation(false));
        var invalid = await Apply(new
        {
            kind = "createVertex",
            id = a,
            labels = new[] { "Company" }
        },
            new
            {
                kind = "createEdge",
                id = edge,
                from = a,
                to = b,
                label = "EMPLOYS"
            });
        Assert.IsTrue(invalid.TryGetProperty("errors", out _));
        Assert.IsNull(graph.GetVertex(new(a)), "Native planning failure must roll back the earlier vertex creation.");
        Commit(await Apply(new
        {
            kind = "createVertex",
            id = a,
            labels = new[] { "Company" }
        },
            new
            {
                kind = "createVertex",
                id = b,
                labels = new[] { "Person" }
            },
            new
            {
                kind = "createEdge",
                id = edge,
                from = a,
                to = b,
                label = "EMPLOYS"
            },
            new
            {
                kind = "setEdgeProperty",
                id = edge,
                name = "salary",
                value = new
                {
                    kind = "integer",
                    lexical = "9223372036854775807"
                }
            },
            new
            {
                kind = "addVertexLabel",
                id = a,
                label = "Employer"
            }));
        Assert.IsTrue(graph.GetVertex(new(a))!.Labels.Contains("Employer"));
        Assert.AreEqual(new DatabaseValue.Int64(long.MaxValue), graph.Outgoing(new GraphVertexId(a)).Single().Properties["salary"]);
        object[] values = [
            new { kind = "string", lexical = "hello" },
            new { kind = "boolean", lexical = "true" },
            new { kind = "integer", lexical = "9223372036854775807" },
            new { kind = "floatingPoint", lexical = "1.25" },
            new { kind = "dateTime", lexical = "2026-10-24T12:34:56+01:00" },
            new { kind = "uri", lexical = "urn:value" },
            new { kind = "binary", lexical = "AQID" },
            new { kind = "json", json = new { number = 12345678901234567890.123456789m } }];
        Commit(await Apply(values.Select((value, i) => (object)new { kind = "setVertexProperty", id = a, name = "p" + i, value }).ToArray()));
        var properties = graph.GetVertex(new(a))!.Properties;
        Assert.AreEqual(new DatabaseValue.Int64(long.MaxValue), properties["p2"]);
        Assert.AreEqual(new DatabaseValue.Double(1.25), properties["p3"]);
        Assert.AreEqual(new DatabaseValue.DateTimeOffset(DateTimeOffset.Parse("2026-10-24T12:34:56+01:00")), properties["p4"]);
        Assert.AreEqual(new DatabaseValue.Uri(new Uri("urn:value")), properties["p5"]);
        Assert.AreEqual(new DatabaseValue.Binary(new byte[] { 1, 2, 3 }), properties["p6"]);
        StringAssert.Contains(((DatabaseValue.Json)properties["p7"]).Utf8Json, "12345678901234567890.123456789");
        Commit(await Apply(new
        {
            kind = "removeVertexProperty",
            id = a,
            name = "p0"
        }, new
        {
            kind = "removeEdgeProperty",
            id = edge,
            name = "salary"
        },
            new
            {
                kind = "removeVertexLabel",
                id = a,
                label = "Employer"
            }));
        Assert.IsFalse(graph.GetVertex(new(a))!.Properties.ContainsKey("p0"));
        Assert.IsFalse(graph.GetVertex(new(a))!.Labels.Contains("Employer"));
        Assert.AreEqual(0, graph.Outgoing(new GraphVertexId(a)).Single().Properties.Count);
        Commit(await Apply(new
        {
            kind = "deleteEdge",
            id = edge
        }, new
        {
            kind = "deleteVertex",
            id = b
        }, new
        {
            kind = "deleteVertex",
            id = a
        }));
        Assert.IsNull(graph.GetVertex(new(a)));
        Assert.IsNull(graph.GetVertex(new(b)));
    }

    [TestMethod]
    public async Task Profile_GraphQl_rdf_preserves_named_graphs_blank_nodes_literals_and_quoted_triples()
    {
        _directory = NewDirectory();
        await using var host = ProfileHost(true);
        await host.StartAsync();
        using var client = Client(host);
        var rdf = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>().RdfDatabase;
        var subject = new RdfBlankNode("stable-id");
        var predicate = new RdfIri("urn:label");
        var named = new RdfIri("urn:graph");
        RdfQuad[] quads = [new(subject, predicate, new RdfLiteral("Bonjour", language: "fr"), named),
            new(subject, predicate, new RdfLiteral("42", "http://www.w3.org/2001/XMLSchema#integer")),
            new(new RdfTripleTerm(subject, predicate, new RdfLiteral("quoted")), new RdfIri("urn:certainty"), new RdfLiteral("high"), named)];
        var input = JsonSerializer.SerializeToNode(new
        {
            transactionId = Guid.NewGuid(),
            changes = quads.Select(q => new RdfMutationWriteRequest("assert", Wire(q)))
        },
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        Commit(await Mutate(client, input, ProfileMutation(true)));
        CollectionAssert.AreEquivalent(quads, rdf.Find(new()).ToArray());
        input["transactionId"] = Guid.NewGuid();
        input["changes"]!.AsArray().RemoveAt(2);
        input["changes"]!.AsArray().RemoveAt(1);
        input["changes"]![0]!["kind"] = "retract";
        Commit(await Mutate(client, input, ProfileMutation(true)));
        CollectionAssert.AreEquivalent(quads.Skip(1).ToArray(), rdf.Find(new()).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Profile_GraphQl_lost_response_and_batch_bound_preserve_native_retry(bool rdf)
    {
        _directory = NewDirectory();
        await using var host = ProfileHost(rdf);
        await host.StartAsync();
        using var client = Client(host);
        var input = ProfileBatch(rdf);
        using var unreliable = new HttpClient(new LostGraphQlReply()) { BaseAddress = client.BaseAddress };
        await Assert.ThrowsExactlyAsync<HttpRequestException>(async () => await Mutate(unreliable, input, ProfileMutation(rdf)));
        Assert.AreEqual("IdempotentReplay", Commit(await Mutate(client, input, ProfileMutation(rdf))).GetProperty("outcome").GetString());
        var tooMany = ProfileBatch(rdf);
        var changes = tooMany["changes"]!.AsArray();
        var first = changes[0]!.DeepClone();
        changes.Clear();
        for (int i = 0; i < 4097; i++)
        {
            changes.Add(first.DeepClone());
        }

        Assert.IsTrue((await Mutate(client, tooMany, ProfileMutation(rdf))).TryGetProperty("errors", out _));
        Assert.AreEqual(1, (await ProfileRead(client, rdf)).GetProperty("nodes").GetArrayLength());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Profile_GraphQl_invalid_later_change_and_multiple_roots_do_not_write(bool rdf)
    {
        _directory = NewDirectory();
        await using var host = ProfileHost(rdf);
        await host.StartAsync();
        using var client = Client(host);
        var input = ProfileBatch(rdf);
        if (rdf)
        {
            input["changes"]![1]!["quad"]!["predicate"] = new JsonObject { ["kind"] = "literal", ["lexicalForm"] = "invalid" };
        }
        else
        {
            input["changes"]![1]!["name"] = null;
        }

        Assert.IsTrue((await Mutate(client, input, ProfileMutation(rdf))).TryGetProperty("errors", out _));
        Assert.AreEqual(0, (await ProfileRead(client, rdf)).GetProperty("nodes").GetArrayLength());
        string profile = rdf ? "Rdf" : "Graph";
        string multiple = $$"""mutation($input:{{profile}}ChangeBatchInput!) { a:apply{{profile}}Changes(input:$input) {version} b:apply{{profile}}Changes(input:$input) {version} }""";
        Assert.IsTrue((await Mutate(client, ProfileBatch(rdf), multiple)).TryGetProperty("errors", out _));
        Assert.AreEqual(0, (await ProfileRead(client, rdf)).GetProperty("nodes").GetArrayLength());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Profile_GraphQl_schema_and_cached_mutations_respect_writer_authority(bool rdf)
    {
        _directory = NewDirectory();
        await using var host = ProfileHost(rdf, true);
        await host.StartAsync();
        using var reader = Client(host, "profile-graphql-reader-0000000001");
        using var writer = Client(host, "profile-graphql-writer-0000000001");
        var input = ProfileBatch(rdf);
        var denied = await Mutate(reader, input, ProfileMutation(rdf));
        Assert.AreEqual("FORBIDDEN", denied.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString());
        Assert.AreEqual(0, (await ProfileRead(reader, rdf)).GetProperty("nodes").GetArrayLength());
        Commit(await Mutate(writer, input, ProfileMutation(rdf)));
        denied = await Mutate(reader, input, ProfileMutation(rdf));
        Assert.AreEqual("FORBIDDEN", denied.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString());
        using var schema = await reader.PostAsJsonAsync("/graphql", new EntityForgeGraphQlRequest("{ __schema { mutationType { fields { name } } } }"));
        string json = await schema.Content.ReadAsStringAsync();
        StringAssert.Contains(json, rdf ? "applyRdfChanges" : "applyGraphChanges");
        Assert.IsFalse(json.Contains("applySemanticChanges", StringComparison.Ordinal));
    }
}
