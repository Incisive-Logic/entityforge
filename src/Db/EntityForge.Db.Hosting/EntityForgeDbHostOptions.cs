using EntityForge.Db.Gql;
using EntityForge.Db.Storage.Local;
using EntityForge.Db;
using EntityForge.Values;
using EntityForge.Db.Storage;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Hosting;

/// <summary>Configures files, limits, profile selection and retention for an owned local server store.</summary>
public sealed class EntityForgeDbHostOptions
{
    /// <summary>Resource limits for GQL and SPARQL scan endpoints.</summary>
    public QueryScanOptions QueryScans { get; set; } = new();

    /// <summary>Optional exact-URL allowlist for bounded RDF SPARQL LOAD operations.</summary>
    public EntityForge.Db.Rdf.RdfSparqlHttpResources? RdfHttpResources
    {
        get; set;
    }

    /// <summary>Capacity and idle expiry of RDF query continuations.</summary>
    public EntityForge.Db.Rdf.RdfQueryPagingOptions RdfQueryPaging { get; set; } = new();

    /// <summary>Gets or sets immutable named read and write procedures available to GQL requests.</summary>
    public GqlProcedureCatalog? GqlProcedures
    {
        get; set;
    }

    /// <summary>Gets or sets the immutable catalog of additional local property graphs available to GQL.</summary>
    public GqlLocalGraphCatalog? GqlGraphs
    {
        get; set;
    }

    /// <summary>Gets or sets the optional directory owning the persistent managed GQL catalog and its graph stores.</summary>
    /// <remarks>Requires the property-graph profile and a configured <see cref="GqlGraphs"/> default graph.
    /// The directory must not overlap any configured store. Startup validates all cataloged store identities before becoming ready.</remarks>
    public string? ManagedGqlDirectory
    {
        get; set;
    }

    /// <summary>Default database-owned directory.</summary>
    public const string DefaultDataDirectory = "data";

    /// <summary>Default write-ahead log filename.</summary>
    public const string DefaultCommitLogFileName = "entityforge.wal";

    /// <summary>Default checkpoint filename.</summary>
    public const string DefaultCheckpointFileName = "entityforge.checkpoint";

    /// <summary>Default committed-change journal filename.</summary>
    public const string DefaultChangeJournalFileName = "entityforge.changes";

    /// <summary>Default database manifest filename.</summary>
    public const string DefaultManifestFileName = "entityforge.manifest";

    /// <summary>Default selected-format page filename.</summary>
    public const string DefaultPageFileName = "entityforge.pages";

    /// <summary>Default maximum HTTP request body size in bytes.</summary>
    public const long DefaultMaximumRequestBodyBytes = 64L * 1024L * 1024L;

    /// <summary>Default automatic checkpoint interval in minutes.</summary>
    public const int DefaultCheckpointIntervalMinutes = 5;

    /// <summary>Gets or sets the directory that exclusively owns all configured database files.</summary>
    public string DataDirectory { get; set; } = DefaultDataDirectory;

    /// <summary>Gets or sets the relative write-ahead log filename.</summary>
    public string CommitLogFileName { get; set; } = DefaultCommitLogFileName;

    /// <summary>Gets or sets the relative checkpoint filename.</summary>
    public string CheckpointFileName { get; set; } = DefaultCheckpointFileName;

    /// <summary>Gets or sets the relative committed-change journal filename.</summary>
    public string ChangeJournalFileName { get; set; } = DefaultChangeJournalFileName;

    /// <summary>Gets or sets the relative database manifest filename.</summary>
    public string ManifestFileName { get; set; } = DefaultManifestFileName;

    /// <summary>Gets or sets the relative selected-format page filename.</summary>
    public string PageFileName { get; set; } = DefaultPageFileName;

    /// <summary>Gets or sets the maximum accepted HTTP request body size in bytes.</summary>
    public long MaximumRequestBodyBytes { get; set; } = DefaultMaximumRequestBodyBytes;

    /// <summary>Gets or sets the periodic checkpoint interval.</summary>
    public TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromMinutes(DefaultCheckpointIntervalMinutes);

    /// <summary>Gets or sets the single database profile hosted by the server.</summary>
    public DatabaseProfileId Profile { get; set; } = DatabaseProfileId.Semantic;

    /// <summary>Gets or sets admission limits for atomic JSON values.</summary>
    public JsonValueAdmissionPolicy JsonAdmissionPolicy { get; set; } = JsonValueAdmissionPolicy.Default;

    /// <summary>Gets or sets limits for hidden Semantic contribution assembly.</summary>
    public SemanticContributionRevisionLimits ContributionRevisionLimits
    {
        get; set;
    }
    =
        SemanticContributionRevisionLimits.Default;

    /// <summary>Gets or sets committed-change journal retention.</summary>
    public DatabaseChangeJournalRetentionOptions ChangeJournalRetention { get; set; } = new();

    /// <summary>Gets or sets limits shared by checkpoint and backup administration jobs.</summary>
    public EntityForgeDbAdministrationOptions Administration { get; set; } = new();

    /// <summary>Gets or sets the directory beneath which named backups may be created.</summary>
    public string BackupDirectory { get; set; } = "backups";

    /// <summary>Gets or sets archive-before-reclaim configuration; null preserves operational history policy.</summary>
    public HistoricalEvidenceArchiveOptions? HistoricalArchive
    {
        get; set;
    }

    /// <summary>Gets or sets the optional directory containing the published EntityForge DB Studio browser assets.</summary>
    public string? StudioAssetsDirectory
    {
        get; set;
    }

    internal EntityForgeDbHostOptions Snapshot()
    {
        ArgumentNullException.ThrowIfNull(QueryScans);
        QueryScans.Validate();
        ArgumentNullException.ThrowIfNull(RdfQueryPaging);
        RdfQueryPaging.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(DataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(CommitLogFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(CheckpointFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ChangeJournalFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ManifestFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(PageFileName);
        if (MaximumRequestBodyBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRequestBodyBytes));
        }

        if (CheckpointInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CheckpointInterval));
        }

        ArgumentNullException.ThrowIfNull(JsonAdmissionPolicy);
        ArgumentNullException.ThrowIfNull(ContributionRevisionLimits);
        ArgumentNullException.ThrowIfNull(ChangeJournalRetention);
        ArgumentNullException.ThrowIfNull(Administration);
        ArgumentException.ThrowIfNullOrWhiteSpace(BackupDirectory);
        if (!Enum.IsDefined(Profile))
        {
            throw new ArgumentOutOfRangeException(nameof(Profile));
        }

        string[] databaseFileNames =
        [
            ValidateOwnedFileName(CommitLogFileName),
            ValidateOwnedFileName(CheckpointFileName),
            ValidateOwnedFileName(ChangeJournalFileName),
            ValidateOwnedFileName(ManifestFileName),
            ValidateOwnedFileName(PageFileName)
        ];
        if (GqlGraphs is not null)
        {
            if (Profile != DatabaseProfileId.PropertyGraph || HistoricalArchive is not null)
            {
                throw new ArgumentException("A GQL graph catalog requires the property-graph profile without historical archive configuration.");
            }
            GqlGraphs.ValidateDefaultDirectory(DataDirectory);
        }
        if (ManagedGqlDirectory is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ManagedGqlDirectory);
            if (GqlGraphs is null)
            {
                throw new ArgumentException("Managed GQL storage requires a configured default graph.");
            }
            GqlLocalGraphCatalog.ValidateDirectories(GqlGraphs.StoreDirectories.Values
                .Append(Path.GetFullPath(DataDirectory)).Append(Path.GetFullPath(ManagedGqlDirectory)));
        }
        if (databaseFileNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != databaseFileNames.Length)
        {
            throw new ArgumentException("Every database artifact filename must be distinct.");
        }
        if (databaseFileNames.Any(name => name.Equals(LocalDatabaseRetirement.FileName, StringComparison.OrdinalIgnoreCase) ||
            name.Equals(LocalDatabaseRetirement.FileName + ".new", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Database artifact filenames cannot use reserved retirement metadata names.");
        }

        return new()
        {
            RdfHttpResources = RdfHttpResources,
            RdfQueryPaging = RdfQueryPaging,
            QueryScans = QueryScans,
            GqlProcedures = GqlProcedures,
            GqlGraphs = GqlGraphs,
            ManagedGqlDirectory = ManagedGqlDirectory is null ? null : Path.GetFullPath(ManagedGqlDirectory),
            DataDirectory = Path.GetFullPath(DataDirectory),
            CommitLogFileName = CommitLogFileName,
            CheckpointFileName = CheckpointFileName,
            ChangeJournalFileName = ChangeJournalFileName,
            ManifestFileName = ManifestFileName,
            PageFileName = PageFileName,
            MaximumRequestBodyBytes = MaximumRequestBodyBytes,
            CheckpointInterval = CheckpointInterval,
            Profile = Profile,
            JsonAdmissionPolicy = JsonAdmissionPolicy,
            ContributionRevisionLimits = ContributionRevisionLimits,
            ChangeJournalRetention = ChangeJournalRetention,
            Administration = Administration,
            BackupDirectory = Path.GetFullPath(BackupDirectory),
            HistoricalArchive = HistoricalArchive,
            StudioAssetsDirectory = StudioAssetsDirectory is null ? null : ValidateStudioAssets(StudioAssetsDirectory)
        };
    }

    private static string ValidateStudioAssets(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string directory = Path.GetFullPath(value);
        if (!Directory.Exists(directory) || !File.Exists(Path.Combine(directory, "index.html")))
        {
            throw new ArgumentException("The Studio assets directory must exist and contain index.html.", nameof(value));
        }

        return directory;
    }

    private static string ValidateOwnedFileName(string value)
    {
        if (Path.IsPathRooted(value) || !String.Equals(value, Path.GetFileName(value), StringComparison.Ordinal) ||
            value is "." or "..")
        {
            throw new ArgumentException("Database filenames must be simple names beneath the owned data directory.");
        }

        return value;
    }
}
