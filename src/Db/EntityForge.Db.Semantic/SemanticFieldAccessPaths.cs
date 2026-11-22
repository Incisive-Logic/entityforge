using System.Diagnostics;
using EntityForge.Db.AccessPaths;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Pages;
using EntityForge.Db.Semantic.Durability;

namespace EntityForge.Db.Semantic;

internal sealed record SemanticFieldProjectionPlan(
    IReadOnlyList<AppendMergeMutation<Guid, byte[]>> Evidence,
    IReadOnlyList<AppendMergeMutation<SemanticAccessKey, byte[]>> Equality,
    IReadOnlyList<AppendMergeMutation<SemanticAccessKey, byte[]>> Subject)
{
    public bool IsEmpty => Evidence.Count == 0 && Equality.Count == 0 && Subject.Count == 0;
}

internal sealed class SemanticFieldAccessPaths
{
    private const int AccessMethodVersion = 1;
    private const int EqualityAccessMethodVersion = 2;
    internal static readonly PhysicalAccessPathId EvidenceId =
        new(new Guid("4b434fa4-e468-4904-9bb7-f4bc335117f3"));

    internal static readonly PhysicalAccessMethodKind EvidenceMethod =
        new("append-merge.semantic-field-evidence", AccessMethodVersion);

    internal static readonly PhysicalAccessPathId EqualityId =
        new(new Guid("eca172f8-e7d1-426f-a01b-3293a4907bb2"));

    internal static readonly PhysicalAccessPathId SubjectId =
        new(new Guid("41dffac8-b651-4a96-aec0-9a8af5332bc2"));

    internal static readonly PhysicalAccessMethodKind EqualityMethod =
        new("append-merge.semantic-field-equality", EqualityAccessMethodVersion);

    internal static readonly PhysicalAccessMethodKind SubjectMethod =
        new("append-merge.semantic-subject-field", AccessMethodVersion);

    private readonly AppendMergeAccessPath<Guid, byte[]> _evidence;
    private readonly AppendMergeAccessPath<SemanticAccessKey, byte[]> _equality;
    private readonly AppendMergeAccessPath<SemanticAccessKey, byte[]> _subject;
    private readonly PhysicalPageSpace _pageSpace;
    private readonly HashSet<Guid> _staged = [];
    private readonly HashSet<Guid> _stagedEquality = [];
    private readonly HashSet<Guid> _stagedSubject = [];

    internal IDisposable[] PinReplacementSources() =>
        [_evidence.PinReplacementSource(), _equality.PinReplacementSource(), _subject.PinReplacementSource()];

    private SemanticFieldAccessPaths(AppendMergeAccessPath<Guid, byte[]> evidence,
        AppendMergeAccessPath<SemanticAccessKey, byte[]> equality,
        AppendMergeAccessPath<SemanticAccessKey, byte[]> subject, PhysicalPageSpace pageSpace)
    {
        _evidence = evidence;
        _equality = equality;
        _subject = subject;
        _pageSpace = pageSpace;
    }

    public static async ValueTask<SemanticFieldAccessPaths> OpenAsync(IResizablePhysicalPageStore pages,
        PhysicalPageSpace pageSpace, IOrderedIndexCodec<byte[]> valueCodec,
        IComparer<byte[]> valueComparer, IOrderedIndexCodec<SemanticAccessKey> keyCodec,
        IComparer<SemanticAccessKey> keyComparer, CancellationToken cancellationToken)
    {
        PhysicalPageSpaceDirectory directory = pageSpace.CaptureDirectory();
        AppendMergeAccessPath<Guid, byte[]> evidence = await AppendMergeAccessPath<Guid, byte[]>
            .OpenAsync(pages, EvidenceId, EvidenceMethod, Entry(directory), new GuidCodec(), valueCodec,
                valueComparer: valueComparer, options: new(compactOnWrite: true),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        AppendMergeAccessPath<SemanticAccessKey, byte[]> equality = await OpenMembershipAsync(
            pages, EqualityId, EqualityMethod, directory, keyCodec, valueCodec, keyComparer,
            valueComparer, cancellationToken).ConfigureAwait(false);
        AppendMergeAccessPath<SemanticAccessKey, byte[]> subject = await OpenMembershipAsync(
            pages, SubjectId, SubjectMethod, directory, keyCodec, valueCodec, keyComparer,
            valueComparer, cancellationToken).ConfigureAwait(false);
        return new(evidence, equality, subject, pageSpace);
    }

    public static SemanticFieldProjectionPlan Plan(IReadOnlyList<SemanticProjectionMutationBatch> batches)
    {
        SemanticProjectionMutationBatch? evidence = batches.SingleOrDefault(
            item => item.Role == SemanticProjectionRole.FieldEvidence);
        SemanticProjectionMutationBatch? equality = batches.SingleOrDefault(
            item => item.Role == SemanticProjectionRole.FieldMembership);
        SemanticProjectionMutationBatch? subject = batches.SingleOrDefault(
            item => item.Role == SemanticProjectionRole.SubjectField);
        return new(evidence?.Mutations.Select(item => new AppendMergeMutation<Guid, byte[]>(
                item.Kind is OrderedPageTreeMutationKind.Add
                    ? AppendMergeMutationKind.Add : AppendMergeMutationKind.Remove,
                Guid.ParseExact(item.Key.Term, "N"), item.Value)).ToArray() ?? [],
            ToAppend(equality), ToAppend(subject));
    }

    public async ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PrepareAsync(Guid transactionId,
        long databaseVersion, PhysicalPageSpaceTransaction physical, SemanticFieldProjectionPlan plan,
        SemanticProjectionTelemetry telemetry, CancellationToken cancellationToken,
        IReadOnlyList<(long Version, SemanticFieldProjectionPlan Plan)>? group = null)
    {
        if (plan.IsEmpty)
        {
            return Array.Empty<DatabasePhysicalPageMutation>();
        }

        Task<AppendMergeMutationPlan<Guid, byte[]>>? evidencePlanning = SemanticPhysicalMutationGroup.Plan(
            _evidence, plan.Evidence, databaseVersion, cancellationToken,
            group?.Select(item => (item.Version, item.Plan.Evidence)).ToArray());
        Task<AppendMergeMutationPlan<SemanticAccessKey, byte[]>>? equalityPlanning = SemanticPhysicalMutationGroup.Plan(
            _equality, plan.Equality, databaseVersion, cancellationToken,
            group?.Select(item => (item.Version, item.Plan.Equality)).ToArray());
        Task<AppendMergeMutationPlan<SemanticAccessKey, byte[]>>? subjectPlanning = SemanticPhysicalMutationGroup.Plan(
            _subject, plan.Subject, databaseVersion, cancellationToken,
            group?.Select(item => (item.Version, item.Plan.Subject)).ToArray());
        Task[] planningTasks = [.. new Task?[] { evidencePlanning, equalityPlanning, subjectPlanning }
            .OfType<Task>()];
        await Task.WhenAll(planningTasks).ConfigureAwait(false);
        var result = new List<DatabasePhysicalPageMutation>();
        if (plan.Evidence.Count != 0)
        {
            AppendMergeMutationPlan<Guid, byte[]> prepared = evidencePlanning!.Result;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            using var activity = telemetry.StartPrepare(SemanticProjectionRole.FieldEvidence);
            AppendMergeStage stage = await _evidence.PrepareAsync(transactionId, prepared,
                physical.OpenAccessPath(EvidenceId, EvidenceMethod,
                    Entry(_pageSpace.CaptureDirectory())?.Generation ?? -1),
                cancellationToken).ConfigureAwait(false);
            telemetry.Prepared(SemanticProjectionRole.FieldEvidence, stage.PhysicalMutations,
                prepared.PlanningDuration + Stopwatch.GetElapsedTime(started),
                prepared.AllocatedBytes + GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
            _staged.Add(transactionId);
            result.AddRange(stage.PhysicalMutations);
        }
        await PrepareMembershipAsync(_equality, EqualityId, EqualityMethod,
            SemanticProjectionRole.FieldMembership, equalityPlanning?.Result, _stagedEquality, result,
            transactionId, physical, telemetry, cancellationToken).ConfigureAwait(false);
        await PrepareMembershipAsync(_subject, SubjectId, SubjectMethod,
            SemanticProjectionRole.SubjectField, subjectPlanning?.Result, _stagedSubject, result,
            transactionId, physical, telemetry, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public SemanticFieldSnapshot CaptureSnapshot(Guid? stagedTransactionId = null, long? maximumVersion = null, bool pinExactPages = false) =>
        new(_evidence.CaptureSnapshot(stagedTransactionId, maximumVersion, pinExactPages),
            _equality.CaptureSnapshot(stagedTransactionId, maximumVersion, pinExactPages),
            _subject.CaptureSnapshot(stagedTransactionId, maximumVersion, pinExactPages));

    public bool NeedsMaintenance => _equality.GetMaintenanceStatus().NeedsMaintenance ||
                                    _subject.GetMaintenanceStatus().NeedsMaintenance ||
                                    _evidence.GetMaintenanceStatus().NeedsMaintenance;

    public IReadOnlyList<AppendMergeMaintenanceStatus> MaintenanceStatuses =>
        [_evidence.GetMaintenanceStatus(), _equality.GetMaintenanceStatus(), _subject.GetMaintenanceStatus()];

    public void RecordMaintenanceStatus(SemanticProjectionTelemetry telemetry)
    {
        telemetry.MaintenanceStatus(SemanticProjectionRole.FieldEvidence, _evidence.GetMaintenanceStatus());
        telemetry.MaintenanceStatus(SemanticProjectionRole.FieldMembership, _equality.GetMaintenanceStatus());
        telemetry.MaintenanceStatus(SemanticProjectionRole.SubjectField, _subject.GetMaintenanceStatus());
    }

    public async ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PrepareMaintenanceAsync(
        Guid transactionId, PhysicalPageSpaceTransaction physical, SemanticProjectionTelemetry telemetry,
        CancellationToken cancellationToken, bool compact = false, DatabaseVersion? pruneThrough = null, long retentionFloorVersion = 0, bool reclaimOnly = false)
    {
        var result = new List<DatabasePhysicalPageMutation>();
        await PrepareEvidenceMaintenanceAsync(result, transactionId, physical, telemetry,
            forceVacuum: compact, cancellationToken, pruneThrough, retentionFloorVersion, reclaimOnly).ConfigureAwait(false);
        await PrepareMaintenanceAsync(_equality, EqualityId, EqualityMethod,
            SemanticProjectionRole.FieldMembership, _stagedEquality, result, transactionId,
            physical, telemetry, forceVacuum: compact, cancellationToken, retentionFloorVersion: retentionFloorVersion, reclaimOnly: reclaimOnly).ConfigureAwait(false);
        await PrepareMaintenanceAsync(_subject, SubjectId, SubjectMethod,
            SemanticProjectionRole.SubjectField, _stagedSubject, result, transactionId,
            physical, telemetry, forceVacuum: compact, cancellationToken, retentionFloorVersion: retentionFloorVersion, reclaimOnly: reclaimOnly).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PrepareVacuumAsync(
        Guid transactionId, PhysicalMaintenanceTarget target, PhysicalPageSpaceTransaction physical,
        SemanticProjectionTelemetry telemetry, CancellationToken cancellationToken, long retentionFloorVersion = 0)
    {
        var result = new List<DatabasePhysicalPageMutation>();
        if (target is PhysicalMaintenanceTarget.FieldEvidence)
        {
            await PrepareEvidenceMaintenanceAsync(result, transactionId, physical, telemetry,
                forceVacuum: true, cancellationToken, retentionFloorVersion: retentionFloorVersion).ConfigureAwait(false);
        }

        if (target is PhysicalMaintenanceTarget.FieldEquality)
        {
            await PrepareMaintenanceAsync(_equality, EqualityId, EqualityMethod,
                SemanticProjectionRole.FieldMembership, _stagedEquality, result, transactionId,
                physical, telemetry, forceVacuum: true, cancellationToken, retentionFloorVersion: retentionFloorVersion).ConfigureAwait(false);
        }

        if (target is PhysicalMaintenanceTarget.SubjectField)
        {
            await PrepareMaintenanceAsync(_subject, SubjectId, SubjectMethod,
                SemanticProjectionRole.SubjectField, _stagedSubject, result, transactionId,
                physical, telemetry, forceVacuum: true, cancellationToken, retentionFloorVersion: retentionFloorVersion).ConfigureAwait(false);
        }

        return result;
    }

    public void Publish(Guid transactionId)
    {
        if (_staged.Remove(transactionId))
        {
            _evidence.Publish(transactionId);
        }

        if (_stagedEquality.Remove(transactionId))
        {
            _equality.Publish(transactionId);
        }

        if (_stagedSubject.Remove(transactionId))
        {
            _subject.Publish(transactionId);
        }
    }

    public void Abandon(Guid transactionId)
    {
        if (_staged.Remove(transactionId))
        {
            _evidence.Abandon(transactionId);
        }

        if (_stagedEquality.Remove(transactionId))
        {
            _equality.Abandon(transactionId);
        }

        if (_stagedSubject.Remove(transactionId))
        {
            _subject.Abandon(transactionId);
        }
    }

    public ValueTask BuildReplacementAsync(PhysicalMaintenanceTarget target, Guid replacementId,
        long retentionFloorVersion, PhysicalPageWorkspace workspace,
        CancellationToken cancellationToken, Func<Guid, byte[], bool>? retainEvidence = null) => target switch
        {
            PhysicalMaintenanceTarget.FieldEvidence =>
                _evidence.BuildReplacementAsync(replacementId, retentionFloorVersion, workspace, false, cancellationToken, retainEvidence),
            PhysicalMaintenanceTarget.FieldEquality =>
                _equality.BuildReplacementAsync(replacementId, retentionFloorVersion, workspace, false, cancellationToken),
            PhysicalMaintenanceTarget.SubjectField =>
                _subject.BuildReplacementAsync(replacementId, retentionFloorVersion, workspace, false, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

    public (PhysicalAccessPathId Id, PhysicalAccessMethodKind Method, long Generation) ReplacementMetadata(
        PhysicalMaintenanceTarget target, Guid replacementId) => target switch
        {
            PhysicalMaintenanceTarget.FieldEvidence =>
                (EvidenceId, EvidenceMethod, _evidence.GetReplacementExpectedGeneration(replacementId)),
            PhysicalMaintenanceTarget.FieldEquality =>
                (EqualityId, EqualityMethod, _equality.GetReplacementExpectedGeneration(replacementId)),
            PhysicalMaintenanceTarget.SubjectField =>
                (SubjectId, SubjectMethod, _subject.GetReplacementExpectedGeneration(replacementId)),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

    public ValueTask CatchUpReplacementAsync(PhysicalMaintenanceTarget target, Guid replacementId,
        long retentionFloorVersion, CancellationToken cancellationToken) => target switch
        {
            PhysicalMaintenanceTarget.FieldEvidence => _evidence.CatchUpReplacementAsync(
                replacementId, retentionFloorVersion, false, cancellationToken),
            PhysicalMaintenanceTarget.FieldEquality => _equality.CatchUpReplacementAsync(
                replacementId, retentionFloorVersion, false, cancellationToken),
            PhysicalMaintenanceTarget.SubjectField => _subject.CatchUpReplacementAsync(
                replacementId, retentionFloorVersion, false, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

    public AppendMergeStage PrepareReplacement(Guid transactionId, PhysicalMaintenanceTarget target,
        Guid replacementId)
    {
        if (target is PhysicalMaintenanceTarget.FieldEvidence)
        {
            _staged.Add(transactionId);
            return _evidence.PrepareReplacement(transactionId, replacementId);
        }
        if (target is PhysicalMaintenanceTarget.FieldEquality)
        {
            _stagedEquality.Add(transactionId);
            return _equality.PrepareReplacement(transactionId, replacementId);
        }
        if (target is PhysicalMaintenanceTarget.SubjectField)
        {
            _stagedSubject.Add(transactionId);
            return _subject.PrepareReplacement(transactionId, replacementId);
        }
        throw new ArgumentOutOfRangeException(nameof(target));
    }

    public void AbandonReplacement(PhysicalMaintenanceTarget target, Guid replacementId)
    {
        if (target is PhysicalMaintenanceTarget.FieldEvidence)
        {
            _evidence.AbandonReplacement(replacementId);
        }
        else if (target is PhysicalMaintenanceTarget.FieldEquality)
        {
            _equality.AbandonReplacement(replacementId);
        }
        else if (target is PhysicalMaintenanceTarget.SubjectField)
        {
            _subject.AbandonReplacement(replacementId);
        }
    }

    private static AppendMergeMutation<SemanticAccessKey, byte[]>[] ToAppend(
        SemanticProjectionMutationBatch? batch) => batch?.Mutations.Select(item => new AppendMergeMutation<SemanticAccessKey, byte[]>(
            item.Kind is OrderedPageTreeMutationKind.Add ? AppendMergeMutationKind.Add : AppendMergeMutationKind.Remove,
            item.Key, item.Value)).ToArray() ?? [];

    private static async ValueTask<AppendMergeAccessPath<SemanticAccessKey, byte[]>> OpenMembershipAsync(
        IResizablePhysicalPageStore pages, PhysicalAccessPathId id, PhysicalAccessMethodKind method,
        PhysicalPageSpaceDirectory directory, IOrderedIndexCodec<SemanticAccessKey> keyCodec,
        IOrderedIndexCodec<byte[]> valueCodec, IComparer<SemanticAccessKey> keyComparer,
        IComparer<byte[]> valueComparer, CancellationToken cancellationToken) =>
        await AppendMergeAccessPath<SemanticAccessKey, byte[]>.OpenAsync(pages, id, method,
            directory.Entries.SingleOrDefault(entry => entry.AccessPathId == id), keyCodec, valueCodec,
            keyComparer, valueComparer, options: new(compactOnWrite: true), cancellationToken: cancellationToken).ConfigureAwait(false);

    private async ValueTask PrepareMembershipAsync(AppendMergeAccessPath<SemanticAccessKey, byte[]> access,
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
        long started = Stopwatch.GetTimestamp();
        using var activity = telemetry.StartPrepare(role);
        AppendMergeStage stage = await access.PrepareAsync(transactionId, prepared,
            physical.OpenAccessPath(id, method, current?.Generation ?? -1), cancellationToken).ConfigureAwait(false);
        telemetry.Prepared(role, stage.PhysicalMutations,
            prepared.PlanningDuration + Stopwatch.GetElapsedTime(started),
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

        long started = Stopwatch.GetTimestamp();
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
        telemetry.Maintenance(role, maintenance, Stopwatch.GetElapsedTime(started));
        foreach (DatabasePhysicalPageMutation mutation in maintenance.Stage.PhysicalMutations)
        {
            result.Add(mutation);
        }

        staged.Add(transactionId);
    }

    private static PhysicalAccessPathDirectoryEntry? Entry(PhysicalPageSpaceDirectory directory) =>
        directory.Entries.SingleOrDefault(entry => entry.AccessPathId == EvidenceId);

    private async ValueTask PrepareEvidenceMaintenanceAsync(
        ICollection<DatabasePhysicalPageMutation> result, Guid transactionId,
        PhysicalPageSpaceTransaction physical, SemanticProjectionTelemetry telemetry,
        bool forceVacuum, CancellationToken cancellationToken, DatabaseVersion? pruneThrough = null, long retentionFloorVersion = 0, bool reclaimOnly = false)
    {
        AppendMergeMaintenanceStatus status = _evidence.GetMaintenanceStatus();
        telemetry.MaintenanceStatus(SemanticProjectionRole.FieldEvidence, status);
        if (!status.NeedsMaintenance && !forceVacuum)
        {
            return;
        }

        long started = Stopwatch.GetTimestamp();
        PhysicalAccessPathDirectoryEntry? current = Entry(_pageSpace.CaptureDirectory());
        PhysicalAccessPathAllocationScope allocation = physical.OpenAccessPath(
            EvidenceId, EvidenceMethod, current?.Generation ?? -1);
        AppendMergeMaintenanceStage? maintenance = forceVacuum
            ? await _evidence.PrepareVacuumAsync(transactionId, retentionFloorVersion, allocation,
                cancellationToken, pruneThrough is { } floor ? (_, bytes) =>
                    new SemanticAssertionRowCodec().DecodeField(bytes).EndVersion is not { } end || end.Value > floor.Value : null).ConfigureAwait(false)
            : await _evidence.PrepareMaintenanceAsync(transactionId, retentionFloorVersion, allocation,
                cancellationToken, reclaimOnly).ConfigureAwait(false);
        if (maintenance is null)
        {
            physical.CancelAccessPath(allocation);
            return;
        }
        telemetry.Maintenance(SemanticProjectionRole.FieldEvidence, maintenance,
            Stopwatch.GetElapsedTime(started));
        foreach (DatabasePhysicalPageMutation mutation in maintenance.Stage.PhysicalMutations)
        {
            result.Add(mutation);
        }

        _staged.Add(transactionId);
    }

    internal sealed class SemanticFieldSnapshot(AppendMergeSnapshot<Guid, byte[]> evidence,
        AppendMergeSnapshot<SemanticAccessKey, byte[]> equality,
        AppendMergeSnapshot<SemanticAccessKey, byte[]> subject) : IDisposable
    {
        public long EvidenceGeneration => evidence.Generation;

        public long? EvidenceMaximumVersion => evidence.MaximumVersion;

        public long EqualityGeneration => equality.Generation;

        public long? EqualityMaximumVersion => equality.MaximumVersion;

        public IAsyncEnumerable<KeyValuePair<Guid, byte[]>> ReadVisibleEvidenceAsync(CancellationToken token) =>
            evidence.ReadVisibleAsync(cancellationToken: token);

        public ValueTask<AppendMergePage<Guid, byte[]>> ReadEvidencePageAsync(int pageSize,
            AppendMergeCursor<Guid, byte[]>? cursor, CancellationToken cancellationToken) =>
            evidence.ReadPageAsync(pageSize, cursor: cursor, cancellationToken: cancellationToken);

        public async ValueTask<IReadOnlyDictionary<Guid, byte[]>> FindEvidenceAsync(
            IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<Guid, IReadOnlyList<byte[]>> matches =
                await evidence.FindManyAsync(ids, cancellationToken).ConfigureAwait(false);
            var result = new Dictionary<Guid, byte[]>(matches.Count);
            foreach ((Guid id, IReadOnlyList<byte[]> values) in matches)
            {
                if (values.Count > 1)
                {
                    throw new InvalidDataException("Field evidence contains multiple visible values for one assertion.");
                }

                if (values.Count == 1)
                {
                    result.Add(id, values[0]);
                }
            }
            return result;
        }

        public async ValueTask<IReadOnlyList<byte[]>> FindEqualityAsync(SemanticAccessKey key,
            CancellationToken cancellationToken) => await equality.FindAsync(key, cancellationToken).ConfigureAwait(false);

        public async ValueTask<IReadOnlyDictionary<SemanticAccessKey, IReadOnlyList<byte[]>>> FindEqualitiesAsync(
            IReadOnlyList<SemanticAccessKey> keys, CancellationToken cancellationToken) =>
            await equality.FindManyAsync(keys, cancellationToken).ConfigureAwait(false);

        public async ValueTask<IReadOnlyDictionary<SemanticAccessKey, IReadOnlyList<byte[]>>> FindSubjectsAsync(
            IReadOnlyList<SemanticAccessKey> keys, CancellationToken cancellationToken) =>
            await subject.FindManyAsync(keys, cancellationToken).ConfigureAwait(false);

        public void Dispose()
        {
            evidence.Dispose();
            equality.Dispose();
            subject.Dispose();
        }
    }

    private sealed class GuidCodec : IOrderedIndexCodec<Guid>
    {
        public byte[] Encode(Guid value) => value.ToByteArray();

        public Guid Decode(ReadOnlySpan<byte> value) => new(value);
    }
}
