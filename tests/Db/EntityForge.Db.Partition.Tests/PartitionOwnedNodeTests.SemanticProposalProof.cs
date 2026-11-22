using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Durability;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public void Semantic_proposal_proof_owns_exact_bytes_and_rejects_changed_version_input_or_evidence()
    {
        var proof = new SemanticProposalProof();
        byte[] request = [1, 2], envelope = [3, 4];
        ReadOnlyMemory<byte>[] requests = [request];
        var version = new DatabaseVersion(12);
        Assert.IsFalse(proof.Matches(version, requests, envelope));
        proof.Remember(version, requests, envelope);
        Assert.IsTrue(proof.Matches(version, requests, envelope));
        Assert.IsFalse(proof.Matches(new(13), requests, envelope));
        Assert.IsFalse(proof.Matches(version, [new byte[] { 1 }, new byte[] { 2 }], envelope));
        request[0] = 9;
        Assert.IsFalse(proof.Matches(version, requests, envelope));
        request[0] = 1;
        envelope[0] = 9;
        Assert.IsFalse(proof.Matches(version, requests, envelope));
        envelope[0] = 3;
        Assert.IsTrue(proof.Matches(version, requests, envelope));
        proof.Remember(version, requests, new byte[SemanticProposalProof.MaximumRetainedBytes + 1]);
        Assert.IsFalse(proof.Matches(version, requests, new byte[SemanticProposalProof.MaximumRetainedBytes + 1]));
        proof.Remember(version, [new byte[] { 5 }], [6]);
        Assert.IsFalse(proof.Matches(version, requests, envelope));
        Assert.IsTrue(proof.Matches(version, [new byte[] { 5 }], [6]));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Semantic_proposal_proof_reuse_and_eviction_preserve_full_values_and_replay(bool evict)
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
        {
            const int Entities = 64;
            var transaction = remote.CreateTransaction();
            for (int i = 0; i < Entities; i++)
            {
                transaction.Save(new Item("proof-" + i, "complete-" + i));
            }

            var selected = await PartitionSemanticPlanning.SelectOwnerOrDistributedAsync(client.Generation,
                transaction.TransactionId, nodes[0].Routing, nodes, transaction.Capture(), default);
            Assert.AreEqual(2, selected.Owners.Count);
            await authority.BindApplicationAsync(selected);
            if (evict)
            {
                var other = remote.CreateTransaction();
                for (int i = 0; i < Entities; i++)
                {
                    other.Save(new Item("eviction-" + i, "not committed"));
                }

                _ = await PartitionSemanticPlanning.SelectOwnerOrDistributedAsync(client.Generation,
                    other.TransactionId, nodes[0].Routing, nodes, other.Capture(), default);
            }
            long reused = 0, missed = 0;
            var previous = DistributedReadTelemetry.ObserveWork;
            DistributedReadTelemetry.ObserveWork = (name, count) =>
            {
                previous?.Invoke(name, count);
                if (name == "partition.semantic-reconstruct.proposal-reused")
                {
                    Interlocked.Add(ref reused, count);
                }

                if (name == "partition.semantic-reconstruct.proposal-missed")
                {
                    Interlocked.Add(ref missed, count);
                }
            };
            try
            {
                await transaction.CommitAsync();
            }
            finally
            {
                DistributedReadTelemetry.ObserveWork = previous;
            }
            Assert.AreEqual(evict ? 0L : 2L, reused);
            Assert.AreEqual(evict ? 2L : 0L, missed);
            for (int i = 0; i < Entities; i++)
            {
                Assert.AreEqual("complete-" + i, (await remote.ReadSemanticAsync<Item>("proof-" + i)).Value!.Name);
            }

            var replay = await client.DeliverAsync(transaction.TransactionId, transaction.Capture());
            Assert.IsTrue(replay.Members.All(member => member.Receipt.Outcome == DatabaseCommitOutcome.IdempotentReplay));
        });
    }

    [TestMethod]
    public async Task Semantic_proposal_proof_rejects_reframed_modified_dependency_evidence_before_native_prepare()
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
        {
            var transaction = remote.CreateTransaction();
            for (int i = 0; i < 64; i++)
            {
                transaction.Save(new Item("tampered-proof-" + i, "value"));
            }

            var selected = await PartitionSemanticPlanning.SelectOwnerOrDistributedAsync(client.Generation,
                transaction.TransactionId, nodes[0].Routing, nodes, transaction.Capture(), default);
            var evidence = PartitionSemanticPlanning.ReadValidated(selected, nodes[0].Routing);
            var part = evidence.Expansions.Single(part => part.Owner == nodes[0].Domain.Identity);
            var contributions = part.Contributions.ToArray();
            string fingerprint = contributions[0].Read!.Fingerprint;
            contributions[0] = contributions[0] with
            {
                Read = contributions[0].Read! with
                {
                    Fingerprint = fingerprint[..^1] + (fingerprint[^1] == '0' ? '1' : '0')
                }
            };
            var altered = part.Envelope with
            {
                Expanded = new SemanticCommitCodec().Encode(
                DurableDatabaseCommit.ForSemanticTransaction(contributions))
            };
            var expansions = evidence.Evidence.Expansions.Select(item => item.Owner == part.Owner
                ? item with
                {
                    Envelope = PartitionSemanticEnvelopeCodec.Encode(altered)
                } : item).ToArray();
            var wrong = new PartitionApplicationBinding(selected.Operation, selected.Owners, selected.Requests,
                planningEvidence: PartitionSelectionCodec.EncodeEvidence(evidence.Evidence with
                {
                    Expansions = expansions
                }));
            await authority.BindApplicationAsync(wrong);
            var plan = PartitionSemanticPlanning.Plan(wrong, nodes[0].Routing, nodes.Select(node => node.Domain).ToArray());
            await authority.BindAsync(plan);
            long reused = 0, missed = 0;
            var previous = DistributedReadTelemetry.ObserveWork;
            DistributedReadTelemetry.ObserveWork = (name, count) =>
            {
                previous?.Invoke(name, count);
                if (name == "partition.semantic-reconstruct.proposal-reused")
                {
                    Interlocked.Add(ref reused, count);
                }

                if (name == "partition.semantic-reconstruct.proposal-missed")
                {
                    Interlocked.Add(ref missed, count);
                }
            };
            try
            {
                await Assert.ThrowsExactlyAsync<EntityForge.Db.Kernel.DatabasePartitionConflictException>(
                    () => nodes[0].PrepareNativeAsync(plan).AsTask());
            }
            finally
            {
                DistributedReadTelemetry.ObserveWork = previous;
            }
            Assert.AreEqual(0L, reused);
            Assert.AreEqual(1L, missed);
            Assert.AreEqual(0, (await nodes[0].Kernel.PartitionTransactions.ReadAsync()).Count);
            await authority.DecideAsync(plan.Operation, plan.Descriptor, false);
        });
    }
}
