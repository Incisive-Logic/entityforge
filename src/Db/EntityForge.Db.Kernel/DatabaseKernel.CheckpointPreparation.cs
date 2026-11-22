using EntityForge.Db.Storage;
using EntityForge.Db.Durability;

namespace EntityForge.Db.Kernel;

public sealed partial class DatabaseKernel
{
    private async ValueTask PrepareCheckpointMetadataAsync(IDatabaseProfileCheckpointPreparation preparation,
        DatabaseVersion version, CancellationToken token)
    {
        if (_commitLog is not IPhysicalDatabaseCommitLog physicalLog)
        {
            throw new InvalidOperationException("Checkpoint metadata preparation requires physical redo.");
        }

        Guid id = Guid.NewGuid();
        bool submitted = false;
        BeginReaderPublication();
        try
        {
            var mutations = await preparation.PrepareCheckpointAsync(id, version, token).ConfigureAwait(false);
            if (mutations.Count != 0)
            {
                var envelope = new DurableTransactionEnvelope(_databaseIncarnation, version, id,
                    new(_profile.Profile, _profile.CodecId, _profile.CodecVersion, ReadOnlyMemory<byte>.Empty),
                    DurableCommittedChangeKind.CheckpointMaintenance, null, StorageLayout, VersioningMode);
                submitted = true;
                await physicalLog.CommitWithPhysicalMutationsAsync(envelope, mutations, token).ConfigureAwait(false);
            }
            await preparation.PublishCheckpointPreparationAsync(id, CancellationToken.None).ConfigureAwait(false);
            CompleteReaderPublication();
        }
        catch (Exception failure)
        {
            if (submitted)
            {
                RequireRecovery("checkpoint_metadata_preparation");
            }
            else
            {
                try
                {
                    await preparation.AbandonCheckpointPreparationAsync(id).ConfigureAwait(false);
                    CompleteReaderPublication();
                }
                catch (Exception cleanup)
                {
                    RequireRecovery("checkpoint_metadata_cleanup");
                    EntityFailureDiagnostics.Attach(failure, cleanup);
                }
            }
            throw;
        }
        finally
        {
            AbortReaderPublicationIfRequired();
        }
    }
}
