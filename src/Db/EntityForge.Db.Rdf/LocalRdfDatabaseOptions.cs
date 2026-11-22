using EntityForge.Db.Storage;

namespace EntityForge.Db.Rdf;

/// <summary>Configures an embedded RDF database that owns local durable storage.</summary>
public sealed class LocalRdfDatabaseOptions
{
    /// <summary>Optional exact-URL allowlist for bounded SPARQL LOAD requests. Null disables remote loading.</summary>
    public RdfSparqlHttpResources? HttpResources
    {
        get; init;
    }

    /// <summary>Limits and idle expiry for retained RDF query continuations.</summary>
    public RdfQueryPagingOptions QueryPaging { get; init; } = new();

    /// <summary>Limits initial bulk-load input capture and deduplication work.</summary>
    public RdfBulkLoadOptions BulkLoad { get; init; } = new();

    internal DatabaseVersioningMode VersioningMode
    {
        get; init;
    }

    internal EntityForge.Db.Kernel.IDatabaseBoundedClock? TimestampClock
    {
        get; init;
    }

    /// <summary>Default WAL filename.</summary>
    public const string DefaultCommitLogFileName = "entityforge.wal";

    /// <summary>Default checkpoint filename.</summary>
    public const string DefaultCheckpointFileName = "entityforge.checkpoint";

    /// <summary>Default change-journal directory.</summary>
    public const string DefaultChangeJournalDirectoryName = "entityforge.changes";

    /// <summary>Default format-manifest filename.</summary>
    public const string DefaultManifestFileName = "entityforge.manifest";

    /// <summary>Default physical-page filename.</summary>
    public const string DefaultPageFileName = "entityforge.pages";

    /// <summary>Creates options for an owned local directory.</summary>
    public LocalRdfDatabaseOptions(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = dataDirectory;
    }

    /// <summary>Gets the owned data directory.</summary>
    public string DataDirectory
    {
        get;
    }

    /// <summary>Immutable creation-time storage contract; reopening cannot change it.</summary>
    public DatabaseStorageLayout StorageLayout { get; init; } = DatabaseStorageLayout.Current;

    /// <summary>Gets or initializes the WAL filename.</summary>
    public string CommitLogFileName { get; init; } = DefaultCommitLogFileName;

    /// <summary>Gets or initializes the checkpoint filename.</summary>
    public string CheckpointFileName { get; init; } = DefaultCheckpointFileName;

    /// <summary>Gets or initializes the change-journal directory.</summary>
    public string ChangeJournalDirectoryName { get; init; } = DefaultChangeJournalDirectoryName;

    /// <summary>Gets or initializes the manifest filename.</summary>
    public string ManifestFileName { get; init; } = DefaultManifestFileName;

    /// <summary>Gets or initializes the physical-page filename.</summary>
    public string PageFileName { get; init; } = DefaultPageFileName;

    /// <summary>Gets or initializes change-journal retention.</summary>
    public DatabaseChangeJournalRetentionOptions ChangeJournalRetention { get; init; } = new();
}
