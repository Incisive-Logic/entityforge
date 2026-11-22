namespace EntityForge.Db.Graph;

/// <summary>Controls operation identity and optional version concurrency for one mapped Graph write.</summary>
public sealed record GraphEntityWriteOptions
{
    /// <summary>Creates explicit mapped Graph write options.</summary>
    public GraphEntityWriteOptions(Guid operationId, DatabaseVersion? expectedVersion = null)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A write operation identity is required.", nameof(operationId));
        }

        OperationId = operationId;
        ExpectedVersion = expectedVersion;
    }

    /// <summary>Creates options with a fresh operation identity.</summary>
    public static GraphEntityWriteOptions Create(DatabaseVersion? expectedVersion = null) =>
        new(EntityOperationIds.New(), expectedVersion);

    /// <summary>Gets this operation identity. Exact retry requires retaining the prepared command, not just this identity.</summary>
    public Guid OperationId
    {
        get;
    }

    /// <summary>Gets the optional version at which the caller observed mapped state.</summary>
    public DatabaseVersion? ExpectedVersion
    {
        get;
    }
}
