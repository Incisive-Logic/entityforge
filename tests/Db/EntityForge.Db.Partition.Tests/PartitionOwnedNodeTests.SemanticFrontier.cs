using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Semantic;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Semantics;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Semantic.Engine;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Semantic_frontier_selects_one_root_from_original_population_without_relaxing_bounds()
    {
        const int population = 100_000;
        var concept = new SemanticTerm(new Uri("urn:frontier:item"), SemanticTermRole.Concept);
        var property = new SemanticTerm(new Uri("urn:frontier:number"), SemanticTermRole.Property);
        var peer = new SemanticTerm(new Uri("urn:frontier:peer"), SemanticTermRole.Relationship);
        var scheme = new SemanticIdentityScheme(new Uri("urn:frontier:id"), "id", SemanticValueKind.String);
        var model = new SemanticModelReference(new Uri("urn:frontier:model"), "1", SemanticModelKind.Vocabulary);
        SemanticEntityIdentity Identity(int key) => new(concept, scheme, new StringSemanticValue(key.ToString()));
        var fields = Enumerable.Range(0, population).Select(key => new SemanticFieldEvidence(Guid.NewGuid(), Identity(key), property,
            new IntegerSemanticValue(key), new(new Uri("urn:frontier:source"), key.ToString(), DateTimeOffset.UnixEpoch, model), new(1), null)).ToArray();
        var link = new SemanticRelationshipEvidence(Guid.NewGuid(), Identity(7), peer, Identity(8), fields[7].Provenance, new(1), null);
        var state = SemanticDatabaseState.Empty with
        {
            Version = new(1),
            Fields = fields,
            Relationships = [link],
            Catalog = new([model], [concept, property, peer], [scheme])
        };
        var incoming = link with
        {
            AssertionId = Guid.NewGuid(),
            Subject = Identity(9),
            Target = Identity(7)
        };
        var self = link with
        {
            AssertionId = Guid.NewGuid(),
            Target = Identity(7)
        };
        var incidentState = state with
        {
            Relationships = [link, incoming, self]
        };
        foreach (var incidentPaths in new[]
        {
            new SemanticEntityAccessPaths(incidentState),
            DatabaseReadView.Create(incidentState, SemanticTimestampIndex.For(incidentState)).EntityAccessPaths
        })
        {
            CollectionAssert.AreEquivalent(new[] { link, incoming, self },
                (await incidentPaths.ReadIncidentEvidenceAsync([Identity(7)], default)).ToArray());
            CollectionAssert.AreEquivalent(new[] { link, incoming, self },
                (await incidentPaths.ReadIncidentEvidenceAsync([Identity(7), Identity(8), Identity(9)], default)).ToArray());
            using var cancelledIncident = new CancellationTokenSource();
            cancelledIncident.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await incidentPaths.ReadIncidentEvidenceAsync([Identity(7)], cancelledIncident.Token));
        }
        var paths = new SemanticEntityAccessPaths(state);
        Assert.IsTrue((await paths.FindConceptMembersAsync(concept, PartitionQueryLimits.MaximumFacts + 1)).Count > PartitionQueryLimits.MaximumFacts,
            "The old complete-concept probe must exceed the unchanged exchange bound.");
        var query = new PortableEntityQueryRequest(concept.Identifier.AbsoluteUri, new(concept.Identifier.AbsoluteUri),
            Filter: new("propertyComparison", property.Identifier.AbsoluteUri, "equal", new(JsonSerializer.SerializeToElement(7), "integer")), ConceptMatchMode: "direct");
        var selection = new PartitionQueryExchangeRequest("entity", EntityQuery: query, SemanticFrontier: new(0));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await PartitionOwnedNode.CaptureSemanticFrontierEvidenceAsync(state, paths, selection, default),
            "An unindexed broad scan remains bounded; this optimization does not raise its admission limit.");
        // Production timestamp views supply complete predicate indexes even for
        // ColdJson fields. Exercise that same view rather than inventing a binding.
        paths = DatabaseReadView.Create(state, SemanticTimestampIndex.For(state)).EntityAccessPaths;
        var root = await PartitionOwnedNode.CaptureSemanticFrontierEvidenceAsync(state, paths, selection, default);
        Assert.AreEqual(1, root.Fields!.Length);
        Assert.AreEqual(Identity(7), root.Fields[0].Subject);
        Assert.AreEqual(0, root.Relationships!.Length);
        var window = query with
        {
            Filter = new("and", Operands: [
            new("propertyComparison", property.Identifier.AbsoluteUri, "greaterThanOrEqual", new(JsonSerializer.SerializeToElement(7), "integer")),
                new("propertyComparison", property.Identifier.AbsoluteUri, "lessThanOrEqual", new(JsonSerializer.SerializeToElement(14), "integer"))])
        };
        var windowRoots = await PartitionOwnedNode.CaptureSemanticFrontierEvidenceAsync(state, paths, selection with
        {
            EntityQuery = window
        }, default);
        CollectionAssert.AreEquivalent(Enumerable.Range(7, 8).Select(Identity).ToArray(), windowRoots.Fields!.Select(field => field.Subject).ToArray());
        var selectedEight = Enumerable.Range(7, 8).Select(Identity).ToArray();
        for (int warm = 0; warm < 20; warm++)
        {
            await paths.ReadEntityEvidenceAsync(selectedEight);
            await paths.ReadIncidentEvidenceAsync(selectedEight);
        }
        var costs = new List<object>();
        for (int round = 0; round < 4; round++)
        {
            foreach (bool incident in (round % 2 == 0 ? new[] { false, true } : new[] { true, false }))
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                long fieldRows = 0, relationshipRows = 0;
                for (int iteration = 0; iteration < 1000; iteration++)
                {
                    if (incident)
                    {
                        relationshipRows += (await paths.ReadIncidentEvidenceAsync(selectedEight)).Count;
                    }
                    else
                    {
                        var slice = await paths.ReadEntityEvidenceAsync(selectedEight);
                        fieldRows += slice.Fields.Count;
                        relationshipRows += slice.Relationships.Count;
                    }
                }
                long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Assert.AreEqual(1000L, relationshipRows);
                Assert.AreEqual(incident ? 0L : 8000L, fieldRows);
                costs.Add(new
                {
                    round,
                    incident,
                    bytes,
                    elapsedMs,
                    fieldRows,
                    relationshipRows
                });
            }
        }
        string? report = Environment.GetEnvironmentVariable("EF_FRONTIER_COST_REPORT");
        if (report is not null)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(costs));
        }
        // Providers without directional relationship support retain their full
        // evidence reader; the projection only consumes its relationship slice.
        var fallback = new FrontierFallbackEvidence(new(fields.Take(8).ToArray(), [link]));
        var fallbackPaths = new SemanticEntityAccessPaths(state);
        typeof(SemanticEntityAccessPaths).GetField("_physicalEvidence", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(fallbackPaths, fallback);
        CollectionAssert.AreEqual(new[] { link }, (await fallbackPaths.ReadIncidentEvidenceAsync(selectedEight)).ToArray());
        Assert.AreEqual(1, fallback.Reads);
        using (var fallbackCancel = new CancellationTokenSource())
        {
            fallbackCancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await fallbackPaths.ReadIncidentEvidenceAsync(selectedEight, fallbackCancel.Token));
            Assert.AreEqual(1, fallback.Reads);
        }
        var support = await PartitionOwnedNode.CaptureSemanticFrontierEvidenceAsync(state, paths,
            selection with
            {
                SemanticFrontier = new(1, [Identity(7)])
            }, default);
        Assert.AreEqual(link, support.Relationships!.Single());
        Assert.AreEqual(0, support.Fields!.Length, "Support round must not repeat selected root fields.");
        CollectionAssert.AreEquivalent(new[] { link }, (await paths.ReadIncidentEvidenceAsync([link.Subject, link.Target], default)).ToArray());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await PartitionOwnedNode.CaptureSemanticFrontierEvidenceAsync(state, paths,
            selection with
            {
                SemanticFrontier = new(1, [Identity(7), Identity(7)])
            }, default));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await PartitionOwnedNode.CaptureSemanticFrontierEvidenceAsync(state, paths,
            selection with
            {
                SemanticFrontier = new(1, fields.Take(PartitionQueryLimits.MaximumFacts + 1).Select(f => f.Subject).ToArray())
            }, default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await PartitionOwnedNode.CaptureSemanticFrontierEvidenceAsync(state, paths, selection, cancelled.Token));
        Assert.AreEqual(1, (await PartitionOwnedNode.CaptureSemanticFrontierEvidenceAsync(state, paths, selection, default)).Fields!.Length);
    }

    [TestMethod]
    public void Semantic_frontier_owns_selection_and_only_accepts_positive_scalar_roots()
    {
        var concept = new SemanticTerm(new Uri("urn:root"), SemanticTermRole.Concept);
        var scheme = new SemanticIdentityScheme(new Uri("urn:id"), "id", SemanticValueKind.String);
        var original = new SemanticEntityIdentity(concept, scheme, new StringSemanticValue("one"));
        var identities = new[] { original };
        var query = new PortableEntityQueryRequest("urn:root", new("urn:root"),
            Filter: new("propertyComparison", "urn:p", "equal", new(JsonSerializer.SerializeToElement("x"))), ConceptMatchMode: "direct");
        Assert.IsTrue(PartitionSemanticFrontier.Supports(query));
        Assert.IsFalse(PartitionSemanticFrontier.Supports(query with
        {
            Filter = new("not", Operand: query.Filter)
        }));
        Assert.IsFalse(PartitionSemanticFrontier.Supports(query with
        {
            Filter = null
        }));
        Assert.IsFalse(PartitionSemanticFrontier.Supports(query with
        {
            ConceptMatchMode = "entailed"
        }));
        var request = new PartitionQueryExchangeRequest("entity", EntityQuery: query, SemanticFrontier: new(1, identities));
        var owned = PartitionQueryOwnedSelection.Capture(request);
        var key = new PartitionQueryWorkKey(null, semantic: request.SemanticFrontier);
        identities[0] = new(concept, scheme, new StringSemanticValue("two"));
        Assert.AreEqual(original, owned.Value.SemanticFrontier!.Entities![0]);
        Assert.AreEqual(key, new PartitionQueryWorkKey(null, semantic: owned.Value.SemanticFrontier));
        Assert.AreNotEqual(key, new PartitionQueryWorkKey(null, semantic: request.SemanticFrontier));
        var interpretation = new PartitionQueryInterpretation(owned);
        interpretation.Require(PartitionQueryOwnedSelection.Capture(owned.Value with
        {
            SemanticFrontier = new(1, identities)
        }));
        interpretation.Require(PartitionQueryOwnedSelection.Capture(owned.Value with
        {
            SemanticFrontier = new(0)
        }));
        foreach (var invalid in new PartitionSemanticFrontier?[] { null, new(-1, identities), new(2, identities) })
        {
            Assert.ThrowsExactly<InvalidDataException>(() => interpretation.Require(PartitionQueryOwnedSelection.Capture(owned.Value with { SemanticFrontier = invalid })));
        }

        var whole = new PartitionQueryInterpretation(PartitionQueryOwnedSelection.Capture(owned.Value with
        {
            SemanticFrontier = null
        }));
        Assert.ThrowsExactly<InvalidDataException>(() => whole.Require(owned));
        Assert.AreNotEqual(key, new PartitionQueryWorkKey(null, semantic: new(2, owned.Value.SemanticFrontier.Entities)));
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task Semantic_frontier_accepts_near_limit_evidence_from_local_and_remote_owners(int leafOwner)
    {
        await WithProfileFragments(DatabaseProfileId.Semantic, async (_, candidate, _, _, http, nodes, _) =>
        {
            var parents = new HttpSemanticEntityClient(http, candidate.Generation).EntityStore<SemanticReferenceParent>();
            var leaves = new HttpSemanticEntityClient(http, candidate.Generation).EntityStore<PartitionSemanticLeaf>();
            string parentId = await SemanticKeyOnOwner(candidate.Generation, nodes[1 - leafOwner], id => new SemanticReferenceParent { Id = id }, "budget-parent-");
            string leafId = await SemanticKeyOnOwner(candidate.Generation, nodes[leafOwner], id => new PartitionSemanticLeaf(id, "target"), "budget-leaf-");
            async Task SaveLeaf(string name)
            {
                var write = leaves.CaptureSave(new(leafId, name), SemanticEntityWriteOperation.Create());
                await candidate.DeliverAsync(write.OperationId, write.Export());
            }
            await SaveLeaf("target");
            var parent = parents.CaptureSave(new()
            {
                Id = parentId,
                Name = "selected",
                Peer = new(leafId, "target")
            }, SemanticEntityWriteOperation.Create());
            await candidate.DeliverAsync(parent.OperationId, parent.Export());
            var root = parents.Manifests.Single(m => m.EntityType.Value == "semantic-reference-parent");
            var target = leaves.Manifests[0];
            string Binding(EntityForge.Db.Mapping.EntityMappingManifest manifest, string member) =>
                manifest.ProfileBindings.Single(b => b.MemberIdentity == manifest.Members.Single(m => m.ClrName == member).Identity).StoredBinding;
            string name = Binding(root, "Name"), peer = Binding(root, "Peer"), targetName = Binding(target, "Name");
            var query = new PortableEntityQueryRequest(root.ProfileTypeBinding,
                new(root.ProfileTypeBinding, Relationships: [new(peer, "peer", new(target.ProfileTypeBinding, Fields: [new(targetName, "name")]))]),
                Filter: new("propertyComparison", name, "equal", new(JsonSerializer.SerializeToElement("selected"))), ConceptMatchMode: "direct");
            var initial = await nodes[leafOwner].CaptureQueryRelationAsync(nodes[leafOwner].Kernel.CurrentVersion,
                new("query-exchange", QueryExchange: new("entity", EntityQuery: query with
                {
                    RootConcept = target.ProfileTypeBinding,
                    Projection = new(target.ProfileTypeBinding),
                    Filter = new("propertyComparison", targetName, "equal", new(JsonSerializer.SerializeToElement("target")))
                }, SemanticFrontier: new(0))), default);
            var identity = initial.Fields!.First().Subject;
            // Size the target expansion round, which still carries target fields.
            var selection = new PartitionQueryExchangeRequest("entity", EntityQuery: query, SemanticFrontier: new(2, [identity]));
            string payload = "target";
            const int margin = 2, sizingAttempts = 4;
            bool nearLimit = false;
            for (int attempt = 0; attempt < sizingAttempts; attempt++)
            {
                var relation = await nodes[leafOwner].CaptureQueryRelationAsync(nodes[leafOwner].Kernel.CurrentVersion,
                    new("query-exchange", QueryExchange: selection), default);
                var page = new PartitionQueryExchangePage(relation, null);
                long canonical = PartitionProjectionValue.Measure(page, PartitionParticipantWire.MaximumResponseBytes, PartitionQueryJson.Options);
                long dom = PartitionProjectionValue.Measure(new PartitionProjectionValue(page, PartitionQueryJson.Options).Element, PartitionParticipantWire.MaximumResponseBytes);
                if (canonical <= PartitionQueryLimits.ExchangePageBytes && dom > PartitionQueryLimits.ExchangePageBytes)
                {
                    Console.WriteLine($"Owner={leafOwner}, canonical={canonical}, DOM={dom}");
                    nearLimit = true;
                    break;
                }
                payload = new string('x', checked(payload.Length + (int)(PartitionQueryLimits.ExchangePageBytes - margin - canonical)));
                await SaveLeaf(payload);
            }
            Assert.IsTrue(nearLimit, "The fixture must fit the canonical page budget and exceed the old DOM remeasurement.");
            var result = await candidate.QueryAsync(new(candidate.Generation, candidate.Profile, "entity", EntityQuery: query), default);
            Assert.AreEqual("semantic-identity-frontier", result.Statistics!.Strategy);
            Assert.AreEqual(1, result.Value.GetProperty("entities").GetArrayLength());
            Assert.IsTrue(result.Value.GetRawText().Contains(payload, StringComparison.Ordinal), "The complete requested value must survive exchange.");
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Semantic_frontier_matches_global_relation_for_cross_owner_scalar_and_relationship_reads(bool commands)
    {
        await WithProfileFragments(DatabaseProfileId.Semantic, async (control, candidate, _, _, http, nodes, _) =>
        {
            var parents = new HttpSemanticEntityClient(http, candidate.Generation).EntityStore<SemanticReferenceParent>();
            var leaves = new HttpSemanticEntityClient(http, candidate.Generation).EntityStore<PartitionSemanticLeaf>();
            string parentId = await SemanticKeyOnOwner(candidate.Generation, nodes[0], id => new SemanticReferenceParent { Id = id }, "frontier-parent-");
            string leafId = await SemanticKeyOnOwner(candidate.Generation, nodes[1], id => new PartitionSemanticLeaf(id, "target"), "frontier-leaf-");
            var leaf = leaves.CaptureSave(new(leafId, "target"), SemanticEntityWriteOperation.Create());
            await candidate.DeliverAsync(leaf.OperationId, leaf.Export());
            var parent = parents.CaptureSave(new()
            {
                Id = parentId,
                Name = "selected",
                Peer = new(leafId, "target")
            }, SemanticEntityWriteOperation.Create());
            await candidate.DeliverAsync(parent.OperationId, parent.Export());
            for (int i = 0; i < PartitionQueryLimits.ExchangePageFacts + 1; i++)
            {
                var other = parents.CaptureSave(new()
                {
                    Id = "unrelated-" + i,
                    Name = "other",
                    Peer = i == 0 ? new(leafId, "target") : null
                }, SemanticEntityWriteOperation.Create());
                await candidate.DeliverAsync(other.OperationId, other.Export());
            }
            var root = parents.Manifests.Single(m => m.EntityType.Value == "semantic-reference-parent");
            var target = leaves.Manifests[0];
            string Binding(EntityForge.Db.Mapping.EntityMappingManifest manifest, string member) =>
                manifest.ProfileBindings.Single(b => b.MemberIdentity == manifest.Members.Single(m => m.ClrName == member).Identity).StoredBinding;
            string name = Binding(root, "Name"), peer = Binding(root, "Peer"), targetName = Binding(target, "Name");
            var scalar = new PortableEntityQueryRequest(root.ProfileTypeBinding, new(root.ProfileTypeBinding, Fields: [new(name, "name")]),
                Filter: new("propertyComparison", name, "equal", new(JsonSerializer.SerializeToElement("selected"))), IncludeEvidence: true, ConceptMatchMode: "direct");
            var outgoing = scalar with
            {
                Projection = new(root.ProfileTypeBinding, Relationships: [new(peer, "peer", new(target.ProfileTypeBinding, Fields: [new(targetName, "name")]))])
            };
            var incoming = scalar with
            {
                RootConcept = target.ProfileTypeBinding,
                Filter = new("propertyComparison", targetName, "equal", new(JsonSerializer.SerializeToElement("target"))),
                Projection = new(target.ProfileTypeBinding, Relationships: [new(peer, "parents", scalar.Projection, "incoming")])
            };
            var nested = outgoing with
            {
                Projection = new(root.ProfileTypeBinding, Relationships: [new(peer, "peer",
                new(target.ProfileTypeBinding, Relationships: [new(peer, "parents", scalar.Projection, "incoming")]))])
            };
            PortableEntityQueryRequest[] queries = [scalar,
                outgoing,
                incoming,
                nested,
                outgoing with { First = 1 },
                scalar with { Filter = new("propertyComparison", name, "equal", new(JsonSerializer.SerializeToElement("other"))), First = 5 },
                outgoing with { TemporalScopes = [new("validTime", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1))] },
                outgoing with { AssertionPolicy = new(AllowedProviders: ["urn:absent"]) },
                scalar with { Filter = new("propertyComparison", name, "equal", new(JsonSerializer.SerializeToElement("absent"))) }];
            foreach (var query in queries)
            {
                var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "entity", EntityQuery: query);
                var expected = await control.QueryAsync(request, default);
                var actual = await candidate.QueryAsync(request, default);
                Assert.AreEqual(Canonical(expected.Value), Canonical(actual.Value));
                Assert.AreEqual("semantic-identity-frontier", actual.Statistics!.Strategy);
            }
            var traversal = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "entity", EntityQuery: outgoing);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await candidate.QueryAsync(traversal, cancelled.Token));
            // The native limit counts expanded relationship items, not scalar
            // roots. This leaf has two incoming parents and must reject a limit of one.
            await Assert.ThrowsAsync<Exception>(async () => await candidate.QueryAsync(traversal with
            {
                EntityQuery = incoming with
                {
                    MaximumExpandedEntities = 1
                }
            }, default));
            Assert.AreEqual(1, (await candidate.QueryAsync(traversal, default)).Value.GetProperty("entities").GetArrayLength());
        }, commands: commands);
    }

    private sealed class FrontierFallbackEvidence(SemanticEvidenceSlice slice) : ISemanticEntityEvidenceAccessPath
    {
        internal int Reads;

        public ValueTask<IReadOnlyList<SemanticEntitySupport>> ReadEntitySupportAsync(IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<SemanticEvidenceSlice> ReadEntityEvidenceAsync(IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return ValueTask.FromResult(slice);
        }
    }
}
