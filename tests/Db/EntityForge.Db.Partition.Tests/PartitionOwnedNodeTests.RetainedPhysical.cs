using EntityForge.Db.Kernel;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, "commit")]
    [DataRow(DatabaseProfileId.Rdf, "commit")]
    [DataRow(DatabaseProfileId.PropertyGraph, "intervening")]
    [DataRow(DatabaseProfileId.Rdf, "intervening")]
    [DataRow(DatabaseProfileId.PropertyGraph, "checkpoint")]
    [DataRow(DatabaseProfileId.Rdf, "checkpoint")]
    [DataRow(DatabaseProfileId.PropertyGraph, "abort")]
    [DataRow(DatabaseProfileId.Rdf, "abort")]
    [DataRow(DatabaseProfileId.PropertyGraph, "reopen")]
    [DataRow(DatabaseProfileId.Rdf, "reopen")]
    public async Task Retained_physical_preparation_reuses_only_the_unchanged_predecessor(DatabaseProfileId profile, string action)
    {
        string path = Path.Combine(_root, "retained-physical");
        var opened = await OpenReservationStore(profile, path);
        Guid[] subjects = [Guid.NewGuid(), Guid.NewGuid()];
        Guid seed = Guid.NewGuid(), operation = Guid.NewGuid();
        try
        {
            var kernel = opened.Kernel;
            await kernel.CommitAsync(seed, ReservationPayload(profile, seed, profile == DatabaseProfileId.PropertyGraph ? [subjects[1]] : subjects, null, create: true));
            var version = kernel.CurrentVersion;
            byte[] payload = profile == DatabaseProfileId.PropertyGraph
                ? GraphTransactionCodec.Encode(new(operation, [new CreateVertex(new(subjects[0]), []),
                    new SetVertexProperty(new(subjects[0]), "name", new DatabaseValue.String("changed"))],
                    ReadVersion: version, ReadPredicates: [new WholeVertexRead(new(subjects[0]))]))
                : ReservationPayload(profile, operation, [subjects[0]], version);
            await kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, version, payload);
            Assert.IsTrue(kernel.HasRetainedPhysicalPreparation);
            Assert.AreEqual(version, kernel.CurrentVersion);
            if (action == "intervening")
            {
                Guid other = Guid.NewGuid();
                await kernel.CommitAsync(other, ReservationPayload(profile, other, [subjects[1]], null));
            }
            else if (action == "checkpoint")
            {
                await kernel.PartitionTransactions.CheckpointForTransferAsync();
            }
            else if (action == "reopen")
            {
                await opened.Database.DisposeAsync();
                opened = await OpenReservationStore(profile, path);
                kernel = opened.Kernel;
            }
            if (action is "intervening" or "checkpoint" or "reopen")
            {
                Assert.IsFalse(kernel.HasRetainedPhysicalPreparation);
            }

            await kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, action != "abort");
            Assert.IsFalse(kernel.HasRetainedPhysicalPreparation);
            Assert.AreEqual(action == "commit" ? 1L : 0L, kernel.RetainedPhysicalPreparationHits);
            if (action != "abort")
            {
                var receipt = await kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, true);
                Assert.AreEqual(kernel.CurrentVersion, receipt!.Version);
                Assert.AreEqual(action == "commit" ? 1L : 0L, kernel.RetainedPhysicalPreparationHits, "Replay cannot republish a retained stage.");
            }
            if (opened.Database is IGraphDatabase graph)
            {
                if (action == "abort")
                {
                    Assert.IsNull(graph.GetVertex(new(subjects[0])));
                }
                else
                {
                    Assert.AreEqual(new DatabaseValue.String("changed"), graph.GetVertex(new(subjects[0]))!.Properties["name"]);
                }
            }
            else
            {
                var rows = ((RdfDatabaseEngine)opened.Database).Find(new RdfQuadPattern(Subject: new RdfIri("urn:reservation:" + subjects[0].ToString("N"))));
                Assert.AreEqual(1, rows.Count);
                Assert.AreEqual(new RdfLiteral(action == "abort" ? "seed" : "changed"), rows[0].Object);
            }
        }
        finally
        {
            await opened.Database.DisposeAsync();
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, RedoDurabilityStage.BeforeWalWrite)]
    [DataRow(DatabaseProfileId.Rdf, RedoDurabilityStage.BeforeWalWrite)]
    [DataRow(DatabaseProfileId.PropertyGraph, RedoDurabilityStage.AfterWalFlush)]
    [DataRow(DatabaseProfileId.Rdf, RedoDurabilityStage.AfterWalFlush)]
    public async Task Retained_physical_preparation_preserves_WAL_failure_recovery(DatabaseProfileId profile, RedoDurabilityStage boundary)
    {
        string path = Path.Combine(_root, "retained-fault");
        var observer = new RetainedPreparationFault();
        Guid seed = Guid.NewGuid(), operation = Guid.NewGuid(), subject = Guid.NewGuid();
        var opened = await OpenCohortStore(profile, path, observer);
        await using (opened.Database)
        {
            await opened.Kernel.CommitAsync(seed, ReservationPayload(profile, seed, [profile == DatabaseProfileId.Rdf ? subject : Guid.NewGuid()], null, true));
            var version = opened.Kernel.CurrentVersion;
            await opened.Kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, version,
                ReservationPayload(profile, operation, [subject], version, profile == DatabaseProfileId.PropertyGraph));
            Assert.IsTrue(opened.Kernel.HasRetainedPhysicalPreparation);
            observer.Boundary = boundary;
            await Assert.ThrowsAsync<Exception>(() => opened.Kernel.PartitionTransactions.CompleteAsync(
                operation, ReservationDescriptor, true).AsTask());
            Assert.IsFalse(opened.Kernel.HasRetainedPhysicalPreparation);
            Assert.AreEqual(DatabaseKernelState.RecoveryRequired, opened.Kernel.State);
        }
        opened = await OpenReservationStore(profile, path);
        await using (opened.Database)
        {
            Assert.AreEqual(boundary == RedoDurabilityStage.AfterWalFlush ? 2L : 1L, opened.Kernel.CurrentVersion.Value);
            await opened.Kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, true);
            Assert.AreEqual(2L, opened.Kernel.CurrentVersion.Value);
            Assert.AreEqual(0L, opened.Kernel.RetainedPhysicalPreparationHits, "Recovery rebuilds from durable evidence.");
            if (opened.Database is IGraphDatabase graph)
            {
                Assert.IsNotNull(graph.GetVertex(new(subject)));
            }
            else
            {
                Assert.AreEqual(1, ((RdfDatabaseEngine)opened.Database).Find(new RdfQuadPattern(
                Subject: new RdfIri("urn:reservation:" + subject.ToString("N")))).Count);
            }
        }
    }

    private sealed class RetainedPreparationFault : IRedoDurabilityObserver
    {
        internal RedoDurabilityStage? Boundary;

        public void Reached(RedoDurabilityStage stage)
        {
            if (Boundary != stage)
            {
                return;
            }

            Boundary = null;
            throw new IOException("Injected retained preparation WAL boundary failure.");
        }
    }

    [TestMethod]
    public async Task Oversized_physical_preview_falls_back_without_changing_the_complete_value()
    {
        string path = Path.Combine(_root, "retained-bound");
        var opened = await OpenReservationStore(DatabaseProfileId.PropertyGraph, path);
        await using var database = opened.Database;
        Guid operation = Guid.NewGuid(), subject = Guid.NewGuid();
        var value = new string('x', checked((int)DatabaseKernel.MaximumRetainedPhysicalPreparationBytes));
        byte[] payload = GraphTransactionCodec.Encode(new(operation,
            [new CreateVertex(new(subject), []), new SetVertexProperty(new(subject), "large", new DatabaseValue.String(value))],
            ReadVersion: opened.Kernel.CurrentVersion, ReadPredicates: [new WholeVertexRead(new(subject))]));
        await opened.Kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, opened.Kernel.CurrentVersion, payload);
        Assert.IsFalse(opened.Kernel.HasRetainedPhysicalPreparation);
        await opened.Kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, true);
        Assert.AreEqual(0L, opened.Kernel.RetainedPhysicalPreparationHits);
        Assert.AreEqual(new DatabaseValue.String(value), ((IGraphDatabase)database).GetVertex(new(subject))!.Properties["large"]);
    }

}
