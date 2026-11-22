using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Pages;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

internal sealed class SemanticDirectionalRelationshipAccessPaths
{
    private const int GuidEncodedLength = 16;
    private const int EncodedValueLength = 3 * GuidEncodedLength;
    private const int AccessMethodVersion = 2;
    internal static readonly PhysicalAccessPathId OutgoingId =
        new(new Guid("7478dd73-013b-4c30-bb70-f4c4a22efe94"));

    internal static readonly PhysicalAccessPathId IncomingId =
        new(new Guid("133529ff-0780-4b74-bd1d-acd680170e8e"));

    internal static readonly PhysicalAccessMethodKind OutgoingMethod =
        new("append-merge.semantic-outgoing", AccessMethodVersion);

    internal static readonly PhysicalAccessMethodKind IncomingMethod =
        new("append-merge.semantic-incoming", AccessMethodVersion);

    private readonly AppendMergeAccessPath<SemanticAccessKey, byte[]> _outgoing;
    private readonly AppendMergeAccessPath<SemanticAccessKey, byte[]> _incoming;
    private readonly PhysicalPageSpace _pageSpace;
    private readonly HashSet<Guid> _stagedOutgoing = [];
    private readonly HashSet<Guid> _stagedIncoming = [];

    internal IDisposable[] PinReplacementSources() =>
        [_outgoing.PinReplacementSource(), _incoming.PinReplacementSource()];

    private SemanticDirectionalRelationshipAccessPaths(
        AppendMergeAccessPath<SemanticAccessKey, byte[]> outgoing,
        AppendMergeAccessPath<SemanticAccessKey, byte[]> incoming,
        PhysicalPageSpace pageSpace)
    {
        _outgoing = outgoing;
        _incoming = incoming;
        _pageSpace = pageSpace;
    }

    public static async ValueTask<SemanticDirectionalRelationshipAccessPaths> OpenAsync(
        IResizablePhysicalPageStore pages, PhysicalPageSpace pageSpace,
        IOrderedIndexCodec<SemanticAccessKey> keyCodec, IOrderedIndexCodec<byte[]> valueCodec,
        IComparer<SemanticAccessKey> keyComparer, IComparer<byte[]> valueComparer,
        CancellationToken cancellationToken)
    {
        PhysicalPageSpaceDirectory directory = pageSpace.CaptureDirectory();
        AppendMergeAccessPath<SemanticAccessKey, byte[]> outgoing = await OpenAsync(
            pages, OutgoingId, OutgoingMethod, directory, keyCodec, valueCodec,
            keyComparer, valueComparer, cancellationToken).ConfigureAwait(false);
        AppendMergeAccessPath<SemanticAccessKey, byte[]> incoming = await OpenAsync(
            pages, IncomingId, IncomingMethod, directory, keyCodec, valueCodec,
            keyComparer, valueComparer, cancellationToken).ConfigureAwait(false);
        return new(outgoing, incoming, pageSpace);
    }

    public async ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PrepareAsync(
        Guid transactionId, long databaseVersion, PhysicalPageSpaceTransaction physical,
        IReadOnlyList<AppendMergeMutation<SemanticAccessKey, byte[]>> outgoing,
        IReadOnlyList<AppendMergeMutation<SemanticAccessKey, byte[]>> incoming,
        SemanticProjectionTelemetry telemetry, CancellationToken cancellationToken,
        IReadOnlyList<(long Version, SemanticProjectionPlan Plan)>? group = null)
    {
        Task<AppendMergeMutationPlan<SemanticAccessKey, byte[]>>? outgoingPlanning = SemanticPhysicalMutationGroup.Plan(
            _outgoing, outgoing, databaseVersion, cancellationToken,
            group?.Select(item => (item.Version, item.Plan.OutgoingMutations)).ToArray());
        Task<AppendMergeMutationPlan<SemanticAccessKey, byte[]>>? incomingPlanning = SemanticPhysicalMutationGroup.Plan(
            _incoming, incoming, databaseVersion, cancellationToken,
            group?.Select(item => (item.Version, item.Plan.IncomingMutations)).ToArray());
        Task[] planningTasks = [.. new Task?[] { outgoingPlanning, incomingPlanning }.OfType<Task>()];
        await Task.WhenAll(planningTasks).ConfigureAwait(false);
        var result = new List<DatabasePhysicalPageMutation>();
        await PrepareAsync(_outgoing, OutgoingId, OutgoingMethod, SemanticProjectionRole.OutgoingRelationship,
            outgoingPlanning?.Result, _stagedOutgoing, result, transactionId, physical,
            telemetry, cancellationToken).ConfigureAwait(false);
        await PrepareAsync(_incoming, IncomingId, IncomingMethod, SemanticProjectionRole.IncomingRelationship,
            incomingPlanning?.Result, _stagedIncoming, result, transactionId, physical,
            telemetry, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public SemanticDirectionalRelationshipSnapshot CaptureSnapshot(Guid? stagedTransactionId = null,
        long? maximumVersion = null) =>
        new(_outgoing.CaptureSnapshot(stagedTransactionId, maximumVersion),
            _incoming.CaptureSnapshot(stagedTransactionId, maximumVersion));

    public bool NeedsMaintenance => _outgoing.GetMaintenanceStatus().NeedsMaintenance ||
                                    _incoming.GetMaintenanceStatus().NeedsMaintenance;

    public IReadOnlyList<AppendMergeMaintenanceStatus> MaintenanceStatuses =>
        [_outgoing.GetMaintenanceStatus(), _incoming.GetMaintenanceStatus()];

    public void RecordMaintenanceStatus(SemanticProjectionTelemetry telemetry)
    {
        telemetry.MaintenanceStatus(SemanticProjectionRole.OutgoingRelationship,
            _outgoing.GetMaintenanceStatus());
        telemetry.MaintenanceStatus(SemanticProjectionRole.IncomingRelationship,
            _incoming.GetMaintenanceStatus());
    }

    public async ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PrepareMaintenanceAsync(
        Guid transactionId, PhysicalPageSpaceTransaction physical,
        SemanticProjectionTelemetry telemetry, CancellationToken cancellationToken, bool compact = false, long retentionFloorVersion = 0, bool reclaimOnly = false)
    {
        var result = new List<DatabasePhysicalPageMutation>();
        await PrepareMaintenanceAsync(_outgoing, OutgoingId, OutgoingMethod,
            SemanticProjectionRole.OutgoingRelationship, _stagedOutgoing, result,
            transactionId, physical, telemetry, forceVacuum: compact, cancellationToken, retentionFloorVersion: retentionFloorVersion, reclaimOnly: reclaimOnly).ConfigureAwait(false);
        await PrepareMaintenanceAsync(_incoming, IncomingId, IncomingMethod,
            SemanticProjectionRole.IncomingRelationship, _stagedIncoming, result,
            transactionId, physical, telemetry, forceVacuum: compact, cancellationToken, retentionFloorVersion: retentionFloorVersion, reclaimOnly: reclaimOnly).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PrepareVacuumAsync(
        Guid transactionId, PhysicalMaintenanceTarget target, PhysicalPageSpaceTransaction physical,
        SemanticProjectionTelemetry telemetry, CancellationToken cancellationToken, long retentionFloorVersion = 0)
    {
        var result = new List<DatabasePhysicalPageMutation>();
        if (target is PhysicalMaintenanceTarget.OutgoingRelationship)
        {
            await PrepareMaintenanceAsync(_outgoing, OutgoingId, OutgoingMethod,
                SemanticProjectionRole.OutgoingRelationship, _stagedOutgoing, result,
                transactionId, physical, telemetry, forceVacuum: true, cancellationToken, retentionFloorVersion: retentionFloorVersion).ConfigureAwait(false);
        }

        if (target is PhysicalMaintenanceTarget.IncomingRelationship)
        {
            await PrepareMaintenanceAsync(_incoming, IncomingId, IncomingMethod,
                SemanticProjectionRole.IncomingRelationship, _stagedIncoming, result,
                transactionId, physical, telemetry, forceVacuum: true, cancellationToken, retentionFloorVersion: retentionFloorVersion).ConfigureAwait(false);
        }

        return result;
    }

    public void Publish(Guid transactionId)
    {
        if (_stagedOutgoing.Remove(transactionId))
        {
            _outgoing.Publish(transactionId);
        }

        if (_stagedIncoming.Remove(transactionId))
        {
            _incoming.Publish(transactionId);
        }
    }

    public void Abandon(Guid transactionId)
    {
        if (_stagedOutgoing.Remove(transactionId))
        {
            _outgoing.Abandon(transactionId);
        }

        if (_stagedIncoming.Remove(transactionId))
        {
            _incoming.Abandon(transactionId);
        }
    }

    public ValueTask BuildReplacementAsync(PhysicalMaintenanceTarget target, Guid replacementId,
        long retentionFloorVersion, PhysicalPageWorkspace workspace,
        CancellationToken cancellationToken) => target switch
        {
            PhysicalMaintenanceTarget.OutgoingRelationship =>
                _outgoing.BuildReplacementAsync(replacementId, retentionFloorVersion, workspace, false, cancellationToken),
            PhysicalMaintenanceTarget.IncomingRelationship =>
                _incoming.BuildReplacementAsync(replacementId, retentionFloorVersion, workspace, false, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

    public (PhysicalAccessPathId Id, PhysicalAccessMethodKind Method, long Generation) ReplacementMetadata(
        PhysicalMaintenanceTarget target, Guid replacementId) => target switch
        {
            PhysicalMaintenanceTarget.OutgoingRelationship =>
                (OutgoingId, OutgoingMethod, _outgoing.GetReplacementExpectedGeneration(replacementId)),
            PhysicalMaintenanceTarget.IncomingRelationship =>
                (IncomingId, IncomingMethod, _incoming.GetReplacementExpectedGeneration(replacementId)),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

    public ValueTask CatchUpReplacementAsync(PhysicalMaintenanceTarget target, Guid replacementId,
        long retentionFloorVersion, CancellationToken cancellationToken) => target switch
        {
            PhysicalMaintenanceTarget.OutgoingRelationship => _outgoing.CatchUpReplacementAsync(
                replacementId, retentionFloorVersion, false, cancellationToken),
            PhysicalMaintenanceTarget.IncomingRelationship => _incoming.CatchUpReplacementAsync(
                replacementId, retentionFloorVersion, false, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

    public AppendMergeStage PrepareReplacement(Guid transactionId, PhysicalMaintenanceTarget target,
        Guid replacementId)
    {
        if (target is PhysicalMaintenanceTarget.OutgoingRelationship)
        {
            _stagedOutgoing.Add(transactionId);
            return _outgoing.PrepareReplacement(transactionId, replacementId);
        }
        if (target is PhysicalMaintenanceTarget.IncomingRelationship)
        {
            _stagedIncoming.Add(transactionId);
            return _incoming.PrepareReplacement(transactionId, replacementId);
        }
        throw new ArgumentOutOfRangeException(nameof(target));
    }

    public void AbandonReplacement(PhysicalMaintenanceTarget target, Guid replacementId)
    {
        if (target is PhysicalMaintenanceTarget.OutgoingRelationship)
        {
            _outgoing.AbandonReplacement(replacementId);
        }
        else if (target is PhysicalMaintenanceTarget.IncomingRelationship)
        {
            _incoming.AbandonReplacement(replacementId);
        }
    }

    public static SemanticAccessKey Key(SemanticRelationshipDirection direction, string identityAtom) =>
        new(direction is SemanticRelationshipDirection.Outgoing
            ? SemanticAccessFamily.OutgoingRelationship : SemanticAccessFamily.IncomingRelationship,
            identityAtom, string.Empty);

    public static byte[] Value(Guid relationshipAtom, Guid relatedEntityAtom, Guid assertionId)
    {
        byte[] result = new byte[EncodedValueLength];
        relationshipAtom.TryWriteBytes(result);
        relatedEntityAtom.TryWriteBytes(result.AsSpan(GuidEncodedLength));
        assertionId.TryWriteBytes(result.AsSpan(2 * GuidEncodedLength));
        return result;
    }

    private static SemanticRelationshipAdjacencyValue Decode(byte[] value)
    {
        if (value.Length != EncodedValueLength)
        {
            throw new InvalidDataException("A Semantic relationship adjacency value is invalid.");
        }

        return new(new Guid(value.AsSpan(0, GuidEncodedLength)),
            new Guid(value.AsSpan(GuidEncodedLength, GuidEncodedLength)),
            new Guid(value.AsSpan(2 * GuidEncodedLength, GuidEncodedLength)));
    }

    private static async ValueTask<AppendMergeAccessPath<SemanticAccessKey, byte[]>> OpenAsync(
        IResizablePhysicalPageStore pages, PhysicalAccessPathId id, PhysicalAccessMethodKind method,
        PhysicalPageSpaceDirectory directory, IOrderedIndexCodec<SemanticAccessKey> keyCodec,
        IOrderedIndexCodec<byte[]> valueCodec, IComparer<SemanticAccessKey> keyComparer,
        IComparer<byte[]> valueComparer, CancellationToken cancellationToken) =>
        await AppendMergeAccessPath<SemanticAccessKey, byte[]>.OpenAsync(pages, id, method,
            directory.Entries.SingleOrDefault(entry => entry.AccessPathId == id), keyCodec, valueCodec,
            keyComparer, valueComparer, options: new(compactOnWrite: true), cancellationToken: cancellationToken).ConfigureAwait(false);

    private async ValueTask PrepareAsync(AppendMergeAccessPath<SemanticAccessKey, byte[]> access,
        PhysicalAccessPathId id, PhysicalAccessMethodKind method, SemanticProjectionRole role,
        AppendMergeMutationPlan<SemanticAccessKey, byte[]>? prepared, ISet<Guid> staged,
        ICollection<DatabasePhysicalPageMutation> result, Guid transactionId,
        PhysicalPageSpaceTransaction physical, SemanticProjectionTelemetry telemetry,
        CancellationToken cancellationToken)
    {
        if (prepared is null)
        {
            return;
        }

        PhysicalAccessPathDirectoryEntry? current = _pageSpace.CaptureDirectory().Entries
            .SingleOrDefault(entry => entry.AccessPathId == id);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var activity = telemetry.StartPrepare(role);
        AppendMergeStage stage = await access.PrepareAsync(transactionId, prepared,
            physical.OpenAccessPath(id, method, current?.Generation ?? -1), cancellationToken)
            .ConfigureAwait(false);
        telemetry.Prepared(role, stage.PhysicalMutations,
            prepared.PlanningDuration + System.Diagnostics.Stopwatch.GetElapsedTime(started),
            prepared.AllocatedBytes + GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        foreach (DatabasePhysicalPageMutation mutation in stage.PhysicalMutations)
        {
            result.Add(mutation);
        }

        staged.Add(transactionId);
    }

    private static Task<AppendMergeMutationPlan<TKey, TValue>>? StartPlanning<TKey, TValue>(
        AppendMergeAccessPath<TKey, TValue> access,
        IReadOnlyList<AppendMergeMutation<TKey, TValue>> mutations,
        long databaseVersion, CancellationToken cancellationToken) where TKey : notnull =>
        mutations.Count == 0 ? null : Task.Run(
            () => access.PlanMutations(mutations, databaseVersion, cancellationToken), cancellationToken);

    private async ValueTask PrepareMaintenanceAsync(
        AppendMergeAccessPath<SemanticAccessKey, byte[]> access, PhysicalAccessPathId id,
        PhysicalAccessMethodKind method, SemanticProjectionRole role, ISet<Guid> staged,
        ICollection<DatabasePhysicalPageMutation> result, Guid transactionId,
        PhysicalPageSpaceTransaction physical, SemanticProjectionTelemetry telemetry,
        bool forceVacuum, CancellationToken cancellationToken, long retentionFloorVersion, bool reclaimOnly = false)
    {
        AppendMergeMaintenanceStatus status = access.GetMaintenanceStatus();
        telemetry.MaintenanceStatus(role, status);
        if (!status.NeedsMaintenance && !forceVacuum)
        {
            return;
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        PhysicalAccessPathDirectoryEntry? current = _pageSpace.CaptureDirectory().Entries
            .SingleOrDefault(entry => entry.AccessPathId == id);
        PhysicalAccessPathAllocationScope allocation = physical.OpenAccessPath(
            id, method, current?.Generation ?? -1);
        AppendMergeMaintenanceStage? maintenance = forceVacuum
            ? await access.PrepareVacuumAsync(transactionId, retentionFloorVersion, allocation,
                cancellationToken).ConfigureAwait(false)
            : await access.PrepareMaintenanceAsync(transactionId, retentionFloorVersion, allocation,
                cancellationToken, reclaimOnly).ConfigureAwait(false);
        if (maintenance is null)
        {
            physical.CancelAccessPath(allocation);
            return;
        }
        telemetry.Maintenance(role, maintenance,
            System.Diagnostics.Stopwatch.GetElapsedTime(started));
        foreach (DatabasePhysicalPageMutation mutation in maintenance.Stage.PhysicalMutations)
        {
            result.Add(mutation);
        }

        staged.Add(transactionId);
    }

    internal sealed class SemanticDirectionalRelationshipSnapshot(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> outgoing,
        AppendMergeSnapshot<SemanticAccessKey, byte[]> incoming) : IDisposable
    {
        public long Generation(SemanticRelationshipDirection direction) =>
            direction is SemanticRelationshipDirection.Outgoing ? outgoing.Generation : incoming.Generation;

        public long? MaximumVersion(SemanticRelationshipDirection direction) =>
            direction is SemanticRelationshipDirection.Outgoing ? outgoing.MaximumVersion : incoming.MaximumVersion;

        public async ValueTask<IReadOnlyList<Guid>> FindAsync(string identityAtom,
            SemanticRelationshipDirection direction, Guid? relationshipAtom,
            CancellationToken cancellationToken)
        {
            AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot =
                direction is SemanticRelationshipDirection.Outgoing ? outgoing : incoming;
            IReadOnlyList<byte[]> values = await snapshot.FindAsync(Key(direction, identityAtom),
                cancellationToken).ConfigureAwait(false);
            return values.Select(Decode)
                .Where(value => relationshipAtom is null || value.RelationshipAtom == relationshipAtom.Value)
                .Select(value => value.AssertionId).ToArray();
        }

        public async ValueTask<IReadOnlyDictionary<string, IReadOnlyList<Guid>>> FindManyAsync(
            IReadOnlyList<string> identityAtoms, SemanticRelationshipDirection direction,
            CancellationToken cancellationToken)
        {
            AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot =
                direction is SemanticRelationshipDirection.Outgoing ? outgoing : incoming;
            SemanticAccessKey[] keys = identityAtoms.Distinct(StringComparer.Ordinal)
                .Select(identity => Key(direction, identity)).ToArray();
            IReadOnlyDictionary<SemanticAccessKey, IReadOnlyList<byte[]>> values =
                await snapshot.FindManyAsync(keys, cancellationToken).ConfigureAwait(false);
            return keys.ToDictionary(key => key.Term,
                key => values.TryGetValue(key, out IReadOnlyList<byte[]>? encoded)
                    ? (IReadOnlyList<Guid>)encoded.Select(Decode).Select(item => item.AssertionId).ToArray()
                    : Array.Empty<Guid>(), StringComparer.Ordinal);
        }

        public async ValueTask<IReadOnlyDictionary<string, IReadOnlyList<SemanticRelationshipAdjacencyValue>>>
            FindManyCandidatesAsync(IReadOnlyList<string> identityAtoms,
                SemanticRelationshipDirection direction, Guid relationshipAtom,
                CancellationToken cancellationToken)
        {
            AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot =
                direction is SemanticRelationshipDirection.Outgoing ? outgoing : incoming;
            SemanticAccessKey[] keys = identityAtoms.Distinct(StringComparer.Ordinal)
                .Select(identity => Key(direction, identity)).ToArray();
            IReadOnlyDictionary<SemanticAccessKey, IReadOnlyList<byte[]>> values =
                await snapshot.FindManyAsync(keys, cancellationToken).ConfigureAwait(false);
            return keys.ToDictionary(key => key.Term,
                key => values.TryGetValue(key, out IReadOnlyList<byte[]>? encoded)
                    ? (IReadOnlyList<SemanticRelationshipAdjacencyValue>)encoded.Select(Decode)
                        .Where(item => item.RelationshipAtom == relationshipAtom).ToArray()
                    : Array.Empty<SemanticRelationshipAdjacencyValue>(), StringComparer.Ordinal);
        }

        public void Dispose()
        {
            outgoing.Dispose();
            incoming.Dispose();
        }
    }

    internal readonly record struct SemanticRelationshipAdjacencyValue(
        Guid RelationshipAtom, Guid RelatedEntityAtom, Guid AssertionId);
}
