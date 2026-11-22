using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Semantic;

internal sealed partial class SemanticDurabilityRuntime
{
    private bool _sourceMetadataComplete;
    private Guid? _sourceMetadataPreparation;
    private bool _sourceMetadataStage;

    public async ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PrepareCheckpointAsync(
        Guid id, DatabaseVersion version, CancellationToken token)
    {
        if (_sourceMetadataComplete)
        {
            return [];
        }
        // One complete import for an old checkpoint or an unmarked initial image. The
        // compact checkpoint published afterwards is the durable coverage declaration.
        var checkpoint = await Participant().CaptureCheckpointAsync(token).ConfigureAwait(false);
        if (checkpoint.Version != version)
        {
            throw new InvalidDataException("Metadata import version differs from checkpoint.");
        }

        var existing = await _physicalWriter.ReadSourceMetadataAsync(version.Value, token).ConfigureAwait(false);
        var oldMemberships = existing.Memberships.ToDictionary(item => (item.SourceSystem, item.SourceRecord, item.Subject));
        var newMemberships = checkpoint.SourceMemberships.ToDictionary(item => (item.SourceSystem, item.SourceRecord, item.Subject));
        var oldProgress = existing.Progress.ToDictionary(item => (item.SourceSystem, item.SourcePartition));
        var newProgress = checkpoint.SourceProgress.ToDictionary(item => (item.SourceSystem, item.SourcePartition));
        var changes = new SemanticSourceMetadataChanges(
            oldMemberships.Keys.Union(newMemberships.Keys).Select(key =>
                new SemanticMetadataReplacement<CheckpointSourceMembership>(oldMemberships.GetValueOrDefault(key),
                    newMemberships.GetValueOrDefault(key))).Where(item => item.Before != item.After).ToArray(),
            oldProgress.Keys.Union(newProgress.Keys).Select(key =>
                new SemanticMetadataReplacement<CheckpointSourcePartitionProgress>(oldProgress.GetValueOrDefault(key),
                    newProgress.GetValueOrDefault(key))).Where(item => item.Before != item.After).ToArray());
        token.ThrowIfCancellationRequested();
        var stage = await _physicalWriter.PrepareAsync(id, version.Value, new([]), checkpoint.PropertyStorage,
            token, sourceMetadata: changes).ConfigureAwait(false);
        _sourceMetadataPreparation = id;
        _sourceMetadataStage = stage is not null;
        return stage?.PhysicalMutations ?? [];
    }

    public async ValueTask PublishCheckpointPreparationAsync(Guid id, CancellationToken token)
    {
        if (_sourceMetadataComplete)
        {
            return;
        }

        if (_sourceMetadataPreparation != id)
        {
            throw new InvalidOperationException("Metadata import was not prepared.");
        }

        if (_sourceMetadataStage)
        {
            await _physicalWriter.PersistAsync(id, token).ConfigureAwait(false);
            _physicalWriter.Publish(id);
        }
        _sourceMetadataPreparation = null;
        _sourceMetadataComplete = true;
    }

    public ValueTask AbandonCheckpointPreparationAsync(Guid id)
    {
        if (_sourceMetadataPreparation == id)
        {
            if (_sourceMetadataStage)
            {
                _physicalWriter.Abandon(id);
            }

            _sourceMetadataPreparation = null;
        }
        return ValueTask.CompletedTask;
    }

    private async ValueTask<SemanticCheckpoint> CaptureKeyedCheckpointAsync(CancellationToken token)
    {
        if (!_sourceMetadataComplete)
        {
            throw new InvalidOperationException("Source metadata must be durably prepared before compact checkpoint capture.");
        }

        var checkpoint = await Participant().CaptureCheckpointAsync(false, token).ConfigureAwait(false);
        var counts = checkpoint.SourceMetadataReference ??
            throw new InvalidOperationException("Participant does not support compact source metadata capture.");
        return checkpoint with
        {
            SourceMetadataReference = _physicalWriter.SourceMetadataReference(
            checkpoint.Version, counts.MembershipCount, counts.ProgressCount)
        };
    }
}
