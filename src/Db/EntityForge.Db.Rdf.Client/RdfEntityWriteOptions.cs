namespace EntityForge.Db.Rdf;

/// <summary>Controls operation identity and RDF subject-state concurrency for one mapped RDF write.</summary>
public sealed record RdfEntityWriteOptions
{
    /// <summary>Creates explicit mapped RDF write options.</summary>
    /// <param name="operationId">Nonempty identity retained with the captured request for retries.</param>
    /// <param name="expectedVersion">Version from a prior mapped read, supplied together with its subject-state token.</param>
    /// <param name="expectedSubjectStateToken">Opaque token from that same read, or null for an unconditional write.</param>
    /// <exception cref="ArgumentException">The operation identity is empty, or only one component of the concurrency guard is supplied.</exception>
    /// <remarks>Both guard components must come from the same observation. Prefer <see cref="FromRead{T}"/> to assembling the guard manually.</remarks>
    public RdfEntityWriteOptions(Guid operationId, DatabaseVersion? expectedVersion = null,
        string? expectedSubjectStateToken = null)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A write operation identity is required.", nameof(operationId));
        }

        if (expectedVersion.HasValue != (expectedSubjectStateToken is not null))
        {
            throw new ArgumentException(
                "An RDF conditional write requires both the observed version and subject-state token.");
        }

        OperationId = operationId;
        ExpectedVersion = expectedVersion;
        ExpectedSubjectStateToken = expectedSubjectStateToken;
    }

    /// <summary>Creates options with a fresh operation identity.</summary>
    /// <param name="expectedVersion">Observed version, or null when no concurrency guard is required.</param>
    /// <param name="expectedSubjectStateToken">Matching observed subject-state token; it must be supplied exactly when a version is supplied.</param>
    /// <returns>Options for a new logical operation.</returns>
    /// <remarks>Calling this again creates a new operation rather than retrying an existing one.</remarks>
    public static RdfEntityWriteOptions Create(DatabaseVersion? expectedVersion = null,
        string? expectedSubjectStateToken = null) =>
        new(EntityOperationIds.New(), expectedVersion, expectedSubjectStateToken);

    /// <summary>Creates a safe conditional write from one mapped RDF read result.</summary>
    /// <typeparam name="T">Type of the previously materialized entity.</typeparam>
    /// <param name="read">RDF read result supplying both the snapshot version and subject-state token.</param>
    /// <param name="operationId">Identity for the new operation, or null to generate one.</param>
    /// <returns>Options carrying the complete observed subject-state guard.</returns>
    /// <exception cref="ArgumentNullException">The read result is null.</exception>
    /// <exception cref="ArgumentException">The read has no subject-state token, or the supplied operation identity is empty.</exception>
    public static RdfEntityWriteOptions FromRead<T>(EntityReadResult<T> read,
        Guid? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (read.ConcurrencyToken is null)
        {
            throw new ArgumentException("The read result does not contain an RDF subject-state token.", nameof(read));
        }

        return new(operationId ?? EntityOperationIds.New(), read.SnapshotVersion, read.ConcurrencyToken);
    }

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

    /// <summary>Gets the optional opaque token for the previously observed RDF subject state.</summary>
    public string? ExpectedSubjectStateToken
    {
        get;
    }
}
