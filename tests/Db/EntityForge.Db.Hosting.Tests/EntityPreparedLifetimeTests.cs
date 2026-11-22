using System.Text;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class EntityPreparedLifetimeTests
{
    [TestMethod]
    public async Task Disposal_waits_for_delivery_and_failed_release_remains_retryable()
    {
        object owner = new();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int releases = 0;
        var lifetime = new EntityPreparedLifetime<int>(owner, async _ =>
        {
            entered.SetResult();
            await finish.Task;
            return 42;
        }, () => ++releases == 1 ? ValueTask.FromException(new IOException("release reply lost")) : ValueTask.CompletedTask);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await lifetime.DeliverAsync(new(), default));
        var delivery = lifetime.DeliverAsync(owner, default).AsTask();
        await entered.Task;
        var disposal = lifetime.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted);
        Assert.AreEqual(0, releases);
        finish.SetResult();
        Assert.AreEqual(42, await delivery);
        await Assert.ThrowsExceptionAsync<IOException>(async () => await disposal);
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () => await lifetime.DeliverAsync(owner, default));
        await lifetime.DisposeAsync();
        await lifetime.DisposeAsync();
        Assert.AreEqual(2, releases);
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public void Preparation_tokens_require_complete_exact_destination_and_identity(DatabaseProfileId profile)
    {
        var handle = new EntityPreparedHandle(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        byte[] token = EntityPreparationToken.Encode(handle, profile);
        Assert.AreEqual(handle, EntityPreparationToken.Decode(token, handle.DatabaseIncarnation, handle.OperationId, profile));
        Assert.ThrowsException<InvalidDataException>(() => EntityPreparationToken.Decode(token, Guid.NewGuid(), handle.OperationId, profile));
        Assert.ThrowsException<InvalidDataException>(() => EntityPreparationToken.Decode(token, handle.DatabaseIncarnation, Guid.NewGuid(), profile));
        var other = profile == DatabaseProfileId.Rdf ? DatabaseProfileId.Semantic : DatabaseProfileId.Rdf;
        Assert.ThrowsException<InvalidDataException>(() => EntityPreparationToken.Decode(token, handle.DatabaseIncarnation, handle.OperationId, other));
        string framing = Encoding.UTF8.GetString(token);
        foreach (string malformed in new[]
        {
            framing.Replace("\"version\":1", "\"version\":1,\"version\":1"),
            framing.Replace($"\"id\":\"{handle.Id}\"", "\"id\":null"),
            framing.Replace($"\"id\":\"{handle.Id}\"", "\"unknown\":1"),
            framing.Replace($"\"id\":\"{handle.Id}\"", $"\"id\":\"{Guid.Empty}\""),
            framing[..^1],
        })
        {
            Assert.ThrowsException<InvalidDataException>(() => EntityPreparationToken.Decode(Encoding.UTF8.GetBytes(malformed),
                handle.DatabaseIncarnation, handle.OperationId, profile));
        }
    }

    [TestMethod]
    public void Authoritative_receipts_do_not_invent_native_effect_counts_or_readability()
    {
        var id = Guid.NewGuid();
        var receipt = new DatabaseCommitReceipt(Guid.NewGuid(), id, DatabaseProfileId.PropertyGraph,
            new(12), DatabaseCommitOutcome.Committed, null, null);
        var pending = new EntityOperationResolution<DatabaseCommitReceipt>(id, true, receipt, readable: false);
        Assert.AreEqual(EntityOperationState.CommittedPendingReadability, pending.State);
        Assert.AreSame(receipt, pending.Receipt);
        Assert.AreEqual(EntityOperationState.Committed,
            new EntityOperationResolution<DatabaseCommitReceipt>(id, true, receipt).State);
        Assert.ThrowsException<ArgumentException>(() => new DatabaseCommitReceipt(receipt.DatabaseIncarnation,
            id, receipt.Profile, receipt.Version, DatabaseCommitOutcome.Committed, null, 0));
    }
}
