using System.Diagnostics;
using System.Diagnostics.Metrics;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Semantic;

internal enum SemanticProjectionRole
{
    Identity,
    ConceptMembership,
    FieldMembership,
    StringRange,
    SubjectField,
    FieldEvidence,
    OutgoingRelationship,
    IncomingRelationship,
    RelationshipEvidence,
    AtomDirectory,
    AtomPayload,
    AtomSequence,
    PageSpaceDirectory,
    SharedSemanticExact
}

internal static class SemanticProjectionRoles
{
    public static string Name(SemanticProjectionRole role) => role switch
    {
        SemanticProjectionRole.Identity => "identity",
        SemanticProjectionRole.ConceptMembership => "concept_membership",
        SemanticProjectionRole.FieldMembership => "field_membership",
        SemanticProjectionRole.StringRange => "string_range",
        SemanticProjectionRole.SubjectField => "subject_field",
        SemanticProjectionRole.FieldEvidence => "field_evidence",
        SemanticProjectionRole.OutgoingRelationship => "outgoing_relationship",
        SemanticProjectionRole.IncomingRelationship => "incoming_relationship",
        SemanticProjectionRole.RelationshipEvidence => "relationship_evidence",
        SemanticProjectionRole.AtomDirectory => "atom_directory",
        SemanticProjectionRole.AtomPayload => "atom_payload",
        SemanticProjectionRole.AtomSequence => "atom_sequence",
        SemanticProjectionRole.PageSpaceDirectory => "page_space_directory",
        SemanticProjectionRole.SharedSemanticExact => "shared_semantic_exact",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unsupported Semantic projection role.")
    };
}

internal sealed record SemanticProjectionBinding(
    SemanticProjectionRole Role,
    string AccessMethod,
    bool ExactPhysicalAttribution);

internal static class SemanticProjectionBindings
{
    private const string Ordered = "ordered";
    private const string AppendMerge = "append_merge";
    private static readonly IReadOnlyDictionary<SemanticProjectionRole, SemanticProjectionBinding> Bindings =
        Enum.GetValues<SemanticProjectionRole>().ToDictionary(role => role, Create);

    public static SemanticProjectionBinding For(SemanticProjectionRole role) => Bindings[role];

    private static SemanticProjectionBinding Create(SemanticProjectionRole role) => role switch
    {
        SemanticProjectionRole.ConceptMembership => new(role, AppendMerge, true),
        SemanticProjectionRole.FieldMembership => new(role, AppendMerge, true),
        SemanticProjectionRole.SubjectField => new(role, AppendMerge, true),
        SemanticProjectionRole.OutgoingRelationship => new(role, AppendMerge, true),
        SemanticProjectionRole.IncomingRelationship => new(role, AppendMerge, true),
        SemanticProjectionRole.FieldEvidence => new(role, AppendMerge, true),
        SemanticProjectionRole.StringRange => new(role, AppendMerge, true),
        SemanticProjectionRole.PageSpaceDirectory => new(role, "metadata", true),
        SemanticProjectionRole.SharedSemanticExact => new(role, AppendMerge, true),
        SemanticProjectionRole.Identity => new(role, AppendMerge, false),
        SemanticProjectionRole.RelationshipEvidence => new(role, AppendMerge, false),
        SemanticProjectionRole.AtomDirectory => new(role, AppendMerge, false),
        SemanticProjectionRole.AtomPayload => new(role, AppendMerge, false),
        SemanticProjectionRole.AtomSequence => new(role, AppendMerge, false),
        _ => new(role, Ordered, false)
    };
}

internal sealed class SemanticProjectionTelemetry : IDisposable
{
    public const string MeterName = "EntityForge.Db.Semantic.Projections";
    public const string ActivitySourceName = MeterName;
    public const string ProjectionTag = "entityforge.db.projection";
    public const string AccessMethodTag = "entityforge.db.access_method";
    public const string EffectsInstrument = "entityforge.db.semantic.projection.effects";
    public const string MutationsInstrument = "entityforge.db.semantic.projection.mutations";
    public const string EncodedBytesInstrument = "entityforge.db.semantic.projection.encoded_bytes";
    public const string PlanDurationInstrument = "entityforge.db.semantic.projection.plan.duration";
    public const string PhysicalPagesInstrument = "entityforge.db.semantic.projection.physical.pages";
    public const string PhysicalBytesInstrument = "entityforge.db.semantic.projection.physical.bytes";
    public const string PrepareDurationInstrument = "entityforge.db.semantic.projection.prepare.duration";
    public const string PrepareAllocatedBytesInstrument =
        "entityforge.db.semantic.projection.prepare.allocated_bytes";

    public const string RunCountInstrument = "entityforge.db.semantic.projection.runs";
    public const string CompactionDebtInstrument = "entityforge.db.semantic.projection.compaction.debt";
    public const string MaintenanceBytesInstrument = "entityforge.db.semantic.projection.maintenance.bytes";
    public const string ReclaimedPagesInstrument = "entityforge.db.semantic.projection.reclaimed.pages";
    public const string MaintenanceDurationInstrument = "entityforge.db.semantic.projection.maintenance.duration";
    public const string RetiredPagesInstrument = "entityforge.db.semantic.projection.retired.pages";
    public const string ReclaimablePagesInstrument = "entityforge.db.semantic.projection.reclaimable.pages";
    public const string CompactionCountInstrument = "entityforge.db.semantic.projection.compactions";
    public const string ReclamationCountInstrument = "entityforge.db.semantic.projection.reclamations";
    public const string AtomCountInstrument = "entityforge.db.semantic.atoms.count";
    public const string AtomSequenceLookupsInstrument = "entityforge.db.semantic.atoms.sequence_lookups";
    public const string AtomBytesInstrument = "entityforge.db.semantic.atoms.bytes";
    private readonly Meter _meter = new(MeterName);
    private readonly ActivitySource _activities = new(ActivitySourceName);
    private readonly Counter<long> _effects;
    private readonly Counter<long> _mutations;
    private readonly Histogram<long> _encodedBytes;
    private readonly Histogram<double> _planDuration;
    private readonly Histogram<long> _physicalPages;
    private readonly Histogram<long> _physicalBytes;
    private readonly Histogram<double> _prepareDuration;
    private readonly Histogram<long> _prepareAllocatedBytes;
    private readonly Histogram<long> _runCount;
    private readonly Histogram<long> _compactionDebt;
    private readonly Counter<long> _maintenanceBytes;
    private readonly Counter<long> _reclaimedPages;
    private readonly Histogram<double> _maintenanceDuration;
    private readonly Histogram<long> _retiredPages;
    private readonly Histogram<long> _reclaimablePages;
    private readonly Counter<long> _compactions;
    private readonly Counter<long> _reclamations;
    private readonly Counter<long> _atomCount;
    private readonly Counter<long> _atomSequenceLookups;
    private readonly Counter<long> _atomBytes;
    private readonly Counter<long> _checkpointCompaction;

    public SemanticProjectionTelemetry()
    {
        _effects = _meter.CreateCounter<long>(EffectsInstrument);
        _mutations = _meter.CreateCounter<long>(MutationsInstrument);
        _encodedBytes = _meter.CreateHistogram<long>(EncodedBytesInstrument, "By");
        _planDuration = _meter.CreateHistogram<double>(PlanDurationInstrument, "ms");
        _physicalPages = _meter.CreateHistogram<long>(PhysicalPagesInstrument, "{page}");
        _physicalBytes = _meter.CreateHistogram<long>(PhysicalBytesInstrument, "By");
        _prepareDuration = _meter.CreateHistogram<double>(PrepareDurationInstrument, "ms");
        _prepareAllocatedBytes = _meter.CreateHistogram<long>(PrepareAllocatedBytesInstrument, "By");
        _runCount = _meter.CreateHistogram<long>(RunCountInstrument, "{run}");
        _compactionDebt = _meter.CreateHistogram<long>(CompactionDebtInstrument, "{run}");
        _maintenanceBytes = _meter.CreateCounter<long>(MaintenanceBytesInstrument, "By");
        _reclaimedPages = _meter.CreateCounter<long>(ReclaimedPagesInstrument, "{page}");
        _maintenanceDuration = _meter.CreateHistogram<double>(MaintenanceDurationInstrument, "ms");
        _retiredPages = _meter.CreateHistogram<long>(RetiredPagesInstrument, "{page}");
        _reclaimablePages = _meter.CreateHistogram<long>(ReclaimablePagesInstrument, "{page}");
        _compactions = _meter.CreateCounter<long>(CompactionCountInstrument, "{operation}");
        _reclamations = _meter.CreateCounter<long>(ReclamationCountInstrument, "{operation}");
        _atomCount = _meter.CreateCounter<long>(AtomCountInstrument, "{atom}");
        _atomSequenceLookups = _meter.CreateCounter<long>(AtomSequenceLookupsInstrument, "{lookup}");
        _atomBytes = _meter.CreateCounter<long>(AtomBytesInstrument, "By");
        _checkpointCompaction = _meter.CreateCounter<long>("entityforge.db.semantic.checkpoint.compaction.events");
    }

    internal void CheckpointCompaction(string stage, long count = 1) =>
        _checkpointCompaction.Add(count, new KeyValuePair<string, object?>("stage", stage));

    public void Plan(SemanticProjectionRole role, int effects, int mutations, long encodedBytes,
        TimeSpan? duration = null)
    {
        TagList tags = Tags(role, SemanticProjectionBindings.For(role).AccessMethod);
        _effects.Add(effects, tags);
        _mutations.Add(mutations, tags);
        _encodedBytes.Record(encodedBytes, tags);
        if (duration is not null)
        {
            _planDuration.Record(duration.Value.TotalMilliseconds, tags);
        }
    }

    internal void ObserveCaches(Func<IEnumerable<Measurement<long>>> counts, Func<IEnumerable<Measurement<long>>> bytes)
    {
        _meter.CreateObservableGauge("entityforge.db.semantic.cache.entries", counts, "{entry}");
        _meter.CreateObservableGauge("entityforge.db.semantic.cache.payload_bytes", bytes, "By");
    }

    public void ReadAtomSequence() => _atomSequenceLookups.Add(1);

    public Activity? StartPrepare(SemanticProjectionRole role) =>
        _activities.StartActivity("semantic.projection.prepare")?
            .AddTag(ProjectionTag, SemanticProjectionRoles.Name(role))
            .AddTag(AccessMethodTag, SemanticProjectionBindings.For(role).AccessMethod);

    public void Prepared(SemanticProjectionRole role,
        IReadOnlyList<EntityForge.Db.Storage.DatabasePhysicalPageMutation> mutations, TimeSpan duration,
        long allocatedBytes = 0)
    {
        TagList tags = Tags(role, SemanticProjectionBindings.For(role).AccessMethod);
        _physicalPages.Record(mutations.Count, tags);
        _physicalBytes.Record(mutations.Sum(item => (long)item.Bytes.Length), tags);
        _prepareDuration.Record(duration.TotalMilliseconds, tags);
        _prepareAllocatedBytes.Record(allocatedBytes, tags);
    }

    public void Maintenance(SemanticProjectionRole role, AppendMergeMaintenanceStage stage,
        TimeSpan duration)
    {
        TagList tags = Tags(role, SemanticProjectionBindings.For(role).AccessMethod);
        _maintenanceBytes.Add(stage.Stage.PhysicalMutations.Sum(item => (long)item.Bytes.Length), tags);
        _reclaimedPages.Add(stage.ReclaimedPageCount, tags);
        _maintenanceDuration.Record(duration.TotalMilliseconds, tags);
        if (stage.Kind is AppendMergeMaintenanceKind.Compaction)
        {
            _compactions.Add(1, tags);
        }
        else
        {
            _reclamations.Add(1, tags);
        }
    }

    public void MaintenanceStatus(SemanticProjectionRole role, AppendMergeMaintenanceStatus status)
    {
        TagList tags = Tags(role, SemanticProjectionBindings.For(role).AccessMethod);
        _runCount.Record(status.RunCount, tags);
        _compactionDebt.Record(status.CompactionDebt, tags);
        _retiredPages.Record(status.RetiredPageCount, tags);
        _reclaimablePages.Record(status.ReclaimablePageCount, tags);
    }

    public void AddedAtom(SemanticAtomKind kind, int canonicalBytes)
    {
        var tags = new TagList { { "entityforge.db.atom_kind", kind.ToString().ToLowerInvariant() } };
        _atomCount.Add(1, tags);
        _atomBytes.Add(canonicalBytes, tags);
    }

    private static TagList Tags(SemanticProjectionRole role, string? accessMethod = null)
    {
        var tags = new TagList { { ProjectionTag, SemanticProjectionRoles.Name(role) } };
        if (accessMethod is not null)
        {
            tags.Add(AccessMethodTag, accessMethod);
        }

        return tags;
    }

    public void Dispose()
    {
        _activities.Dispose();
        _meter.Dispose();
    }
}

internal sealed record SemanticProjectionMutationBatch(
    SemanticProjectionRole Role,
    int EffectCount,
    IReadOnlyList<OrderedPageTreeMutation<SemanticAccessKey, byte[]>> Mutations);
