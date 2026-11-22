using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;
using EntityForge.Db.Durability;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Query_dependency_reservations_survive_transfer_checkpoint_and_reject_phantoms(DatabaseProfileId profile)
    {
        var authority = new Authority();
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var secondAuthority = new Authority
        {
            Lease = authority.Lease with
            {
                Placement = authority.Lease.Placement with
                {
                    Claim = authority.Lease.Placement.Claim with
                    {
                        Shard = new("b"),
                        Owner = new("second")
                    }
                }
            }
        };
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile,
            authority.Lease, routing, authority, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), profile,
            secondAuthority.Lease, routing, secondAuthority, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        GraphVertexId Vertex()
        {
            GraphVertexId id;
            do
            {
                id = GraphVertexId.New();
            }
            while (routing.RouteGraphVertex(id.Value).Value != "a");
            return id;
        }
        RdfIri Subject()
        {
            RdfIri id;
            do
            {
                id = new("urn:" + Guid.NewGuid());
            }
            while (routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(id)).Value != "a");
            return id;
        }
        var vertex = Vertex();
        var other = Vertex();
        var subject = Subject();
        var another = Subject();
        var number = new RdfIri("urn:number");
        RdfQuad[] quads = [new(subject, number, new RdfLiteral("1"))];
        var seed = EntityOperationIds.New();
        await node.Kernel.CommitWithReceiptAsync(seed, profile == DatabaseProfileId.PropertyGraph
            ? GraphTransactionCodec.Encode(new(seed, [new CreateVertex(vertex, ["Item"]), new SetVertexProperty(vertex, "number", new DatabaseValue.Int64(1))]))
            : RdfTransactionCodec.Encode(new(seed, quads.Select(q => (RdfMutation)new AssertRdfQuad(q)).ToArray())));
        var operation = EntityOperationIds.New();
        var version = node.Kernel.CurrentVersion;
        var request = new PartitionQueryWrite(node.Domain.Generation, profile, operation, profile == DatabaseProfileId.PropertyGraph ? "gql" : "sparql",
            profile == DatabaseProfileId.PropertyGraph ? "MATCH (n:Item) WHERE n.`number` = 1 SET n.`value` = \"after\""
                : "INSERT { ?s <urn:value> \"after\" } WHERE { ?s <urn:number> \"1\" }");
        var evidence = new PartitionQueryUpdate.Evidence(new([new(node.Domain.Identity, version), new(second.Domain.Identity, second.Kernel.CurrentVersion)]),
            [new(node.Domain.Identity, await node.ReadFrozenAsync(version, new("query-relation"), default)),
                new(second.Domain.Identity, await second.ReadFrozenAsync(second.Kernel.CurrentVersion, new("query-relation"), default))]);
        var application = new PartitionApplicationBinding(operation, [node.Domain.Identity, second.Domain.Identity], [PartitionQueryUpdate.Encode(request)],
            planningEvidence: System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(await PartitionQueryUpdate.MaterializeAsync(request, evidence, routing, default), PartitionParticipantWire.Json));
        var plan = new PartitionTransactionPlan(operation, routing.MapVersion,
            [await PartitionQueryUpdate.CompileAsync(application, node.Domain, routing, default), await PartitionQueryUpdate.CompileAsync(application, second.Domain, routing, default)]);
        application = PartitionQueryUpdateEvidence.Narrow(application, routing, plan.Members);
        await decisions.BindApplicationAsync(application);
        await decisions.BindAsync(plan);
        await node.PrepareNativeAsync(plan);
        await second.PrepareNativeAsync(plan);
        // Transfer checkpoint preserves pending reservations and their exact bytes.
        var image = await node.SealAndExportAsync(new(Guid.NewGuid()), Path.Combine(_root, "image"));
        await node.DisposeAsync();
        await using var transfer = new PartitionOwnedTransfer(Path.Combine(_root, "image"), Path.Combine(_root, "destination"), image,
            routing, authority, new("destination"), new("https://localhost:22001"), Options, decisions, timestampClock: ReadClock);
        var reopened = await transfer.CompleteAsync();
        var phantom = EntityOperationIds.New();
        byte[] phantomPayload = profile == DatabaseProfileId.PropertyGraph
            ? GraphTransactionCodec.Encode(new(phantom, [new CreateVertex(other, ["Item"]), new SetVertexProperty(other, "number", new DatabaseValue.Int64(1))]))
            : RdfTransactionCodec.Encode(new(phantom, [new AssertRdfQuad(new(another, number, new RdfLiteral("1")))]));
        await Assert.ThrowsExactlyAsync<DatabasePartitionConflictException>(() => reopened.Kernel.CommitWithReceiptAsync(phantom, phantomPayload).AsTask());
        await SelectTimestampDecisionAsync(decisions, plan, reopened, second);
        var result = await new PartitionTransactionCoordinator(decisions).ExecuteSelectedAsync(plan, [reopened, second]);
        Assert.IsTrue(result.Decision.Commit);
        Assert.AreEqual(DurablePartitionTransactionState.Committed, (await reopened.Kernel.PartitionTransactions.ReadAsync()).Single(p => p.Operation == operation).State);
        await reopened.Kernel.CommitWithReceiptAsync(phantom, phantomPayload);
        Assert.AreEqual(version.Value + 2, reopened.Kernel.CurrentVersion.Value);
    }

    [TestMethod]
    public void Query_fingerprints_cover_selector_membership_absence_labels_and_complete_candidate_state()
    {
        var a = GraphVertexId.New();
        var b = GraphVertexId.New();
        GraphVertex Vertex(GraphVertexId id, long number, string value, string label = "Item") => new(id, new HashSet<string> { label },
            new Dictionary<string, DatabaseValue> { ["number"] = new DatabaseValue.Int64(number), ["value"] = new DatabaseValue.String(value) });
        GraphVertex[] graph = [Vertex(a, 1, "a"), Vertex(b, 2, "b")];
        var read = GraphQueryStateRead.Capture(["number"], [a], graph);
        Assert.AreEqual(read.ExpectedFingerprint, read.Fingerprint([graph[0], Vertex(b, 2, "unrelated")]));
        Assert.AreNotEqual(read.ExpectedFingerprint, read.Fingerprint([graph[0], Vertex(b, 1, "b")]));
        Assert.AreNotEqual(read.ExpectedFingerprint, read.Fingerprint([Vertex(a, 1, "changed"), graph[1]]));
        Assert.AreNotEqual(read.ExpectedFingerprint, read.Fingerprint([graph[0], Vertex(b, 2, "b", "Changed")]));
        Assert.AreNotEqual(read.ExpectedFingerprint, read.Fingerprint([graph[0]]));
        var noMatch = GraphQueryStateRead.Capture(["number"], [], graph);
        Assert.AreNotEqual(noMatch.ExpectedFingerprint, noMatch.Fingerprint([graph[0], Vertex(b, 99, "b")]));
        Guid operation = EntityOperationIds.New();
        byte[] bytes = GraphTransactionCodec.Encode(new(operation, [], new(1), [read]));
        CollectionAssert.AreEqual(bytes, GraphTransactionCodec.Encode(GraphTransactionCodec.Decode(operation, bytes)));
        var predicate = new RdfIri("urn:number");
        var subject = new RdfIri("urn:a");
        var other = new RdfIri("urn:b");
        RdfQuad[] rdf = [new(subject, predicate, new RdfLiteral("1")), new(other, predicate, new RdfLiteral("2"))];
        var rdfRead = RdfQueryStateRead.Capture(predicate, new RdfLiteral("1"), rdf);
        Assert.AreEqual(rdfRead.ExpectedFingerprint, rdfRead.Fingerprint(rdf.Append(new(other, new("urn:value"), new RdfLiteral("unrelated")))));
        // A candidate missing a second WHERE property can become a match.
        Assert.AreNotEqual(rdfRead.ExpectedFingerprint, rdfRead.Fingerprint(rdf.Append(new(subject, new("urn:required"), new RdfLiteral("present")))));
        Assert.AreNotEqual(rdfRead.ExpectedFingerprint, rdfRead.Fingerprint(rdf.Append(new(other, predicate, new RdfLiteral("1")))));
        Assert.AreNotEqual(rdfRead.ExpectedFingerprint, rdfRead.Fingerprint(rdf.Skip(1)));
        bytes = RdfTransactionCodec.Encode(new(operation, [], ReadVersion: new(1), ReadPredicates: [rdfRead]));
        CollectionAssert.AreEqual(bytes, RdfTransactionCodec.Encode(RdfTransactionCodec.DecodeTransaction(operation, bytes)));
        Assert.IsNull(RdfSparqlUpdate.QueryUpdateAnchor("INSERT { ?s <urn:v> ?o } WHERE { ?s <urn:n> \"1\" . ?x <urn:other> ?o }"));
        Assert.IsNull(RdfSparqlUpdate.QueryUpdateAnchor("INSERT { ?s <urn:v> ?o } WHERE { ?s <urn:n> \"1\" OPTIONAL { ?s <urn:other> ?o } }"));
    }

    [TestMethod]
    public void Rdf_previous_native_transaction_version_keeps_subject_read_evidence()
    {
        var operation = Guid.NewGuid();
        var subject = new RdfIri("urn:legacy");
        var read = RdfSubjectStateRead.Capture(subject, null, true, []);
        // Explicit v5 framing, independently constructed (v6 adds predicate tags).
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("EFRD0005"u8);
        writer.Write(operation.ToByteArray());
        writer.Write(false);
        writer.Write(false);
        writer.Write(true);
        writer.Write(1L);
        writer.Write(false);
        writer.Write(0);
        writer.Write(true);
        writer.Write(1);
        RdfPhysicalCodec.WriteTerm(writer, subject);
        writer.Write(false);
        writer.Write(true);
        writer.Write(read.ExpectedFingerprint);
        writer.Flush();
        var decoded = RdfTransactionCodec.DecodeTransaction(operation, stream.ToArray());
        Assert.AreEqual(read, decoded.ReadPredicates!.Single());
        Assert.AreEqual(new DatabaseVersion(1), decoded.ReadVersion);
        CollectionAssert.AreEqual(RdfTransactionCodec.Encode(decoded), RdfTransactionCodec.Encode(RdfTransactionCodec.DecodeTransaction(operation, RdfTransactionCodec.Encode(decoded))));
    }
}
