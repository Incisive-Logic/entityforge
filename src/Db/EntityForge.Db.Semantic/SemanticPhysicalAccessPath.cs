using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Diagnostics;
using EntityForge.Db.Storage.Pages;
using EntityForge.Db.Storage;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.AccessPaths;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Kernel;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

internal enum SemanticAccessFamily : byte
{
    Identity = 1,
    Concept = 2,
    FieldEquality = 3,
    StringRange = 4,
    FieldAssertion = 5,
    RelationshipAssertion = 6,
    AtomDirectory = 7,
    OutgoingRelationship = 8,
    IncomingRelationship = 9,
    SubjectField = 10,
    AtomPayload = 11,
    AtomSequence = 12,
    EntityStringOrder = 13,
    SourceMembership = 14,
    SourceProgress = 15
}

internal readonly record struct SemanticAccessKey(SemanticAccessFamily Family, string Term, string Value);

// Owns physical staging/publication. Queries and evidence hydration are grouped in read partials;
// Encoding defines persistent keys, Maintenance owns replacement work, and Recovery restores evidence.
// The owner remains shared so generation-bound caches and staged transactions have one lifetime.
internal sealed partial class SemanticPhysicalAccessPath : ISemanticEntityCandidateAccessPath,
    ISemanticFieldCandidateAccessPath, ISemanticFieldEvidenceAccessPath,
    ISemanticFieldEqualityEvidenceAccessPath,
    ISemanticRelationshipEvidenceAccessPath, ISemanticOrderedEntityAccessPath
{
    private static readonly PhysicalAccessPathId AccessPathId =
        new(new Guid("e3c59eaa-3fb7-4d7f-8ae0-d9825e468c57"));

    private static readonly PhysicalAccessMethodKind AccessMethod = new("append-merge.semantic-ordered", 1);
    private static readonly PhysicalAccessPathId SharedAccessPathId =
        new(new Guid("42488fa9-0759-407f-82a6-8552777a78c9"));

    // Version 3 stores source memberships as immutable atom references. Earlier
    // shared layouts require explicit import; they cannot be silently reinterpreted.
    private static readonly PhysicalAccessMethodKind SharedAccessMethod = new("append-merge.semantic-shared", 3);
    private static readonly PhysicalAccessPathId ConceptAccessPathId =
        new(new Guid("02f89f45-9b8e-43ba-bf9b-aeb39bdd10bc"));

    private static readonly PhysicalAccessMethodKind ConceptAccessMethod = new("append-merge.semantic-concept", 1);
    private const int GuidByteLength = 16;
    private const int AtomDirectoryValueLength = sizeof(long) + sizeof(int);
    private const int MaximumCachedRelationshipRows = 65_536;
    private const int MaximumCachedRelationshipEvidence = 65_536;
    private const int MaximumCachedFieldEvidence = 65_536;
    private const int MaximumCachedRelationshipAdjacencies = 65_536;
    private const int MaximumCachedRelationshipAssertionSets = 65_536;
    private const int MaximumCachedFieldCandidateSets = 65_536;
    private const int MaximumCachedFieldAssertionSets = 65_536;
    private const int MaximumCachedAtomKeys = 65_536;
    private const int MaximumCachedDecodedIdentities = 131_072;
    private const int MaximumCachedIdentityMemberships = 131_072;
    private readonly AppendMergeAccessPath<SemanticAccessKey, byte[]> _ranges;
    private readonly AppendMergeAccessPath<SemanticAccessKey, byte[]> _shared;
    private readonly AppendMergeAccessPath<SemanticAccessKey, byte[]> _concepts;
    private readonly SemanticDirectionalRelationshipAccessPaths _relationships;
    private readonly SemanticFieldAccessPaths _fields;
    private readonly PhysicalPageSpace _pageSpace;
    private readonly IResizablePhysicalPageStore _pages;
    private readonly Dictionary<Guid, IReadOnlyList<DatabasePhysicalPageMutation>> _stagedMutations = [];
    private readonly Dictionary<Guid, byte[]> _stagedAtomSequences = [];
    private readonly Dictionary<Guid, PublishedAtomSequence> _stagedUnchangedAtomSequences = [];
    private PublishedAtomSequence? _publishedAtomSequence;

    private sealed record PublishedAtomSequence(long Generation, byte[]? Value);

    private readonly HashSet<Guid> _stagedRanges = [];
    private readonly HashSet<Guid> _stagedShared = [];
    private readonly HashSet<Guid> _stagedConcepts = [];
    private readonly Dictionary<Guid, (PhysicalMaintenanceTarget Target, PhysicalPageWorkspace Workspace)>
        _appendReplacements = [];

    private readonly object _replacementGate = new();
    private readonly SemanticCanonicalAtomCodec _atoms = new();
    private readonly SemanticAtomStorageCodec _atomStorage = new();
    private readonly SemanticAtomCache _atomCache = new();
    private readonly SemanticAssertionRowCodec _rows = new();
    private readonly SemanticEntitySupportReader _supportReader;
    private readonly SemanticProjectionTelemetry _telemetry;
    private readonly SemanticProjectionPlanBuilder _projectionPlanner;
    // Conservative invocation-lifetime proof: a fresh, append-only image cannot
    // contain obsolete atom references. Reopen and any uncertain/removing write
    // permanently restore the full retention traversal for this instance.
    private bool _retentionMayBeNeeded;

    internal long RetentionReferenceScans
    {
        get; private set;
    }

    private readonly AsyncLocal<EncodingCache?> _encodingCache = new();

    private SemanticPhysicalAccessPath(AppendMergeAccessPath<SemanticAccessKey, byte[]> ranges,
        AppendMergeAccessPath<SemanticAccessKey, byte[]> shared,
        AppendMergeAccessPath<SemanticAccessKey, byte[]> concepts, PhysicalPageSpace pageSpace,
        SemanticDirectionalRelationshipAccessPaths relationships, SemanticFieldAccessPaths fields, KeyCodec keyCodec,
        SemanticProjectionTelemetry telemetry, IResizablePhysicalPageStore pages)
    {
        _ranges = ranges;
        _shared = shared;
        _concepts = concepts;
        _relationships = relationships;
        _fields = fields;
        _pageSpace = pageSpace;
        _pages = pages;
        _telemetry = telemetry;
        // Diagnostics must not keep a replaced physical access path alive.
        var observed = new WeakReference<SemanticPhysicalAccessPath>(this);
        telemetry.ObserveCaches(
            () => observed.TryGetTarget(out var path) ? path.ObserveCacheCounts() : [],
            () => observed.TryGetTarget(out var path)
                ? [new System.Diagnostics.Metrics.Measurement<long>(path._atomCache.Observe().PayloadBytes,
                    new KeyValuePair<string, object?>("cache", "atoms"))] : []);
        _projectionPlanner = new(keyCodec.GetEncodedLength, telemetry);
        _retentionMayBeNeeded = pageSpace.CaptureDirectory().Entries.Count != 0;
        _supportReader = new(_rows, AtomKey, DecodeAssertion);
    }

    public static async ValueTask<SemanticPhysicalAccessPath> OpenAsync(IResizablePhysicalPageStore pages,
        PhysicalPageSpace pageSpace, SemanticProjectionTelemetry telemetry,
        CancellationToken cancellationToken = default)
    {
        pages = new PhysicalMaintenancePageStore(pages);
        PhysicalPageSpaceDirectory directory = pageSpace.CaptureDirectory();
        PhysicalAccessPathDirectoryEntry? published = directory.Entries
            .SingleOrDefault(entry => entry.AccessPathId == AccessPathId);
        PhysicalAccessPathDirectoryEntry? publishedConcepts = directory.Entries
            .SingleOrDefault(entry => entry.AccessPathId == ConceptAccessPathId);
        PhysicalAccessPathDirectoryEntry? publishedShared = directory.Entries
            .SingleOrDefault(entry => entry.AccessPathId == SharedAccessPathId);
        var keyCodec = new KeyCodec();
        var valueCodec = new ByteArrayCodec();
        AppendMergeAccessPath<SemanticAccessKey, byte[]> ordered = await
            AppendMergeAccessPath<SemanticAccessKey, byte[]>.OpenAsync(pages,
            AccessPathId, AccessMethod, published,
            keyCodec, valueCodec, KeyComparer.Instance,
            ByteArrayComparer.Instance, options: new(compactOnWrite: true), cancellationToken: cancellationToken).ConfigureAwait(false);
        AppendMergeAccessPath<SemanticAccessKey, byte[]> shared = await
            AppendMergeAccessPath<SemanticAccessKey, byte[]>.OpenAsync(pages,
                SharedAccessPathId, SharedAccessMethod, publishedShared,
                keyCodec, valueCodec, KeyComparer.Instance, SourceMetadataValueComparer.Instance,
                options: new(compactOnWrite: true), cancellationToken: cancellationToken).ConfigureAwait(false);
        AppendMergeAccessPath<SemanticAccessKey, byte[]> concepts = await
            AppendMergeAccessPath<SemanticAccessKey, byte[]>.OpenAsync(pages,
                ConceptAccessPathId, ConceptAccessMethod, publishedConcepts,
                keyCodec, valueCodec, KeyComparer.Instance, ByteArrayComparer.Instance,
                options: new(compactOnWrite: true), cancellationToken: cancellationToken).ConfigureAwait(false);
        SemanticDirectionalRelationshipAccessPaths relationships = await
            SemanticDirectionalRelationshipAccessPaths.OpenAsync(pages, pageSpace, keyCodec, valueCodec,
                KeyComparer.Instance, ByteArrayComparer.Instance, cancellationToken).ConfigureAwait(false);
        SemanticFieldAccessPaths fields = await SemanticFieldAccessPaths.OpenAsync(pages, pageSpace,
            valueCodec, ByteArrayComparer.Instance, keyCodec, KeyComparer.Instance, cancellationToken)
            .ConfigureAwait(false);
        return new(ordered, shared, concepts, pageSpace, relationships, fields, keyCodec, telemetry, pages);
    }

    public async ValueTask<OrderedPageTreeStage?> PrepareAsync(Guid transactionId,
        long databaseVersion, SemanticCommittedEffects effects,
        PropertyStorageCatalogCheckpoint propertyStorage, CancellationToken cancellationToken,
        IReadOnlyList<(long Version, SemanticCommittedEffects Effects)>? group = null,
        SemanticSourceMetadataChanges? sourceMetadata = null,
        IReadOnlyList<(long Version, SemanticSourceMetadataChanges Changes)>? sourceMetadataGroup = null)
    {
        bool Removes(SemanticCommittedEffects value) => value.Effects.Any(effect => effect is
            SemanticFieldAssertionRetracted or SemanticRelationshipAssertionRetracted or
            SemanticConceptMembershipChanged { IsMember: false } or
            SemanticFieldAssertionAdded { Assertion.EndVersion: not null } or
            SemanticRelationshipAssertionAdded { Assertion.EndVersion: not null });
        bool Replaces(SemanticSourceMetadataChanges? value) => value?.Memberships.Any(change => change.Before is not null) == true;
        if (Removes(effects) || group?.Any(member => Removes(member.Effects)) == true ||
            Replaces(sourceMetadata) || sourceMetadataGroup?.Any(member => Replaces(member.Changes)) == true)
        {
            _retentionMayBeNeeded = true;
        }

        if (_encodingCache.Value is not null)
        {
            throw new InvalidOperationException("Semantic physical preparation cannot be nested.");
        }

        _encodingCache.Value = new();
        try
        {
            return await PrepareCoreAsync(transactionId, databaseVersion, effects, propertyStorage,
                    cancellationToken, group, sourceMetadata, sourceMetadataGroup)
                .ConfigureAwait(false);
        }
        finally
        {
            _encodingCache.Value = null;
            if (!_stagedMutations.ContainsKey(transactionId))
            {
                _stagedAtomAdmission.Remove(transactionId);
                _stagedCompactionAtoms.Remove(transactionId);
            }
        }
    }

    public async ValueTask<OrderedPageTreeStage?> PreparePropertyStorageChangeAsync(
        Guid transactionId, long databaseVersion, SemanticPropertyStorageChange change,
        CancellationToken cancellationToken)
    {
        if (_encodingCache.Value is not null)
        {
            throw new InvalidOperationException("Semantic physical preparation cannot be nested.");
        }

        _encodingCache.Value = new();
        try
        {
            return await PreparePropertyStorageChangeCoreAsync(transactionId, databaseVersion,
                change, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _encodingCache.Value = null;
        }
    }

    private async ValueTask<OrderedPageTreeStage?> PreparePropertyStorageChangeCoreAsync(
        Guid transactionId, long databaseVersion, SemanticPropertyStorageChange change,
        CancellationToken cancellationToken)
    {
        await using AppendMergeSnapshot<SemanticAccessKey, byte[]> shared = _shared.CaptureSnapshot();
        using SemanticFieldAccessPaths.SemanticFieldSnapshot fields = _fields.CaptureSnapshot();
        IReadOnlyList<SemanticFieldEvidence> assertions = (await ScanFieldEvidenceAsync(
                shared, fields, change.Property, cancellationToken).ConfigureAwait(false))
            .Where(item => item.EndVersion is null).ToArray();
        if (assertions.Count != 0)
        {
            _retentionMayBeNeeded = true;
        }

        AppendMergeMutationKind? equalityKind = change.PreviousStorageKind is SemanticPropertyStorageKind.ColdJson &&
                                                change.StorageKind is not SemanticPropertyStorageKind.ColdJson
            ? AppendMergeMutationKind.Add
            : change.PreviousStorageKind is not SemanticPropertyStorageKind.ColdJson &&
              change.StorageKind is SemanticPropertyStorageKind.ColdJson
                ? AppendMergeMutationKind.Remove : null;
        AppendMergeMutationKind? rangeKind = change.PreviousStorageKind is not SemanticPropertyStorageKind.HotScalar &&
                                             change.StorageKind is SemanticPropertyStorageKind.HotScalar
            ? AppendMergeMutationKind.Add
            : change.PreviousStorageKind is SemanticPropertyStorageKind.HotScalar &&
              change.StorageKind is not SemanticPropertyStorageKind.HotScalar
                ? AppendMergeMutationKind.Remove : null;
        AppendMergeMutation<SemanticAccessKey, byte[]>[] equality = equalityKind is null ? [] : assertions
            .Select(item => new AppendMergeMutation<SemanticAccessKey, byte[]>(equalityKind.Value,
                FieldKey(item.Field, item.Value), EncodeFieldRow(item with
                {
                    EndVersion = null
                }))).ToArray();
        AppendMergeMutation<SemanticAccessKey, byte[]>[] ranges = rangeKind is null ? [] : assertions
            .Where(item => item.Value is StringSemanticValue)
            .SelectMany(item => PlacementRangeMutations(item, rangeKind.Value)).ToArray();
        if (equality.Length == 0 && ranges.Length == 0)
        {
            return null;
        }

        Task<AppendMergeMutationPlan<SemanticAccessKey, byte[]>>? rangePlanning = StartPlanning(
            _ranges, ranges, databaseVersion, cancellationToken);
        var fieldPlan = new SemanticFieldProjectionPlan([], equality, []);
        if (rangePlanning is not null)
        {
            await rangePlanning.ConfigureAwait(false);
        }

        PhysicalPageSpaceTransaction physical = _pageSpace.BeginTransaction(transactionId);
        try
        {
            var mutations = new List<DatabasePhysicalPageMutation>();
            long generation = _ranges.Generation;
            if (rangePlanning is not null)
            {
                PhysicalAccessPathDirectoryEntry? current = _pageSpace.CaptureDirectory().Entries
                    .SingleOrDefault(entry => entry.AccessPathId == AccessPathId);
                AppendMergeStage stage = await _ranges.PrepareAsync(transactionId, rangePlanning.Result,
                    physical.OpenAccessPath(AccessPathId, AccessMethod, current?.Generation ?? -1),
                    cancellationToken).ConfigureAwait(false);
                mutations.AddRange(stage.PhysicalMutations);
                generation = stage.Generation;
                _stagedRanges.Add(transactionId);
            }
            mutations.AddRange(await _fields.PrepareAsync(transactionId, databaseVersion, physical,
                fieldPlan, _telemetry, cancellationToken).ConfigureAwait(false));
            PhysicalPageSpacePersistenceStage directory = await _pageSpace.PrepareAsync(
                physical, cancellationToken).ConfigureAwait(false);
            _stagedMutations.Add(transactionId, mutations);
            return new(transactionId, new(generation),
                mutations.Concat(directory.PhysicalMutations).ToArray());
        }
        catch
        {
            if (_stagedRanges.Remove(transactionId))
            {
                _ranges.Abandon(transactionId);
            }

            _fields.Abandon(transactionId);
            _pageSpace.Abandon(transactionId);
            throw;
        }
    }

    private IEnumerable<AppendMergeMutation<SemanticAccessKey, byte[]>> PlacementRangeMutations(
        SemanticFieldEvidence assertion, AppendMergeMutationKind kind)
    {
        string text = ((StringSemanticValue)assertion.Value).Value;
        string ordered = EncodeOrderedValue(text, OrderIdentity(assertion.Subject));
        byte[] value = EncodeAssertion(assertion.AssertionId);
        yield return new(kind, new(SemanticAccessFamily.StringRange, AtomKey(assertion.Field), ordered), value);
        yield return new(kind, new(SemanticAccessFamily.EntityStringOrder,
            OrderedTermKey(assertion.Subject.Concept, assertion.Field), ordered), value);
    }

    private async ValueTask<OrderedPageTreeStage?> PrepareCoreAsync(Guid transactionId,
        long databaseVersion, SemanticCommittedEffects effects,
        PropertyStorageCatalogCheckpoint propertyStorage, CancellationToken cancellationToken,
        IReadOnlyList<(long Version, SemanticCommittedEffects Effects)>? group,
        SemanticSourceMetadataChanges? sourceMetadata,
        IReadOnlyList<(long Version, SemanticSourceMetadataChanges Changes)>? sourceMetadataGroup)
    {
        if (sourceMetadataGroup is not null && (group is null ||
            sourceMetadataGroup.Count != group.Count ||
            sourceMetadataGroup.Where((member, index) => member.Version != group[index].Version).Any()))
        {
            throw new ArgumentException("Source metadata group must match every physical commit version.", nameof(sourceMetadataGroup));
        }

        var (plan, nextAtomSequence, plans) = await CompilePhysicalProjectionAsync(
            transactionId, effects, propertyStorage, cancellationToken, group, sourceMetadata, sourceMetadataGroup)
            .ConfigureAwait(false);
        SemanticFieldProjectionPlan fieldPlan = SemanticFieldAccessPaths.Plan(plan.Batches);
        IReadOnlyList<OrderedPageTreeMutation<SemanticAccessKey, byte[]>> sharedOrdered = plan.OrderedMutations;
        if (sharedOrdered.Count == 0 && plan.SharedMutations.Count == 0 && fieldPlan.IsEmpty && plan.ConceptMutations.Count == 0 &&
            plan.OutgoingMutations.Count == 0 && plan.IncomingMutations.Count == 0)
        {
            return null;
        }

        AppendMergeMutation<SemanticAccessKey, byte[]>[] rangeMutations = sharedOrdered.Count == 0
            ? [] : sharedOrdered.Select(ToAppendMergeMutation).ToArray();
        Task<AppendMergeMutationPlan<SemanticAccessKey, byte[]>>? rangePlanning =
            SemanticPhysicalMutationGroup.Plan(_ranges, rangeMutations, databaseVersion, cancellationToken,
                plans?.Select(item => (item.Version, (IReadOnlyList<AppendMergeMutation<SemanticAccessKey, byte[]>>)item.Plan.OrderedMutations.Select(ToAppendMergeMutation).ToArray())).ToArray());
        Task<AppendMergeMutationPlan<SemanticAccessKey, byte[]>>? sharedPlanning =
            SemanticPhysicalMutationGroup.Plan(_shared, plan.SharedMutations, databaseVersion, cancellationToken,
                plans?.Select(item => (item.Version, item.Plan.SharedMutations)).ToArray());
        Task<AppendMergeMutationPlan<SemanticAccessKey, byte[]>>? conceptPlanning =
            SemanticPhysicalMutationGroup.Plan(_concepts, plan.ConceptMutations, databaseVersion, cancellationToken,
                plans?.Select(item => (item.Version, item.Plan.ConceptMutations)).ToArray());
        Task[] planningTasks = [.. new Task?[] { rangePlanning, sharedPlanning, conceptPlanning }
            .OfType<Task>()];
        await Task.WhenAll(planningTasks).ConfigureAwait(false);
        PhysicalPageSpaceTransaction physical = _pageSpace.BeginTransaction(transactionId);
        try
        {
            var accessMutations = new List<DatabasePhysicalPageMutation>();
            long resultingGeneration = _ranges.Generation;
            if (sharedOrdered.Count != 0)
            {
                AppendMergeMutationPlan<SemanticAccessKey, byte[]> prepared = rangePlanning!.Result;
                PhysicalAccessPathDirectoryEntry? current = _pageSpace.CaptureDirectory().Entries
                    .SingleOrDefault(entry => entry.AccessPathId == AccessPathId);
                PhysicalAccessPathAllocationScope allocation = physical.OpenAccessPath(
                    AccessPathId, AccessMethod, current?.Generation ?? -1);
                long orderedAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                long orderedStarted = Stopwatch.GetTimestamp();
                using var orderedActivity = _telemetry.StartPrepare(SemanticProjectionRole.StringRange);
                AppendMergeStage tree = await _ranges.PrepareAsync(transactionId,
                    prepared, allocation, cancellationToken).ConfigureAwait(false);
                _telemetry.Prepared(SemanticProjectionRole.StringRange,
                    tree.PhysicalMutations, prepared.PlanningDuration + Stopwatch.GetElapsedTime(orderedStarted),
                    prepared.AllocatedBytes + GC.GetAllocatedBytesForCurrentThread() - orderedAllocatedBefore);
                accessMutations.AddRange(tree.PhysicalMutations);
                resultingGeneration = tree.Generation;
                _stagedRanges.Add(transactionId);
            }
            if (plan.SharedMutations.Count != 0)
            {
                AppendMergeMutationPlan<SemanticAccessKey, byte[]> prepared = sharedPlanning!.Result;
                PhysicalAccessPathDirectoryEntry? currentShared = _pageSpace.CaptureDirectory().Entries
                    .SingleOrDefault(entry => entry.AccessPathId == SharedAccessPathId);
                PhysicalAccessPathAllocationScope sharedAllocation = physical.OpenAccessPath(
                    SharedAccessPathId, SharedAccessMethod, currentShared?.Generation ?? -1);
                long sharedAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                long sharedStarted = Stopwatch.GetTimestamp();
                using var sharedActivity = _telemetry.StartPrepare(SemanticProjectionRole.SharedSemanticExact);
                AppendMergeStage sharedStage = await _shared.PrepareAsync(transactionId,
                    prepared, sharedAllocation, cancellationToken).ConfigureAwait(false);
                _telemetry.Prepared(SemanticProjectionRole.SharedSemanticExact,
                    sharedStage.PhysicalMutations, prepared.PlanningDuration + Stopwatch.GetElapsedTime(sharedStarted),
                    prepared.AllocatedBytes + GC.GetAllocatedBytesForCurrentThread() - sharedAllocatedBefore);
                accessMutations.AddRange(sharedStage.PhysicalMutations);
                _stagedShared.Add(transactionId);
            }
            if (plan.ConceptMutations.Count != 0)
            {
                AppendMergeMutationPlan<SemanticAccessKey, byte[]> prepared = conceptPlanning!.Result;
                PhysicalAccessPathDirectoryEntry? currentConcepts = _pageSpace.CaptureDirectory().Entries
                    .SingleOrDefault(entry => entry.AccessPathId == ConceptAccessPathId);
                PhysicalAccessPathAllocationScope conceptAllocation = physical.OpenAccessPath(
                    ConceptAccessPathId, ConceptAccessMethod, currentConcepts?.Generation ?? -1);
                long conceptAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                long conceptStarted = Stopwatch.GetTimestamp();
                using var conceptActivity = _telemetry.StartPrepare(
                    SemanticProjectionRole.ConceptMembership);
                AppendMergeStage conceptStage = await _concepts.PrepareAsync(transactionId,
                    prepared, conceptAllocation, cancellationToken).ConfigureAwait(false);
                _telemetry.Prepared(SemanticProjectionRole.ConceptMembership,
                    conceptStage.PhysicalMutations, prepared.PlanningDuration + Stopwatch.GetElapsedTime(conceptStarted),
                    prepared.AllocatedBytes + GC.GetAllocatedBytesForCurrentThread() - conceptAllocatedBefore);
                accessMutations.AddRange(conceptStage.PhysicalMutations);
                _stagedConcepts.Add(transactionId);
            }
            accessMutations.AddRange(await _relationships.PrepareAsync(transactionId, databaseVersion,
                physical, plan.OutgoingMutations, plan.IncomingMutations, _telemetry,
                cancellationToken, plans).ConfigureAwait(false));
            accessMutations.AddRange(await _fields.PrepareAsync(transactionId, databaseVersion,
                physical, fieldPlan, _telemetry, cancellationToken,
                plans?.Select(item => (item.Version, SemanticFieldAccessPaths.Plan(item.Plan.Batches))).ToArray()).ConfigureAwait(false));
            long directoryAllocatedBefore = GC.GetTotalAllocatedBytes();
            long directoryStarted = Stopwatch.GetTimestamp();
            PhysicalPageSpacePersistenceStage directory = await _pageSpace.PrepareAsync(
                physical, cancellationToken).ConfigureAwait(false);
            _telemetry.Prepared(SemanticProjectionRole.PageSpaceDirectory,
                directory.PhysicalMutations, Stopwatch.GetElapsedTime(directoryStarted),
                GC.GetTotalAllocatedBytes() - directoryAllocatedBefore);
            _stagedMutations.Add(transactionId, accessMutations);
            CarryAtomAdmission(transactionId);
            if (nextAtomSequence is not null)
            {
                _stagedAtomSequences.Add(transactionId, nextAtomSequence);
            }
            else if (_publishedAtomSequence is { } unchanged && unchanged.Generation == _shared.Generation)
            {
                // This ordinary projection plan did not change the atom sequence. A
                // metadata-only shared run must not force the next writer to search
                // every shared run for that unchanged singleton again.
                _stagedUnchangedAtomSequences.Add(transactionId, unchanged);
            }

            return new(transactionId, new(resultingGeneration),
                accessMutations.Concat(directory.PhysicalMutations).ToArray());
        }
        catch
        {
            if (_stagedRanges.Remove(transactionId))
            {
                _ranges.Abandon(transactionId);
            }

            if (_stagedShared.Remove(transactionId))
            {
                _shared.Abandon(transactionId);
            }

            if (_stagedConcepts.Remove(transactionId))
            {
                _concepts.Abandon(transactionId);
            }

            _relationships.Abandon(transactionId);
            _fields.Abandon(transactionId);
            _pageSpace.Abandon(transactionId);
            throw;
        }
    }

    private void PlanEffect(SemanticCommittedEffect effect,
        SemanticProjectionPlanAccumulator plan,
        ICollection<AppendMergeMutation<SemanticAccessKey, byte[]>> concepts,
        ICollection<AppendMergeMutation<SemanticAccessKey, byte[]>> outgoing,
        ICollection<AppendMergeMutation<SemanticAccessKey, byte[]>> incoming,
        ICollection<SemanticAtomRecord> atoms, ISet<SemanticAtomId> atomIds,
        IReadOnlyDictionary<SemanticTerm, SemanticPropertyStorageKind> storage)
    {
        switch (effect)
        {
            case SemanticConceptMembershipChanged membership:
                {
                    byte[] identity = EncodeIdentity(membership.Subject);
                    OrderedPageTreeMutationKind mutation = membership.IsMember
                        ? OrderedPageTreeMutationKind.Add : OrderedPageTreeMutationKind.Remove;
                    plan.AddMutation(1, new(mutation,
                        IdentityKey(membership.Subject), identity));
                    concepts.Add(new(membership.IsMember
                            ? AppendMergeMutationKind.Add : AppendMergeMutationKind.Remove,
                        ConceptKey(membership.Subject.Concept), identity));
                    break;
                }
            case SemanticFieldAssertionAdded added:
                PlanField(OrderedPageTreeMutationKind.Add, added.Assertion, plan,
                    storage.GetValueOrDefault(added.Assertion.Field, SemanticPropertyStorageKind.ColdJson));
                CollectAtoms(added.Assertion.Subject, added.Assertion.Field,
                    added.Assertion.Value, added.Assertion.Provenance, atoms, atomIds);
                break;
            case SemanticFieldAssertionRetracted retracted:
                PlanField(OrderedPageTreeMutationKind.Remove, retracted.Assertion, plan,
                    storage.GetValueOrDefault(retracted.Assertion.Field, SemanticPropertyStorageKind.ColdJson));
                break;
            case SemanticRelationshipAssertionAdded added:
                PlanRelationship(OrderedPageTreeMutationKind.Add, added.Assertion, plan,
                    outgoing, incoming);
                CollectAtoms(added.Assertion.Subject, added.Assertion.Relationship,
                    added.Assertion.Target, added.Assertion.Provenance, atoms, atomIds);
                break;
            case SemanticRelationshipAssertionRetracted retracted:
                PlanRelationship(OrderedPageTreeMutationKind.Remove, retracted.Assertion, plan,
                    outgoing, incoming);
                break;
        }
    }

    private void PlanField(OrderedPageTreeMutationKind kind, SemanticFieldEvidence assertion,
        SemanticProjectionPlanAccumulator plan, SemanticPropertyStorageKind storageKind)
    {
        byte[] value = EncodeAssertion(assertion.AssertionId);
        byte[] active = EncodeFieldRow(assertion with
        {
            EndVersion = null
        });
        SemanticAccessKey assertionKey = AssertionKey(
            SemanticAccessFamily.FieldAssertion, assertion.AssertionId);
        plan.AddMutation(1, new(kind, new(SemanticAccessFamily.SubjectField,
            AtomKey(assertion.Subject), string.Empty), value));
        if (storageKind is not SemanticPropertyStorageKind.ColdJson)
        {
            plan.AddMutation(1, new(kind, FieldKey(assertion.Field, assertion.Value), active));
        }

        if (storageKind is SemanticPropertyStorageKind.HotScalar &&
            assertion.Value is StringSemanticValue text)
        {
            string orderedValue = EncodeOrderedValue(text.Value, OrderIdentity(assertion.Subject));
            plan.AddMutation(1, new(kind, new(SemanticAccessFamily.StringRange, AtomKey(assertion.Field),
                orderedValue), value));
            plan.AddMutation(1, new(kind, new(SemanticAccessFamily.EntityStringOrder,
                OrderedTermKey(assertion.Subject.Concept, assertion.Field),
                orderedValue), value));
        }
        if (kind is OrderedPageTreeMutationKind.Remove)
        {
            plan.AddMutation(1, new(OrderedPageTreeMutationKind.Remove, assertionKey, active));
        }

        plan.AddMutation(1, new(OrderedPageTreeMutationKind.Add, assertionKey,
            kind is OrderedPageTreeMutationKind.Remove ? EncodeFieldRow(assertion) : active));
    }

    private void PlanRelationship(OrderedPageTreeMutationKind kind,
        SemanticRelationshipEvidence assertion, SemanticProjectionPlanAccumulator plan,
        ICollection<AppendMergeMutation<SemanticAccessKey, byte[]>> outgoing,
        ICollection<AppendMergeMutation<SemanticAccessKey, byte[]>> incoming)
    {
        SemanticAccessKey assertionKey = AssertionKey(
            SemanticAccessFamily.RelationshipAssertion, assertion.AssertionId);
        byte[] active = EncodeRelationshipRow(assertion with
        {
            EndVersion = null
        });
        if (kind is OrderedPageTreeMutationKind.Remove)
        {
            plan.AddMutation(1, new(OrderedPageTreeMutationKind.Remove, assertionKey, active));
        }

        plan.AddMutation(1, new(OrderedPageTreeMutationKind.Add, assertionKey,
            kind is OrderedPageTreeMutationKind.Remove ? EncodeRelationshipRow(assertion) : active));
        Guid relationshipAtom = Atom(assertion.Relationship).Id.Value;
        byte[] outgoingValue = SemanticDirectionalRelationshipAccessPaths.Value(
            relationshipAtom, Atom(assertion.Target).Id.Value, assertion.AssertionId);
        byte[] incomingValue = SemanticDirectionalRelationshipAccessPaths.Value(
            relationshipAtom, Atom(assertion.Subject).Id.Value, assertion.AssertionId);
        AppendMergeMutationKind membershipKind = kind is OrderedPageTreeMutationKind.Add
            ? AppendMergeMutationKind.Add : AppendMergeMutationKind.Remove;
        outgoing.Add(new(membershipKind, SemanticDirectionalRelationshipAccessPaths.Key(
            SemanticRelationshipDirection.Outgoing, AtomKey(assertion.Subject)), outgoingValue));
        incoming.Add(new(membershipKind, SemanticDirectionalRelationshipAccessPaths.Key(
            SemanticRelationshipDirection.Incoming, AtomKey(assertion.Target)), incomingValue));
    }

    private byte[] EncodeFieldRow(SemanticFieldEvidence assertion) => _rows.Encode(
        new SemanticFieldAssertionRow(assertion.AssertionId, Atom(assertion.Subject).Id,
            Atom(assertion.Field).Id, Atom(assertion.Value).Id, Atom(assertion.Provenance).Id,
            assertion.BeginVersion, assertion.EndVersion));

    private byte[] EncodeRelationshipRow(SemanticRelationshipEvidence assertion) => _rows.Encode(
        new SemanticRelationshipAssertionRow(assertion.AssertionId, Atom(assertion.Subject).Id,
            Atom(assertion.Relationship).Id, Atom(assertion.Target).Id, Atom(assertion.Provenance).Id,
            assertion.BeginVersion, assertion.EndVersion));

    private void CollectAtoms(SemanticEntityIdentity subject, SemanticTerm term,
        object valueOrTarget, SemanticProvenance provenance,
        ICollection<SemanticAtomRecord> atoms, ISet<SemanticAtomId> atomIds)
    {
        CollectAtom(Atom(subject), atoms, atomIds);
        CollectAtom(Atom(term), atoms, atomIds);
        CollectAtom(valueOrTarget is SemanticValue value
            ? Atom(value) : Atom((SemanticEntityIdentity)valueOrTarget), atoms, atomIds);
        CollectAtom(Atom(provenance), atoms, atomIds);
    }

    private static void CollectAtom(SemanticAtomRecord atom,
        ICollection<SemanticAtomRecord> atoms, ISet<SemanticAtomId> atomIds)
    {
        if (atomIds.Add(atom.Id))
        {
            atoms.Add(atom);
        }
    }

    private async ValueTask<byte[]?> AddNewAtomsAsync(
        Guid transactionId,
        List<OrderedPageTreeMutation<SemanticAccessKey, byte[]>> mutations,
        IReadOnlyList<SemanticAtomRecord> atoms, CancellationToken cancellationToken)
    {
        if (atoms.Count == 0)
        {
            return null;
        }

        await using AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot =
            _shared.CaptureSnapshot();
        SemanticAtomRecord[] unique = atoms.GroupBy(atom => atom.Id)
            .Select(group => group.First()).OrderBy(atom => atom.Id.Value).ToArray();
        var identifiers = await ReadAtomAdmissionAsync(snapshot, cancellationToken).ConfigureAwait(false);
        SemanticAccessKey sequenceKey = AtomSequenceKey();
        // This is an owned copy of the sequence for exactly this published generation.
        // Maintenance/replacement changes that generation; reopening starts without a cache.
        PublishedAtomSequence? cached = _publishedAtomSequence;
        bool hasSequence = cached is not null && cached.Generation == snapshot.Generation;
        if (!hasSequence)
        {
            _telemetry.ReadAtomSequence();
        }

        byte[]? previousSequence = hasSequence
            ? cached!.Value : (await snapshot.FindUniqueAsync([sequenceKey], cancellationToken).ConfigureAwait(false))
                .GetValueOrDefault(sequenceKey);
        if (!hasSequence)
        {
            _publishedAtomSequence = new(snapshot.Generation, previousSequence);
        }

        SemanticAtomRecord[] added = unique.Where(atom => !identifiers.Contains(atom.Id.Value))
            .ToArray();
        _stagedAtomAdmission.Add(transactionId, identifiers.Union(added.Select(atom => atom.Id.Value)));
        if (_checkpointCompaction is { } compaction)
        {
            _stagedCompactionAtoms.Add(transactionId, (compaction, unique.Select(atom => atom.Id.Value).ToArray()));
            // A private base may already have filtered an atom that a later write
            // uses again. Re-emit its exact existing ordinal and complete payload
            // into the delta; allocating another ordinal would duplicate identity.
            var existing = unique.Where(atom => identifiers.Contains(atom.Id.Value)).ToArray();
            var directories = await snapshot.FindUniqueAsync(existing.Select(atom => AtomDirectoryKey(atom.Id)).ToArray(),
                cancellationToken).ConfigureAwait(false);
            foreach (var atom in existing)
            {
                var directory = directories[AtomDirectoryKey(atom.Id)];
                var decoded = DecodeAtomDirectory(directory);
                mutations.Add(new(OrderedPageTreeMutationKind.Add, AtomDirectoryKey(atom.Id), directory));
                foreach (var chunk in _atomStorage.Chunk(atom))
                {
                    mutations.Add(new(OrderedPageTreeMutationKind.Add, AtomPayloadKey(decoded.Ordinal, chunk.Key.Sequence), _atomStorage.Encode(chunk)));
                }

                compaction.Protect(atom.Id, decoded.Ordinal);
            }
        }
        if (added.Length == 0)
        {
            return null;
        }

        long next = previousSequence is null ? 0 : DecodeAtomSequence(previousSequence);
        foreach (SemanticAtomRecord atom in added)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _telemetry.AddedAtom(atom.Kind, atom.CanonicalBytes.Length);
            SemanticAtomChunk[] chunks = _atomStorage.Chunk(atom).ToArray();
            long ordinal = next++;
            _checkpointCompaction?.Protect(atom.Id, ordinal);
            mutations.Add(new(OrderedPageTreeMutationKind.Add, AtomDirectoryKey(atom.Id),
                EncodeAtomDirectory(ordinal, chunks.Length)));
            foreach (SemanticAtomChunk chunk in chunks)
            {
                mutations.Add(new(OrderedPageTreeMutationKind.Add,
                    AtomPayloadKey(ordinal, chunk.Key.Sequence), _atomStorage.Encode(chunk)));
            }
        }
        if (previousSequence is not null)
        {
            mutations.Add(new(OrderedPageTreeMutationKind.Remove, sequenceKey, previousSequence));
        }

        byte[] nextSequence = EncodeAtomSequence(next);
        mutations.Add(new(OrderedPageTreeMutationKind.Add, sequenceKey, nextSequence));
        return nextSequence;
    }

    public async ValueTask PersistAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        await _pageSpace.PersistAsync(transactionId, _stagedMutations[transactionId], cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<bool> TryTrimUnownedTailAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _pageSpace.TrimUnownedTailAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (PhysicalPageLifecycleException)
        {
            // Tail trimming is reclamation, not part of replacement correctness. A concurrent reader may
            // briefly own a buffered page; leave the unowned tail for the next maintenance pass.
            return false;
        }
    }

    public void Publish(Guid transactionId)
    {
        if (_stagedRanges.Remove(transactionId))
        {
            _ranges.Publish(transactionId);
        }

        if (_stagedShared.Remove(transactionId))
        {
            _shared.Publish(transactionId);
        }

        if (_stagedConcepts.Remove(transactionId))
        {
            _concepts.Publish(transactionId);
        }

        _relationships.Publish(transactionId);
        _fields.Publish(transactionId);
        _pageSpace.Publish(transactionId);
        _stagedMutations.Remove(transactionId);
        PublishedIncrementalCompaction(transactionId);
        if (_stagedCompactionAtoms.Remove(transactionId, out var delta) && ReferenceEquals(_checkpointCompaction, delta.Work))
        {
            delta.Work.RetainedAtoms.UnionWith(delta.Atoms);
        }

        if (_stagedAtomAdmission.Remove(transactionId, out var identifiers))
        {
            _atomAdmission = new(_shared.Generation, identifiers);
        }

        if (_stagedAtomSequences.Remove(transactionId, out byte[]? sequence))
        {
            _publishedAtomSequence = new(_shared.Generation, sequence);
        }
        else if (_stagedUnchangedAtomSequences.Remove(transactionId, out var unchanged) &&
                 ReferenceEquals(_publishedAtomSequence, unchanged))
        {
            _publishedAtomSequence = new(_shared.Generation, unchanged.Value);
        }

        _relationshipRowCache.Clear();
        _relationshipEvidenceCache.Clear();
    }

    public void Abandon(Guid transactionId, bool reclaimUnsubmittedTail = false)
    {
        _stagedCompactionAtoms.Remove(transactionId);
        _stagedAtomAdmission.Remove(transactionId);
        _stagedAtomSequences.Remove(transactionId);
        _stagedUnchangedAtomSequences.Remove(transactionId);
        if (_stagedRanges.Remove(transactionId))
        {
            _ranges.Abandon(transactionId);
        }

        if (_stagedShared.Remove(transactionId))
        {
            _shared.Abandon(transactionId);
        }

        if (_stagedConcepts.Remove(transactionId))
        {
            _concepts.Abandon(transactionId);
        }

        _relationships.Abandon(transactionId);
        _fields.Abandon(transactionId);
        _pageSpace.Abandon(transactionId, reclaimUnsubmittedTail);
        _stagedMutations.Remove(transactionId);
    }

    public ISemanticCandidateSnapshot CaptureSnapshot(Guid? stagedTransactionId = null,
        DatabaseVersion? maximumVersion = null) =>
        new CandidateSnapshot(_shared.CaptureSnapshot(stagedTransactionId, maximumVersion?.Value),
            _ranges.CaptureSnapshot(stagedTransactionId, maximumVersion?.Value),
            _concepts.CaptureSnapshot(stagedTransactionId, maximumVersion?.Value),
            _relationships.CaptureSnapshot(stagedTransactionId, maximumVersion?.Value),
            _fields.CaptureSnapshot(stagedTransactionId, maximumVersion?.Value), this);

    private sealed class ByteArrayCodec : IValidatingOrderedIndexCodec<byte[]>
    {
        public byte[] Encode(byte[] value) => value;

        public byte[] Decode(ReadOnlySpan<byte> value) => value.ToArray();

        // Every byte sequence is a valid opaque value; Decode adds only owned storage.
        public void Validate(ReadOnlySpan<byte> value)
        {
        }
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }

    private sealed class KeyComparer : IComparer<SemanticAccessKey>
    {
        public static KeyComparer Instance { get; } = new();

        public int Compare(SemanticAccessKey x, SemanticAccessKey y)
        {
            int family = ((byte)x.Family).CompareTo((byte)y.Family);
            if (family != 0)
            {
                return family;
            }

            int term = StringComparer.Ordinal.Compare(x.Term, y.Term);
            return term != 0 ? term : StringComparer.Ordinal.Compare(x.Value, y.Value);
        }
    }

    private sealed class CandidateSnapshot(AppendMergeSnapshot<SemanticAccessKey, byte[]> tree,
        AppendMergeSnapshot<SemanticAccessKey, byte[]> ranges,
        AppendMergeSnapshot<SemanticAccessKey, byte[]> concepts,
        SemanticDirectionalRelationshipAccessPaths.SemanticDirectionalRelationshipSnapshot relationships,
        SemanticFieldAccessPaths.SemanticFieldSnapshot fields,
        SemanticPhysicalAccessPath owner) :
        ISemanticCandidateSnapshot, ISemanticOrderedEntityAccessPath,
        ISemanticFieldCandidateAccessPath, ISemanticFieldEqualityEvidenceAccessPath,
        ISemanticRelationshipEvidenceAccessPath, ISemanticRecoverySnapshot, ISemanticSourceMetadataSnapshot
    {
        public ValueTask<SemanticSourceMetadataImage> ReadSourceMetadataAsync(CancellationToken token = default) =>
            owner.ReadSourceMetadataAsync(tree, token);

        public async ValueTask<bool> ContainsIdentityAsync(SemanticEntityIdentity identity,
            CancellationToken cancellationToken = default) =>
            await owner.ContainsIdentityAsync(tree, identity, cancellationToken).ConfigureAwait(false);

        public async ValueTask<IReadOnlySet<SemanticEntityIdentity>> FindExistingIdentitiesAsync(
            IReadOnlyList<SemanticEntityIdentity> identities, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(identities);
            SemanticEntityIdentity[] unique = identities.Distinct().ToArray();
            SemanticAccessKey[] keys = unique.Select(owner.IdentityKey).ToArray();
            IReadOnlyDictionary<SemanticAccessKey, byte[]> matches = await tree.FindUniqueAsync(
                keys, cancellationToken).ConfigureAwait(false);
            return unique.Where((_, index) => matches.ContainsKey(keys[index])).ToHashSet();
        }

        public async ValueTask<IReadOnlyList<SemanticEntityIdentity>> FindConceptMembersAsync(SemanticTerm concept,
            int? maximumResults = null, CancellationToken cancellationToken = default) =>
            (await concepts.FindAsync(owner.ConceptKey(concept), cancellationToken, maximumResults).ConfigureAwait(false))
            .Select(owner.DecodeIdentity).ToArray();

        public async ValueTask<long> CountConceptMembersAsync(SemanticTerm concept,
            CancellationToken cancellationToken = default) =>
            (await concepts.FindAsync(owner.ConceptKey(concept), cancellationToken).ConfigureAwait(false)).Count;

        public async ValueTask<IReadOnlyList<Guid>> FindFieldAssertionsAsync(SemanticTerm field, SemanticValue value,
            CancellationToken cancellationToken = default) =>
            await owner.FindFieldAssertionsAsync(fields, field, value, cancellationToken).ConfigureAwait(false);

        public ValueTask<IReadOnlyList<SemanticFieldCandidate>> FindFieldCandidatesAsync(
            SemanticTerm field, SemanticValue value, CancellationToken cancellationToken = default) =>
            owner.FindFieldCandidatesAsync(tree, fields, field, value, cancellationToken);

        public ValueTask<IReadOnlyList<IReadOnlyList<Guid>>> FindFieldAssertionsAsync(
            IReadOnlyList<SemanticFieldEqualityQuery> queries, CancellationToken cancellationToken = default) =>
            owner.FindFieldAssertionsAsync(fields, queries, cancellationToken);

        public ValueTask<IReadOnlyList<IReadOnlyList<SemanticFieldEvidence>>> FindFieldEvidenceAsync(
            IReadOnlyList<SemanticFieldEqualityQuery> queries, CancellationToken cancellationToken = default) =>
            owner.FindFieldEvidenceAsync(tree, fields, queries, cancellationToken);

        public async ValueTask<IReadOnlyList<Guid>> FindStringAssertionsAsync(SemanticTerm field,
            string? lowerInclusive, string? upperInclusive, CancellationToken cancellationToken = default)
            => await owner.FindStringAssertionsAsync(ranges, field, lowerInclusive, upperInclusive,
                cancellationToken).ConfigureAwait(false);

        public ValueTask<IReadOnlyList<SemanticFieldEvidence>> ReadFieldEvidenceAsync(
            IReadOnlyList<Guid> assertionIds, CancellationToken cancellationToken = default) =>
            owner.ReadFieldEvidenceAsync(tree, fields, assertionIds, cancellationToken);

        public ValueTask<IReadOnlyList<SemanticFieldEvidence>> ScanFieldEvidenceAsync(
            SemanticTerm field, CancellationToken cancellationToken = default) =>
            owner.ScanFieldEvidenceAsync(tree, fields, field, cancellationToken);

        public ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(
            SemanticEntityIdentity entity, SemanticRelationshipDirection direction, SemanticTerm? relationship,
            CancellationToken cancellationToken = default) => owner.FindRelationshipAssertionsAsync(
            relationships, entity, direction, relationship, cancellationToken);

        public ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(
            IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
            CancellationToken cancellationToken = default) => owner.FindRelationshipAssertionsAsync(
            relationships, entities, direction, cancellationToken);

        public ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(
            IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
            SemanticTerm relationship, CancellationToken cancellationToken = default) =>
            owner.FindRelationshipAssertionsAsync(relationships, entities, direction, relationship,
                cancellationToken);

        public ValueTask<IReadOnlyList<SemanticRelationshipCandidate>> FindRelationshipCandidatesAsync(
            IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
            SemanticTerm relationship, CancellationToken cancellationToken = default) =>
            owner.ReadCompactRelationshipCandidatesAsync(tree, relationships, entities, direction,
                relationship, cancellationToken);

        public ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> ReadRelationshipEvidenceAsync(
            IReadOnlyList<Guid> assertionIds, CancellationToken cancellationToken = default) =>
            owner.ReadRelationshipEvidenceAsync(tree, assertionIds, cancellationToken);

        public ValueTask<IReadOnlyList<SemanticEntitySupport>> ReadEntitySupportAsync(
            IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken = default) =>
            owner._supportReader.ReadAsync(tree, relationships, fields, entities, cancellationToken);

        public ValueTask<SemanticEvidenceSlice> ReadEntityEvidenceAsync(
            IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken = default) =>
            owner.ReadEntityEvidenceAsync(tree, relationships, fields, entities, cancellationToken);

        public ValueTask<IReadOnlyList<SemanticFieldEvidence>> ReadEntityFieldsAsync(
            IReadOnlyList<SemanticEntityIdentity> entities, IReadOnlySet<SemanticTerm> properties,
            CancellationToken cancellationToken = default) =>
            owner.ReadSubjectFieldsAsync(tree, fields, entities, properties, cancellationToken);

        public ValueTask<SemanticEvidenceSlice> ReadOwnedEntityEvidenceAsync(
            IReadOnlyList<SemanticEntityIdentity> subjects, CancellationToken cancellationToken = default) =>
            owner.ReadOwnedEntityEvidenceAsync(tree, relationships, fields, subjects, cancellationToken);

        public ValueTask<SemanticEvidenceSlice> ReadRecoveryEvidenceAsync(
            IReadOnlySet<SemanticEntityIdentity> subjects, CancellationToken cancellationToken = default) =>
            owner.ReadRecoveryEvidenceAsync(tree, fields, subjects, cancellationToken);

        public ValueTask<SemanticOrderedEntityPage> FindOrderedStringPageAsync(
            SemanticTerm concept, SemanticTerm field, IReadOnlyList<SemanticEntityIdentity>? candidates,
            SemanticOrderValueSelection selection, SemanticOrderedEntityPosition? after,
            int pageSize, DatabaseVersion snapshot, CancellationToken cancellationToken = default) =>
            owner.FindOrderedStringPageAsync(concept, field, candidates, selection, after, pageSize, snapshot,
                cancellationToken);

        public void Dispose()
        {
            tree.Dispose();
            ranges.Dispose();
            concepts.Dispose();
            relationships.Dispose();
            fields.Dispose();
        }
    }

    private async ValueTask<(SemanticProjectionPlan Plan, byte[]? NextAtomSequence,
        (long Version, SemanticProjectionPlan Plan)[]? Members)> CompilePhysicalProjectionAsync(
        Guid transactionId, SemanticCommittedEffects effects, PropertyStorageCatalogCheckpoint propertyStorage,
        CancellationToken cancellationToken, IReadOnlyList<(long Version, SemanticCommittedEffects Effects)>? group,
        SemanticSourceMetadataChanges? sourceMetadata,
        IReadOnlyList<(long Version, SemanticSourceMetadataChanges Changes)>? sourceMetadataGroup)
    {
        long planningStarted = Stopwatch.GetTimestamp();
        SemanticProjectionPlan plan;
        byte[]? nextAtomSequence;
        (long Version, SemanticProjectionPlan Plan)[]? plans = null;
        if (group is null)
        {
            var compiled = CompileProjection(effects, propertyStorage);
            var atomMutations = new List<OrderedPageTreeMutation<SemanticAccessKey, byte[]>>();
            var atoms = compiled.Atoms.Concat(MetadataAtoms(sourceMetadata)).DistinctBy(atom => atom.Id).ToArray();
            nextAtomSequence = await AddNewAtomsAsync(transactionId, atomMutations, atoms, cancellationToken).ConfigureAwait(false);
            var atomAccumulator = _projectionPlanner.Begin(0);
            atomAccumulator.Add(atoms.Length, atomMutations);
            var atomPlan = atomAccumulator.Complete([], [], [], TimeSpan.Zero);
            plan = compiled.Plan with
            {
                Batches = compiled.Plan.Batches.Concat(atomPlan.Batches).ToArray(),
                SharedMutations = compiled.Plan.SharedMutations.Concat(atomPlan.SharedMutations).ToArray()
            };
            plan = WithSourceMetadata(plan, sourceMetadata, cancellationToken);
        }
        else
        {
            var members = group;
            var compiled = members.Select(member => CompileProjection(member.Effects, propertyStorage)).ToArray();
            var atoms = compiled.SelectMany(item => item.Atoms).Concat(sourceMetadataGroup is null
                ? [] : sourceMetadataGroup.SelectMany(member => MetadataAtoms(member.Changes))).DistinctBy(atom => atom.Id).ToArray();
            var atomMutations = new List<OrderedPageTreeMutation<SemanticAccessKey, byte[]>>();
            nextAtomSequence = await AddNewAtomsAsync(transactionId, atomMutations, atoms, cancellationToken).ConfigureAwait(false);
            var atomAccumulator = _projectionPlanner.Begin(0);
            atomAccumulator.Add(atoms.Length, atomMutations);
            var atomPlan = atomAccumulator.Complete([], [], [], TimeSpan.Zero);
            plans = members.Select((member, index) => (member.Version, Plan: WithSourceMetadata(index == 0
                ? compiled[index].Plan with
                {
                    Batches = compiled[index].Plan.Batches.Concat(atomPlan.Batches).ToArray(),
                    SharedMutations = compiled[index].Plan.SharedMutations.Concat(atomPlan.SharedMutations).ToArray()
                } : compiled[index].Plan, sourceMetadataGroup is null ? null : sourceMetadataGroup[index].Changes,
                cancellationToken))).ToArray();
            plan = plans.Length == 1 ? plans[0].Plan : new(
                plans.SelectMany(item => item.Plan.Batches).GroupBy(batch => batch.Role).Select(items =>
                    new SemanticProjectionMutationBatch(items.Key, items.Sum(item => item.EffectCount),
                        items.SelectMany(item => item.Mutations).ToArray())).ToArray(),
                plans.SelectMany(item => item.Plan.OrderedMutations).ToArray(),
                plans.SelectMany(item => item.Plan.SharedMutations).ToArray(),
                plans.SelectMany(item => item.Plan.ConceptMutations).ToArray(),
                plans.SelectMany(item => item.Plan.OutgoingMutations).ToArray(),
                plans.SelectMany(item => item.Plan.IncomingMutations).ToArray());
        }
        return (plan, nextAtomSequence, plans);
    }
}
