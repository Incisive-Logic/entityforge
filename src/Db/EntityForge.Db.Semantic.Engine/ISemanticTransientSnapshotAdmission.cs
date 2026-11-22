namespace EntityForge.Db.Semantic.Engine;

/// <summary>Retains a semantic read version without creating a local database commit.</summary>
internal interface ISemanticTransientSnapshotAdmission
{
    bool RetainsRecentDistributedVersions
    {
        get;
    }

    ValueTask<SemanticDistributedState> CaptureCurrentStateAsync(
        Func<DatabaseVersion, IDisposable, SemanticDistributedState> capture, CancellationToken cancellationToken);

    ValueTask<IDisposable> RetainTransientSnapshotAsync(DatabaseVersion version,
        CancellationToken cancellationToken = default);
}
