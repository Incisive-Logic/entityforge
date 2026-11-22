using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Client;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using EntityForge.Semantics;
using EntityForge.Query.Projection;
using EntityForge.Semantics.Protobuf;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityOperationTransportTests
{
    private static async Task ExerciseReadScopes(EntityForgeDbRuntime runtime, HttpClient http, DatabaseProfileId profile, Guid incarnation)
    {
        EntityMappingManifest[] manifests;
        object query;
        string kind;
        Func<Task> update;
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            var store = runtime.GraphDatabase.EntityStore<Item>();
            var write = await store.PrepareSaveAsync(new("scope", "scope-before"), Guid.NewGuid());
            query = write.Transaction.Mutations.OfType<CreateVertex>().Single().Vertex.Value;
            await runtime.GraphDatabase.Operations().DeliverAsync(write);
            manifests = [store.Mapping.Manifest];
            kind = "vertex";
            update = async () =>
{
    await store.SaveAsync(new("scope", "scope-after"));
};
        }
        else if (profile == DatabaseProfileId.Rdf)
        {
            var store = runtime.RdfDatabase.EntityStore<Item>();
            var write = await store.PrepareSaveAsync(new("scope", "scope-before"), Guid.NewGuid());
            query = ((RdfIri)write.Transaction.Mutations.OfType<AssertRdfQuad>().First().Quad.Subject).Value;
            await runtime.RdfDatabase.Operations().DeliverAsync(write);
            manifests = [store.Mapping.Manifest];
            kind = "subject";
            update = async () =>
{
    await store.SaveAsync(new("scope", "scope-after"));
};
        }
        else
        {
            var store = runtime.Database.EntityStore<Item>();
            await store.SaveAsync(new("scope", "scope-before"));
            manifests = store.Manifests.ToArray();
            kind = "project";
            var binding = store.TargetBindings.First(b => !b.IdentityScheme.Identifier.AbsoluteUri.Contains("owned-slot"));
            query = new EntityReadScopeEndpoints.ProjectionRead(new(binding.Concept, binding.IdentityScheme, new StringSemanticValue("scope")),
                binding.Members.Where(m => m.Kind == SemanticTargetMemberKind.Property)
                    .Select(m => new EntityReadScopeEndpoints.ProjectionField(m.Term, m.Slot, EntityFieldValueSelection.AllCandidates, null)).ToArray());
            var projection = (EntityReadScopeEndpoints.ProjectionRead)query;
            var jsonTerm = new SemanticTerm(new Uri("urn:scope:json"), SemanticTermRole.Property);
            query = projection with
            {
                Fields = [.. projection.Fields, new(jsonTerm, "json", EntityFieldValueSelection.AllCandidates, null)]
            };
            Guid sessionId = Guid.NewGuid();
            var source = new Uri("urn:scope:json-source");
            var start = new SemanticChangeSessionStart(sessionId, SemanticChangeSessionKind.Incremental,
                new(source, new Uri("urn:scope:json-partition"), "1", Guid.NewGuid(), "1"), Guid.NewGuid(), DateTimeOffset.UtcNow);
            var change = new SemanticEntityChange(Guid.NewGuid(), SemanticEntityChangeKind.ReplaceSourceState, projection.Identity,
                new(source, "scope", DateTimeOffset.UtcNow, new(new Uri("urn:scope:vocabulary"), "1", SemanticModelKind.Vocabulary)),
                fields: [new(jsonTerm, SemanticFieldChangeKind.Set, new JsonSemanticValue("{\"z\":2,\"a\":[true,null]}"))]);
            SemanticChangeSessionPage[] pages = [new(sessionId, 0, [change])];
            await runtime.Database.Operations().ApplyAsync(new(start, pages, new(sessionId, 1, 1,
                new SemanticChangeSessionTransportCodec().ComputeContentHash(pages), DateTimeOffset.UtcNow)));
            update = async () =>
{
    await store.SaveAsync(new("scope", "scope-after"));
};
        }
        string target = $"?databaseIncarnation={incarnation:D}&expectedProfile={profile}";
        var description = new EntityReadScopeEndpoints.OpenRequest(manifests, 60_000,
            SemanticApplicationIdentity.Default.Identifier, new Uri("urn:scope:test"));
        http.DefaultRequestHeaders.Authorization = null;
        using (var denied = await http.PostAsJsonAsync("/v1/application/read-scopes" + target, description))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Reader);
        var unknown = manifests.Select(m => new EntityMappingManifest(new("reader-must-not-register"), m.Convention,
            m.EntityType, m.Profile, m.ProfileTypeBinding, m.Members, m.ProfileBindings)).ToArray();
        EntityMappingRegistry registry = profile switch
        {
            DatabaseProfileId.PropertyGraph => runtime.GraphDatabase.Mappings(),
            DatabaseProfileId.Rdf => runtime.RdfDatabase.Mappings(),
            _ => runtime.Database.Mappings()
        };
        using (var rejected = await http.PostAsJsonAsync("/v1/application/read-scopes" + target, description with
        {
            Manifests = unknown
        }))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, rejected.StatusCode);
        }

        foreach (var manifest in unknown)
        {
            Assert.IsNull(await registry.GetAsync(manifest.Model, manifest.EntityType));
        }

        using var opened = await http.PostAsJsonAsync("/v1/application/read-scopes" + target, description);
        Assert.AreEqual(HttpStatusCode.OK, opened.StatusCode, await opened.Content.ReadAsStringAsync());
        var scope = (await opened.Content.ReadFromJsonAsync<EntityReadScopeEndpoints.ScopeResponse>())!;
        string route = $"/v1/application/read-scopes/{scope.ScopeId:D}";
        IGraphEntityReadScope? graphScope = profile == DatabaseProfileId.PropertyGraph
            ? await HttpGraphEntityReadScope.OpenAsync(http, incarnation, manifests[0], TimeSpan.FromMinutes(1)) : null;
        IRdfEntityReadScope? rdfScope = profile == DatabaseProfileId.Rdf
            ? await HttpRdfEntityReadScope.OpenAsync(http, incarnation, manifests[0], TimeSpan.FromMinutes(1)) : null;
        await update();
        {
            using var counting = new ScalarReadRequests();
            using var scalarHttp = new HttpClient(counting) { BaseAddress = http.BaseAddress };
            scalarHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Reader);
            var scalar = profile switch
            {
                DatabaseProfileId.PropertyGraph => await new HttpGraphEntityClient(scalarHttp, incarnation).EntityStore<Item>().FindAsync("scope"),
                DatabaseProfileId.Rdf => await new HttpRdfEntityClient(scalarHttp, incarnation).EntityStore<Item>().FindAsync("scope"),
                _ => await new HttpSemanticEntityClient(scalarHttp, incarnation).EntityStore<Item>().FindAsync("scope")
            };
            Assert.AreEqual("scope-after", scalar!.Name);
            Assert.AreEqual(1, counting.Requests, "Ordinary materialization is one completed read, including receiver cleanup.");
            CollectionAssert.AreEqual(new[] { kind }, counting.Kinds.ToArray(),
                "Scalar materialization must not issue an unused traversal.");
        }
        if (graphScope is not null)
        {
            await using (graphScope)
            {
                var mapped = await GraphEntityMaterialization.ReadEntityAsync(graphScope,
                    runtime.GraphDatabase.EntityStore<Item>().Mapping.Map, "scope", default);
                Assert.AreEqual(new Item("scope", "scope-before"), mapped!.Value);
                Assert.AreEqual(graphScope.Version, mapped.SnapshotVersion);
                Assert.IsNull(await graphScope.GetVertexAsync(GraphVertexId.New()));
                Assert.AreEqual(0, (await graphScope.OutgoingAsync(new((Guid)query), "no-edge")).Count);
                await graphScope.ValidateAsync();
            }
        }
        if (rdfScope is not null)
        {
            await using (rdfScope)
            {
                var mapped = await RdfEntityMaterialization.ReadEntityAsync(rdfScope,
                    runtime.RdfDatabase.EntityStore<Item>().Mapping.Map, "scope", default);
                Assert.AreEqual(new Item("scope", "scope-before"), mapped!.Value);
                Assert.AreEqual(rdfScope.Version, mapped.SnapshotVersion);
                Assert.IsNotNull(mapped.ConcurrencyToken);
                Assert.AreEqual(0, (await rdfScope.ReadDefaultSubjectAsync(new("urn:scope:absent"))).Count);
                await rdfScope.ValidateAsync();
            }
        }
        using var read = await http.PostAsJsonAsync(route + "/read" + target, new
        {
            kind,
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement;
        Assert.AreEqual(scope.SnapshotVersion, body.GetProperty("snapshotVersion").GetInt64());
        StringAssert.Contains(body.GetProperty("value").GetRawText(), "scope-before");
        Assert.IsFalse(body.GetProperty("value").GetRawText().Contains("scope-after"));
        if (profile == DatabaseProfileId.Semantic)
        {
            var values = Descendants(body.GetProperty("value")).Where(e => e.ValueKind == JsonValueKind.Object &&
                e.TryGetProperty("$queryValue", out var tag) && tag.GetString() == "json").ToArray();
            Assert.AreEqual(1, values.Length);
            Assert.AreEqual("{\"a\":[true,null],\"z\":2}", values[0].GetProperty("value").GetProperty("value").GetString());
        }
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Writer);
        using (var denied = await http.PostAsync(route + "/release" + target, null))
        {
            Assert.AreEqual(HttpStatusCode.Gone, denied.StatusCode);
        }

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Reader);
        using (var valid = await http.PostAsync(route + "/validate" + target, null))
        {
            valid.EnsureSuccessStatusCode();
        }

        using (var wrong = await http.PostAsync(route + "/release" + $"?databaseIncarnation={incarnation:D}&expectedProfile=wrong", null))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, wrong.StatusCode);
        }

        using (var valid = await http.PostAsync(route + "/validate" + target, null))
        {
            valid.EnsureSuccessStatusCode();
        }

        for (int retry = 0; retry < 2; retry++)
        {
            using (var released = await http.PostAsync(route + "/release" + target, null))
            {
                Assert.AreEqual(HttpStatusCode.NoContent, released.StatusCode);
            }
        }

        using (var unavailable = await http.PostAsync(route + "/validate" + target, null))
        {
            Assert.AreEqual(HttpStatusCode.Gone, unavailable.StatusCode);
        }

        using var loss = new LoseReleaseReply();
        using var remoteHttp = new HttpClient(loss) { BaseAddress = http.BaseAddress };
        remoteHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Reader);
        var transport = new HttpEntityReadTransport(remoteHttp, incarnation, profile switch
        {
            DatabaseProfileId.PropertyGraph => (IEntityReadRecipePolicy)EntityForge.Db.Graph.GraphReadRecipePolicy.Instance,
            DatabaseProfileId.Rdf => EntityForge.Db.Rdf.RdfReadRecipePolicy.Instance,
            DatabaseProfileId.Semantic => EntityForge.Db.Semantic.SemanticReadRecipePolicy.Instance,
            _ => throw new ArgumentException()
        });
        var remoteScope = await transport.OpenAsync(manifests, TimeSpan.FromMinutes(1), description.SourceSystem, description.SourcePartition);
        var remoteValue = await remoteScope.ReadAsync(kind, query);
        StringAssert.Contains(remoteValue.GetRawText(), "scope-after");
        await remoteScope.ValidateAsync();
        await remoteScope.DisposeAsync();
        await remoteScope.DisposeAsync();
        Assert.AreEqual(2, loss.Releases, "A lost successful release reply must be retried with the same handle.");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Writer);
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            await ExerciseGraphReferences(runtime, http);
        }
    }

    private sealed record ScopeTarget(string Id, string Name);

    private sealed record ScopeOwner(Guid Id, ScopeTarget Target);

    private sealed class FaultReferenceRead : DelegatingHandler
    {
        internal int Reads, Releases;
        internal bool Fail;

        internal FaultReferenceRead() : base(new HttpClientHandler())
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/read") && ++Reads == 3 && Fail)
            {
                return new(HttpStatusCode.Gone)
                {
                    Content = JsonContent.Create(new
                    {
                        code = "read_scope_unavailable"
                    })
                };
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/release"))
            {
                Releases++;
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    private static async Task ExerciseGraphReferences(EntityForgeDbRuntime runtime, HttpClient http)
    {
        var targetMap = GraphEntityMap.For<ScopeTarget>().VertexLabel("ScopeTarget").Identity(x => x.Id, "id")
            .Property(x => x.Name, "name").Materialize(c => new(c.IdentityAs<string>(), c.Required<string>("Name")));
        var ownerMap = GraphEntityMap.For<ScopeOwner>().VertexLabel("ScopeOwner").Identity(x => x.Id, "id")
            .Edge(x => x.Target, "TARGET", targetMap).Materialize(c => new(c.IdentityAs<Guid>(), c.Required<ScopeTarget>("Target")));
        var target = new ScopeTarget("target", "before");
        var owner = new ScopeOwner(Guid.NewGuid(), target);
        var targets = runtime.GraphDatabase.EntityStore(targetMap);
        var owners = runtime.GraphDatabase.EntityStore(ownerMap);
        await targets.SaveAsync(target);
        await owners.SaveAsync(owner);
        var edgeId = runtime.GraphDatabase.Outgoing(new GraphVertexId(owner.Id), "TARGET").Single().Id;
        double[] specialValues = [double.NaN, double.PositiveInfinity, double.NegativeInfinity];
        await runtime.GraphDatabase.CommitAsync(new(Guid.NewGuid(), specialValues.SelectMany((value, index) => new GraphMutation[]
        {
            new SetVertexProperty(new(owner.Id), "special" + index, new DatabaseValue.Double(value)),
            new SetEdgeProperty(edgeId, "special" + index, new DatabaseValue.Double(value))
        }).ToArray()));
        using var handler = new FaultReferenceRead();
        using var reader = new HttpClient(handler) { BaseAddress = http.BaseAddress };
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Reader);
        var completed = await new HttpGraphEntityClient(reader, runtime.GraphDatabase.Operations().DatabaseIncarnation)
            .EntityStore(ownerMap).FindAsync(owner.Id);
        Assert.AreEqual("before", completed!.Target.Name, "Complete reads must retain typed target mapping and native value decoding.");
        Assert.AreEqual(0, handler.Reads, "Complete materialization must not fall back to fragment requests.");
        var scope = await HttpGraphEntityReadScope.OpenAsync(reader, runtime.GraphDatabase.Operations().DatabaseIncarnation,
            owners.Mapping.Manifest, TimeSpan.FromMinutes(1));
        var nativeVertex = (await scope.GetVertexAsync(new(owner.Id)))!;
        var nativeEdge = (await scope.OutgoingAsync(new(owner.Id), "TARGET")).Single();
        for (int index = 0; index < specialValues.Length; index++)
        {
            Assert.AreEqual(specialValues[index], ((DatabaseValue.Double)nativeVertex.Properties["special" + index]).Value);
            Assert.AreEqual(specialValues[index], ((DatabaseValue.Double)nativeEdge.Properties["special" + index]).Value);
        }
        await targets.SaveAsync(target with
        {
            Name = "after"
        });
        var loaded = await EntityReadScopeLifetime.RunAsync(scope, async () =>
        {
            var result = await GraphEntityMaterialization.ReadEntityAsync(scope, ownerMap, owner.Id, default);
            return result;
        }, () => scope.ValidateAsync());
        Assert.AreEqual("before", loaded!.Value.Target.Name);
        Assert.AreEqual(1, handler.Releases);
        handler.Reads = 0;
        handler.Fail = true;
        var failedScope = await HttpGraphEntityReadScope.OpenAsync(reader, runtime.GraphDatabase.Operations().DatabaseIncarnation,
            owners.Mapping.Manifest, TimeSpan.FromMinutes(1));
        await Assert.ThrowsExceptionAsync<HttpRequestException>(async () => await EntityReadScopeLifetime.RunAsync(failedScope,
            () => GraphEntityMaterialization.ReadEntityAsync(failedScope, ownerMap, owner.Id, default), () => failedScope.ValidateAsync()));
        Assert.AreEqual(3, handler.Reads);
        Assert.AreEqual(2, handler.Releases, "The failed reference read must release the same scope exactly once.");
    }

    private static IEnumerable<JsonElement> Descendants(JsonElement value)
    {
        yield return value;
        var children = value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().Select(p => p.Value)
            : value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().AsEnumerable() : [];
        foreach (var child in children)
        {
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class LoseReleaseReply() : DelegatingHandler(new HttpClientHandler())
    {
        internal int Releases;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (request.RequestUri!.AbsolutePath.EndsWith("/release") && ++Releases == 1)
            {
                response.EnsureSuccessStatusCode();
                response.Dispose();
                throw new HttpRequestException("The successful release response was lost.");
            }
            return response;
        }
    }

    private sealed class ScalarReadRequests() : DelegatingHandler(new HttpClientHandler())
    {
        internal List<string> Kinds { get; } = [];

        internal int Requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
                foreach (var shape in body.GetProperty("recipe").GetProperty("shapes").EnumerateArray())
                {
                    Kinds.Add(shape.GetProperty("kind").GetString()!);
                    Assert.AreEqual(0, shape.GetProperty("links").GetArrayLength());
                }
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/read", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
                Kinds.Add(body.GetProperty("kind").GetString()!);
            }
            return await base.SendAsync(request, token);
        }
    }
}
