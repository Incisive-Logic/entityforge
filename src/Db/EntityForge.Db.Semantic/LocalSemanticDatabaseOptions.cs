using EntityForge.Db.Storage;
using EntityForge.Values;

namespace EntityForge.Db.Semantic;

/// <summary>Configures an embedded semantic database that owns local durable storage.</summary>
public sealed class LocalSemanticDatabaseOptions
{
    internal DatabaseVersioningMode VersioningMode
    {
        get; init;
    }

    internal EntityForge.Db.Kernel.IDatabaseBoundedClock? TimestampClock
    {
        get; init;
    }

    /// <summary>Evaluators for persisted governance rules. Missing or ambiguous evaluators reject governed writes.</summary>
    public IReadOnlyList<ISemanticGovernanceEvaluator> GovernanceEvaluators { get; init; } = [];

    /// <summary>Default write-ahead log filename.</summary>
    public const string DefaultCommitLogFileName = "entityforge.wal";

    /// <summary>Default checkpoint filename.</summary>
    public const string DefaultCheckpointFileName = "entityforge.checkpoint";

    /// <summary>Default committed-change journal directory name.</summary>
    public const string DefaultChangeJournalDirectoryName = "entityforge.changes";

    /// <summary>Default database manifest filename.</summary>
    public const string DefaultManifestFileName = "entityforge.manifest";

    /// <summary>Default selected-format page filename.</summary>
    public const string DefaultPageFileName = "entityforge.pages";

    /// <summary>Initializes local semantic database options for an owned data directory.</summary>
    public LocalSemanticDatabaseOptions(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = dataDirectory;
    }

    /// <summary>Gets the directory that exclusively owns all database files.</summary>
    public string DataDirectory
    {
        get;
    }

    /// <summary>Immutable creation-time storage contract; reopening cannot change it.</summary>
    public DatabaseStorageLayout StorageLayout { get; init; } = DatabaseStorageLayout.Current;

    /// <summary>Gets or initializes the relative write-ahead log filename.</summary>
    public string CommitLogFileName { get; init; } = DefaultCommitLogFileName;

    /// <summary>Gets or initializes the relative checkpoint filename.</summary>
    public string CheckpointFileName { get; init; } = DefaultCheckpointFileName;

    /// <summary>Gets or initializes the relative committed-change journal directory name.</summary>
    public string ChangeJournalDirectoryName { get; init; } = DefaultChangeJournalDirectoryName;

    /// <summary>Gets or initializes the relative database manifest filename.</summary>
    public string ManifestFileName { get; init; } = DefaultManifestFileName;

    /// <summary>Gets or initializes the relative selected-format page filename.</summary>
    public string PageFileName { get; init; } = DefaultPageFileName;

    /// <summary>Gets or initializes committed-change journal retention limits.</summary>
    public DatabaseChangeJournalRetentionOptions ChangeJournalRetention { get; init; } = new();

    /// <summary>Gets or initializes admission limits for atomic structured JSON values.</summary>
    public JsonValueAdmissionPolicy JsonAdmissionPolicy { get; init; } = JsonValueAdmissionPolicy.Default;

    /// <summary>Gets or initializes limits for database-owned hidden contribution assembly.</summary>
    public SemanticContributionRevisionLimits ContributionRevisionLimits
    {
        get; init;
    }
    =
        SemanticContributionRevisionLimits.Default;
}
