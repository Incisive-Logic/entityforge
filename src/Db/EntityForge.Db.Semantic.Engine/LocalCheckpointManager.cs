using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Semantic;
using EntityForge.Db.Telemetry;

namespace EntityForge.Db.Semantic.Engine;

internal sealed class LocalCheckpointManager
{
    private readonly ISemanticCommitLog _log;
    private readonly EntityForgeDbTelemetry _telemetry;

    public LocalCheckpointManager(ISemanticCommitLog log, EntityForgeDbTelemetry telemetry)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    }

    public async ValueTask<DatabaseVersion> CreateAsync(CancellationToken cancellationToken)
    {
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.checkpoint");
        try
        {
            DatabaseVersion version = await _log.CreateCheckpointAsync(cancellationToken).ConfigureAwait(false);
            _telemetry.RecordCheckpoint(version.Value, 0);
            return version;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            operation.RecordException(exception);
            if (exception is EntityForgeDbCheckpointException or EntityForgeSnapshotRetentionException)
            {
                throw;
            }

            throw new EntityForgeDbCheckpointException("The database checkpoint could not be created.", exception);
        }
    }
}
