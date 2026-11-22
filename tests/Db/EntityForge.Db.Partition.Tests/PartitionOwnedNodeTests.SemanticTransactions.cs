using EntityForge.Db.Semantic;
using EntityForge.Semantics;
using EntityForge.Db.Semantic.Durability;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(64)]
    public async Task Semantic_transaction_has_one_native_commit_per_owner_and_preserves_every_source(int count)
    {
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
        await using var authority = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.Semantic, a.Lease, routing, a, Options, transactionAuthority: authority, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.Semantic, b.Lease, routing, b, Options, transactionAuthority: authority, timestampClock: ReadClock);
        PartitionOwnedNode[] nodes = [first, second];
        var client = new PartitionApplicationClient(authority, routing, nodes, ReadClock);
        using var certificate = PartitionCertificate();
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint });
        await using var applicationService = new PartitionApplicationHttpService(client);
        await using var gateway = await StartApplicationHostAsync(applicationService, certificate);
        var remote = new HttpPartitionEntityClient(http, PartitionAddress(gateway), PartitionToken, client.Generation, DatabaseProfileId.Semantic);
        {
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
            var transaction = remote.CreateTransaction();
            foreach (int i in Enumerable.Range(0, count))
            {
                transaction.Save(new Item(i.ToString(), "transaction-" + i));
            }

            var requests = transaction.Capture();
            var operation = SemanticEntityRequest.Restore(requests[0].Span)._metadata.Operation;
            Assert.Throws<InvalidOperationException>(() => transaction.Save(new Item("late", "never")));
            await client.PrepareAsync(operation.OperationId, requests, default);
            var binding = (await authority.ReadApplicationAsync(operation.OperationId))!;
            var plan = PartitionSemanticPlanning.Plan(binding, nodes[0].Routing, nodes.Select(n => n.Domain).ToArray());
            var originals = new List<SemanticSessionEnvelope>();
            foreach (var request in requests)
            {
                originals.Add((await PartitionSemanticPlanning.OriginalTypedAsync(
                    SemanticEntityRequest.Restore(request.Span).ForDatabase(first.Domain.Incarnation))).Session);
            }

            foreach (var member in plan.Members)
            {
                var commit = ((ISemanticCommitDecoder)new SemanticCommitCodec()).Decode(DatabaseVersion.Initial, member.Payload.Span);
                var expected = originals.Where(original => ProfilePartitionRouting.SemanticHome(routing,
                    original.Start.Source.SourceSystem, original.Start.Source.SourcePartition) == member.Domain.Partition).ToArray();
                Assert.AreEqual(expected.Length, commit.Contributions.Count, "Only locally responsible sources belong in this native commit.");
                CollectionAssert.AreEquivalent(expected.Select(original => original.Start.Source.SourcePartition).ToArray(),
                    commit.Contributions.Select(contribution => contribution.Session.Start.Source.SourcePartition).ToArray());
                Assert.AreEqual(operation.OperationId, commit.TransactionId);
                var bytes = member.Payload.ToArray();
                var decoder = (ISemanticCommitDecoder)new SemanticCommitCodec();
                Assert.Throws<InvalidDataException>(() => decoder.Decode(DatabaseVersion.Initial, bytes.AsSpan(0, bytes.Length - 1)));
                Assert.Throws<InvalidDataException>(() => decoder.Decode(DatabaseVersion.Initial, bytes.Concat(new byte[] { 0 }).ToArray()));
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), DurableDatabaseCommit.MaximumTransactionContributions + 1);
                Assert.Throws<InvalidDataException>(() => decoder.Decode(DatabaseVersion.Initial, bytes));

            }
            var before = nodes.Select(n => n.Kernel.CurrentVersion).ToArray();
            var receipt = await transaction.CommitAsync();
            Assert.AreEqual(operation.OperationId, receipt.OperationId);
            for (int i = 0; i < nodes.Length; i++)
            {
                Assert.AreEqual(before[i].Value + (binding.Owners.Contains(nodes[i].Domain.Identity) ? 1 : 0), nodes[i].Kernel.CurrentVersion.Value);
            }

            foreach (int i in Enumerable.Range(0, count))
            {
                var found = await remote.ReadSemanticAsync<Item>(i.ToString());
                Assert.AreEqual("transaction-" + i, found.Value!.Name);
            }
            await gateway.StopAsync();
            await applicationService.DisposeAsync();
            await first.DisposeAsync();
            await second.DisposeAsync();
            await using var reopenedA = await PartitionOwnedNode.OpenAsync(Path.Combine(_root, "a"), DatabaseProfileId.Semantic,
                a.Lease, routing, a, Options, transactionAuthority: authority, timestampClock: ReadClock);
            await using var reopenedB = await PartitionOwnedNode.OpenAsync(Path.Combine(_root, "b"), DatabaseProfileId.Semantic,
                b.Lease, routing, b, Options, transactionAuthority: authority, timestampClock: ReadClock);
            foreach (var node in new[] { reopenedA, reopenedB })
            {
                var native = typeof(PartitionOwnedNode).GetField("_native", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(node)!;
                var database = (EntityForge.Db.Semantic.Engine.SemanticDatabaseEngine)native.GetType().GetProperty("Database",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(native)!;
                using var state = await database.AcquireCurrentDistributedStateAsync(default);
                foreach (var original in originals)
                {
                    var source = original.Start.Source;
                    Assert.AreEqual(ProfilePartitionRouting.SemanticHome(routing, source.SourceSystem, source.SourcePartition) == node.Domain.Partition,
                        state.State.SourceProgress.ContainsKey(new(source.SourceSystem, source.SourcePartition)),
                        "Reopen must preserve responsible progress without introducing progress at unrelated owners.");
                }
            }
            var reopened = new PartitionApplicationClient(authority, routing, [reopenedA, reopenedB], ReadClock);
            await using var reopenedService = new PartitionApplicationHttpService(reopened);
            await using var reopenedGateway = await StartApplicationHostAsync(reopenedService, certificate);
            var recovered = new HttpPartitionEntityClient(http, PartitionAddress(reopenedGateway), PartitionToken, client.Generation, DatabaseProfileId.Semantic);
            foreach (int i in Enumerable.Range(0, count))
            {
                Assert.AreEqual("transaction-" + i, (await recovered.ReadSemanticAsync<Item>(i.ToString())).Value!.Name);
            }

            var replay = await reopened.DeliverAsync(operation.OperationId, requests);
            Assert.IsTrue(replay.Members.All(m => m.Receipt.Outcome == DatabaseCommitOutcome.IdempotentReplay));
            var changed = requests.ToArray();
            changed[^1] = store.CaptureSave(new((count - 1).ToString(), "changed"), operation).Export();
            await Assert.ThrowsAsync<EntityForgeDbIdempotencyException>(() => reopened.DeliverAsync(operation.OperationId, changed).AsTask());
            var successor = SemanticEntityWriteOperation.Create();
            var guarded = new List<ReadOnlyMemory<byte>>();
            foreach (int i in Enumerable.Range(0, count))
            {
                var original = PartitionSemanticCodec.Original(await PartitionSemanticPlanning.OriginalAsync(
                    SemanticEntityRequest.Restore(requests[i].Span).ForDatabase(reopenedA.Domain.Incarnation)));
                guarded.Add(store.CaptureSave(new(i.ToString(), "guarded-" + i), successor,
                    SemanticSourceProgressPrecondition.After(original.Session.Start.Source.Position)).Export());
            }
            await reopened.DeliverAsync(successor.OperationId, guarded);
            foreach (int i in Enumerable.Range(0, count))
            {
                Assert.AreEqual("guarded-" + i, (await recovered.ReadSemanticAsync<Item>(i.ToString())).Value!.Name);
            }
        }
    }
}
