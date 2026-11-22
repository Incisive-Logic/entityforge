using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Indexed_serving_rejects_bad_minimum_without_poisoning_other_readers(bool warm, bool wrongIncarnation)
    {
        await WithQueryHome(async (node, placements, home, decisions) =>
        {
            var client = new PartitionApplicationClient(decisions, Routing, [node], ReadClock)
            {
                QueryHome = home,
                IndexedServingProperties = ["key"],
                IndexedServingDemandDriven = true
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var request = new PartitionQueryRequest(node.Domain.Generation, DatabaseProfileId.PropertyGraph, "gql", "MATCH (n:Serving) RETURN n");
            try
            {
                if (warm)
                {
                    _ = await client.QueryAsync(request, timeout.Token);
                }

                var identity = node.Domain.Identity;
                if (wrongIncarnation)
                {
                    identity = new(identity.Generation, identity.Profile, identity.Routing, identity.Partition, Guid.NewGuid());
                }

                var minimum = new DatabasePositionVector([new DatabasePartitionPosition(identity, new DatabaseVersion(node.Kernel.CurrentVersion.Value + 1))]);
                await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryAsync(request with { MinimumPosition = minimum }, timeout.Token).AsTask());
                _ = await client.QueryAsync(request, timeout.Token);
                Guid operation = Guid.NewGuid();
                var vertex = new GraphVertexId(Guid.NewGuid());
                await node.Authority.ExecuteAsync(async () =>
                {
                    await node.Kernel.CommitAsync(operation, GraphTransactionCodec.Encode(new(operation, [new CreateVertex(vertex, ["Serving"])])));
                    return true;
                });
                var valid = new DatabasePositionVector([new(node.Domain.Identity, node.Kernel.CurrentVersion)]);
                var answer = await client.QueryAsync(request with
                {
                    MinimumPosition = valid
                }, timeout.Token);
                Assert.IsTrue(answer.Position.Covers(valid));
                Assert.AreEqual(1, answer.Value.GetProperty("matches").GetArrayLength());
            }
            finally
            {
                await client.CloseReadEpochAsync();
            }
        });
    }

    [DataTestMethod]
    [DataRow("omitted")]
    [DataRow("restart")]
    [DataRow("progress")]
    public async Task Indexed_serving_rejects_incomplete_or_reassociated_progress(string fault)
    {
        await WithQueryHome(async (node, placements, home, decisions) =>
        {
            var cut = await node.AcquireHistoryProtectionAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
            int pulls = 0;
            await using var serving = new PartitionIndexedServing(Routing, ["key"], async (cursors, minimum, token) =>
            {
                var time = PartitionTimestampClock.Read(ReadClock.Domain, ReadClock);
                var request = new PartitionServingRequest(Guid.NewGuid(), cursors.GetValueOrDefault("a"));
                var raw = await ((IPartitionReadParticipant)node).ReadCutValueAsync(cut, new("serving-feed", ReadTime: time, Serving: request), token);
                var source = JsonSerializer.Deserialize<PartitionServingResponse>(raw, PartitionQueryJson.Options)!;
                if (Interlocked.Increment(ref pulls) > 1)
                {
                    source = fault switch
                    {
                        "omitted" => source with
                        {
                            Cursor = source.Cursor with
                            {
                                Sequence = source.Cursor.Sequence + 1
                            }
                        },
                        "restart" => source with
                        {
                            Cursor = source.Cursor with
                            {
                                Source = Guid.NewGuid()
                            }
                        },
                        _ => source with
                        {
                            CompleteThrough = new(Guid.NewGuid(), time.Physical, time.Logical, time.ReservedReadThrough, true)
                        }
                    };
                }

                return (new[] { source }, new DatabasePositionVector([cut.Position]));
            });
            try
            {
                await serving.ReadyAsync(default);
                await Task.Delay(30);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAsync<InvalidOperationException>(() => serving.ReadAsync(PartitionTimestampClock.Read(ReadClock.Domain, ReadClock), null, timeout.Token).AsTask());
            }
            finally
            {
                await node.AbandonCutAsync(cut.Id);
            }
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Indexed_serving_preserves_remote_traversal_bags_and_rejects_corrupt_adjacency(bool referenceProjection)
    {
        await WithQueryHomePair(async (first, second, ordinary, routing, decisions) =>
        {
            var owners = new[]
            {
                first,
                second
            };
            var client = new PartitionApplicationClient(decisions, routing, owners, ReadClock)
            {
                QueryHome = ordinary.QueryHome,
                IndexedServingProperties = ["key"]
            };
            Guid Identity(Func<Guid, string> route, string owner)
            {
                Guid id;
                do
                {
                    id = Guid.NewGuid();
                } while (route(id) != owner);
                return id;
            }

            var a = new GraphVertexId(Identity(id => routing.RouteGraphVertex(id).Value, "a"));
            var b = new GraphVertexId(Identity(id => routing.RouteGraphVertex(id).Value, "b"));
            var e = new GraphEdgeId(Identity(id => routing.RouteGraphEdge(id).Value, "b"));
            var f = new GraphEdgeId(Identity(id => routing.RouteGraphEdge(id).Value, "a"));
            try
            {
                foreach (var owner in owners)
                {
                    var mutations = new List<GraphMutation>();
                    foreach (var v in new[]
                    {
                        a,
                        b
                    }.Where(v => routing.RouteGraphVertex(v.Value) == owner.Domain.Partition))
                    {
                        mutations.Add(new CreateVertex(v, ["Person"]));
                        mutations.Add(new SetVertexProperty(v, "key", new DatabaseValue.String("雪\"\\")));
                        mutations.Add(new SetVertexProperty(v, "samples", new DatabaseValue.Array([new DatabaseValue.Int64(1), new DatabaseValue.Int64(2)])));
                    }

                    foreach (var edge in new[]
                    {
                        e,
                        f
                    }

                    )
                    {
                        var role = GraphEdgeStorageRole.None;
                        if (routing.RouteGraphEdge(edge.Value) == owner.Domain.Partition)
                        {
                            role |= GraphEdgeStorageRole.Authoritative;
                        }

                        if (routing.RouteGraphVertex(a.Value) == owner.Domain.Partition)
                        {
                            role |= GraphEdgeStorageRole.OutgoingAdjacency;
                        }

                        if (routing.RouteGraphVertex(b.Value) == owner.Domain.Partition)
                        {
                            role |= GraphEdgeStorageRole.IncomingAdjacency;
                        }

                        mutations.Add(new CreateEdgeReplica(edge, a, b, "peer", role | (referenceProjection ? GraphEdgeStorageRole.ReferenceProjection : GraphEdgeStorageRole.None)));
                        if (role.HasFlag(GraphEdgeStorageRole.Authoritative))
                        {
                            mutations.Add(new SetEdgeProperty(edge, "weight", new DatabaseValue.Int64(edge == e ? 2 : 1)));
                        }
                    }

                    Guid op = Guid.NewGuid();
                    await owner.Kernel.CommitAsync(op, GraphTransactionCodec.Encode(new(op, mutations)));
                }

                string[] queries = ["MATCH (n:Person)-[e:peer]->(t:Person) WHERE e.weight > 1 RETURN e.weight AS answer ORDER BY answer DESC", "MATCH (n:Person)-[e:peer]->(t:Person) RETURN DISTINCT n.key AS answer", "MATCH (n:Person)-[e:peer]->(t:Person) WHERE LEFT(n.key,1)='雪' RETURN NORMALIZE(t.key,NFC) AS answer", "MATCH (n:Person) OPTIONAL MATCH (n)-[e:peer]->(t:Person) WHERE LEFT(t.key,7)='missing' RETURN COALESCE(t.key,'none') AS answer", "MATCH (n:Person) OPTIONAL MATCH (n)-[e:peer]->(t:Person) WHERE e.weight>1 RETURN COALESCE(e.weight,0) AS answer ORDER BY answer", "MATCH (n:Person)-[e:peer]->(t:Person) RETURN e.weight AS answer OFFSET 1 LIMIT 0", "MATCH (n:Person {key: '雪\"\\\\'}) RETURN n", "MATCH (n:Person)-[e:peer]->(t:Person) RETURN CHAR_LENGTH(t.key)+1 AS length ORDER BY length DESC LIMIT 1", "MATCH (n:Person)-[e:peer]->(t:Person) WHERE CHAR_LENGTH(n.key)>0 AND COALESCE(t.missing,t.key)=n.key RETURN UPPER(n.key) AS answer", "MATCH (n:Person)-[e:peer]->(t:Person) RETURN COALESCE(n.missing, UPPER(n.key)) AS text, CHAR_LENGTH(t.key)+1 AS length", "MATCH (n:Person)-[e:peer]->(t:Person) RETURN n, t", "MATCH (n:Person)-[e:peer]->(t:Person) RETURN COUNT(e) AS total", "MATCH (n:Person)<-[e:peer]-(t:Person) RETURN n, t", "MATCH (n:Person) OPTIONAL MATCH (n)-[e:missing]->(t:Person) RETURN n, t"];
                queries = [.. queries,
                    "MATCH (n:Person {samples:[1.0,2.0]}) RETURN n.samples AS answer",
                    "MATCH (n:Person) FOR item IN n.samples RETURN sum(item) AS answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) WHERE e.weight=2.0 RETURN t.key AS answer",
                    "MATCH (a:Person)-[:peer]->(b:Person),(c:Person)-[:peer]->(d:Person) RETURN b.key AS answer,d.key AS other",
                    "MATCH (a:Person) OPTIONAL MATCH (a)-[:peer]->(b:Person),(c:Person) WHERE c.key=a.key RETURN b.key AS answer,c.key AS other",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN e.weight+1e300 AS answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN sum(CAST(e.weight AS FLOAT)) AS answer",
                    "MATCH p=(n:Person)-[e:peer]->(t:Person) RETURN PATH_LENGTH(p) AS answer",
                    "MATCH p = SHORTEST 2 GROUPS (n:Person)-[:peer]->+(t:Person) RETURN PATH_LENGTH(p) AS answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) WHERE n IS LABELED Person&!Absent RETURN e IS LABELED peer AS answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) WHERE CHAR_LENGTH(n.key)>=0 RETURN POWER(e.weight,2) AS answer",
                    "MATCH p=(n:Person)-[e:peer]->(t:Person) RETURN VALUE { FOR x IN ELEMENTS(p) FILTER x IS TYPED NODE RETURN COLLECT_LIST(x.key) AS items } AS answer",
                    "OPTIONAL MATCH p=(n:Absent)-[e:peer]->(t:Person) RETURN PATH_LENGTH(p) AS answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN e.weight AS weight,t.key AS key NEXT FILTER weight>1 RETURN key AS answer ORDER BY answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN e.weight AS weight NEXT RETURN SUM(weight) AS answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN n AS p,n AS q NEXT RETURN p,q",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN e AS edgeRef NEXT RETURN edgeRef",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN DISTINCT t.key AS key NEXT RETURN key AS answer ORDER BY answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN COLLECT_LIST(e.weight) AS weights NEXT FOR weight IN weights RETURN SUM(weight) AS answer",
                    "RETURN 42 AS answer",
                    "MATCH (n:Person) CALL (n) { MATCH (n)-[e:peer]->(t:Person) RETURN COUNT(*) AS total } RETURN n.key AS answer,total AS other",
                    "CALL { RETURN 1 AS x UNION ALL RETURN 2 AS x } RETURN x AS answer",
                    "MATCH (n:Person) OPTIONAL CALL (n) { MATCH (n)-[:missing]->(t) RETURN t.key AS answer } RETURN answer",
                    "FOR x IN [1,2] CALL (x) { RETURN x+1 AS y } RETURN y AS answer",
                    "MATCH (n:Person) RETURN n NEXT MATCH (n)-[e:peer]->(t:Person) RETURN t.key AS answer",
                    "MATCH (n:Person) RETURN n NEXT OPTIONAL MATCH (n)-[e:peer]->(t:Person) WHERE e.weight>2 RETURN n.key AS answer,t.key AS other",
                    "FOR weight IN [1,2] MATCH (n:Person)-[e:peer]->(t:Person) WHERE e.weight=weight RETURN t.key AS answer",
                    "MATCH (n:Person) RETURN n NEXT MATCH (m:Person) RETURN COUNT(*) AS answer",
                    "RETURN 1 AS answer UNION ALL RETURN 1 AS answer",
                    "MATCH (n:Person) RETURN COUNT(*) AS answer UNION ALL RETURN 42 AS answer",
                    "MATCH (n:Person) RETURN n.key AS answer UNION RETURN 42 AS answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN e.weight AS answer UNION MATCH (n:Person) RETURN n.key AS answer",
                    "MATCH (n:Person) RETURN n UNION MATCH (n:Person) RETURN n",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN e IS LABELED peer AS answer ORDER BY answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN ELEMENT_ID(e) AS answer ORDER BY answer",
                    "MATCH (n:Person) RETURN RECORD {age:n.age,name:n.name} AS answer ORDER BY answer.age",
                    "MATCH (n:Person) RETURN n IS LABELED Person AS answer ORDER BY answer",
                    "RETURN VALUE { FOR x IN [1,2,3] FILTER x>1 RETURN COLLECT_LIST(x*2) AS items } AS answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN VALUE { FOR x IN [e.weight] RETURN COLLECT_LIST(x+1) AS items } AS answer ORDER BY answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) WHERE EXISTS { FOR x IN [e.weight] FILTER x>1 } RETURN t.key AS answer ORDER BY answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN CAST(e.weight AS INTEGER)+1 AS answer ORDER BY answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN SUM(e.weight)+COUNT(*) AS answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN n.key AS key,SUM(e.weight)+1 AS answer ORDER BY key",
                    "FOR x IN [3,1,2] RETURN x AS answer ORDER BY answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN n ORDER BY n.key LIMIT 1 NEXT RETURN n",
                    "MATCH (n:Person)-[e:peer]->(t:Person) LET m=RECORD {weight:e.weight,key:t.key} RETURN m.key AS key,m.weight AS weight ORDER BY key,weight",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN e.weight AS weight ORDER BY weight",
                    "MATCH (n:Person) WHERE n.key IS NOT NULL RETURN n.key AS answer ORDER BY answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN n AS p NEXT RETURN p,COUNT(*) AS total",
                    "MATCH (n:Person)-[e:peer]->+(t:Person) RETURN COUNT(*) AS total",
                    "MATCH (n:Person)-[e:peer]-*(t:Person) RETURN COUNT(*) AS total",
                    "MATCH (n:Person)-[e:peer]->(t:Person) WHERE EXISTS { FOR item IN [1,2] FILTER item=e.weight } RETURN [e.weight,t.key] AS answer ORDER BY answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN PERCENTILE_CONT(e.weight,0.25) AS answer, STDDEV_POP(e.weight) AS deviation",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN n.key AS key, COUNT(*) AS total, SUM(e.weight) AS weight, MIN(t.key) AS smallest ORDER BY key",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN MOD(e.weight,2) AS bucket, COUNT(*) AS total ORDER BY bucket",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN SUM(e.weight*2) AS answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) RETURN COUNT(DISTINCT CHAR_LENGTH(t.key)) AS answer",
                    "MATCH (n:Person)-[e:peer {weight:2}]-(t:Person) RETURN t.key AS answer",
                    "MATCH (n:Person) OPTIONAL MATCH (n)-[e:peer {weight:9}]-(t:Person) RETURN COALESCE(t.key,'none') AS answer",
                    "MATCH (n:Person)-[e:peer]-(t:Person) RETURN e.weight AS answer ORDER BY answer",
                    "MATCH (n:Person)-[e:peer]-(t:Person)-[f:peer]-(n) RETURN COUNT(*) AS answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) WHERE NULL IS NULL AND e.weight > 1 RETURN t.key AS answer",
                    "MATCH (n:Person)-[e:peer]->(t:Person) WHERE TRUE IS NULL RETURN t.key AS answer"];
                foreach (string text in queries)
                {
                    var request = new PartitionQueryRequest(first.Domain.Generation, DatabaseProfileId.PropertyGraph, "gql", text);
                    var expected = await ordinary.QueryAsync(request, default);
                    var actual = await client.QueryAsync(request, default);
                    // Scalar aggregations intentionally return execution-shaped
                    // rows, while entity returns retain their graph envelope.
                    Assert.AreEqual(Canonical(expected.Value), Canonical(actual.Value), text);
                    if (text == queries[0])
                    {
                        var matches = actual.Value.GetProperty("matches");
                        Assert.AreEqual(1, matches.GetArrayLength());
                        Assert.AreEqual(2, matches[0].GetProperty("bindings").GetProperty("answer").GetInt32());
                    }
                }

                Guid corrupt = Guid.NewGuid();
                await first.Kernel.CommitAsync(corrupt, GraphTransactionCodec.Encode(new(corrupt, [new DeleteEdgeReplica(e, a, b, "peer", GraphEdgeStorageRole.OutgoingAdjacency), new CreateEdgeReplica(e, a, a, "peer", GraphEdgeStorageRole.OutgoingAdjacency)])));
                await Assert.ThrowsAsync<InvalidDataException>(() => client.QueryAsync(new(first.Domain.Generation, DatabaseProfileId.PropertyGraph, "gql", queries[0]), default).AsTask());
            }
            finally
            {
                await client.CloseReadEpochAsync();
            }
        });
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Serving_export_carries_profile_effects_metadata_and_rejects_pre_reopen_cursor(DatabaseProfileId profile)
    {
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "decisions"));
        string path = Path.Combine(_root, "profile-feed");
        var node = await PartitionOwnedNode.CreateAsync(path, profile, placement.Lease, Routing, placement, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        DatabaseTimestampServingCursor old;
        try
        {
            using var protection = await node.Kernel.PartitionTransactions.ProtectTimestampHistoryAsync();
            var basis = await node.Kernel.PartitionTransactions.CaptureTimestampServingAsync(protection, PartitionTimestampClock.Read(ReadClock.Domain, ReadClock));
            Guid id = PartitionOwnerOperation.New(Routing, new("a"));
            if (profile == DatabaseProfileId.PropertyGraph)
            {
                var adapter = await node.CreateGraphExecutionAsync();
                await adapter.ExecuteAsync(node.Domain, id, adapter.CaptureSave(new Item("one", "before"), id).Export());
            }
            else if (profile == DatabaseProfileId.Rdf)
            {
                var adapter = await node.CreateRdfExecutionAsync();
                await adapter.ExecuteAsync(node.Domain, id, adapter.CaptureSave(new Item("one", "before"), id).Export());
            }
            else
            {
                var adapter = await node.CreateSemanticExecutionAsync();
                await adapter.ExecuteAsync(node.Domain, id, adapter.CaptureSave(new Item("one", "before"), new SemanticEntityWriteOperation(id, DateTimeOffset.UtcNow)).Export());
            }

            var delta = await node.Kernel.PartitionTransactions.CaptureTimestampServingAsync(protection, PartitionTimestampClock.Read(ReadClock.Domain, ReadClock), basis.Cursor);
            Assert.IsTrue(delta.Changes.Length > 0);
            Assert.IsTrue(delta.Changes.Any(c => c.Effects is { EffectCount: > 0 }));
            Assert.IsTrue(delta.Changes.All(c => c.Mutation.Length > 0 && c.Time.Mvcc));
            Assert.AreEqual(delta.Cursor.Sequence - basis.Cursor.Sequence, (long)delta.Changes.Length);
            old = delta.Cursor;
        }
        finally
        {
            await node.DisposeAsync();
        }

        await using var reopened = await PartitionOwnedNode.OpenAsync(path, profile, placement.Lease, Routing, placement, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        using var pin = await reopened.Kernel.PartitionTransactions.ProtectTimestampHistoryAsync();
        var time = PartitionTimestampClock.Read(ReadClock.Domain, ReadClock);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => reopened.Kernel.PartitionTransactions.CaptureTimestampServingAsync(pin, time, old).AsTask());
        var recovery = await reopened.Kernel.PartitionTransactions.CaptureTimestampServingAsync(pin, time);
        Assert.IsTrue(recovery.Baseline);
        Assert.AreNotEqual(old.Source, recovery.Cursor.Source);
    }

    [TestMethod]
    public async Task Serving_export_rejects_dropped_prefix_and_accepts_explicit_current_baseline()
    {
        var placement = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "overflow"), DatabaseProfileId.PropertyGraph, placement.Lease, Routing, placement, Options, timestampClock: ReadClock);
        using var pin = await node.Kernel.PartitionTransactions.ProtectTimestampHistoryAsync();
        var basis = await node.Kernel.PartitionTransactions.CaptureTimestampServingAsync(pin, PartitionTimestampClock.Read(ReadClock.Domain, ReadClock));
        for (int i = 0; i <= DatabaseTimestampServingBatch.MaximumChanges; i++)
        {
            Guid id = Guid.NewGuid();
            await node.Kernel.CommitAsync(id, GraphTransactionCodec.Encode(new(id, [new CreateVertex(new(Guid.NewGuid()), [])])));
        }

        var time = PartitionTimestampClock.Read(ReadClock.Domain, ReadClock);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => node.Kernel.PartitionTransactions.CaptureTimestampServingAsync(pin, time, basis.Cursor).AsTask());
        Assert.IsTrue((await node.Kernel.PartitionTransactions.CaptureTimestampServingAsync(pin, time)).Baseline);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Serving_export_waits_for_lower_preparation_and_preserves_out_of_order_publication(bool abort)
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "serving-order"), DatabaseProfileId.PropertyGraph, authority.Lease, Routing, authority, Options, timestampClock: ReadClock);
        var a = new GraphVertexId(Guid.NewGuid());
        var b = new GraphVertexId(Guid.NewGuid());
        Guid seed = Guid.NewGuid();
        await node.Kernel.CommitAsync(seed, GraphTransactionCodec.Encode(new(seed, [new CreateVertex(a, []), new CreateVertex(b, [])])));
        using var pin = await node.Kernel.PartitionTransactions.ProtectTimestampHistoryAsync();
        var basis = await node.Kernel.PartitionTransactions.CaptureTimestampServingAsync(pin, PartitionTimestampClock.Read(ReadClock.Domain, ReadClock));
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        string firstHash = new('A', 64), secondHash = new('B', 64);
        byte[] Payload(Guid operation, GraphVertexId vertex) => GraphTransactionCodec.Encode(new(operation, [new SetVertexProperty(vertex, "name", new DatabaseValue.String("written"))], node.Kernel.CurrentVersion, [new VertexRead(vertex)]));
        await node.Kernel.PartitionTransactions.PrepareAsync(first, firstHash, node.Kernel.CurrentVersion, Payload(first, a));
        var firstTime = await node.Kernel.ChooseTimestampAsync([(await node.Kernel.ReadTimestampVoteAsync(first, default))!], default);
        await node.Kernel.PartitionTransactions.PrepareAsync(second, secondHash, node.Kernel.CurrentVersion, Payload(second, b));
        var secondTime = await node.Kernel.ChooseTimestampAsync([(await node.Kernel.ReadTimestampVoteAsync(second, default))!], default);
        await node.Kernel.PartitionTransactions.CompleteTimestampedAsync(second, secondHash, true, secondTime);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waiting = node.Kernel.PartitionTransactions.CaptureTimestampServingAsync(pin, secondTime, basis.Cursor, timeout.Token).AsTask();
        await Task.Delay(30);
        Assert.IsFalse(waiting.IsCompleted, "Closed progress cannot pass a lower unresolved vote.");
        await node.Kernel.PartitionTransactions.CompleteTimestampedAsync(first, firstHash, !abort, firstTime);
        var answer = await waiting;
        Assert.AreEqual(abort ? 1 : 2, answer.Changes.Length);
        Assert.AreEqual(second, answer.Changes[0].Operation);
        if (!abort)
        {
            Assert.AreEqual(first, answer.Changes[1].Operation);
            Assert.IsTrue(answer.Changes[0].Time.CompareTo(answer.Changes[1].Time) > 0);
        }

        Assert.AreEqual(secondTime, answer.CompleteThrough);
    }

    [TestMethod]
    public async Task Indexed_serving_waits_for_complete_coordinated_pair()
    {
        await WithQueryHomePair(async (first, second, ordinary, routing, decisions) =>
        {
            var owners = new[]
            {
                first,
                second
            };
            var client = new PartitionApplicationClient(decisions, routing, owners, ReadClock)
            {
                QueryHome = ordinary.QueryHome,
                IndexedServingProperties = ["key"]
            };
            var vertices = owners.Select(owner =>
            {
                Guid id;
                do
                {
                    id = Guid.NewGuid();
                } while (routing.RouteGraphVertex(id) != owner.Domain.Partition);
                return new GraphVertexId(id);
            }).ToArray();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                for (int i = 0; i < owners.Length; i++)
                {
                    Guid op = Guid.NewGuid();
                    await owners[i].Kernel.CommitAsync(op, GraphTransactionCodec.Encode(new(op, [new CreateVertex(vertices[i], ["Pair"]), new SetVertexProperty(vertices[i], "key", new DatabaseValue.String("pair")), new SetVertexProperty(vertices[i], "value", new DatabaseValue.String("0"))])));
                }

                var request = new PartitionQueryRequest(first.Domain.Generation, DatabaseProfileId.PropertyGraph, "gql", "MATCH (n:Pair) WHERE n.key = \"pair\" RETURN n.`value` AS pairValue");
                _ = await client.QueryAsync(request, timeout.Token);
                for (int epoch = 1; epoch <= 8; epoch++)
                {
                    Guid op = Guid.NewGuid();
                    var plan = new PartitionTransactionPlan(op, routing.MapVersion, owners.Select((owner, i) => new PartitionTransactionMember(owner.Domain, owner.Kernel.CurrentVersion, GraphTransactionCodec.Encode(new(op, [new SetVertexProperty(vertices[i], "value", new DatabaseValue.String(epoch.ToString()))], owner.Kernel.CurrentVersion, [])))).ToArray());
                    await decisions.BindAsync(plan);
                    await first.PrepareNativeAsync(plan);
                    await second.PrepareNativeAsync(plan);
                    await SelectTimestampDecisionAsync(decisions, plan, first, second);
                    await first.ApplyPartitionDecisionAsync(op);
                    var pending = client.QueryAsync(request, timeout.Token).AsTask();
                    await Task.Delay(30, timeout.Token);
                    Assert.IsFalse(pending.IsCompleted);
                    await second.ApplyPartitionDecisionAsync(op);
                    var answer = await pending;
                    var rows = answer.Value.GetProperty("matches").EnumerateArray().ToArray();
                    Assert.AreEqual(2, rows.Length);
                    Assert.IsTrue(rows.All(row => row.GetProperty("bindings").GetProperty("pairValue").GetString() == epoch.ToString()));
                }
            }
            finally
            {
                await client.CloseReadEpochAsync();
            }
        });
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Serving_export_closes_timestamp_progress_for_every_native_profile(DatabaseProfileId profile)
    {
        var placements = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "serving-" + profile), profile, placements.Lease, Routing, placements, Options, timestampClock: ReadClock);
        using var protection = await node.Kernel.PartitionTransactions.ProtectTimestampHistoryAsync();
        var time = PartitionTimestampClock.Read(ReadClock.Domain, ReadClock);
        var basis = await node.Kernel.PartitionTransactions.CaptureTimestampServingAsync(protection, time);
        Assert.IsTrue(basis.Baseline);
        Assert.IsNotNull(basis.View);
        var next = await node.Kernel.PartitionTransactions.CaptureTimestampServingAsync(protection, PartitionTimestampClock.Read(ReadClock.Domain, ReadClock), basis.Cursor);
        Assert.IsFalse(next.Baseline);
        Assert.AreEqual(basis.Cursor, next.Cursor);
        Assert.IsTrue(next.CompleteThrough.CompareTo(time) >= 0);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await node.Kernel.PartitionTransactions.CaptureTimestampServingAsync(protection, PartitionTimestampClock.Read(ReadClock.Domain, ReadClock), basis.Cursor with { Source = Guid.NewGuid() }));
        protection.Dispose();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await node.Kernel.PartitionTransactions.CaptureTimestampServingAsync(protection, PartitionTimestampClock.Read(ReadClock.Domain, ReadClock), basis.Cursor));
    }

    [TestMethod]
    public async Task Serving_export_contains_exact_owned_effects_and_rejects_missing_cursor_prefix()
    {
        var placements = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "serving-effects"), DatabaseProfileId.PropertyGraph, placements.Lease, Routing, placements, Options, timestampClock: ReadClock);
        using var protection = await node.Kernel.PartitionTransactions.ProtectTimestampHistoryAsync();
        var basis = await node.Kernel.PartitionTransactions.CaptureTimestampServingAsync(protection, PartitionTimestampClock.Read(ReadClock.Domain, ReadClock));
        Guid operation = Guid.NewGuid();
        var vertex = new GraphVertexId(Guid.NewGuid());
        await node.Authority.ExecuteAsync(async () =>
        {
            await node.Kernel.CommitAsync(operation, GraphTransactionCodec.Encode(new(operation, [new CreateVertex(vertex, ["Serving"])])));
            return true;
        });
        var time = PartitionTimestampClock.Read(ReadClock.Domain, ReadClock);
        var delta = await node.Kernel.PartitionTransactions.CaptureTimestampServingAsync(protection, time, basis.Cursor);
        Assert.AreEqual(1, delta.Changes.Length);
        Assert.AreEqual(operation, delta.Changes[0].Operation);
        Assert.IsTrue(delta.Changes[0].Time.CompareTo(time) <= 0);
        var original = delta.Changes[0].Mutation.ToArray();
        delta.Changes[0].Mutation[0] ^= 1;
        var replay = await node.Kernel.PartitionTransactions.CaptureTimestampServingAsync(protection, time, basis.Cursor);
        CollectionAssert.AreEqual(original, replay.Changes[0].Mutation);
        var historical = await node.Kernel.PartitionTransactions.CaptureTimestampServingAsync(protection, basis.CompleteThrough);
        Assert.IsTrue(historical.Baseline);
        Assert.AreEqual(1, historical.Changes.Length);
        Assert.AreEqual(operation, historical.Changes[0].Operation);
        Assert.AreEqual(delta.Cursor, historical.Cursor, "An initial historical basis must carry its published future tail.");
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await node.Kernel.PartitionTransactions.CaptureTimestampServingAsync(protection, time, basis.Cursor with { Sequence = delta.Cursor.Sequence + 1 }));
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Indexed_serving_preserves_fresh_positive_negative_and_duplicate_answers(bool http, bool demand)
    {
        await WithQueryHome(async (node, placements, home, decisions) =>
        {
            using var certificate = PartitionCertificate();
            await using var service = new PartitionParticipantHttpService(node);
            await using var host = await StartPartitionHostAsync(service, certificate);
            using var transport = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint });
            IPartitionTransactionParticipant owner = http ? new HttpPartitionTransactionParticipant(transport, PartitionAddress(host), PartitionToken, node.Domain, node.TransactionAuthorityIdentity!) : node;
            var client = new PartitionApplicationClient(decisions, Routing, [owner], ReadClock)
            {
                QueryHome = home,
                IndexedServingProperties = ["key"],
                IndexedServingDemandDriven = demand
            };
            var ordinary = new PartitionApplicationClient(decisions, Routing, [owner], ReadClock)
            {
                QueryHome = home
            };
            bool revoked = false;
            var a = new GraphVertexId(Guid.NewGuid());
            var b = new GraphVertexId(Guid.NewGuid());
            async Task Write(params GraphMutation[] mutations)
            {
                Guid operation = Guid.NewGuid();
                await node.Authority.ExecuteAsync(async () =>
                {
                    await node.Kernel.CommitAsync(operation, GraphTransactionCodec.Encode(new(operation, mutations)));
                    return true;
                });
            }

            async Task Equal(string statement)
            {
                var query = new PartitionQueryRequest(node.Domain.Generation, DatabaseProfileId.PropertyGraph, "gql", statement);
                var expected = await ordinary.QueryAsync(query, CancellationToken.None);
                var actual = await client.QueryAsync(query, CancellationToken.None);
                var left = System.Text.Json.Nodes.JsonNode.Parse(expected.Value.GetRawText())!.AsObject();
                var right = System.Text.Json.Nodes.JsonNode.Parse(actual.Value.GetRawText())!.AsObject();
                left.Remove("queryId");
                right.Remove("queryId");
                Assert.IsTrue(System.Text.Json.Nodes.JsonNode.DeepEquals(left, right), left + " != " + right);
            }

            try
            {
                await Write(new CreateVertex(a, ["Serving"]), new SetVertexProperty(a, "key", new DatabaseValue.String("match")));
                await Equal("MATCH (n:Serving) WHERE n.key = \"match\" RETURN n");
                await Equal("MATCH (n:Serving) WHERE n.key = \"missing\" RETURN n");
                await Write(new CreateVertex(b, ["Serving"]), new SetVertexProperty(b, "key", new DatabaseValue.String("match")));
                await Equal("MATCH (n:Serving) WHERE n.key = \"match\" RETURN n");
                await Write(new SetVertexProperty(a, "key", new DatabaseValue.String("changed")));
                await Equal("MATCH (n:Serving) WHERE n.key = \"changed\" RETURN n");
                await Equal("MATCH (n:Serving) WHERE n.key = \"match\" RETURN n");
                if (demand)
                {
                    await Task.Delay(TimeSpan.FromSeconds(6)); // Original source protection retires during quiescence.
                    await Equal("MATCH (n:Serving) WHERE n.key = \"changed\" RETURN n");
                }

                await ordinary.CloseReadEpochAsync();
                await placements.ReleaseAsync(node.CurrentLease);
                revoked = true;
                // Existing protected replica plus issued read authority survives
                // write/liveness release. Ownership replacement must still wait.
                _ = await client.QueryAsync(new(node.Domain.Generation, DatabaseProfileId.PropertyGraph, "gql", "MATCH (n:Serving) RETURN n"), CancellationToken.None);
                await Assert.ThrowsExactlyAsync<ShardServingGrantActiveException>(() => placements.AcquireAsync(node.Domain.Partition, new("replacement"), new("https://localhost:22002"), new(node.Domain.Generation), Routing.MapVersion, Options.LeaseLifetime).AsTask());
            }
            finally
            {
                if (revoked)
                {
                    await Assert.ThrowsAsync<Exception>(() => client.CloseReadEpochAsync().AsTask());
                }
                else
                {
                    await client.CloseReadEpochAsync();
                    await ordinary.CloseReadEpochAsync();
                }
            }
        });
    }
}
