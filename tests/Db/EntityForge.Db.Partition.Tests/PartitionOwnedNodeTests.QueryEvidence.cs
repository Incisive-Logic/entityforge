using System.Text.Json;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow("UNWIND [1] AS i CALL (i) { CREATE (a:Volatile {uuid:randomUUID()})-[r:link {number:rand()}]->(b:Volatile {uuid:randomUUID()}) RETURN a.uuid AS first,b.uuid AS second,r.number AS number } RETURN first,second,number,rand() AS nonce")]
    [DataRow("CREATE (a:Volatile {uuid:randomUUID()}) WITH a CREATE (a)-[r:link {number:rand()}]->(b:Volatile {uuid:randomUUID()}) RETURN a.uuid AS first,b.uuid AS second,r.number AS number,rand() AS nonce")]
    [DataRow("CREATE (a:Volatile {uuid:randomUUID()}) WITH a CREATE (a)-[r:link {number:rand()}]->(b:Volatile {uuid:randomUUID()})")]
    [DataRow("MERGE (a:Volatile) ON CREATE SET a.uuid=randomUUID() WITH a MERGE (a)-[r:link]->(b:Volatile) ON CREATE SET b.uuid=randomUUID(),r.number=rand() WITH a SET a:Recorded")]
    [DataRow("CREATE (a:Volatile)-[r:link]->(b:Volatile) WITH a,r,b SET a.uuid=randomUUID(),b.uuid=randomUUID(),r.number=rand() WITH a,r,b DELETE r")]
    [DataRow("UNWIND [1] AS i MERGE (a:Volatile {key:i}) ON CREATE SET a.uuid=randomUUID(),a.number=rand()")]
    [DataRow("UNWIND [1] AS i MERGE (a:Volatile)-[r:link]->(b:Volatile) ON CREATE SET a.uuid=randomUUID(),b.uuid=randomUUID(),r.number=rand()")]
    [DataRow("UNWIND [1] AS i MERGE (a:Volatile)-[r:link]->(b:Volatile)-[:tail]->(c:Volatile) ON CREATE SET a.uuid=randomUUID(),b.uuid=randomUUID(),r.number=rand()")]
    [DataRow("UNWIND [1] AS i MERGE (a:Volatile)-[r:link]->(b:Volatile) SET a.uuid=randomUUID(),b.uuid=randomUUID(),r.number=rand()")]
    [DataRow("MERGE (a:Volatile)-[r:link]->(b:Volatile) ON CREATE SET a.uuid=randomUUID(),b.uuid=randomUUID(),r.number=rand()")]
    [DataRow("UNWIND [1] AS i CREATE (a:Volatile {uuid:randomUUID()})-[r:link {number:rand()}]->(b:Volatile {uuid:randomUUID()})")]
    [DataRow("UNWIND [1] AS i CREATE (a:Volatile)-[r:link]->(b:Volatile) SET a.uuid=randomUUID(),b.uuid=randomUUID(),r.number=rand()")]
    public async Task Gql_materialization_rejects_vendor_extensions(string statement)
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        Guid generation = Guid.NewGuid();
        var owners = routing.Shards.Select(shard => new PartitionExecutionDomain(generation, shard, routing.Fingerprint, DatabaseProfileId.PropertyGraph, Guid.NewGuid())).ToArray();
        var request = new PartitionQueryWrite(generation, DatabaseProfileId.PropertyGraph, EntityOperationIds.New(), "gql", statement);
        var captured = new PartitionQueryUpdate.Evidence(new(owners.Select(owner => new DatabasePartitionPosition(owner.Identity, new(0))).ToArray()),
            owners.Select(owner => new PartitionQueryUpdate.Fact(owner.Identity, JsonSerializer.SerializeToUtf8Bytes(
                new PartitionQueryRelation(Vertices: [], Edges: []), PartitionQueryJson.Options))).ToArray());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => PartitionQueryUpdate.MaterializeAsync(request, captured, routing, default).AsTask());
    }

    [TestMethod]
    [DataRow("FOR i IN [1] CALL (i) { INSERT (a:Captured {stamp:CURRENT_TIMESTAMP})-[r:link {stamp:CURRENT_TIMESTAMP}]->(b:Captured {stamp:CURRENT_TIMESTAMP}) RETURN a,r,b } RETURN a,r,b")]
    [DataRow("INSERT (a:Captured {stamp:CURRENT_TIMESTAMP}) RETURN a NEXT INSERT (a)-[r:link {stamp:CURRENT_TIMESTAMP}]->(b:Captured {stamp:CURRENT_TIMESTAMP}) RETURN a,r,b")]
    [DataRow("INSERT (a:Captured {stamp:CURRENT_TIMESTAMP}) RETURN a NEXT INSERT (a)-[r:link {stamp:CURRENT_TIMESTAMP}]->(b:Captured {stamp:CURRENT_TIMESTAMP})")]
    [DataRow("INSERT (a:Captured)-[r:link]->(b:Captured) SET a.stamp=CURRENT_TIMESTAMP,b.stamp=CURRENT_TIMESTAMP,r.stamp=CURRENT_TIMESTAMP")]
    [DataRow("FOR i IN [1] INSERT (a:Captured {stamp:CURRENT_TIMESTAMP})-[r:link {stamp:CURRENT_TIMESTAMP}]->(b:Captured {stamp:CURRENT_TIMESTAMP})")]
    public async Task Gql_materialized_update_preserves_captured_clocks_across_owners_and_recompilation(string statement)
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        Guid generation = Guid.NewGuid();
        var owners = routing.Shards.Select(shard => new PartitionExecutionDomain(generation, shard, routing.Fingerprint, DatabaseProfileId.PropertyGraph, Guid.NewGuid())).ToArray();
        Guid operation = EntityOperationIds.New();
        var request = new PartitionQueryWrite(generation, DatabaseProfileId.PropertyGraph, operation, "gql", statement);
        var captured = new PartitionQueryUpdate.Evidence(new(owners.Select(owner => new DatabasePartitionPosition(owner.Identity, new(0))).ToArray()),
            owners.Select(owner => new PartitionQueryUpdate.Fact(owner.Identity, JsonSerializer.SerializeToUtf8Bytes(
                new PartitionQueryRelation(Vertices: [], Edges: []), PartitionQueryJson.Options))).ToArray());
        var evidence = await PartitionQueryUpdate.MaterializeAsync(request, captured, routing, default);
        Assert.IsNotNull(evidence.GraphPlan);
        var capturedPlan = GraphTransactionCodec.Decode(operation, evidence.GraphPlan);
        Assert.AreEqual(3, capturedPlan.Mutations.Count(mutation => mutation is SetVertexProperty or SetEdgeProperty));
        var expected = GraphTransactionCodec.Decode(operation, evidence.GraphPlan).Mutations
            .Where(mutation => mutation is SetVertexProperty or SetEdgeProperty).ToArray();
        Assert.AreEqual(3, expected.Length);
        var application = new PartitionApplicationBinding(operation, owners.Select(owner => owner.Identity).ToArray(), [PartitionQueryUpdate.Encode(request)],
            planningEvidence: JsonSerializer.SerializeToUtf8Bytes(evidence, PartitionParticipantWire.Json));
        var combined = new List<GraphMutation>();
        foreach (var owner in owners)
        {
            var first = await PartitionQueryUpdate.CompileAsync(application, owner, routing, default);
            var replay = await PartitionQueryUpdate.CompileAsync(application, owner, routing, default);
            CollectionAssert.AreEqual(first.Payload.ToArray(), replay.Payload.ToArray());
            combined.AddRange(GraphTransactionCodec.Decode(operation, first.Payload.Span).Mutations
                .Where(mutation => mutation is SetVertexProperty or SetEdgeProperty));
        }
        CollectionAssert.AreEquivalent(expected, combined.ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Rdf_materialized_update_preserves_volatile_values_across_owners_and_recompilation(bool data)
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var generation = Guid.NewGuid();
        var owners = routing.Shards.Select(s => new PartitionExecutionDomain(generation, s, routing.Fingerprint, DatabaseProfileId.Rdf, Guid.NewGuid())).ToArray();
        var operation = EntityOperationIds.New();
        var existing = new RdfQuad(new RdfBlankNode(operation.ToString("N") + ":b"), new("urn:keep"), new RdfLiteral("native"));
        var request = new PartitionQueryWrite(generation, DatabaseProfileId.Rdf, operation, "sparql", data
            ? "INSERT DATA { <urn:fixed> <urn:nonce> 1 . _:b <urn:link> <urn:fixed> }"
            : "INSERT { ?id <urn:nonce> ?value . _:b <urn:link> ?id } WHERE { BIND(UUID() AS ?id) BIND(RAND() AS ?value) }");
        var captured = new PartitionQueryUpdate.Evidence(new(owners.Select(o => new DatabasePartitionPosition(o.Identity, new(0))).ToArray()),
            owners.Select(o => new PartitionQueryUpdate.Fact(o.Identity, JsonSerializer.SerializeToUtf8Bytes(
                new PartitionQueryRelation(Quads: new[] { existing }.Where(quad =>
                    routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(quad.Subject)) == o.Partition).ToArray()),
                PartitionQueryJson.Options))).ToArray());
        var evidence = await PartitionQueryUpdate.MaterializeAsync(request, captured, routing, default);
        Assert.AreEqual(2, evidence.RdfMutations!.Length);
        var application = new PartitionApplicationBinding(operation, owners.Select(o => o.Identity).ToArray(), [PartitionQueryUpdate.Encode(request)],
            planningEvidence: JsonSerializer.SerializeToUtf8Bytes(evidence, PartitionParticipantWire.Json));
        var combined = new List<RdfMutation>();
        foreach (var owner in owners)
        {
            var first = await PartitionQueryUpdate.CompileAsync(application, owner, routing, default);
            var replay = await PartitionQueryUpdate.CompileAsync(application, owner, routing, default);
            CollectionAssert.AreEqual(first.Payload.ToArray(), replay.Payload.ToArray());
            combined.AddRange(RdfTransactionCodec.DecodeTransaction(operation, first.Payload.Span).Mutations);
        }
        CollectionAssert.AreEquivalent(evidence.RdfMutations, combined.ToArray());
        var nonce = combined.Single(mutation => mutation.Quad.Predicate.Value == "urn:nonce").Quad;
        var link = combined.Single(mutation => mutation.Quad.Predicate.Value == "urn:link").Quad;
        Assert.AreEqual(nonce.Subject, link.Object);
        Assert.IsInstanceOfType<RdfBlankNode>(link.Subject);
        Assert.AreNotEqual(existing.Subject, link.Subject);
    }

    [TestMethod]
    [DataRow("DELETE WHERE { GRAPH <urn:archive> { ?s <urn:value> ?o } }")]
    [DataRow("CLEAR NAMED")]
    public async Task Rdf_delete_where_compiles_owner_retractions_with_snapshot_protection(string statement)
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var generation = Guid.NewGuid();
        var owners = routing.Shards.Select(s => new PartitionExecutionDomain(generation, s, routing.Fingerprint, DatabaseProfileId.Rdf, Guid.NewGuid())).ToArray();
        var quads = Enumerable.Range(0, 16).Select(i => new RdfQuad(new RdfIri($"urn:delete:{i}"),
            new("urn:value"), new RdfLiteral(i.ToString()), new RdfIri("urn:archive"))).ToArray();
        var operation = EntityOperationIds.New();
        var request = new PartitionQueryWrite(generation, DatabaseProfileId.Rdf, operation, "sparql",
            statement);
        var evidence = new PartitionQueryUpdate.Evidence(new(owners.Select(o => new DatabasePartitionPosition(o.Identity, new(1))).ToArray()),
            owners.Select(o => new PartitionQueryUpdate.Fact(o.Identity, JsonSerializer.SerializeToUtf8Bytes(new PartitionQueryRelation(Quads:
                quads.Where(q => routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(q.Subject)) == o.Partition).ToArray()), PartitionQueryJson.Options))).ToArray());
        var application = new PartitionApplicationBinding(operation, owners.Select(o => o.Identity).ToArray(), [PartitionQueryUpdate.Encode(request)],
            planningEvidence: JsonSerializer.SerializeToUtf8Bytes(await PartitionQueryUpdate.MaterializeAsync(request, evidence, routing, default), PartitionParticipantWire.Json));
        var deleted = new List<RdfQuad>();
        foreach (var owner in owners)
        {
            var member = await PartitionQueryUpdate.CompileAsync(application, owner, routing, default);
            var transaction = RdfTransactionCodec.DecodeTransaction(operation, member.Payload.Span);
            Assert.AreEqual(new DatabaseVersion(1), transaction.ReadVersion);
            foreach (var mutation in transaction.Mutations)
            {
                Assert.IsInstanceOfType<RetractRdfQuad>(mutation);
                Assert.AreEqual(owner.Partition, routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(mutation.Quad.Subject)));
                deleted.Add(mutation.Quad);
            }
        }
        CollectionAssert.AreEquivalent(quads, deleted.ToArray());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Query_evidence_projection_preserves_exact_plan_candidates_and_capacity(bool absent)
    {
        const int entityCount = 64;
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var generation = Guid.NewGuid();
        var owners = routing.Shards.Select(s => new PartitionExecutionDomain(generation, s, routing.Fingerprint, DatabaseProfileId.Rdf, Guid.NewGuid())).ToArray();
        var quads = Enumerable.Range(0, entityCount).SelectMany(i => new RdfQuad[]
        {
            new(new RdfIri($"urn:row:{i}"), new("urn:number"), new RdfLiteral(i < 2 ? "target" : i.ToString())),
            new(new RdfIri($"urn:row:{i}"), new("urn:payload"), new RdfLiteral(new string('x', 128))),
        }).Append(new RdfQuad(new RdfIri("urn:row:0"), new("urn:value"), new RdfLiteral("old"))).ToArray();
        var operation = EntityOperationIds.New();
        var request = new PartitionQueryWrite(generation, DatabaseProfileId.Rdf, operation, "sparql",
            $"DELETE {{ ?s <urn:value> ?old }} INSERT {{ ?s <urn:value> \"after\" }} WHERE {{ ?s <urn:number> \"{(absent ? "missing" : "target")}\" . ?s <urn:value> ?old }}");
        var evidence = new PartitionQueryUpdate.Evidence(new(owners.Select(o => new DatabasePartitionPosition(o.Identity, new(1))).ToArray()),
            owners.Select(o => new PartitionQueryUpdate.Fact(o.Identity, JsonSerializer.SerializeToUtf8Bytes(new PartitionQueryRelation(Quads:
                quads.Where(q => routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(q.Subject)) == o.Partition).ToArray()), PartitionQueryJson.Options))).ToArray());
        var original = new PartitionApplicationBinding(operation, owners.Select(o => o.Identity).ToArray(), [PartitionQueryUpdate.Encode(request)],
            planningEvidence: JsonSerializer.SerializeToUtf8Bytes(await PartitionQueryUpdate.MaterializeAsync(request, evidence, routing, default), PartitionParticipantWire.Json));
        var members = new List<PartitionTransactionMember>();
        foreach (var owner in owners)
        {
            members.Add(await PartitionQueryUpdate.CompileAsync(original, owner, routing, default));
        }

        var narrowed = PartitionQueryUpdateEvidence.Narrow(original, routing, members);
        foreach (var member in members)
        {
            CollectionAssert.AreEqual(member.Payload.ToArray(), (await PartitionQueryUpdate.CompileAsync(narrowed, member.Domain, routing, default)).Payload.ToArray());
            var read = (RdfQueryStateRead)RdfTransactionCodec.DecodeTransaction(operation, member.Payload.Span).ReadPredicates!.Single();
            var local = quads.Where(q => routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(q.Subject)) == member.Domain.Partition).ToArray();
            Assert.AreEqual(read.ExpectedFingerprint, read.Fingerprint(local));
            // A candidate rejected by the later WHERE clause must remain protected.
            if (!absent && read.Subjects.Contains(new RdfIri("urn:row:1")))
            {
                Assert.AreNotEqual(read.ExpectedFingerprint, read.Fingerprint(local.Append(new(new RdfIri("urn:row:1"), new("urn:value"), new RdfLiteral("new")))));
            }
        }
        int before = PartitionSelectionCodec.EncodeApplication(original).Length;
        int after = PartitionSelectionCodec.EncodeApplication(narrowed).Length;
        Assert.IsTrue(after * 8 < before, $"Evidence bytes {before} -> {after}");
        // More than a full 15-second retention/maintenance interval at 50 updates/s.
        new PartitionApplicationUsage(1000, 1000L * after).RequireAvailable();
        Console.WriteLine($"Evidence bytes {before} -> {after}; 1000 retained selections = {1000L * after}");

        // Unsupported dependency shapes remain byte-for-byte unchanged.
        var fallback = members.Select(m => new PartitionTransactionMember(m.Domain, m.ReadVersion,
            RdfTransactionCodec.Encode(new RdfTransaction(operation, [], ReadVersion: m.ReadVersion)))).ToArray();
        Assert.AreSame(original, PartitionQueryUpdateEvidence.Narrow(original, routing, fallback));
    }
}
