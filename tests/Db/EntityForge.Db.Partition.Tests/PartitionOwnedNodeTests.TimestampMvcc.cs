using EntityForge.Db.Storage.Pages;
using System.Diagnostics;
using System.Text.Json;
using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    private static IDisposable TimestampVisibility()
    {
        var prior = DatabaseTimestampCostOptions.QualificationOverride.Value;
        DatabaseTimestampCostOptions.QualificationOverride.Value = new(Guid.NewGuid(), () => Stopwatch.GetElapsedTime(0).Ticks,
            () => true, TimeSpan.FromMilliseconds(50))
        {
            Visibility = true
        };
        return new TimestampRestore(prior);
    }

    private sealed class TimestampRestore(DatabaseTimestampCostOptions? prior) : IDisposable
    {
        public void Dispose() => DatabaseTimestampCostOptions.QualificationOverride.Value = prior;
    }

    private sealed class IndependentTestClock(Guid domain, long offset) : IDatabaseBoundedClock
    {
        internal bool Healthy = true;

        public Guid Domain => domain;

        public DatabaseClockInterval Read()
        {
            if (!Healthy)
            {
                throw new InvalidOperationException("Clock monitoring has lost its bound.");
            }

            long center = DateTime.UtcNow.Ticks + offset;
            const long error = 2 * TimeSpan.TicksPerMillisecond;
            return new(center - error, center + error);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Configured_clock_selects_timestamp_reads_without_per_query_flags(bool bounded)
    {
        var clock = ReadClock;
        var placement = new Authority();
        await using var authority = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "automatic-clock-authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "automatic-clock"), DatabaseProfileId.PropertyGraph,
            placement.Lease, Routing, placement, Options, transactionAuthority: authority, timestampClock: clock);
        var client = new PartitionApplicationClient(authority, Routing, [node], clock);
        try
        {
            var result = await client.ReadAsync(new(node.Domain.Generation, DatabaseProfileId.PropertyGraph, Guid.NewGuid(),
                [new("vertex", Guid.NewGuid())]), default);
            Assert.AreEqual("null", System.Text.Encoding.UTF8.GetString(result.Values.Single()));
            Assert.IsTrue(node.Kernel.PartitionTransactions.TimestampVisibilityEnabled);
            Assert.AreEqual(1, node.Kernel.TimestampProtectionCount);
        }
        finally
        {
            await client.CloseReadEpochAsync();
        }
        Assert.AreEqual(0, node.Kernel.TimestampProtectionCount);
    }

    [TestMethod]
    public async Task Timestamp_mvcc_clock_loss_rejects_projection_and_releases_protection()
    {
        var clock = new IndependentTestClock(Guid.NewGuid(), 0);
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "clock-loss"), DatabaseProfileId.PropertyGraph,
            authority.Lease, Routing, authority, Options, timestampClock: clock);
        var epoch = new PartitionReadEpoch([node], Routing, clock);
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => epoch.ExecuteAsync(async (read, token) =>
            {
                clock.Healthy = false;
                return await read.ReadAsync("a", new("vertex", Guid.NewGuid()), token);
            }, null, default).AsTask());
        }
        finally
        {
            await epoch.DisposeAsync();
        }
        Assert.AreEqual(0, node.Kernel.TimestampProtectionCount);
        clock.Healthy = true;
        await using var recovered = new PartitionReadEpoch([node], Routing, clock);
        var result = await recovered.ExecuteAsync((read, token) => read.ReadAsync("a", new("vertex", Guid.NewGuid()), token), null, default);
        Assert.AreEqual("null", System.Text.Encoding.UTF8.GetString(result.Value));
    }

    [TestMethod]
    public async Task Timestamp_mvcc_independent_clock_bounds_preserve_late_owner_snapshot()
    {
        Guid domain = Guid.NewGuid();
        var leftClock = new IndependentTestClock(domain, -TimeSpan.TicksPerMillisecond);
        var rightClock = new IndependentTestClock(domain, TimeSpan.TicksPerMillisecond);
        var gatewayClock = new IndependentTestClock(domain, 0);
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var a = new Authority();
        var b = new Authority
        {
            Lease = a.Lease with
            {
                Placement = a.Lease.Placement with
                {
                    Claim = a.Lease.Placement.Claim with
                    {
                        Shard = new("b"),
                        Owner = new("second")
                    }
                }
            }
        };
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "bounded-a"), DatabaseProfileId.PropertyGraph,
            a.Lease, routing, a, Options, timestampClock: leftClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "bounded-b"), DatabaseProfileId.PropertyGraph,
            b.Lease, routing, b, Options, timestampClock: rightClock);
        GraphVertexId Id(string owner)
        {
            Guid id;
            do
            {
                id = Guid.NewGuid();
            } while (routing.RouteGraphVertex(id).Value != owner);
            return new(id);
        }
        var left = Id("a");
        var right = Id("b");
        async Task Write(PartitionOwnedNode node, params GraphMutation[] mutations)
        {
            Guid operation = Guid.NewGuid();
            await node.Authority.ExecuteAsync(async () =>
            {
                await node.Kernel.CommitAsync(operation, GraphTransactionCodec.Encode(new(operation, mutations)));
                return true;
            });
        }
        await Write(first, new CreateVertex(left, []));
        await Write(second, new CreateVertex(right, []), new SetVertexProperty(right, "name", new DatabaseValue.String("before")));
        await using var epoch = new PartitionReadEpoch([first, second], routing, gatewayClock);
        await epoch.ExecuteAsync(async (read, token) =>
        {
            await read.ReadAsync("a", new("vertex", left.Value), token);
            await Write(second, new SetVertexProperty(right, "name", new DatabaseValue.String("after")));
            string value = System.Text.Encoding.UTF8.GetString(await read.ReadAsync("b", new("vertex", right.Value), token));
            StringAssert.Contains(value, "before");
            Assert.IsFalse(value.Contains("after"));
            return true;
        }, null, default);
        var fresh = await epoch.ExecuteAsync((read, token) => read.ReadAsync("b", new("vertex", right.Value), token), null, default);
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(fresh.Value), "after");
    }

    [TestMethod]
    public async Task Timestamp_mvcc_local_allocation_advances_past_a_remote_logical_commit()
    {
        var prior = DatabaseTimestampCostOptions.QualificationOverride.Value;
        Guid domain = Guid.NewGuid();
        DatabaseTimestampCostOptions.QualificationOverride.Value = new(domain, () => TimeSpan.TicksPerSecond,
            () => true, TimeSpan.FromMilliseconds(50))
        {
            Visibility = true
        };
        using var restore = new TimestampRestore(prior);
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "remote-causal"), DatabaseProfileId.PropertyGraph,
            authority.Lease, Routing, authority, Options);
        Guid first = Guid.NewGuid();
        string descriptor = new('D', 64);
        await node.Kernel.PartitionTransactions.PrepareAsync(first, descriptor, node.Kernel.CurrentVersion,
            GraphTransactionCodec.Encode(new(first, [new CreateVertex(GraphVertexId.New(), [])], ReadVersion: node.Kernel.CurrentVersion)));
        var vote = (await node.Kernel.ReadTimestampVoteAsync(first, default))!;
        var received = new DurableTimestampState(domain, vote.Physical, checked(vote.Logical + 100), 0, true);
        await node.Kernel.PartitionTransactions.CompleteTimestampedAsync(first, descriptor, true, received);
        Guid second = Guid.NewGuid();
        await node.Authority.ExecuteAsync(async () =>
        {
            await node.Kernel.CommitAsync(second, GraphTransactionCodec.Encode(new(second, [new CreateVertex(GraphVertexId.New(), [])])));
            return true;
        });
        Assert.IsTrue(node.Kernel.TimestampCostState!.CompareTo(received) > 0);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Timestamp_mvcc_real_home_HTTP_agrees_commit_and_replays(bool live)
    {
        using var clock = TimestampVisibility();
        await Native_home_delivers_two_owner_mapped_save_without_central_operation_records(DatabaseProfileId.PropertyGraph, false, live);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Timestamp_mvcc_Rdf_real_home_HTTP_agrees_commit_and_replays(bool live)
    {
        using var clock = TimestampVisibility();
        await Native_home_delivers_two_owner_mapped_save_without_central_operation_records(DatabaseProfileId.Rdf, false, live);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Timestamp_mvcc_Rdf_HTTP_reads_and_minimum_position(bool live)
    {
        using var clock = TimestampVisibility();
        await Read_epoch_https_reuses_pin_and_preserves_minimum_after_write(DatabaseProfileId.Rdf, live);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Timestamp_mvcc_Semantic_HTTP_replace_replay_prepare_and_abort(bool live)
    {
        // HTTP requests do not inherit a test's AsyncLocal clock. Give the nodes
        // and gateway the same explicit provider, as production composition must.
        using var clock = new TimestampRestore(DatabaseTimestampCostOptions.QualificationOverride.Value);
        DatabaseTimestampCostOptions.QualificationOverride.Value = DatabaseTimestampCostOptions.FromClock(new IndependentTestClock(Guid.NewGuid(), 0));
        await SemanticPartitionLifecycleAsync(false, live);
    }

    [TestMethod]
    public async Task Timestamp_mvcc_Semantic_retained_projection_survives_later_save()
    {
        using var clock = TimestampVisibility();
        await Semantic_mvcc_pin_preserves_projection_across_a_later_save();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Timestamp_mvcc_Semantic_logical_projection_survives_replacement(bool bounded)
    {
        using var clock = bounded ? null : TimestampVisibility();
        IDatabaseBoundedClock independentClock = bounded ? new IndependentTestClock(Guid.NewGuid(), 0) : ReadClock;
        var placement = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "semantic-logical"), DatabaseProfileId.Semantic,
            placement.Lease, Routing, placement, Options, timestampClock: independentClock);
        var adapter = await node.CreateSemanticExecutionAsync();
        async Task Save(string name)
        {
            var request = adapter.CaptureSave(new Item("one", name), EntityForge.Db.Semantic.SemanticEntityWriteOperation.Create());
            await adapter.ExecuteAsync(node.Domain, request.OperationId, request.Export());
        }
        await Save("before");
        using var http = new HttpClient();
        var producer = new EntityForge.Db.Semantic.HttpSemanticEntityClient(http, node.Domain.Generation).EntityStore<Item>();
        var (_, recipe) = producer.DescribePartitionRead("one");
        await using var epoch = new PartitionReadEpoch([node], Routing, independentClock);
        PartitionReadQuery? query = null;
        await epoch.ExecuteAsync(async (read, token) =>
        {
            await Save("after");
            await SemanticReadExpansionWalker.WalkAsync(new(recipe, [new(0, SemanticEntityReadDecoder.SemanticIdentity(recipe.Identity))]),
                _ => true, async (projection, ct) =>
                {
                    query = new("semantic-project", Subject: JsonSerializer.Serialize(projection, PartitionParticipantWire.Json));
                    byte[] result = await read.ReadAsync("a", query, ct);
                    StringAssert.Contains(System.Text.Encoding.UTF8.GetString(result), "before");
                    Assert.IsFalse(System.Text.Encoding.UTF8.GetString(result).Contains("after"));
                    using var json = JsonDocument.Parse(result);
                    return SemanticEntityReadDecoder.Projection(json.RootElement, projection.Query());
                }, (_, _) => throw new InvalidOperationException("Scalar recipe unexpectedly traversed relationships."), token);
            return true;
        }, null, default);
        Assert.IsNotNull(query);
        var fresh = await epoch.ExecuteAsync((read, token) => read.ReadAsync("a", query, token), null, default);
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(fresh.Value), "after");
        await epoch.DisposeAsync();
        await node.DisposeAsync();
        await using var reopened = await PartitionOwnedNode.OpenAsync(Path.Combine(_root, "semantic-logical"), DatabaseProfileId.Semantic,
            placement.Lease, Routing, placement, Options, timestampClock: independentClock);
        await using var recoveredEpoch = new PartitionReadEpoch([reopened], Routing, independentClock);
        var recovered = await recoveredEpoch.ExecuteAsync((read, token) => read.ReadAsync("a", query, token), null, default);
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(recovered.Value), "after");
        Assert.IsFalse(System.Text.Encoding.UTF8.GetString(recovered.Value).Contains("before"));
    }

    [TestMethod]
    public async Task Timestamp_mvcc_query_remains_stable_after_relevant_write_and_late_projection()
    {
        using var clock = TimestampVisibility();
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "logical"), DatabaseProfileId.PropertyGraph,
            authority.Lease, Routing, authority, Options);
        var vertex = new GraphVertexId(Guid.NewGuid());
        async Task Write(params GraphMutation[] mutations)
        {
            Guid operation = Guid.NewGuid();
            await node.Authority.ExecuteAsync(async () =>
            {
                await node.Kernel.CommitAsync(operation,
                GraphTransactionCodec.Encode(new(operation, mutations)));
                return true;
            });
        }
        await Write(new CreateVertex(vertex, []), new SetVertexProperty(vertex, "name", new DatabaseValue.String("before")));
        await using var epochs = new PartitionReadEpoch([node], Routing, ReadClock);
        int invocations = 0;
        await epochs.ExecuteAsync(async (read, token) =>
        {
            invocations++;
            var query = new PartitionReadQuery("vertex", vertex.Value);
            byte[] before = await read.ReadAsync("a", query, token);
            await Write(new SetVertexProperty(vertex, "name", new DatabaseValue.String("after")));
            byte[] after = await read.ReadAsync("a", query, token);
            CollectionAssert.AreEqual(before, after, "The same logical query must not refresh after a write.");
            return true;
        }, null, default);
        Assert.AreEqual(1, invocations);
        var fresh = await epochs.ExecuteAsync((read, token) => read.ReadAsync("a", new("vertex", vertex.Value), token), null, default);
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(fresh.Value), "after");
    }

    [TestMethod]
    public async Task Timestamp_mvcc_out_of_order_commit_excludes_newer_effects_and_relevant_intents_wait()
    {
        using var clock = TimestampVisibility();
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "logical-order"), DatabaseProfileId.PropertyGraph,
            authority.Lease, Routing, authority, Options);
        var a = new GraphVertexId(Guid.NewGuid());
        var b = new GraphVertexId(Guid.NewGuid());
        Guid seed = Guid.NewGuid();
        await node.Authority.ExecuteAsync(async () =>
        {
            await node.Kernel.CommitAsync(seed,
            GraphTransactionCodec.Encode(new(seed, [new CreateVertex(a, []), new CreateVertex(b, [])])));
            return true;
        });
        using var pin = await node.Kernel.PartitionTransactions.ProtectTimestampHistoryAsync();
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        string firstHash = new('A', 64), secondHash = new('B', 64);
        byte[] Payload(Guid operation, GraphVertexId target, string value) => GraphTransactionCodec.Encode(
            new(operation, [new SetVertexProperty(target, "name", new DatabaseValue.String(value))], ReadVersion: node.Kernel.CurrentVersion,
                ReadPredicates: [new VertexRead(target)]));
        await node.Kernel.PartitionTransactions.PrepareAsync(first, firstHash, node.Kernel.CurrentVersion, Payload(first, a, "older"));
        var firstTime = await node.Kernel.ChooseTimestampAsync([(await node.Kernel.ReadTimestampVoteAsync(first, default))!], default);
        await node.Kernel.PartitionTransactions.PrepareAsync(second, secondHash, node.Kernel.CurrentVersion, Payload(second, b, "newer"));
        var secondTime = await node.Kernel.ChooseTimestampAsync([(await node.Kernel.ReadTimestampVoteAsync(second, default))!], default);
        var log = typeof(DatabaseKernel).GetField("_commitLog", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(node.Kernel)!;
        var stream = typeof(GroupedRedoDatabaseCommitLog).GetField("_stream", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(log)!;
        using var pause = new PausedReadAdmissionWal { Armed = true };
        stream.GetType().GetField("_observer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(stream, pause);
        var laterCommit = Task.Run(async () => await node.Kernel.PartitionTransactions.CompleteTimestampedAsync(second, secondHash, true, secondTime));
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await node.Kernel.CaptureTimestampProjectionAsync(pin, firstTime, null, cancellation.Token));
            Assert.IsFalse(laterCommit.IsCompleted, "A later append cannot make an older unresolved intent safe.");
        }
        finally
        {
            pause.Release.Set();
        }
        await laterCommit.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, node.Kernel.UnresolvedTimestampPreparationCount);
        var waiting = node.Kernel.CaptureTimestampProjectionAsync(pin, firstTime, [a.Value.ToString("N")], default).AsTask();
        await Task.Delay(30);
        Assert.IsFalse(waiting.IsCompleted);
        // An unrelated leaf does not join the older A reservation.
        var unrelated = await node.Kernel.CaptureTimestampProjectionAsync(pin, firstTime, [b.Value.ToString("N")], default);
        using (node.Kernel.EnterTimestampProjection(unrelated))
        {
            byte[] bytes = await node.ReadFrozenAsync(pin.Position, new("vertex", b.Value), default, false);
            Assert.IsFalse(System.Text.Encoding.UTF8.GetString(bytes).Contains("newer"));
        }
        await node.Kernel.PartitionTransactions.CompleteTimestampedAsync(first, firstHash, true, firstTime);
        Assert.AreEqual(0, node.Kernel.UnresolvedTimestampPreparationCount);
        var view = await waiting;
        using (node.Kernel.EnterTimestampProjection(view))
        {
            StringAssert.Contains(System.Text.Encoding.UTF8.GetString(await node.ReadFrozenAsync(pin.Position, new("vertex", a.Value), default, false)), "older");
            Assert.IsFalse(System.Text.Encoding.UTF8.GetString(await node.ReadFrozenAsync(pin.Position, new("vertex", b.Value), default, false)).Contains("newer"));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Timestamp_mvcc_reopen_preserves_prepared_vote_and_exact_commit(bool checkpoint)
    {
        using var clock = TimestampVisibility();
        string path = Path.Combine(_root, "mvcc-recovery");
        async Task<IGraphDatabase> Open() => await GraphDatabase.OpenLocalAsync(new(path)
        {
            VersioningMode = DatabaseVersioningMode.PartitionLocal
        });
        Guid operation = Guid.NewGuid();
        string descriptor = new('C', 64);
        var vertex = new GraphVertexId(Guid.NewGuid());
        DurableTimestampState chosen;
        await using (var graph = await Open())
        {
            var kernel = ((GraphDatabaseEngine)graph).PartitionKernel;
            await kernel.PartitionTransactions.PrepareAsync(operation, descriptor, kernel.CurrentVersion,
                GraphTransactionCodec.Encode(new(operation, [new CreateVertex(vertex, [])], ReadVersion: kernel.CurrentVersion,
                    ReadPredicates: [new VertexRead(vertex)])));
            chosen = await kernel.ChooseTimestampAsync([(await kernel.ReadTimestampVoteAsync(operation, default))!], default);
            if (checkpoint)
            {
                await kernel.PartitionTransactions.CheckpointForTransferAsync();
            }
        }
        await using (var graph = await Open())
        {
            var kernel = ((GraphDatabaseEngine)graph).PartitionKernel;
            Assert.AreEqual(1, kernel.UnresolvedTimestampPreparationCount);
            Assert.IsTrue((await kernel.ReadTimestampVoteAsync(operation, default))!.CompareTo(chosen) < 0);
            await kernel.PartitionTransactions.CompleteTimestampedAsync(operation, descriptor, true, chosen);
            Assert.AreEqual(0, kernel.UnresolvedTimestampPreparationCount);
            Assert.AreEqual(0, (await kernel.PartitionTransactions.ReadAsync()).Single().Timestamp!.CompareTo(chosen));
            Assert.IsNotNull(await graph.GetVertexAsync(vertex, graph.CurrentVersion));
        }
        await using var again = await Open();
        Assert.AreEqual(0, (await ((GraphDatabaseEngine)again).PartitionKernel.PartitionTransactions.ReadAsync()).Single().Timestamp!.CompareTo(chosen));
    }

    [TestMethod]
    public async Task Timestamp_mvcc_adjacency_and_tombstones_remain_at_query_time()
    {
        using var clock = TimestampVisibility();
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "mvcc-edges"), DatabaseProfileId.PropertyGraph,
            authority.Lease, Routing, authority, Options);
        var a = new GraphVertexId(Guid.NewGuid());
        var b = new GraphVertexId(Guid.NewGuid());
        var edge = new GraphEdgeId(Guid.NewGuid());
        async Task Write(params GraphMutation[] mutations)
        {
            Guid operation = Guid.NewGuid();
            await node.Authority.ExecuteAsync(async () =>
            {
                await node.Kernel.CommitAsync(operation,
                GraphTransactionCodec.Encode(new(operation, mutations)));
                return true;
            });
        }
        await Write(new CreateVertex(a, []), new CreateVertex(b, []), new CreateEdge(edge, a, b, "peer"));
        await using var epoch = new PartitionReadEpoch([node], Routing, ReadClock);
        await epoch.ExecuteAsync(async (read, token) =>
        {
            byte[] outgoing = await read.ReadAsync("a", new("outgoing", a.Value), token);
            await Write(new DeleteEdge(edge), new DeleteVertex(b));
            CollectionAssert.AreEqual(outgoing, await read.ReadAsync("a", new("outgoing", a.Value), token));
            Assert.AreNotEqual("null", System.Text.Encoding.UTF8.GetString(await read.ReadAsync("a", new("vertex", b.Value), token)));
            Assert.AreNotEqual("[]", System.Text.Encoding.UTF8.GetString(await read.ReadAsync("a", new("incoming", b.Value), token)));
            return true;
        }, null, default);
        var current = await epoch.ExecuteAsync((read, token) => read.ReadAsync("a", new("vertex", b.Value), token), null, default);
        Assert.AreEqual("null", System.Text.Encoding.UTF8.GetString(current.Value));
    }

    [TestMethod]
    public async Task Timestamp_mvcc_transfer_drains_readers()
    {
        using var clock = TimestampVisibility();
        await Mvcc_snapshot_lifetime_is_drained_before_sealed_transfer(DatabaseProfileId.PropertyGraph);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Timestamp_mvcc_independent_clock_survives_transfer_and_reopen(int clockLossStage) =>
        await TransferGraphReplayAsync(new IndependentTestClock(Guid.NewGuid(), 0), clockLossStage);

    [TestMethod]
    public async Task Timestamp_mvcc_late_owner_expansion_uses_original_time()
    {
        using var clock = TimestampVisibility();
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var a = new Authority();
        var b = new Authority
        {
            Lease = a.Lease with
            {
                Placement = a.Lease.Placement with
                {
                    Claim = a.Lease.Placement.Claim with
                    {
                        Shard = new("b"),
                        Owner = new("second")
                    }
                }
            }
        };
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "expand-a"), DatabaseProfileId.PropertyGraph, a.Lease, routing, a, Options);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "expand-b"), DatabaseProfileId.PropertyGraph, b.Lease, routing, b, Options);
        GraphVertexId Id(string owner)
        {
            Guid id;
            do
            {
                id = Guid.NewGuid();
            } while (routing.RouteGraphVertex(id).Value != owner);
            return new(id);
        }
        var left = Id("a");
        var right = Id("b");
        async Task Write(PartitionOwnedNode node, params GraphMutation[] mutations)
        {
            Guid operation = Guid.NewGuid();
            await node.Authority.ExecuteAsync(async () =>
            {
                await node.Kernel.CommitAsync(operation,
                GraphTransactionCodec.Encode(new(operation, mutations)));
                return true;
            });
        }
        await Write(first, new CreateVertex(left, []));
        await Write(second, new CreateVertex(right, []), new SetVertexProperty(right, "name", new DatabaseValue.String("before")));
        await using var epoch = new PartitionReadEpoch([first, second], routing, ReadClock);
        await epoch.ExecuteAsync(async (read, token) =>
        {
            await read.ReadAsync("a", new("vertex", left.Value), token);
            await Write(second, new SetVertexProperty(right, "name", new DatabaseValue.String("after")));
            // B is contacted for projection only after its state changes.
            StringAssert.Contains(System.Text.Encoding.UTF8.GetString(await read.ReadAsync("b", new("vertex", right.Value), token)), "before");
            return true;
        }, null, default);
        var fresh = await epoch.ExecuteAsync((read, token) => read.ReadAsync("b", new("vertex", right.Value), token), null, default);
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(fresh.Value), "after");
    }

    [TestMethod]
    public async Task Timestamp_epoch_does_not_retry_version_invalidation_and_preserves_failures()
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "epoch-fanout"), DatabaseProfileId.PropertyGraph,
            authority.Lease, Routing, authority, Options, timestampClock: ReadClock);
        await using var epoch = new PartitionReadEpoch([node], Routing, ReadClock);
        int attempts = 0;
        await Assert.ThrowsExactlyAsync<AggregateException>(() => epoch.ExecuteAsync<bool>(async (_, token) =>
        {
            attempts++;
            await PartitionRemoteRead.RunSnapshotRoundAsync(new[] { 1, 2 },
                (_, _) => ValueTask.FromException(new PartitionReadEpochChangedException()), token);
            return true;
        }, null, default).AsTask());
        Assert.AreEqual(1, attempts);
        await Assert.ThrowsExactlyAsync<AggregateException>(() => epoch.ExecuteAsync<bool>(async (_, token) =>
        {
            await PartitionRemoteRead.RunSnapshotRoundAsync(new[] { 1, 2 }, (i, _) => ValueTask.FromException(
                i == 1 ? new PartitionReadEpochChangedException() : new IOException("Keep this failure")), token);
            return true;
        }, null, default).AsTask());
    }

    [TestMethod]
    public async Task Timestamp_mvcc_retiring_old_generation_reclaims_without_invalidating_new_generation()
    {
        using var clock = TimestampVisibility();
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "mvcc-reclaim"), DatabaseProfileId.PropertyGraph,
            authority.Lease, Routing, authority, Options);
        var vertex = GraphVertexId.New();
        async Task Write(params GraphMutation[] mutations)
        {
            Guid operation = Guid.NewGuid();
            await node.Authority.ExecuteAsync(async () =>
            {
                await node.Kernel.CommitAsync(operation,
                GraphTransactionCodec.Encode(new(operation, mutations)));
                return true;
            });
        }
        await Write(new CreateVertex(vertex, []));
        using var old = await node.Kernel.PartitionTransactions.CaptureSnapshotAsync();
        var oldTime = PartitionTimestampClock.Read(node.Kernel.TimestampCostState!.Domain);
        await Write(new SetVertexProperty(vertex, "name", new DatabaseValue.String("middle")));
        using var current = await node.Kernel.PartitionTransactions.CaptureSnapshotAsync();
        var currentTime = PartitionTimestampClock.Read(node.Kernel.TimestampCostState!.Domain);
        old.Dispose();
        await Write(new SetVertexProperty(vertex, "name", new DatabaseValue.String("latest")));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => node.Kernel.CaptureTimestampProjectionAsync(current, oldTime, null, default).AsTask());
        object view = await node.Kernel.CaptureTimestampProjectionAsync(current, currentTime, null, default);
        using (node.Kernel.EnterTimestampProjection(view))
        {
            StringAssert.Contains(System.Text.Encoding.UTF8.GetString(await node.ReadFrozenAsync(current.Evidence.Version, new("vertex", vertex.Value), default, false)), "middle");
        }

        current.Dispose();
        Assert.AreEqual(0, node.Kernel.TimestampProtectionCount);
    }

}
