using EntityForge.Db.Query.Adapters;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntityForge.Db.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    private const string ApplyMutation = """
        mutation Apply($input: SemanticChangeBatchInput!) {
          commit: applySemanticChanges(input: $input) {
            sessionId databaseIncarnation transactionId version outcome effectCount changeCursor
            sourceProgress { sourceSystem sourcePartition generationId sourceVersion sourcePosition databaseVersion }
          }
        }
        """;

    private static JsonObject MutationBatch(string provider = "urn:provider:a", string value = "Alpha", string identity = "1") => JsonSerializer.SerializeToNode(new
    {
        sessionId = Guid.NewGuid(),
        producerActivityId = Guid.NewGuid(),
        startedAt = "2026-10-24T10:00:00Z",
        completedAt = "2026-10-24T10:00:01Z",
        source = new
        {
            sourceSystem = provider,
            sourcePartition = "urn:partition:companies",
            generationId = Guid.NewGuid(),
            sourceVersion = "1",
            sourcePosition = "1"
        },
        changes = new[]
        {
            new
        {
            changeId = Guid.NewGuid(), kind = "upsert",
            subject = new { concept = "urn:Company", scheme = "urn:company:number", value = new { kind = "string", lexical = identity } },
            provenance = new { sourceRecord = identity, observedAt = "2026-10-24T10:00:00Z", targetVocabulary = new { identifier = "urn:vocabulary", version = "1" } },
            fields = new[] { new { property = "urn:name", kind = "set", value = new { kind = "string", lexical = value } } }
        }
        }
    })!.AsObject();

    private static async Task<JsonElement> Mutate(HttpClient client, JsonObject input, string query = ApplyMutation, string? operation = null)
    {
        using var response = await client.PostAsJsonAsync("/graphql", new EntityForgeGraphQlRequest(query,
            new Dictionary<string, JsonElement> { ["input"] = JsonSerializer.SerializeToElement(input) }, operation));
        string json = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, json);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement Commit(JsonElement response)
    {
        Assert.IsFalse(response.TryGetProperty("errors", out _), response.GetRawText());
        return response.GetProperty("data").GetProperty("commit");
    }

    [TestMethod]
    public async Task GraphQl_mutation_json_values_retain_exact_numbers_through_variable_coercion()
    {
        _directory = NewDirectory();
        await using var host = BuildHost();
        await host.StartAsync();
        using var client = Client(host);
        var input = MutationBatch();
        input["changes"]![0]!["fields"]![0]!["value"] = JsonNode.Parse("""
            {"kind":"json","json":{"precise":12345678901234567890.123456789,"integer":9223372036854775807}}
            """);
        Commit(await Mutate(client, input));
        var value = (await MutationEntities(client)).GetProperty("nodes")[0].GetProperty("name").GetProperty("values")[0].GetProperty("value");
        // Semantic JSON values retain their canonical lexical representation in query results.
        using var bag = JsonDocument.Parse(value.GetString()!);
        Assert.AreEqual("12345678901234567890.123456789", bag.RootElement.GetProperty("precise").GetRawText());
        Assert.AreEqual("9223372036854775807", bag.RootElement.GetProperty("integer").GetRawText());
    }

    [TestMethod]
    public async Task GraphQl_mutation_documented_example_executes_against_runtime_schema()
    {
        _directory = NewDirectory();
        await using var host = BuildHost();
        await host.StartAsync();
        using var client = Client(host);
        string design = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GraphQlDesign", "SEMANTIC_GRAPHQL_DESIGN.md"));
        string mutation = System.Text.RegularExpressions.Regex.Matches(design, "```graphql\\s*([\\s\\S]*?)```")
            .Select(m => m.Groups[1].Value).Single(s => s.TrimStart().StartsWith("mutation", StringComparison.Ordinal));
        string json = System.Text.RegularExpressions.Regex.Match(design, "```json\\s*([\\s\\S]*?)```").Groups[1].Value;
        var result = await Mutate(client, JsonNode.Parse(json)!["input"]!.AsObject(), mutation);
        Assert.IsFalse(result.TryGetProperty("errors", out _), result.GetRawText());
        Assert.IsTrue(result.GetProperty("data").GetProperty("applySemanticChanges").GetProperty("version").GetInt64() > 0);
    }

    private static async Task<JsonElement> MutationEntities(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/graphql", new EntityForgeGraphQlRequest("""
            { entities(concept: "urn:Company", first: 100, includeEvidence: true) {
              nodes { identity name: field(property: "urn:name") { values { value evidence } } }
              evidence { identifier provider sourceRecord observedAt }
            } }
            """));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.IsFalse(document.RootElement.TryGetProperty("errors", out _), document.RootElement.GetRawText());
        return document.RootElement.GetProperty("data").GetProperty("entities").Clone();
    }

    [TestMethod]
    public async Task GraphQl_mutation_atomic_batch_receipt_replay_and_reopen()
    {
        _directory = NewDirectory();
        var input = MutationBatch();
        input["changes"]!.AsArray().Add(MutationBatch(identity: "2")["changes"]![0]!.DeepClone());
        long version;
        string transaction;
        await using (var host = BuildHost())
        {
            await host.StartAsync();
            using var client = Client(host);
            var receipt = Commit(await Mutate(client, input));
            version = receipt.GetProperty("version").GetInt64();
            transaction = receipt.GetProperty("transactionId").GetString()!;
            Assert.AreEqual(input["sessionId"]!.GetValue<Guid>().ToString(), receipt.GetProperty("sessionId").GetString());
            Assert.AreEqual(version, receipt.GetProperty("sourceProgress").GetProperty("databaseVersion").GetInt64());
            Assert.AreEqual(2, (await MutationEntities(client)).GetProperty("nodes").GetArrayLength());
            var replay = Commit(await Mutate(client, input));
            Assert.AreEqual(version, replay.GetProperty("version").GetInt64());
            Assert.AreEqual("IdempotentReplay", replay.GetProperty("outcome").GetString());
            var different = input.DeepClone().AsObject();
            different["changes"]![0]!["fields"]![0]!["value"]!["lexical"] = "Different";
            Assert.IsTrue((await Mutate(client, different)).TryGetProperty("errors", out _));
            await host.StopAsync();
        }
        await using (var recovered = BuildHost())
        {
            await recovered.StartAsync();
            using var client = Client(recovered);
            var replay = Commit(await Mutate(client, input));
            Assert.AreEqual(version, replay.GetProperty("version").GetInt64());
            Assert.AreEqual(transaction, replay.GetProperty("transactionId").GetString());
            Assert.AreEqual(2, (await MutationEntities(client)).GetProperty("nodes").GetArrayLength());
        }
    }

    [TestMethod]
    public async Task GraphQl_mutation_retractions_preserve_other_provider_contributions()
    {
        _directory = NewDirectory();
        await using var host = BuildHost();
        await host.StartAsync();
        using var client = Client(host);
        Commit(await Mutate(client, MutationBatch()));
        Commit(await Mutate(client, MutationBatch("urn:provider:b", "Beta")));
        Assert.AreEqual(2, (await MutationEntities(client)).GetProperty("nodes")[0].GetProperty("name").GetProperty("values").GetArrayLength());
        var removeValue = MutationBatch();
        removeValue["changes"]![0]!["fields"]![0]!["kind"] = "removeValue";
        Commit(await Mutate(client, removeValue));
        var values = (await MutationEntities(client)).GetProperty("nodes")[0].GetProperty("name").GetProperty("values");
        Assert.AreEqual(1, values.GetArrayLength());
        Assert.AreEqual("Beta", values[0].GetProperty("value").GetString());
        Commit(await Mutate(client, MutationBatch(value: "Restored")));
        var retract = MutationBatch();
        retract["changes"]![0]!["kind"] = "retractSourceEntity";
        retract["changes"]![0]!.AsObject().Remove("fields");
        Commit(await Mutate(client, retract));
        var result = await MutationEntities(client);
        Assert.AreEqual("Beta", result.GetProperty("nodes")[0].GetProperty("name").GetProperty("values")[0].GetProperty("value").GetString());
        Assert.IsTrue(result.GetProperty("evidence").EnumerateArray().All(e => e.GetProperty("provider").GetString() == "urn:provider:b"));
    }

    [TestMethod]
    public async Task GraphQl_mutation_invalid_later_change_and_multiple_roots_write_nothing()
    {
        _directory = NewDirectory();
        await using var host = BuildHost();
        await host.StartAsync();
        using var client = Client(host);
        var input = MutationBatch();
        var invalid = MutationBatch(identity: "2")["changes"]![0]!.DeepClone();
        invalid["fields"]![0]!["kind"] = "remove";
        input["changes"]!.AsArray().Add(invalid);
        var result = await Mutate(client, input);
        Assert.IsTrue(result.TryGetProperty("errors", out _));
        Assert.AreEqual("input.changes[1].fields[0]", result.GetProperty("errors")[0].GetProperty("extensions").GetProperty("inputPath").GetString());
        Assert.AreEqual(0, (await MutationEntities(client)).GetProperty("nodes").GetArrayLength());
        var multiple = "mutation($input: SemanticChangeBatchInput!) { a: applySemanticChanges(input:$input) { version } b: applySemanticChanges(input:$input) { version } }";
        Assert.IsTrue((await Mutate(client, MutationBatch(), multiple)).TryGetProperty("errors", out _));
        Assert.AreEqual(0, (await MutationEntities(client)).GetProperty("nodes").GetArrayLength());
        var oversized = MutationBatch();
        var change = oversized["changes"]![0]!.DeepClone();
        for (int i = 0; i < 128; i++)
        {
            oversized["changes"]!.AsArray().Add(change.DeepClone());
        }

        Assert.IsTrue((await Mutate(client, oversized)).TryGetProperty("errors", out _));
        Assert.AreEqual(0, (await MutationEntities(client)).GetProperty("nodes").GetArrayLength());
    }

    [TestMethod]
    public async Task GraphQl_mutation_lost_response_retries_original_identity_without_reapplying()
    {
        _directory = NewDirectory();
        await using var host = BuildHost();
        await host.StartAsync();
        using var client = Client(host);
        using var unreliable = new HttpClient(new LostGraphQlReply()) { BaseAddress = client.BaseAddress };
        var input = MutationBatch();
        await Assert.ThrowsExactlyAsync<HttpRequestException>(async () => await Mutate(unreliable, input));
        var receipt = Commit(await Mutate(client, input));
        Assert.AreEqual("IdempotentReplay", receipt.GetProperty("outcome").GetString());
        Assert.AreEqual(1, (await MutationEntities(client)).GetProperty("nodes").GetArrayLength());
    }

    private sealed class LostGraphQlReply() : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var response = await base.SendAsync(request, cancellationToken);
            await response.Content.ReadAsByteArrayAsync(cancellationToken);
            throw new HttpRequestException("Simulated loss after the server completed the mutation.");
        }
    }

    [TestMethod]
    public async Task GraphQl_mutation_source_precondition_conflict_is_atomic()
    {
        _directory = NewDirectory();
        await using var host = BuildHost();
        await host.StartAsync();
        using var client = Client(host);
        var first = MutationBatch();
        first["progressPrecondition"] = new JsonObject { ["expectation"] = "noPriorProgress" };
        Commit(await Mutate(client, first));
        var stale = MutationBatch(value: "Wrong");
        stale["progressPrecondition"] = first["progressPrecondition"]!.DeepClone();
        var result = await Mutate(client, stale);
        Assert.AreEqual("SOURCE_PROGRESS_CONFLICT", result.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString());
        Assert.AreEqual("Alpha", (await MutationEntities(client)).GetProperty("nodes")[0].GetProperty("name").GetProperty("values")[0].GetProperty("value").GetString());
    }

    [TestMethod]
    public async Task GraphQl_mutation_requires_writer_even_with_aliases_fragments_and_named_operations()
    {
        _directory = NewDirectory();
        var audit = new CapturingSecurityAuditSink();
        const string admin = "graphql-mutation-administrator-00000001", writer = "graphql-mutation-writer-00000000001", reader = "graphql-mutation-reader-00000000001";
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory)
            .ConfigureServices(services => services.AddSingleton<IEntityForgeDbSecurityAuditSink>(audit))
            .UseBearerTokenSecurity(new(admin, writer, reader, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var read = Client(host, reader);
        using var write = Client(host, writer);
        const string document = """
            query Inspect { __schema { mutationType { name } } }
            mutation Write($input: SemanticChangeBatchInput!) { ...WriteFields }
            fragment WriteFields on Mutation { commit: applySemanticChanges(input:$input) { version outcome } }
            """;
        var input = MutationBatch();
        var denied = await Mutate(read, input, document, "Write");
        Assert.AreEqual("FORBIDDEN", denied.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString());
        Assert.AreEqual(0, (await MutationEntities(read)).GetProperty("nodes").GetArrayLength());
        Assert.IsFalse((await Mutate(read, input, document, "Inspect")).TryGetProperty("errors", out _));
        Commit(await Mutate(write, input, document, "Write"));
        Assert.AreEqual(1, (await MutationEntities(read)).GetProperty("nodes").GetArrayLength());
        var cachedDenial = await Mutate(read, MutationBatch(identity: "2"), document, "Write");
        Assert.AreEqual("FORBIDDEN", cachedDenial.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString());
        Assert.AreEqual(1, (await MutationEntities(read)).GetProperty("nodes").GetArrayLength());
        Assert.IsTrue(audit.Events.Any(e => e.RequiredAccess == EntityForgeDbAccessKind.Write && e.Outcome == "denied"));
        Assert.IsTrue(audit.Events.Any(e => e.RequiredAccess == EntityForgeDbAccessKind.Write && e.Outcome == "allowed"));
    }

    [TestMethod]
    public async Task GraphQl_mutation_relationship_add_remove_clear_and_source_replacement()
    {
        _directory = NewDirectory();
        await using var host = BuildHost();
        await host.StartAsync();
        using var client = Client(host);
        var target = MutationBatch(value: "Target", identity: "2");
        Commit(await Mutate(client, target));
        async Task<int> CountLinks()
        {
            using var response = await client.PostAsJsonAsync("/graphql", new EntityForgeGraphQlRequest("""
                { entities(concept:"urn:Company") { nodes { links: relationship(property:"urn:partner", targetConcept:"urn:Company") { nodes { identity } } } } }
                """));
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.IsFalse(document.RootElement.TryGetProperty("errors", out _), document.RootElement.GetRawText());
            return document.RootElement.GetProperty("data").GetProperty("entities").GetProperty("nodes").EnumerateArray()
                .Sum(n => n.GetProperty("links").GetProperty("nodes").GetArrayLength());
        }
        async Task Edit(string kind)
        {
            var batch = MutationBatch();
            var link = new JsonObject { ["relationship"] = "urn:partner", ["kind"] = kind };
            if (kind != "clear")
            {
                link["target"] = target["changes"]![0]!["subject"]!.DeepClone();
            }

            batch["changes"]![0]!["relationships"] = new JsonArray(link);
            Commit(await Mutate(client, batch));
        }
        await Edit("add");
        Assert.AreEqual(1, await CountLinks());
        await Edit("remove");
        Assert.AreEqual(0, await CountLinks());
        await Edit("add");
        await Edit("clear");
        Assert.AreEqual(0, await CountLinks());
        await Edit("add");
        var replacement = MutationBatch(value: "Replacement");
        replacement["changes"]![0]!["kind"] = "replaceSourceState";
        Commit(await Mutate(client, replacement));
        Assert.AreEqual(0, await CountLinks());
    }

    [TestMethod]
    [DataRow("nullValue", null)]
    [DataRow("boolean", "true")]
    [DataRow("integer", "9223372036854775807")]
    [DataRow("floatingPoint", "1.25")]
    [DataRow("decimal", "12345678901234567890.123456789")]
    [DataRow("string", "Exact text")]
    [DataRow("languageString", "Bonjour")]
    [DataRow("date", "2026-10-24")]
    [DataRow("time", "12:34:56.123")]
    [DataRow("dateTime", "2026-10-24T12:34:56+01:00")]
    [DataRow("duration", "PT12H")]
    [DataRow("binary", "AQID")]
    [DataRow("absoluteUri", "urn:value")]
    [DataRow("guid", "0e080d86-00c8-4818-ab76-4bc510328514")]
    [DataRow("json", null)]
    [DataRow("typedLiteral", "001.0")]
    public void GraphQl_mutation_values_preserve_native_codec_roundtrip(string kind, string? lexical)
    {
        var input = MutationBatch();
        var value = new JsonObject { ["kind"] = kind };
        if (lexical is not null)
        {
            value["lexical"] = lexical;
        }

        if (kind == "languageString")
        {
            value["language"] = "fr";
        }

        if (kind == "typedLiteral")
        {
            value["datatype"] = "urn:datatype";
        }

        if (kind == "json")
        {
            value["json"] = JsonNode.Parse("{\"nested\":[1,true,null]}");
        }

        input["changes"]![0]!["fields"]![0]!["value"] = value;
        var session = SemanticGraphQlMutations.Compile(JsonSerializer.SerializeToElement(input));
        var bytes = _codec.Serialize(session.Pages[0]);
        CollectionAssert.AreEqual(bytes, _codec.Serialize(_codec.DeserializePage(bytes)));
        Assert.AreEqual(kind == "nullValue" ? "null" : kind, session.Pages[0].Changes[0].Fields[0].Value!.Kind.ToString(), ignoreCase: true);
    }
}
