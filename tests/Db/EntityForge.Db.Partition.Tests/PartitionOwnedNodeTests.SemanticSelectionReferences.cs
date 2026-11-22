using dotnet_etcd;
using Etcdserverpb;
using Google.Protobuf;
using Grpc.Core;
using System.Text.Json;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [TestCategory("EtcdIntegration")]
    public async Task Semantic_selected_payload_references_survive_authority_reopen_replay_and_accounting()
    {
        string? endpoint = Environment.GetEnvironmentVariable("ENTITYFORGE_TEST_ETCD_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Assert.Inconclusive("Set ENTITYFORGE_TEST_ETCD_ENDPOINT.");
            return;
        }
        using var etcd = new EtcdClient(endpoint, configureChannelOptions: options => options.Credentials = ChannelCredentials.Insecure);
        string prefix = "/entityforge/semantic-reference/" + Guid.NewGuid().ToString("N") + "/";
        try
        {
            await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, file) =>
            {
                var transaction = remote.CreateTransaction();
                const int Entities = 64, PayloadCharacters = 128;
                for (int i = 0; i < Entities; i++)
                {
                    transaction.Save(new LoadingAuditItem(i.ToString(), new string('x', PayloadCharacters), i));
                }

                await transaction.CommitAsync();
                for (int i = 0; i < Entities; i++)
                {
                    var value = (await remote.ReadSemanticAsync<LoadingAuditItem>(i.ToString())).Value;
                    Assert.IsNotNull(value);
                    Assert.AreEqual(i, value.Number);
                    Assert.AreEqual(new string('x', PayloadCharacters), value.Name);
                }
                await client.CloseReadEpochAsync();
                var binding = (await file.ReadApplicationAsync(transaction.TransactionId))!;
                var plan = (await file.ReadPlanAsync(transaction.TransactionId))!;
                Assert.AreEqual(2, plan.Members.Count);
                var authority = await GrpcEtcdPartitionTransactionAuthority.OpenAsync(etcd, prefix, client.Generation,
                    DatabaseProfileId.Semantic, nodes[0].Routing);
                Assert.AreEqual(plan.Descriptor, (await authority.SelectCombinedAsync(binding, plan))!.Descriptor);
                var storedApplication = (await etcd.GetAsync(prefix + "applications/" + plan.Operation.ToString("N"))).Kvs.Single();
                var storedPlan = (await etcd.GetAsync(prefix + "plans/" + plan.Operation.ToString("N"))).Kvs.Single();
                Assert.AreEqual(storedApplication.ModRevision, storedPlan.ModRevision, "Selection must remain one atomic pair.");
                byte[] planBytes = PartitionSelectionStorage.Decode(storedPlan.Value.Span);
                Assert.IsTrue(PartitionSelectionCodec.IsReferencedPlan(planBytes));
                var usage = JsonSerializer.Deserialize<PartitionApplicationUsage>((await etcd.GetAsync(prefix + "application-usage")).Kvs.Single().Value.Span)!;
                Assert.AreEqual((long)planBytes.Length, usage.PlanBytes);
                Assert.AreEqual((long)PartitionSelectionStorage.Decode(storedApplication.Value.Span).Length, usage.Bytes);
                var reopened = await GrpcEtcdPartitionTransactionAuthority.OpenAsync(etcd, prefix, client.Generation,
                    DatabaseProfileId.Semantic, nodes[0].Routing);
                Assert.AreEqual(plan.Descriptor, (await reopened.ReadPlanAsync(plan.Operation))!.Descriptor);
                Assert.AreEqual(plan.Descriptor, (await reopened.SelectCombinedAsync(binding, plan))!.Descriptor);
                await reopened.BindAsync(plan);
                var terminal = (await reopened.ReadTerminalAsync([plan.Operation], default)).Single();
                Assert.AreEqual(plan.Descriptor, terminal.Plan!.Descriptor);
                Assert.AreEqual(binding.Descriptor, terminal.Application!.Descriptor);
                await reopened.DecideAsync(plan.Operation, plan.Descriptor, false);
                Assert.IsTrue((await reopened.ReadDecisionAsync(plan.Operation))!.Commit, "The original committed decision wins an opposite retry.");
                var after = JsonSerializer.Deserialize<PartitionApplicationUsage>((await etcd.GetAsync(prefix + "application-usage")).Kvs.Single().Value.Span)!;
                Assert.AreEqual(usage, after, "Exact replay must not reserve storage twice.");
                // A dangling reference must fail closed, never become an empty plan.
                await etcd.DeleteAsync(prefix + "applications/" + plan.Operation.ToString("N"));
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadPlanAsync(plan.Operation).AsTask());
                await etcd.PutAsync(new PutRequest { Key = storedApplication.Key, Value = storedApplication.Value });
                await PartitionRetention.RetainAsync(reopened, nodes, DateTimeOffset.UtcNow);
                var reclaimed = JsonSerializer.Deserialize<PartitionApplicationUsage>((await etcd.GetAsync(prefix + "application-usage")).Kvs.Single().Value.Span)!;
                Assert.AreEqual(new PartitionApplicationUsage(0, 0, 0, 0), reclaimed);
                await Assert.ThrowsExactlyAsync<EntityOperationExpiredException>(() => reopened.ReadPlanAsync(plan.Operation).AsTask());
            }, authorityFactory: async (generation, routing) =>
                await GrpcEtcdPartitionTransactionAuthority.OpenAsync(etcd, prefix, generation, DatabaseProfileId.Semantic, routing));
        }
        finally
        {
            await etcd.DeleteAsync(new DeleteRangeRequest
            {
                Key = ByteString.CopyFromUtf8(prefix),
                RangeEnd = ByteString.CopyFromUtf8(prefix[..^1] + "0")
            });
        }
    }

    [TestMethod]
    public async Task Semantic_sparse_contribution_indices_reject_forged_source_associations()
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, file) =>
        {
            var transaction = remote.CreateTransaction();
            const int Entities = 64;
            for (int i = 0; i < Entities; i++)
            {
                transaction.Save(new LoadingAuditItem(i.ToString(), "test", i));
            }

            var binding = await PartitionSemanticPlanning.SelectAsync(client.Generation, transaction.TransactionId,
                nodes[0].Routing, nodes, transaction.Capture(), default);
            var evidence = PartitionSelectionCodec.DecodeEvidence(binding.PlanningEvidence.Span);
            var envelope = PartitionSemanticEnvelopeCodec.Decode(evidence.Expansions[0].Envelope);
            var indices = envelope.ContributionIndices!;
            Assert.IsTrue(indices.Length > 1 && indices.Length < Entities);
            foreach (int[] invalid in new[] { new[] { -1 }, new[] { Entities }, new[] { indices[0], indices[0] }, indices.Reverse().ToArray() })
            {
                Assert.ThrowsExactly<InvalidDataException>(() => PartitionSemanticEnvelopeCodec.Decode(
                    PartitionSemanticEnvelopeCodec.Encode(envelope with
                    {
                        ContributionIndices = invalid
                    })));
            }

            var changed = indices.ToArray();
            // Valid sorted indices, but tied to the wrong captured source.
            int slot = Array.FindIndex(changed, index => index + 1 < Entities && !changed.Contains(index + 1));
            Assert.IsTrue(slot >= 0);
            changed[slot]++;
            var expansions = evidence.Expansions.ToArray();
            expansions[0] = expansions[0] with
            {
                Envelope = PartitionSemanticEnvelopeCodec.Encode(envelope with
                {
                    ContributionIndices = changed
                })
            };
            var forged = new PartitionApplicationBinding(binding.Operation, binding.Owners, binding.Requests,
                planningEvidence: PartitionSelectionCodec.EncodeEvidence(evidence with
                {
                    Expansions = expansions
                }));
            Assert.ThrowsExactly<InvalidDataException>(() => PartitionSemanticPlanning.ReadValidated(forged, nodes[0].Routing));
            Assert.IsNull(await file.ReadApplicationAsync(transaction.TransactionId));
            Assert.IsTrue(nodes.All(node => node.UnreleasedReadCutCount == 0));
        });
    }
}
