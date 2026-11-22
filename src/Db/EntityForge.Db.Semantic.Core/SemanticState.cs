using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Core;

/// <summary>Records the content identity and commit version of an applied semantic session.</summary>
/// <param name="ContentHash">The session content hash used to recognize exact retries.</param>
/// <param name="Version">The database version at which the session was applied.</param>
public sealed record AppliedSession(string ContentHash, DatabaseVersion Version);

/// <summary>Identifies a partition within a source system.</summary>
/// <param name="SourceSystem">The absolute source-system identifier.</param>
/// <param name="SourcePartition">The source-local partition identifier.</param>
public sealed record SourcePartitionKey(Uri SourceSystem, Uri SourcePartition);

/// <summary>Records the latest accepted source position and optional ownership coverage at a database version.</summary>
/// <param name="GenerationId">The source contribution generation identifier.</param>
/// <param name="SourceVersion">The source-defined version token.</param>
/// <param name="SourcePosition">The source-defined position token.</param>
/// <param name="Version">The database version recording this progress.</param>
/// <param name="Coverage">Optional proof of local source-owner coverage.</param>
public sealed record SourcePartitionProgress(Guid GenerationId, string SourceVersion,
    string SourcePosition, DatabaseVersion Version, SemanticOwnerCoverage? Coverage = null);

/// <summary>Captures immutable semantic evidence, catalogs, replay state and source ownership at one version.</summary>
/// <param name="Version">The database version of this state.</param>
/// <param name="Fields">The retained field assertions, including their visibility intervals.</param>
/// <param name="Relationships">The retained relationship assertions, including their visibility intervals.</param>
/// <param name="PropertyStorage">The versioned physical property-storage bindings.</param>
/// <param name="Governance">The versioned governance rule catalog.</param>
/// <param name="SnapshotLeases">The retained snapshot leases protecting historical versions.</param>
/// <param name="ResumableWrites">The durable resumable-write transactions indexed by identity.</param>
/// <param name="AppliedSessions">The session content hashes and versions retained for replay recognition.</param>
/// <param name="SourceMemberships">The immutable source ownership and partition membership index.</param>
/// <param name="SourceProgress">The last accepted progress for each source partition.</param>
public sealed record SemanticDatabaseState(
    DatabaseVersion Version,
    IReadOnlyList<SemanticFieldEvidence> Fields,
    IReadOnlyList<SemanticRelationshipEvidence> Relationships,
    PropertyStorageCatalogState PropertyStorage,
    GovernanceCatalogState Governance,
    SnapshotLeaseState SnapshotLeases,
    IReadOnlyDictionary<Guid, ResumableWriteTransaction> ResumableWrites,
    IReadOnlyDictionary<Guid, AppliedSession> AppliedSessions,
    SourceMembershipIndex SourceMemberships,
    IReadOnlyDictionary<SourcePartitionKey, SourcePartitionProgress> SourceProgress)
{
    private const long InitialDatabaseVersion = 0;

    /// <summary>Gets initial semantic state without assertions, catalogs, sessions or source memberships.</summary>
    public static SemanticDatabaseState Empty
    {
        get;
    }
    = new(new(InitialDatabaseVersion),
        ImmutableList<SemanticFieldEvidence>.Empty, ImmutableList<SemanticRelationshipEvidence>.Empty,
        PropertyStorageCatalogState.Empty,
        GovernanceCatalogState.Empty,
        SnapshotLeaseState.Empty,
        new Dictionary<Guid, ResumableWriteTransaction>(),
        ImmutableDictionary<Guid, AppliedSession>.Empty,
        SourceMembershipIndex.Empty,
        ImmutableDictionary<SourcePartitionKey, SourcePartitionProgress>.Empty);

    /// <summary>Gets or initializes the assertions opened or closed by the latest publication.</summary>
    public SemanticStatePublicationDelta PublicationDelta
    {
        get; init;
    }
    =
        SemanticStatePublicationDelta.Empty;

    /// <summary>Gets or initializes source metadata changes associated with this publication.</summary>
    public SemanticSourceMetadataChanges SourceMetadataChanges
    {
        get; init;
    }
    =
        SemanticSourceMetadataChanges.Empty;

    /// <summary>Gets or initializes current field and relationship assertion counts.</summary>
    public SemanticAssertionStatistics AssertionStatistics
    {
        get; init;
    }
    =
        SemanticAssertionStatistics.Empty;

    /// <summary>Gets or initializes bounded query-planning statistics for this version.</summary>
    public SemanticQueryStatistics QueryStatistics
    {
        get; init;
    }
    =
        SemanticQueryStatistics.Empty;

    /// <summary>Gets or initializes the semantic term, model and vocabulary catalog.</summary>
    public SemanticCatalogSnapshot Catalog
    {
        get; init;
    }
    = new(
        Array.Empty<SemanticModelReference>(), Array.Empty<SemanticTerm>(),
        Array.Empty<SemanticIdentityScheme>());
}

/// <summary>Maintains versioned cardinalities and bounded value-frequency sketches for query planning.</summary>
/// <param name="Version">The database version represented by these statistics.</param>
/// <param name="ConceptCardinalities">The tracked assertion contributions by concept.</param>
/// <param name="PropertyCardinalities">The current assertion counts by property.</param>
/// <param name="RelationshipCardinalities">The current assertion counts by relationship term.</param>
/// <param name="PropertyValueFrequencies">The bounded per-property value-frequency sketches.</param>
public sealed record SemanticQueryStatistics(
    DatabaseVersion Version,
    IReadOnlyDictionary<SemanticTerm, long> ConceptCardinalities,
    IReadOnlyDictionary<SemanticTerm, long> PropertyCardinalities,
    IReadOnlyDictionary<SemanticTerm, long> RelationshipCardinalities,
    IReadOnlyDictionary<SemanticTerm, SemanticValueFrequency> PropertyValueFrequencies)
{
    /// <summary>The maximum distinct value fingerprints tracked per property, 32.</summary>
    public const int MaximumTrackedValuesPerProperty = 32;

    /// <summary>Gets empty statistics at the initial version.</summary>
    public static SemanticQueryStatistics Empty
    {
        get;
    }
    = new(default,
        ImmutableDictionary<SemanticTerm, long>.Empty,
        ImmutableDictionary<SemanticTerm, long>.Empty,
        ImmutableDictionary<SemanticTerm, long>.Empty,
        ImmutableDictionary<SemanticTerm, SemanticValueFrequency>.Empty);

    /// <summary>Returns a tracked occurrence estimate or null when the sketch cannot establish one; lexical sketches do not prove native URI, binary or typed-literal equality.</summary>
    public long? EstimateValue(SemanticTerm property, SemanticValue value)
    {
        // Native URI equality can equate different lexical fingerprints. A lexical
        // sketch cannot certify absence or count native-equal candidates.
        if (value is BinarySemanticValue or AbsoluteUriSemanticValue or TypedLiteralSemanticValue)
        {
            return null;
        }

        if (!PropertyValueFrequencies.TryGetValue(property, out SemanticValueFrequency? frequency))
        {
            return null;
        }

        if (frequency.TrackedValues.TryGetValue(SemanticValueFingerprint.Create(value), out long count))
        {
            return count;
        }

        return frequency.OverflowOccurrences == 0 ? 0 : null;
    }

    /// <summary>Applies assertion openings and closures from a publication delta, rejecting negative counts.</summary>
    public SemanticQueryStatistics Apply(
        SemanticStatePublicationDelta delta, DatabaseVersion publication)
    {
        var concepts = ConceptCardinalities.ToImmutableDictionary().ToBuilder();
        var properties = PropertyCardinalities.ToImmutableDictionary().ToBuilder();
        var relationshipTerms = RelationshipCardinalities.ToImmutableDictionary().ToBuilder();
        var values = new Dictionary<SemanticTerm, MutableSemanticValueFrequency>();
        var fingerprints = new Dictionary<SemanticValue, string>();
        foreach (SemanticFieldEvidence item in delta.Fields)
        {
            int change = Change(item.BeginVersion, item.EndVersion, publication);
            Change(properties, item.Field, change);
            Change(concepts, item.Subject.Concept, change);
            Change(values, PropertyValueFrequencies, item.Field, item.Value, change, fingerprints);
        }
        foreach (SemanticRelationshipEvidence item in delta.Relationships)
        {
            int change = Change(item.BeginVersion, item.EndVersion, publication);
            Change(relationshipTerms, item.Relationship, change);
            Change(concepts, item.Subject.Concept, change);
            Change(concepts, item.Target.Concept, change);
        }
        var frequencies = PropertyValueFrequencies.ToImmutableDictionary().ToBuilder();
        foreach (var item in values)
        {
            frequencies[item.Key] = new(item.Value.Values.ToImmutableDictionary(StringComparer.Ordinal), item.Value.Overflow);
        }

        return new(publication, concepts.ToImmutable(), properties.ToImmutable(),
            relationshipTerms.ToImmutable(), frequencies.ToImmutable());
    }

    private static int Change(DatabaseVersion begin, DatabaseVersion? end,
        DatabaseVersion publication) => begin == publication ? end is null ? 1 : 0
        : end == publication ? -1 : 0;

    private static void Change(ImmutableDictionary<SemanticTerm, long>.Builder counts, SemanticTerm term, int delta)
    {
        if (delta == 0)
        {
            return;
        }

        long value = counts.GetValueOrDefault(term) + delta;
        if (value < 0)
        {
            throw new InvalidDataException("Semantic query statistics cannot become negative.");
        }

        if (value == 0)
        {
            counts.Remove(term);
        }
        else
        {
            counts[term] = value;
        }
    }

    private static void Change(Dictionary<SemanticTerm, MutableSemanticValueFrequency> frequencies,
        IReadOnlyDictionary<SemanticTerm, SemanticValueFrequency> prior,
        SemanticTerm property, SemanticValue value, int delta,
        Dictionary<SemanticValue, string> fingerprints)
    {
        if (delta == 0)
        {
            return;
        }

        if (!frequencies.TryGetValue(property, out MutableSemanticValueFrequency? frequency))
        {
            if (prior.TryGetValue(property, out var previous))
            {
                frequency = new(previous.TrackedValues.ToDictionary(StringComparer.Ordinal), previous.OverflowOccurrences);
            }
            else
            {
                if (delta < 0)
                {
                    throw new InvalidDataException("A semantic value statistic became negative.");
                }

                frequency = new();
            }
            frequencies.Add(property, frequency);
        }
        if (!fingerprints.TryGetValue(value, out string? fingerprint))
        {
            fingerprint = SemanticValueFingerprint.Create(value);
            fingerprints.Add(value, fingerprint);
        }
        if (frequency.Values.TryGetValue(fingerprint, out long current))
        {
            long changed = current + delta;
            if (changed < 0)
            {
                throw new InvalidDataException("A tracked semantic value statistic became negative.");
            }

            frequency.Values[fingerprint] = changed;
        }
        else if (delta > 0 && frequency.Values.Count < MaximumTrackedValuesPerProperty)
        {
            frequency.Values.Add(fingerprint, delta);
        }
        else
        {
            frequency.Overflow += delta;
            if (frequency.Overflow < 0)
            {
                throw new InvalidDataException("A semantic value-statistics overflow became negative.");
            }
        }
    }

    private sealed class MutableSemanticValueFrequency
    {
        public MutableSemanticValueFrequency() : this(new(StringComparer.Ordinal), 0)
        {
        }

        public MutableSemanticValueFrequency(Dictionary<string, long> values, long overflow)
        {
            Values = values;
            Overflow = overflow;
        }

        public Dictionary<string, long> Values
        {
            get;
        }

        public long Overflow
        {
            get; set;
        }
    }
}

/// <summary>Records bounded value fingerprint counts and occurrences outside the tracked set.</summary>
/// <param name="TrackedValues">The occurrence count for each tracked value fingerprint.</param>
/// <param name="OverflowOccurrences">The occurrences represented outside the bounded tracked-value set.</param>
public sealed record SemanticValueFrequency(
    IReadOnlyDictionary<string, long> TrackedValues,
    long OverflowOccurrences);

/// <summary>Counts current field and relationship assertions.</summary>
/// <param name="FieldCount">The number of current field assertions.</param>
/// <param name="RelationshipCount">The number of current relationship assertions.</param>
public sealed record SemanticAssertionStatistics(long FieldCount, long RelationshipCount)
{
    /// <summary>Gets zero field and relationship counts.</summary>
    public static SemanticAssertionStatistics Empty { get; } = new(0, 0);

    /// <summary>Applies publication openings and closures to assertion counts, rejecting negative totals.</summary>
    public SemanticAssertionStatistics Apply(SemanticStatePublicationDelta delta,
        DatabaseVersion publication)
    {
        long fields = FieldCount + delta.Fields.Sum(item => Change(
            item.BeginVersion, item.EndVersion, publication));
        long relationships = RelationshipCount + delta.Relationships.Sum(item => Change(
            item.BeginVersion, item.EndVersion, publication));
        if (fields < 0 || relationships < 0)
        {
            throw new InvalidDataException("Semantic assertion statistics cannot become negative.");
        }

        return new(fields, relationships);
    }

    private static int Change(DatabaseVersion begin, DatabaseVersion? end,
        DatabaseVersion publication) => begin == publication ? end is null ? 1 : 0
        : end == publication ? -1 : 0;
}

/// <summary>Carries assertions whose visibility changed in one publication.</summary>
/// <param name="Fields">Field evidence opened or closed by the publication.</param>
/// <param name="Relationships">Relationship evidence opened or closed by the publication.</param>
public sealed record SemanticStatePublicationDelta(
    IReadOnlyList<SemanticFieldEvidence> Fields,
    IReadOnlyList<SemanticRelationshipEvidence> Relationships)
{
    /// <summary>Gets a delta with no assertion changes.</summary>
    public static SemanticStatePublicationDelta Empty
    {
        get;
    }
    =
        new(Array.Empty<SemanticFieldEvidence>(), Array.Empty<SemanticRelationshipEvidence>());
}

/// <summary>Maintains the active durable snapshot leases.</summary>
/// <param name="Active">The leases indexed by their stable identifiers.</param>
public sealed record SnapshotLeaseState(IReadOnlyDictionary<Guid, SnapshotLease> Active)
{
    private const long InitialLeaseRevision = 1;
    private const long LeaseRevisionIncrement = 1;

    /// <summary>Gets state without active snapshot leases.</summary>
    public static SnapshotLeaseState Empty { get; } = new(new Dictionary<Guid, SnapshotLease>());

    /// <summary>Applies a contiguous acquire, renew or release mutation, rejecting inconsistent lease identity, version or revision.</summary>
    public SnapshotLeaseState Apply(SnapshotLeaseChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.LeaseId == Guid.Empty)
        {
            throw new InvalidDataException("A snapshot lease ID cannot be empty.");
        }

        if (!Enum.IsDefined(change.Kind))
        {
            throw new InvalidDataException($"Unsupported snapshot lease change kind '{change.Kind}'.");
        }

        var leases = new Dictionary<Guid, SnapshotLease>(Active);
        if (change.Kind is SnapshotLeaseChangeKind.Acquire)
        {
            if (leases.ContainsKey(change.LeaseId) || change.Revision != InitialLeaseRevision)
            {
                throw new InvalidDataException("Snapshot lease acquisition is not valid.");
            }

            leases.Add(change.LeaseId, new(change.LeaseId, change.SnapshotVersion,
                change.ExpiresAtUtc, change.Revision));
        }
        else
        {
            if (!leases.TryGetValue(change.LeaseId, out SnapshotLease? existing) ||
                change.Revision != existing.Revision + LeaseRevisionIncrement ||
                change.SnapshotVersion != existing.SnapshotVersion)
            {
                throw new InvalidDataException("Snapshot lease mutation is not contiguous.");
            }

            if (change.Kind is SnapshotLeaseChangeKind.Renew)
            {
                leases[change.LeaseId] = existing with
                {
                    ExpiresAtUtc = change.ExpiresAtUtc,
                    Revision = change.Revision
                };
            }
            else
            {
                leases.Remove(change.LeaseId);
            }
        }
        return new(leases);
    }

    /// <summary>Returns the oldest version protected by an unexpired lease, or the current version when none remains.</summary>
    public DatabaseVersion RetentionFloor(DatabaseVersion current, DateTimeOffset now) =>
        Active.Values.Where(lease => lease.ExpiresAtUtc > now)
            .Select(lease => lease.SnapshotVersion)
            .DefaultIfEmpty(current).MinBy(version => version.Value);
}

/// <summary>Maintains versioned semantic governance rules by stable identifier.</summary>
/// <param name="Version">The governance catalog revision.</param>
/// <param name="Rules">The current rules indexed by their absolute identifiers.</param>
public sealed record GovernanceCatalogState(
    GovernanceCatalogVersion Version,
    IReadOnlyDictionary<Uri, SemanticGovernanceRule> Rules)
{
    private const long InitialCatalogVersion = 0;
    private const long CatalogVersionIncrement = 1;

    /// <summary>Gets the initial empty governance catalog.</summary>
    public static GovernanceCatalogState Empty
    {
        get;
    }
    = new(new(InitialCatalogVersion),
        new Dictionary<Uri, SemanticGovernanceRule>());

    /// <summary>Applies the next catalog revision after validating rule identity, kind and scope.</summary>
    public GovernanceCatalogState Apply(SemanticGovernanceRuleChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Validate(change.Identifier, change.DefinitionReference, change.Revision);
        if (!Enum.IsDefined(change.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(change), change.Kind, "Unsupported governance rule kind.");
        }

        if (Rules.TryGetValue(change.Identifier, out SemanticGovernanceRule? existing) &&
            existing.Kind != change.Kind)
        {
            throw new InvalidDataException("A governance rule's stable identifier cannot change kind.");
        }

        ValidateScope(change.Scope);
        if (change.CatalogVersion.Value != Version.Value + CatalogVersionIncrement)
        {
            throw new InvalidDataException("Governance catalog versions are not contiguous.");
        }

        var rules = new Dictionary<Uri, SemanticGovernanceRule>(Rules)
        {
            [change.Identifier] = new(change.Identifier, change.Kind, change.DefinitionReference,
                change.Revision, change.Enabled, change.CatalogVersion, change.Scope)
        };
        return new(change.CatalogVersion, rules);
    }

    private static void ValidateScope(SemanticGovernanceScope? scope)
    {
        if (scope is null)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(scope.ApplicableConcepts);
        if (!Enum.IsDefined(scope.Enforcement))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope.Enforcement,
                "Unsupported governance enforcement mode.");
        }

        if (scope.ApplicableConcepts.Any(term => term.Role is not SemanticTermRole.Concept))
        {
            throw new ArgumentException("Governance applicability can contain only concept terms.", nameof(scope));
        }
    }

    /// <summary>Requires absolute rule and definition identifiers and a nonblank revision.</summary>
    public static void Validate(Uri identifier, Uri definitionReference, string revision)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(definitionReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        if (!identifier.IsAbsoluteUri)
        {
            throw new ArgumentException("A governance rule identifier must be absolute.", nameof(identifier));
        }

        if (!definitionReference.IsAbsoluteUri)
        {
            throw new ArgumentException("A governance definition reference must be absolute.", nameof(definitionReference));
        }
    }
}

/// <summary>Maintains versioned physical storage choices for semantic properties.</summary>
/// <param name="Version">The property-storage catalog version.</param>
/// <param name="Storage">The explicit property storage bindings.</param>
public sealed record PropertyStorageCatalogState(
    SemanticPropertyStorageCatalogVersion Version,
    IReadOnlyDictionary<SemanticTerm, SemanticPropertyStorage> Storage)
{
    private const long InitialCatalogVersion = 0;
    private const long CatalogVersionIncrement = 1;

    /// <summary>Gets the initial catalog without explicit property bindings.</summary>
    public static PropertyStorageCatalogState Empty
    {
        get;
    }
    = new(new(InitialCatalogVersion),
        new Dictionary<SemanticTerm, SemanticPropertyStorage>());

    /// <summary>Returns the explicit property binding, or an automatically managed cold-JSON binding when absent.</summary>
    public SemanticPropertyStorage Resolve(SemanticTerm property) =>
        Storage.TryGetValue(property, out SemanticPropertyStorage? storage)
            ? storage
            : new(property, SemanticPropertyStorageKind.ColdJson, Version,
                SemanticPropertyStorageManagement.Automatic);

    /// <summary>Applies a contiguous property-storage transition only when its previous storage kind and management mode match.</summary>
    public PropertyStorageCatalogState Apply(SemanticPropertyStorageChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Property.Role is not SemanticTermRole.Property)
        {
            throw new ArgumentException("Only semantic properties can have physical bindings.", nameof(change));
        }

        if (change.CatalogVersion.Value != Version.Value + CatalogVersionIncrement)
        {
            throw new InvalidDataException("Property catalog versions are not contiguous.");
        }

        SemanticPropertyStorage current = Resolve(change.Property);
        if (current.StorageKind != change.PreviousStorageKind ||
            current.Management != change.PreviousManagement)
        {
            throw new InvalidDataException("The property storage transition does not match the current binding.");
        }

        var storage = new Dictionary<SemanticTerm, SemanticPropertyStorage>(Storage)
        {
            [change.Property] = new(change.Property, change.StorageKind, change.CatalogVersion,
                change.Management)
        };
        return new(change.CatalogVersion, storage);
    }
}

/// <summary>Identifies one source record's contribution to a semantic subject.</summary>
/// <param name="Subject">The subject to which the source contributes evidence.</param>
/// <param name="SourceSystem">The source-system identifier.</param>
/// <param name="SourceRecord">The source record's stable identity.</param>
public sealed record SourceOwner(SemanticEntityIdentity Subject, Uri SourceSystem, string SourceRecord);

/// <summary>Orders source owners by source system, record and semantic subject.</summary>
public sealed class SourceOwnerComparer : IComparer<SourceOwner>
{
    /// <summary>Gets the shared source-owner comparer.</summary>
    public static SourceOwnerComparer Instance { get; } = new();

    /// <summary>Compares source system and record ordinally, then semantic identity; null sorts first.</summary>
    public int Compare(SourceOwner? left, SourceOwner? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        int result = StringComparer.Ordinal.Compare(left.SourceSystem.AbsoluteUri,
            right.SourceSystem.AbsoluteUri);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(left.SourceRecord, right.SourceRecord);
        if (result != 0)
        {
            return result;
        }

        return SemanticEntityIdentityOrder.Instance.Compare(left.Subject, right.Subject);
    }
}

/// <summary>Identifies the source-owned field or whole-subject contribution touched by a write.</summary>
/// <param name="Owner">The source record and subject whose evidence is affected.</param>
/// <param name="Field">The affected field, or null for a whole-owner dependency.</param>
public sealed record SemanticWriteDependency(SourceOwner Owner, SemanticTerm? Field)
{
    /// <summary>Checks overlapping source ownership and field dependencies.</summary>
    public bool ConflictsWith(SemanticWriteDependency other) =>
        Owner == other.Owner && (Field is null || other.Field is null || Field == other.Field);
}

/// <summary>Captures source-owned write dependencies from semantic changes.</summary>
public sealed class SemanticDependencyTracker
{
    /// <summary>Derives source-owner and field write dependencies for the supplied changes.</summary>
    public IReadOnlyList<SemanticWriteDependency> Capture(IEnumerable<SemanticEntityChange> changes) =>
        changes.SelectMany(Capture).Distinct().ToArray();

    private static IEnumerable<SemanticWriteDependency> Capture(SemanticEntityChange change)
    {
        var owner = new SourceOwner(change.Subject, change.Provenance.SourceSystem, change.Provenance.SourceRecord);
        if (change.Kind is SemanticEntityChangeKind.ReplaceSourceState or SemanticEntityChangeKind.RetractSourceEntity)
        {
            return new[] { new SemanticWriteDependency(owner, null) };
        }

        return change.Fields.Select(field => new SemanticWriteDependency(owner, field.Field))
            .Concat(change.Relationships.Select(relationship =>
                new SemanticWriteDependency(owner, relationship.Relationship)));
    }
}

/// <summary>Applies durable semantic commits to immutable database state.</summary>
public sealed class SemanticStateProjector
{
    private const int GuidByteLength = 16;
    private SemanticDatabaseState? _indexedState;
    private readonly Dictionary<SourceOwner, List<int>> _fieldIndices = new();
    private readonly Dictionary<SourceOwner, List<int>> _relationshipIndices = new();
    private readonly Dictionary<SemanticTerm, SemanticTerm> _terms = new();
    private readonly Dictionary<SemanticIdentityScheme, SemanticIdentityScheme> _schemes = new();
    private readonly Dictionary<SemanticValue, SemanticValue> _values = new();
    private readonly Dictionary<SemanticModelReference, SemanticModelReference> _models = new();
    private readonly Dictionary<SemanticEntityIdentity, SemanticEntityIdentity> _identities = new();
    private readonly Dictionary<SemanticProvenance, SemanticProvenance> _provenance = new();

    /// <summary>Projects the durable commit into a new state, including evidence, source metadata and catalog changes.</summary>
    public SemanticDatabaseState Apply(
        SemanticDatabaseState state,
        IReadOnlyList<SemanticEntityChange> changes,
        Guid sessionId,
        string contentHash,
        DatabaseVersion version,
        bool retainProjectedEvidence = true,
        Action<string, long, long>? recordStage = null)
    {
        long started = Stopwatch.GetTimestamp();
        long allocated = GC.GetTotalAllocatedBytes();
        EnsureOwnerIndexes(state);
        IList<SemanticFieldEvidence> fields = retainProjectedEvidence
            ? (state.Fields is ImmutableList<SemanticFieldEvidence> immutableFields
                ? immutableFields : state.Fields.ToImmutableList()).ToBuilder()
            : CreateTransientList(state.Fields, changes.Sum(change => change.Fields.Count));
        IList<SemanticRelationshipEvidence> relationships = retainProjectedEvidence
            ? (state.Relationships is ImmutableList<SemanticRelationshipEvidence> immutableRelationships
                ? immutableRelationships : state.Relationships.ToImmutableList()).ToBuilder()
            : CreateTransientList(state.Relationships, changes.Sum(change => change.Relationships.Count));
        recordStage?.Invoke("semantic_projection_setup", started, allocated);
        started = Stopwatch.GetTimestamp();
        allocated = GC.GetTotalAllocatedBytes();
        foreach (SemanticEntityChange change in changes)
        {
            SemanticEntityIdentity subject = Identity(change.Subject);
            SemanticProvenance provenance = Provenance(change.Provenance);
            var owner = new SourceOwner(subject, change.Provenance.SourceSystem,
                change.Provenance.SourceRecord);
            List<int> ownedFieldIndices = GetOrCreate(_fieldIndices, owner);
            List<int> ownedRelationshipIndices = GetOrCreate(_relationshipIndices, owner);
            if (change.Kind is SemanticEntityChangeKind.ReplaceSourceState)
            {
                ApplySourceReplacement(change, version, ownedFieldIndices, ownedRelationshipIndices,
                    fields, relationships);
            }

            if (change.Kind is SemanticEntityChangeKind.RetractSourceEntity)
            {
                RetractSourceEntity(change, version, ownedFieldIndices, ownedRelationshipIndices,
                    fields, relationships);
                continue;
            }
            int fieldAssertionOrdinal = 0;
            ApplyFieldChanges(change, owner, subject, provenance, version, ownedFieldIndices,
                fields, ref fieldAssertionOrdinal);
            ApplyRelationshipChanges(change, owner, subject, provenance, version,
                ownedRelationshipIndices, relationships);
        }
        recordStage?.Invoke("semantic_evidence_projection", started, allocated);
        started = Stopwatch.GetTimestamp();
        allocated = GC.GetTotalAllocatedBytes();
        ImmutableDictionary<Guid, AppliedSession> sessions =
            (state.AppliedSessions as ImmutableDictionary<Guid, AppliedSession> ??
                state.AppliedSessions.ToImmutableDictionary()).SetItem(sessionId, new(contentHash, version));
        SourceOwner[] affectedOwners = changes.Select(change => new SourceOwner(change.Subject,
            change.Provenance.SourceSystem, change.Provenance.SourceRecord)).Distinct().ToArray();
        SemanticFieldEvidence[] fieldDelta = affectedOwners
            .SelectMany(owner => GetOrCreate(_fieldIndices, owner)).Distinct()
            .Select(index => fields[index]).Where(item =>
                (item.BeginVersion == version || item.EndVersion == version) &&
                !(item.BeginVersion == version && item.EndVersion == version)).ToArray();
        SemanticRelationshipEvidence[] relationshipDelta = affectedOwners
            .SelectMany(owner => GetOrCreate(_relationshipIndices, owner)).Distinct()
            .Select(index => relationships[index]).Where(item =>
                (item.BeginVersion == version || item.EndVersion == version) &&
                !(item.BeginVersion == version && item.EndVersion == version)).ToArray();
        var publicationDelta = new SemanticStatePublicationDelta(fieldDelta, relationshipDelta);
        recordStage?.Invoke("semantic_publication_delta", started, allocated);
        started = Stopwatch.GetTimestamp();
        allocated = GC.GetTotalAllocatedBytes();
        SemanticAssertionStatistics assertionStatistics = state.AssertionStatistics.Apply(publicationDelta, version);
        SemanticQueryStatistics queryStatistics = state.QueryStatistics.Apply(publicationDelta, version);
        recordStage?.Invoke("semantic_statistics_projection", started, allocated);
        started = Stopwatch.GetTimestamp();
        allocated = GC.GetTotalAllocatedBytes();
        SemanticDatabaseState result = state with
        {
            Version = version,
            Fields = Freeze(fields, retainProjectedEvidence),
            Relationships = Freeze(relationships, retainProjectedEvidence),
            AppliedSessions = sessions,
            PublicationDelta = publicationDelta,
            AssertionStatistics = assertionStatistics,
            QueryStatistics = queryStatistics
        };
        recordStage?.Invoke("semantic_projection_freeze", started, allocated);
        _indexedState = result;
        return result;
    }

    private static void ApplySourceReplacement(SemanticEntityChange change, DatabaseVersion version,
        IReadOnlyList<int> ownedFieldIndices, IReadOnlyList<int> ownedRelationshipIndices,
        IList<SemanticFieldEvidence> fields,
        IList<SemanticRelationshipEvidence> relationships)
    {
        if (ownedFieldIndices.Count != 0)
        {
            SemanticFieldChange[] desiredFields = change.Fields
                .Where(item => item.Kind is SemanticFieldChangeKind.Set).ToArray();
            foreach (int index in ownedFieldIndices)
            {
                SemanticFieldEvidence existing = fields[index];
                if (IsActiveAndOwnedBy(existing, change) && !desiredFields.Any(item =>
                        item.Field == existing.Field && item.Value == existing.Value))
                {
                    fields[index] = existing with
                    {
                        EndVersion = version
                    };
                }
            }
        }

        if (ownedRelationshipIndices.Count != 0)
        {
            SemanticRelationshipChange[] desiredRelationships = change.Relationships
                .Where(item => item.Kind is SemanticRelationshipChangeKind.Add).ToArray();
            foreach (int index in ownedRelationshipIndices)
            {
                SemanticRelationshipEvidence existing = relationships[index];
                if (IsActiveAndOwnedBy(existing, change) && !desiredRelationships.Any(item =>
                        item.Relationship == existing.Relationship && item.Target == existing.Target))
                {
                    relationships[index] = existing with
                    {
                        EndVersion = version
                    };
                }
            }
        }
    }

    private static void RetractSourceEntity(SemanticEntityChange change, DatabaseVersion version,
        IReadOnlyList<int> ownedFieldIndices, IReadOnlyList<int> ownedRelationshipIndices,
        IList<SemanticFieldEvidence> fields,
        IList<SemanticRelationshipEvidence> relationships)
    {
        foreach (int index in ownedFieldIndices)
        {
            if (IsActiveAndOwnedBy(fields[index], change))
            {
                fields[index] = fields[index] with
                {
                    EndVersion = version
                };
            }
        }

        foreach (int index in ownedRelationshipIndices)
        {
            if (IsActiveAndOwnedBy(relationships[index], change))
            {
                relationships[index] = relationships[index] with
                {
                    EndVersion = version
                };
            }
        }
    }

    private void ApplyFieldChanges(SemanticEntityChange change, SourceOwner owner,
        SemanticEntityIdentity subject, SemanticProvenance provenance,
        DatabaseVersion version, IReadOnlyList<int> ownedFieldIndices,
        IList<SemanticFieldEvidence> fields, ref int assertionOrdinal)
    {
        if (HasUniqueFields(change.Fields))
        {
            foreach (SemanticFieldChange fieldChange in change.Fields)
            {
                ApplySingleFieldChange(change, owner, subject, provenance, version,
                    ownedFieldIndices, fields, fieldChange, ref assertionOrdinal);
            }

            return;
        }

        foreach (IGrouping<SemanticTerm, SemanticFieldChange> propertyChanges in change.Fields.GroupBy(item => item.Field))
        {
            SemanticFieldChange[] changes = propertyChanges.ToArray();
            if (changes.Any(item => item.Kind is SemanticFieldChangeKind.Remove))
            {
                foreach (int index in ownedFieldIndices)
                {
                    if (IsActiveAndOwnedBy(fields[index], change) && fields[index].Field == propertyChanges.Key)
                    {
                        fields[index] = fields[index] with
                        {
                            EndVersion = version
                        };
                    }
                }
            }
            SemanticValue[] replacements = changes.Where(item => item.Kind is SemanticFieldChangeKind.Set)
                .Select(item => item.Value!).Distinct().ToArray();
            if (replacements.Length != 0)
            {
                foreach (int index in ownedFieldIndices)
                {
                    if (IsActiveAndOwnedBy(fields[index], change) && fields[index].Field == propertyChanges.Key &&
                        !replacements.Contains(fields[index].Value))
                    {
                        fields[index] = fields[index] with
                        {
                            EndVersion = version
                        };
                    }
                }
            }
            foreach (SemanticValue removed in changes.Where(item =>
                         item.Kind is SemanticFieldChangeKind.RemoveValue).Select(item => item.Value!))
            {
                foreach (int index in ownedFieldIndices)
                {
                    if (IsActiveAndOwnedBy(fields[index], change) && fields[index].Field == propertyChanges.Key &&
                        fields[index].Value == removed)
                    {
                        fields[index] = fields[index] with
                        {
                            EndVersion = version
                        };
                    }
                }
            }

            IEnumerable<SemanticValue> additions = replacements.Concat(changes.Where(item =>
                item.Kind is SemanticFieldChangeKind.Add).Select(item => item.Value!)).Distinct();
            foreach (SemanticValue addition in additions)
            {
                bool active = ownedFieldIndices.Any(index => IsActiveAndOwnedBy(fields[index], change) &&
                    fields[index].Field == propertyChanges.Key && fields[index].Value == addition);
                if (active)
                {
                    continue;
                }

                int fieldIndex = fields.Count;
                fields.Add(new(AssertionId(change.ChangeId, 1, assertionOrdinal++), subject,
                    Term(propertyChanges.Key), Value(addition), provenance, version, null));
                AddIndex(_fieldIndices, owner, fieldIndex);
            }
        }
    }

    private void ApplySingleFieldChange(SemanticEntityChange change, SourceOwner owner,
        SemanticEntityIdentity subject, SemanticProvenance provenance, DatabaseVersion version,
        IReadOnlyList<int> ownedFieldIndices, IList<SemanticFieldEvidence> fields,
        SemanticFieldChange fieldChange, ref int assertionOrdinal)
    {
        bool alreadyActive = false;
        foreach (int index in ownedFieldIndices)
        {
            SemanticFieldEvidence existing = fields[index];
            if (!IsActiveAndOwnedBy(existing, change) || existing.Field != fieldChange.Field)
            {
                continue;
            }

            bool sameValue = fieldChange.Value is not null && existing.Value == fieldChange.Value;
            bool retract = fieldChange.Kind switch
            {
                SemanticFieldChangeKind.Remove => true,
                SemanticFieldChangeKind.RemoveValue => sameValue,
                SemanticFieldChangeKind.Set => !sameValue,
                _ => false
            };
            if (retract)
            {
                fields[index] = existing with
                {
                    EndVersion = version
                };
            }
            else if (sameValue)
            {
                alreadyActive = true;
            }
        }

        if (fieldChange.Kind is SemanticFieldChangeKind.Remove or SemanticFieldChangeKind.RemoveValue ||
            alreadyActive)
        {
            return;
        }

        int fieldIndex = fields.Count;
        fields.Add(new(AssertionId(change.ChangeId, 1, assertionOrdinal++), subject,
            Term(fieldChange.Field), Value(fieldChange.Value!), provenance, version, null));
        AddIndex(_fieldIndices, owner, fieldIndex);
    }

    private static bool HasUniqueFields(IReadOnlyList<SemanticFieldChange> changes)
    {
        for (int left = 0; left < changes.Count; left++)
        {
            for (int right = left + 1; right < changes.Count; right++)
            {
                if (changes[left].Field == changes[right].Field)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void ApplyRelationshipChanges(SemanticEntityChange change, SourceOwner owner,
        SemanticEntityIdentity subject, SemanticProvenance provenance,
        DatabaseVersion version, IReadOnlyList<int> ownedRelationshipIndices,
        IList<SemanticRelationshipEvidence> relationships)
    {
        for (int assertionOrdinal = 0; assertionOrdinal < change.Relationships.Count; assertionOrdinal++)
        {
            SemanticRelationshipChange relationship = change.Relationships[assertionOrdinal];
            if (relationship.Kind is SemanticRelationshipChangeKind.Clear)
            {
                foreach (int index in ownedRelationshipIndices)
                {
                    if (IsActiveAndOwnedBy(relationships[index], change) &&
                        relationships[index].Relationship == relationship.Relationship)
                    {
                        relationships[index] = relationships[index] with
                        {
                            EndVersion = version
                        };
                    }
                }

                continue;
            }
            if (relationship.Kind is SemanticRelationshipChangeKind.Remove)
            {
                foreach (int index in ownedRelationshipIndices)
                {
                    if (IsActiveAndOwnedBy(relationships[index], change) &&
                        relationships[index].Relationship == relationship.Relationship &&
                        relationships[index].Target == relationship.Target!)
                    {
                        relationships[index] = relationships[index] with
                        {
                            EndVersion = version
                        };
                    }
                }

                continue;
            }

            bool alreadyActive = false;
            foreach (int index in ownedRelationshipIndices)
            {
                SemanticRelationshipEvidence existing = relationships[index];
                if (!IsActiveAndOwnedBy(existing, change) ||
                    existing.Relationship != relationship.Relationship || existing.Target != relationship.Target!)
                {
                    continue;
                }

                alreadyActive = true;
                break;
            }
            if (alreadyActive)
            {
                continue;
            }

            int relationshipIndex = relationships.Count;
            relationships.Add(new(AssertionId(change.ChangeId, 2, assertionOrdinal),
                subject, Term(relationship.Relationship), Identity(relationship.Target!),
                provenance, version, null));
            AddIndex(_relationshipIndices, owner, relationshipIndex);
        }
    }

    private static bool IsActiveAndOwnedBy(SemanticFieldEvidence evidence, SemanticEntityChange change) =>
        evidence.Subject == change.Subject && evidence.Provenance.SourceSystem == change.Provenance.SourceSystem &&
        evidence.Provenance.SourceRecord == change.Provenance.SourceRecord && evidence.EndVersion is null;

    private static bool IsActiveAndOwnedBy(SemanticRelationshipEvidence evidence, SemanticEntityChange change) =>
        evidence.Subject == change.Subject && evidence.Provenance.SourceSystem == change.Provenance.SourceSystem &&
        evidence.Provenance.SourceRecord == change.Provenance.SourceRecord && evidence.EndVersion is null;

    private void EnsureOwnerIndexes(SemanticDatabaseState state)
    {
        if (ReferenceEquals(_indexedState, state))
        {
            return;
        }

        _fieldIndices.Clear();
        _relationshipIndices.Clear();
        _terms.Clear();
        _schemes.Clear();
        _values.Clear();
        _models.Clear();
        _identities.Clear();
        _provenance.Clear();
        for (int index = 0; index < state.Fields.Count; index++)
        {
            SemanticFieldEvidence field = state.Fields[index];
            Remember(field.Subject, field.Field, field.Value, field.Provenance);
            AddIndex(_fieldIndices, new(field.Subject, field.Provenance.SourceSystem,
                field.Provenance.SourceRecord), index);
        }
        for (int index = 0; index < state.Relationships.Count; index++)
        {
            SemanticRelationshipEvidence relationship = state.Relationships[index];
            Remember(relationship.Subject, relationship.Relationship, null, relationship.Provenance);
            _ = Identity(relationship.Target);
            AddIndex(_relationshipIndices, new(relationship.Subject, relationship.Provenance.SourceSystem,
                relationship.Provenance.SourceRecord), index);
        }
        _indexedState = state;
    }

    private void Remember(SemanticEntityIdentity subject, SemanticTerm term,
        SemanticValue? value, SemanticProvenance provenance)
    {
        _ = Identity(subject);
        _ = Term(term);
        if (value is not null)
        {
            _ = Value(value);
        }

        _ = Provenance(provenance);
    }

    private SemanticTerm Term(SemanticTerm value) => Intern(_terms, value);

    private SemanticIdentityScheme Scheme(SemanticIdentityScheme value) => Intern(_schemes, value);

    private SemanticValue Value(SemanticValue value) => Intern(_values, value);

    private SemanticModelReference Model(SemanticModelReference value) => Intern(_models, value);

    private SemanticEntityIdentity Identity(SemanticEntityIdentity value)
    {
        if (_identities.TryGetValue(value, out SemanticEntityIdentity? canonical))
        {
            return canonical;
        }

        canonical = new(Term(value.Concept), Scheme(value.Scheme), Value(value.Value));
        _identities[canonical] = canonical;
        return canonical;
    }

    private SemanticProvenance Provenance(SemanticProvenance value)
    {
        if (_provenance.TryGetValue(value, out SemanticProvenance? canonical))
        {
            return canonical;
        }

        canonical = new(value.SourceSystem, value.SourceRecord, value.ObservedAt,
            Model(value.TargetVocabulary), value.Mappings.Select(Model).ToArray(),
            value.ValidFrom, value.ValidTo, value.CorrelationId, value.SourceEntity,
            value.TransformActivity, value.ResponsibleAgent, value.TransformTemplate,
            value.GeneratedAt, value.SourceVocabulary is null ? null : Model(value.SourceVocabulary),
            value.TargetBinding);
        _provenance[canonical] = canonical;
        return canonical;
    }

    private static T Intern<T>(Dictionary<T, T> values, T value) where T : notnull
    {
        if (values.TryGetValue(value, out T? canonical))
        {
            return canonical;
        }

        values[value] = value;
        return value;
    }

    private static void AddIndex(Dictionary<SourceOwner, List<int>> index,
        SourceOwner owner, int assertionIndex)
    {
        if (!index.TryGetValue(owner, out List<int>? indices))
        {
            index.Add(owner, indices = new List<int>());
        }

        indices.Add(assertionIndex);
    }

    private static List<int> GetOrCreate(Dictionary<SourceOwner, List<int>> index, SourceOwner owner)
    {
        if (!index.TryGetValue(owner, out List<int>? indices))
        {
            index.Add(owner, indices = new List<int>());
        }

        return indices;
    }

    private static Guid AssertionId(Guid changeId, byte category, int ordinal)
    {
        Span<byte> input = stackalloc byte[GuidByteLength + sizeof(byte) + sizeof(int)];
        changeId.TryWriteBytes(input);
        input[GuidByteLength] = category;
        BinaryPrimitives.WriteInt32LittleEndian(input[(GuidByteLength + sizeof(byte))..], ordinal);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.TryHashData(input, digest, out _);
        return new Guid(digest[..GuidByteLength]);
    }

    private static IReadOnlyList<T> Freeze<T>(IList<T> values, bool retainProjectedEvidence) =>
        retainProjectedEvidence
            ? ((ImmutableList<T>.Builder)values).ToImmutable()
            : ((List<T>)values).ToArray();

    private static List<T> CreateTransientList<T>(IReadOnlyList<T> existing, int additionalCapacity)
    {
        var result = new List<T>(checked(existing.Count + additionalCapacity));
        result.AddRange(existing);
        return result;
    }
}
