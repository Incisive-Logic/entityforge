using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Hosting;
using EntityForge.Db.Semantic;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Complete_commands_cancellation_leaves_accepted_work_owned_by_pin_drainage(bool http)
    {
        await WithQueryHome(async (node, placements, home, decisions) =>
        {
            using var certificate = PartitionCertificate();
            await using var service = new PartitionParticipantHttpService(node);
            await using var host = await StartPartitionHostAsync(service, certificate);
            using var client = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
            });
            IPartitionReadParticipant peer = http ? new HttpPartitionTransactionParticipant(client, PartitionAddress(host),
                PartitionToken, node.Domain, node.TransactionAuthorityIdentity!) : node;
            var cut = await peer.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            using var listener = new System.Diagnostics.ActivityListener
            {
                ShouldListenTo = source => source.Name == DistributedReadTelemetry.SourceName,
                Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
                ActivityStarted = activity =>
                {
                    if (activity.OperationName != "partition.query.command")
                    {
                        return;
                    }

                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("Command release timed out.");
                    }
                }
            };
            System.Diagnostics.ActivitySource.AddActivityListener(listener);
            using var cancellation = new CancellationTokenSource();
            var command = new PartitionQueryJobCommand(Guid.NewGuid(), new("gql", "MATCH (n) RETURN n", GraphFrontier: new(0),
                GraphFragments: true), Finish: true, Home: home.Binding, Sequence: 1);
            var executing = Task.Run(async () => await peer.ReadQueryJobValueAsync(cut, new("query-command", QueryJob: command, ReadTime: TestQueryTime(cut)), cancellation.Token));
            Task? drainage = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => executing.WaitAsync(cancellation.Token));
                drainage = node.AbandonCutAsync(cut.Id).AsTask();
                Assert.IsFalse(drainage.IsCompleted, "Accepted command use must keep the native pin alive until drained.");
                release.Set();
                await drainage.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                release.Set();
                try
                {
                    await executing;
                }
                catch
                {

                }
                if (drainage is not null)
                {
                    await drainage;
                }
                else
                {
                    await node.AbandonCutAsync(cut.Id);
                }
            }
            Assert.AreEqual(0, node.ActiveQueryJobCount);
            Assert.AreEqual(0L, node.RetainedQueryWorkspaceBytes);
            await using var freeze = await node.Kernel.PartitionTransactions.FreezeAsync();
        });
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    [DataRow(DatabaseProfileId.Semantic, false)]
    [DataRow(DatabaseProfileId.Semantic, true)]
    public async Task Complete_commands_match_retained_jobs_and_drain_every_profile(DatabaseProfileId profile, bool http)
    {
        await WithQueryHome(async (node, placements, home, decisions) =>
        {
            PartitionQueryExchangeRequest selection;
            if (profile == DatabaseProfileId.PropertyGraph)
            {
                var operation = EntityOperationIds.New();
                await node.Kernel.CommitWithReceiptAsync(operation, GraphTransactionCodec.Encode(new(operation,
                    [new CreateVertex(new(Guid.NewGuid()), ["Item"])], node.Kernel.CurrentVersion, [])));
                selection = new("gql", "MATCH (n:Item) RETURN n", GraphFrontier: new(0), GraphFragments: true);
            }
            else if (profile == DatabaseProfileId.Rdf)
            {
                var adapter = await node.CreateRdfExecutionAsync();
                var capture = adapter.CaptureSave(new Item("one", "Ã©â€ºÂª"), EntityOperationIds.New());
                await adapter.ExecuteAsync(node.Domain, capture.OperationId, capture.Export());
                selection = new("sparql", "SELECT ?s ?p ?o WHERE { ?s ?p ?o }", ProfileFragments: true);
            }
            else
            {
                var adapter = await node.CreateSemanticExecutionAsync();
                var capture = adapter.CaptureSave(new Item("one", "Ã©â€ºÂª"), SemanticEntityWriteOperation.Create());
                await adapter.ExecuteAsync(node.Domain, capture.OperationId, capture.Export());
                using var manifestHttp = new HttpClient();
                var manifest = new HttpSemanticEntityClient(manifestHttp, node.Domain.Generation).EntityStore<Item>().Manifests[0];
                var property = manifest.ProfileBindings.Single(p => p.MemberIdentity == manifest.Members.Single(m => m.ClrName == "Name").Identity).StoredBinding;
                selection = new("entity", EntityQuery: new(manifest.ProfileTypeBinding,
                    new(manifest.ProfileTypeBinding, Fields: [new(property, "name")]), ConceptMatchMode: "direct"), ProfileFragments: true);
            }
            using var certificate = PartitionCertificate();
            await using var service = new PartitionParticipantHttpService(node);
            await using var host = await StartPartitionHostAsync(service, certificate);
            using var client = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
            });
            IPartitionReadParticipant peer = http ? new HttpPartitionTransactionParticipant(client, PartitionAddress(host),
                PartitionToken, node.Domain, node.TransactionAuthorityIdentity!) : node;
            var cut = await peer.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
            try
            {
                var original = await peer.ReadQueryJobValueAsync(cut, new("query-job", QueryJob:
                    new(Guid.NewGuid(), selection, Finish: true, Home: home.Binding, Sequence: 1), ReadTime: TestQueryTime(cut)));
                var command = new PartitionQueryJobCommand(Guid.NewGuid(), selection, Finish: true, Home: home.Binding, Sequence: 1);
                var result = await peer.ReadQueryJobValueAsync(cut, new("query-command", QueryJob: command, ReadTime: TestQueryTime(cut)));
                Assert.AreEqual(original.Value!.Element.GetRawText(), result.Value!.Element.GetRawText());
                Assert.IsTrue(result.Completed);
                Assert.IsNotNull(result.Pending);
                await home.ValidateAsync([result.Pending], default);
                Assert.AreEqual(0, node.ActiveQueryJobCount);
                Assert.AreEqual(0L, node.RetainedQueryWorkspaceBytes);
                // A lost read reply can be reissued without recreating an owner job.
                var replay = await peer.ReadQueryJobValueAsync(cut, new("query-command", QueryJob: command, ReadTime: TestQueryTime(cut)));
                Assert.AreEqual(result.Value.Element.GetRawText(), replay.Value!.Element.GetRawText());
                await placements.ReleaseAsync(node.CurrentLease);
                {
                    // A published read right remains valid; a new owner must wait.
                    await home.ValidateAsync([result.Pending], default);
                    await Assert.ThrowsExactlyAsync<ShardServingGrantActiveException>(() => placements.AcquireAsync(node.Domain.Partition,
                        new("replacement"), new("https://localhost:22004"), new(node.Domain.Generation), Routing.MapVersion,
                        TimeSpan.FromMinutes(1)).AsTask());
                }
            }
            finally
            {
                await peer.AbandonCutAsync(cut.Id);
            }
        }, profile: profile);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Complete_commands_preserve_full_graph_frontiers_and_paging(bool wide) =>
        await WithQueryHomePair(async (first, second, original, routing, decisions) =>
        {
            var client = new PartitionApplicationClient(decisions, routing, [first, second], ReadClock)
            {
                QueryHome = original.QueryHome,
                CompleteOwnerCommands = true
            };
            try
            {
                await AssertQueryFrontiersAsync(first, second, client, routing, wide, decisions);
            }
            finally
            {
                await client.CloseReadEpochAsync();
            }
            Assert.AreEqual(0, first.ActiveQueryJobCount);
            Assert.AreEqual(0, second.ActiveQueryJobCount);
            Assert.AreEqual(0L, first.RetainedQueryWorkspaceBytes);
            Assert.AreEqual(0L, second.RetainedQueryWorkspaceBytes);
        });

    [TestMethod]
    public async Task Complete_commands_preserve_remote_graph_frontiers_and_paging() =>
        await WithQueryHomePair(async (first, second, original, routing, decisions) =>
        {
            using var certificate = PartitionCertificate();
            await using var service = new PartitionParticipantHttpService(second);
            await using var host = await StartPartitionHostAsync(service, certificate);
            using var http = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
            });
            var remote = new HttpPartitionTransactionParticipant(http, PartitionAddress(host), PartitionToken, second.Domain, decisions.Identity);
            var client = new PartitionApplicationClient(decisions, routing, [first, remote], ReadClock)
            {
                QueryHome = original.QueryHome,
                CompleteOwnerCommands = true
            };
            try
            {
                await AssertQueryFrontiersAsync(first, second, client, routing, true, decisions);
            }
            finally
            {
                await client.CloseReadEpochAsync();
            }
            Assert.AreEqual(0, second.ActiveQueryJobCount);
            Assert.AreEqual(0L, second.RetainedQueryWorkspaceBytes);
        });

    [DataTestMethod]
    [DataRow("job")]
    [DataRow("cut")]
    [DataRow("selection")]
    [DataRow("sequence")]
    [DataRow("authority")]
    [DataRow("missing")]
    [DataRow("cleanup")]
    public async Task Complete_commands_reject_corrupt_or_missing_completion(string fault)
    {
        await WithQueryHome(async (node, placements, home, decisions) =>
        {
            var peer = new QueryHomeFaultPeer(node, result =>
            {
                if (result.Pending is not { } pending)
                {
                    return result;
                }

                if (fault == "cleanup")
                {
                    throw new IOException("Injected lost completion response.");
                }

                return result with
                {
                    Pending = fault switch
                    {
                        "job" => pending with { Job = Guid.NewGuid() },
                        "cut" => pending with { Cut = pending.Cut with { Id = Guid.NewGuid() } },
                        "selection" => pending with { Selection = "changed" },
                        "sequence" => pending with { Sequence = pending.Sequence + 1 },
                        "authority" => pending with { Authority = pending.Authority with { Cluster = pending.Authority.Cluster + 1 } },
                        _ => null
                    }
                };
            });
            await Assert.ThrowsAsync<Exception>(async () => await PartitionRemoteRead.ExecuteAsync([peer], Routing,
                async (read, token) =>
                {
                    read.UseAsEpoch();
                    var query = read.BeginQuery(home, true);
                    try
                    {
                        await query.ReadValueAsync("a", new("gql", "MATCH (n) RETURN n", GraphFrontier: new(0), GraphFragments: true), true, token);
                        await query.CompleteAsync(false);
                    }
                    catch
                    {
                        await query.CompleteAsync(true);
                        throw;
                    }
                    return true;
                }, useSnapshots: true));
            Assert.AreEqual(0, node.ActiveQueryJobCount);
            Assert.AreEqual(0L, node.RetainedQueryWorkspaceBytes);
        });
    }

    [TestMethod]
    public async Task Complete_commands_reject_stale_cut_and_invalid_capability()
    {
        await WithQueryHome(async (node, placements, home, decisions) =>
        {
            var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
            var selection = new PartitionQueryExchangeRequest("gql", "MATCH (n) RETURN n", GraphFrontier: new(0), GraphFragments: true);
            var command = new PartitionQueryJobCommand(Guid.NewGuid(), selection, Finish: true, Home: home.Binding, Sequence: 1);
            try
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadQueryJobValueAsync(cut,
                    new("query-command", QueryJob: command with
                    {
                        Home = home.Binding with
                        {
                            Namespace = "/wrong/"
                        }
                    }, ReadTime: TestQueryTime(cut))).AsTask());
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadQueryJobValueAsync(cut,
                    new("query-command", QueryJob: command with
                    {
                        Finish = false
                    }, ReadTime: TestQueryTime(cut))).AsTask());
                var operation = EntityOperationIds.New();
                await node.Kernel.CommitWithReceiptAsync(operation, GraphTransactionCodec.Encode(new(operation,
                    [new CreateVertex(new(Guid.NewGuid()), ["Item"])], node.Kernel.CurrentVersion, [])));
                var result = await node.ReadQueryJobValueAsync(cut, new("query-command", QueryJob: command, ReadTime: TestQueryTime(cut)));
                Assert.IsTrue(result.Valid);
                Assert.IsTrue(result.Completed);
                Assert.IsNotNull(result.Value);
                Assert.IsNotNull(result.Pending);
                Assert.AreEqual(0, node.ActiveQueryJobCount);
                Assert.AreEqual(0L, node.RetainedQueryWorkspaceBytes);
            }
            finally
            {
                await node.AbandonCutAsync(cut.Id);
            }
            await Assert.ThrowsAsync<Exception>(() => node.ReadQueryJobValueAsync(cut, new("query-command", QueryJob: command, ReadTime: TestQueryTime(cut))).AsTask());
        });
    }
}
