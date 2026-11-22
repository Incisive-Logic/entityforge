using System.Diagnostics;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Storage.Pages;

/// <summary>Page-resident append/merge access method built from immutable sorted runs.</summary>
public sealed partial class AppendMergeAccessPath<TKey, TValue> where TKey : notnull
{
    private readonly IResizablePhysicalPageStore _pages;
    private readonly AppendMergeRunCodec<TKey, TValue> _runs;
    private readonly AppendMergeRunWriter<TKey, TValue> _runWriter;
    private readonly AppendMergeRunReader<TKey, TValue> _runReader;
    private readonly AppendMergeCompactionPlanner _compactionPlanner;
    private readonly AppendMergeCompactionExecutor<TKey, TValue> _compactionExecutor;
    private readonly bool _compactOnWrite;
    private readonly SortedDictionary<long, int> _replacementPins = [];

    /// <summary>Protects an incremental replacement's source lineage while allowing
    /// new runs to be committed. Retired source pages stay pinned until disposal.</summary>
    public IDisposable PinReplacementSource()
    {
        lock (_gate)
        {
            long generation = _state.Generation;
            _replacementPins[generation] = _replacementPins.GetValueOrDefault(generation) + 1;
            AddExactReference(_state);
            return new ReplacementPin(this, generation);
        }
    }

    private sealed class ReplacementPin(AppendMergeAccessPath<TKey, TValue> owner, long generation) : IDisposable
    {
        private AppendMergeAccessPath<TKey, TValue>? _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null)
            {
                return;
            }

            lock (current._gate)
            {
                int remaining = current._replacementPins[generation] - 1;
                if (remaining == 0)
                {
                    current._replacementPins.Remove(generation);
                }
                else
                {
                    current._replacementPins[generation] = remaining;
                }
            }
            current.ReleaseExactReference(generation);
        }
    }

    private IDisposable? PinActiveReplacementSource()
    {
        lock (_gate)
        {
            return _replacementPins.Count == 0 ? null : PinReplacementSource();
        }
    }

    private IReadOnlyList<AppendMergeRunDescriptor> SelectCompactionRuns(State current,
        long maximumInputPages = long.MaxValue)
    {
        lock (_gate)
        {
            if (_replacementPins.Count == 0)
            {
                return _compactionPlanner.Select(current.Runs, maximumInputPages);
            }

            long protectedThrough = _replacementPins.Last().Key;
            foreach (var replacement in _replacements.Values)
            {
                protectedThrough = Math.Max(protectedThrough, replacement.ExpectedGeneration);
            }
            // Keep captured source lineage intact while allowing newer runs to
            // merge normally. Otherwise a long private scan would strand all writes.
            return _compactionPlanner.Select(current.Runs.Where(run => run.Generation > protectedThrough).ToArray(), maximumInputPages);
        }
    }

    private const long MaximumWriteCompactionInputBytes = 512 * 1024;
    private readonly AppendMergeReclaimer _reclaimer = new();
    private readonly AppendMergeManifestCodec _manifest = new();
    private readonly IComparer<TKey> _keys;
    private readonly IComparer<TValue> _values;
    private readonly object _gate = new();
    private State _state;
    private State? _maintenanceStatusState;
    private long? _maintenanceStatusOldestSnapshot;
    private AppendMergeMaintenanceStatus? _maintenanceStatus;
    private readonly Dictionary<Guid, StagedState> _staged = [];
    private readonly Dictionary<Guid, ReplacementState> _replacements = [];
    private readonly Dictionary<long, int> _snapshotReferences = [];

    private AppendMergeAccessPath(IResizablePhysicalPageStore pages,
        AppendMergeRunCodec<TKey, TValue> runs, AppendMergeOptions options,
        IComparer<TKey> keys, IComparer<TValue> values, State state)
    {
        _pages = pages;
        _runs = runs;
        _runWriter = new(runs, options);
        _runReader = new(pages, runs);
        _compactionPlanner = new(options);
        _compactionExecutor = new(_runReader, _runWriter, keys, values);
        _compactOnWrite = options.CompactOnWrite;
        _keys = keys;
        _values = values;
        _state = state;
    }

    /// <summary>Gets the published manifest generation.</summary>
    public long Generation
    {
        get
        {
            lock (_gate)
            {
                return _state.Generation;
            }
        }
    }

    /// <summary>Gets the number of immutable runs in the published manifest.</summary>
    public int RunCount
    {
        get
        {
            lock (_gate)
            {
                return _state.Runs.Count;
            }
        }
    }

    /// <summary>Returns whether the explicit compaction policy currently selects work.</summary>
    public bool NeedsCompaction
    {
        get
        {
            lock (_gate)
            {
                return _compactionPlanner.Select(_state.Runs).Count != 0;
            }
        }
    }

    /// <summary>Returns current bounded run, compaction, retirement and reclamation debt.</summary>
    public AppendMergeMaintenanceStatus GetMaintenanceStatus()
    {
        lock (_gate)
        {
            long? oldest = AppendMergeReclaimer.OldestProtectedGeneration(_snapshotReferences.Keys);
            if (ReferenceEquals(_maintenanceStatusState, _state) &&
                _maintenanceStatusOldestSnapshot == oldest && _maintenanceStatus is not null)
            {
                return _maintenanceStatus;
            }

            int selectedRuns = _compactionPlanner.Select(_state.Runs).Count;
            int retiredPages = 0, reclaimablePages = 0;
            for (int index = 0; index < _state.Retired.Count; index++)
            {
                AppendMergeRetiredPages item = _state.Retired[index];
                int pages = 0;
                for (int extent = 0; extent < item.Extents.Count; extent++)
                {
                    pages = checked(pages + checked((int)item.Extents[extent].PageCount));
                }

                retiredPages = checked(retiredPages + pages);
                if (AppendMergeReclaimer.CanReclaim(item, oldest) && !IsExactlyProtected(item))
                {
                    reclaimablePages = checked(reclaimablePages + pages);
                }
            }
            _maintenanceStatusState = _state;
            _maintenanceStatusOldestSnapshot = oldest;
            return _maintenanceStatus = new(_state.Runs.Count, selectedRuns, retiredPages, reclaimablePages);
        }
    }

    /// <summary>Opens the exact access path named by a page-space directory entry.</summary>
    public static async ValueTask<AppendMergeAccessPath<TKey, TValue>> OpenAsync(
        IResizablePhysicalPageStore pages, PhysicalAccessPathId accessPathId,
        PhysicalAccessMethodKind method, PhysicalAccessPathDirectoryEntry? published,
        IOrderedIndexCodec<TKey> keyCodec, IOrderedIndexCodec<TValue> valueCodec,
        IComparer<TKey>? keyComparer = null, IComparer<TValue>? valueComparer = null,
        AppendMergeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(method);
        var manifest = new AppendMergeManifestCodec();
        var runCodec = new AppendMergeRunCodec<TKey, TValue>(keyCodec, valueCodec);
        IComparer<TKey> keys = keyComparer ?? Comparer<TKey>.Default;
        try
        {
            State state;
            if (published is null)
            {
                state = new(accessPathId.Value, 0, [], [], []);
            }
            else
            {
                if (published.AccessPathId != accessPathId || published.Method != method)
                {
                    throw new PhysicalPageCorruptionException(
                        "The append/merge access path does not match its directory entry.");
                }

                AppendMergeManifestRoot root;
                try
                {
                    root = manifest.DecodeRoot(
                        (await pages.ReadAsync(published.RootPage, cancellationToken).ConfigureAwait(false)).Span,
                        published.RootPage);
                }
                catch (PhysicalPageCorruptionException exception)
                {
                    throw new PhysicalPageCorruptionException(
                        $"Append/merge access path {accessPathId.Value} generation {published.Generation} " +
                        $"has invalid manifest root page {published.RootPage.Value}: {exception.Message}");
                }
                if (root.AccessPathId != accessPathId.Value || root.Generation != published.Generation)
                {
                    throw new PhysicalPageCorruptionException(
                        "The append/merge manifest does not match its directory entry.");
                }

                if (root.Branches.Any(branch => !OwnedBy(published, branch)))
                {
                    throw new PhysicalPageCorruptionException(
                        "An append/merge manifest branch is outside its owned page extents.");
                }

                root = await manifest.ResolveBranchIndexAsync(root, published.RootPage,
                    page => pages.ReadAsync(page, cancellationToken), page => OwnedBy(published, page), cancellationToken).ConfigureAwait(false);
                var branchImages = new List<(PhysicalPageId, ReadOnlyMemory<byte>)>();
                foreach (PhysicalPageId branch in root.Branches)
                {
                    branchImages.Add((branch,
                        await pages.ReadAsync(branch, cancellationToken).ConfigureAwait(false)));
                }

                AppendMergeManifestBranch decoded = manifest.DecodeBranches(branchImages);
                IReadOnlyList<AppendMergeRunDescriptor> runs = decoded.Runs.Concat(root.InlineRuns).ToArray();
                IReadOnlyList<AppendMergeRetiredPages> retired = decoded.Retired.Concat(root.InlineRetired).ToArray();
                AppendMergeManifestCodec.ValidateManifestOwnership(runs, retired, [published.RootPage, .. root.Branches, .. root.IndexPages]);
                if (runs.Select(run => run.Generation).Distinct().Count() != runs.Count ||
                    runs.Any(run => run.Generation > root.Generation) ||
                    runs.SelectMany(run => run.OwnedPages).Any(page => !OwnedBy(published, page)) ||
                    retired.SelectMany(item => item.Pages).Any(page => !OwnedBy(published, page)) ||
                    runs.Any(run => keys.Compare(runCodec.DecodeKey(run.MinimumKey.Span),
                        runCodec.DecodeKey(run.MaximumKey.Span)) > 0))
                {
                    throw new PhysicalPageCorruptionException(
                        "Append/merge run generations or owned page extents are invalid.");
                }

                state = new(root.AccessPathId, root.Generation,
                    runs.OrderByDescending(run => run.Generation).ToArray(), retired,
                    new[] { published.RootPage }.Concat(root.Branches).Concat(root.IndexPages).ToArray())
                {
                    RunBranches = decoded.RunBranches
                };
            }
            return new(pages, runCodec, options ?? new AppendMergeOptions(), keys,
                valueComparer ?? Comparer<TValue>.Default, state);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PhysicalPageException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AppendMergeException("The append/merge access path could not be opened.", exception);
        }
    }

    /// <summary>Stages one immutable run and replacement manifest.</summary>
    public ValueTask<AppendMergeStage> PrepareAsync(Guid transactionId,
        IReadOnlyList<AppendMergeMutation<TKey, TValue>> mutations,
        long databaseVersion, PhysicalAccessPathAllocationScope allocation,
        CancellationToken cancellationToken = default)
    {
        AppendMergeMutationPlan<TKey, TValue> plan = PlanMutations(
            mutations, databaseVersion, cancellationToken);
        return PrepareAsync(transactionId, plan, allocation, cancellationToken);
    }

    /// <summary>
    /// Sorts and duplicate-resolves mutations without allocating physical pages. The returned plan
    /// belongs to this access-path instance and can be materialized later by <see cref="PrepareAsync(Guid,AppendMergeMutationPlan{TKey,TValue},PhysicalAccessPathAllocationScope,CancellationToken)"/>.
    /// </summary>
    public AppendMergeMutationPlan<TKey, TValue> PlanMutations(
        IReadOnlyList<AppendMergeMutation<TKey, TValue>> mutations,
        long databaseVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        cancellationToken.ThrowIfCancellationRequested();
        if (databaseVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(databaseVersion));
        }

        long started = Stopwatch.GetTimestamp();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            AppendMergeRunEntry<TKey, TValue>[] normalized = Normalize(mutations, databaseVersion);
            AppendMergeRunPlan<TKey, TValue> run = _runWriter.Plan(normalized);
            return new(this, run, Stopwatch.GetElapsedTime(started),
                GC.GetAllocatedBytesForCurrentThread() - allocated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PhysicalPageException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AppendMergeException("The append/merge mutations could not be planned.", exception);
        }
    }

    /// <summary>Combines owned plans in strictly increasing version order without discarding intermediate versions.</summary>
    public AppendMergeMutationPlan<TKey, TValue> PlanMutationGroup(
        IReadOnlyList<AppendMergeMutationPlan<TKey, TValue>> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        long previous = 0;
        var entries = new List<AppendMergeRunEntry<TKey, TValue>>();
        foreach (var plan in plans)
        {
            if (!ReferenceEquals(plan.Owner, this))
            {
                throw new AppendMergeException("A grouped plan belongs to another access path.");
            }

            if (plan.Count == 0)
            {
                continue;
            }

            long version = plan.Run.Entries[0].Entry.Version;
            if (version <= previous || plan.Run.Entries.Any(entry => entry.Entry.Version != version))
            {
                throw new AppendMergeException("Grouped plans must each contain one strictly increasing version.");
            }

            previous = version;
            entries.AddRange(plan.Run.Entries.Select(entry => entry.Entry));
        }
        var ordered = entries.OrderBy(entry => entry.Key, _keys).ThenBy(entry => entry,
                Comparer<AppendMergeRunEntry<TKey, TValue>>.Create((left, right) => _values.CompareAt(left.Key, left.Value, right.Value)))
            .ThenBy(entry => entry.Version).ToArray();
        return new(this, _runWriter.Plan(ordered), TimeSpan.Zero, 0);
    }

    /// <summary>Stages one previously planned immutable run and replacement manifest.</summary>
    public async ValueTask<AppendMergeStage> PrepareAsync(Guid transactionId,
        AppendMergeMutationPlan<TKey, TValue> plan,
        PhysicalAccessPathAllocationScope allocation,
        CancellationToken cancellationToken = default)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A transaction identity is required.", nameof(transactionId));
        }

        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(allocation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(plan.Owner, this))
        {
            throw new AppendMergeException("The mutation plan belongs to another append/merge access path.");
        }

        State current;
        lock (_gate)
        {
            if (_staged.Count != 0)
            {
                throw new AppendMergeException("Only one append/merge mutation may be staged.");
            }

            current = _state;
        }
        try
        {
            long generation = checked(current.Generation + 1);
            if (allocation.TransactionId != transactionId || allocation.AccessPathId.Value != current.AccessPathId ||
                allocation.ProposedGeneration != generation)
            {
                throw new AppendMergeException("The allocation scope does not match the append/merge mutation.");
            }

            (AppendMergeRetiredPages[] retainedRetired, AppendMergeRetiredPages[] reclaimed) = Reclamation(current);
            foreach (PhysicalPageId page in reclaimed.SelectMany(item => item.Pages))
            {
                allocation.ReleasePage(page);
            }

            if (plan.Count == 0)
            {
                return new AppendMergeStage(transactionId, current.Generation, []);
            }

            IReadOnlyList<AppendMergeRunDescriptor> selected = _compactOnWrite
                ? SelectCompactionRuns(current, MaximumWriteCompactionInputBytes / _pages.PageSize) : [];
            // Keep all historical versions and tombstones. This changes run layout only;
            // expiry/retention remains the explicit maintenance boundary.
            AppendMergeRunImage run = selected.Count == 0 ? _runWriter.WriteInlineOrDisk(plan.Run, generation, allocation) :
                await _compactionExecutor.ExecuteWithPendingAsync(selected, plan.Run,
                    generation, allocation, cancellationToken).ConfigureAwait(false)
                    ?? throw new AppendMergeException("A non-empty mutation produced no run.");
            var images = run.Pages.ToDictionary(item => item.Key, item => item.Value);
            var selectedGenerations = selected.Select(item => item.Generation).ToHashSet();
            AppendMergeRunDescriptor[] visibleRuns = new[] { run.Descriptor }
                .Concat(current.Runs.Where(item => !selectedGenerations.Contains(item.Generation))).ToArray();
            var (manifest, retired) = EncodeChangedManifest(current, generation, visibleRuns,
                selected, retainedRetired, allocation);
            allocation.Complete(manifest.RootPageId);
            images.Add(manifest.RootPageId, manifest.Root);
            foreach ((PhysicalPageId page, ReadOnlyMemory<byte> image) in manifest.Branches)
            {
                images.Add(page, image);
            }

            IReadOnlyList<DatabasePhysicalPageMutation> physical = CreatePhysicalMutations(images);
            var state = new State(current.AccessPathId, generation, visibleRuns, retired,
                new[] { manifest.RootPageId }.Concat(manifest.AllBranches).ToArray())
            {
                RunBranches = manifest.RunBranches
            };
            lock (_gate)
            {
                _staged.Add(transactionId, new(state));
            }

            return new AppendMergeStage(transactionId, generation, physical);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PhysicalPageException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AppendMergeException("The append/merge mutation could not be staged.", exception);
        }
    }

    /// <summary>Captures an immutable view. Long maintenance scans can pin exact published
    /// pages so unrelated later retirements remain reclaimable. Staged views retain generation protection.</summary>
    public AppendMergeSnapshot<TKey, TValue> CaptureSnapshot(Guid? stagedTransactionId = null,
        long? maximumVersion = null, bool pinExactPages = false)
    {
        if (maximumVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumVersion));
        }

        if (pinExactPages && stagedTransactionId is not null)
        {
            throw new ArgumentException("Exact page protection requires a published snapshot.", nameof(stagedTransactionId));
        }

        lock (_gate)
        {
            State state = stagedTransactionId is Guid transactionId &&
                          _staged.TryGetValue(transactionId, out StagedState? staged)
                ? staged.State : _state;
            if (pinExactPages)
            {
                AddExactReference(state);
            }
            else
            {
                _snapshotReferences[state.Generation] = _snapshotReferences.GetValueOrDefault(state.Generation) + 1;
            }

            long generation = state.Generation;
            return new(_runs, _runReader, state.Generation, state.Runs, _keys, _values, maximumVersion,
                () =>
{
    if (pinExactPages)
    {
        ReleaseExactReference(generation);
    }
    else
    {
        ReleaseSnapshot(generation);
    }

});
        }
    }

    /// <summary>Stages one size-tiered compaction or reclamation-only manifest.</summary>
    public async ValueTask<AppendMergeMaintenanceStage?> PrepareMaintenanceAsync(Guid transactionId,
        long retentionFloorVersion, PhysicalAccessPathAllocationScope allocation,
        CancellationToken cancellationToken = default, bool reclaimOnly = false) => await PrepareMaintenanceCoreAsync(transactionId,
        retentionFloorVersion, allocation, forceCompaction: false, cancellationToken, reclaimOnly: reclaimOnly).ConfigureAwait(false);

    /// <summary>Stages a full streaming rewrite of all visible runs for physical-space vacuuming.</summary>
    public async ValueTask<AppendMergeMaintenanceStage?> PrepareVacuumAsync(Guid transactionId,
        long retentionFloorVersion, PhysicalAccessPathAllocationScope allocation,
        CancellationToken cancellationToken = default, Func<TKey, TValue, bool>? retain = null) => await PrepareMaintenanceCoreAsync(transactionId,
        retentionFloorVersion, allocation, forceCompaction: true, cancellationToken, retain).ConfigureAwait(false);

    /// <summary>Builds one compact replacement manifest outside live transaction staging.</summary>
    public async ValueTask BuildReplacementAsync(Guid replacementId, long retentionFloorVersion,
        PhysicalPageWorkspace workspace, bool flushPages = true,
        CancellationToken cancellationToken = default, Func<TKey, TValue, bool>? retain = null)
    {
        if (retentionFloorVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionFloorVersion));
        }

        ArgumentNullException.ThrowIfNull(workspace);
        using var lineage = PinActiveReplacementSource();
        State current;
        lock (_gate)
        {
            if (_replacements.ContainsKey(replacementId))
            {
                throw new AppendMergeException("The append/merge replacement already exists.");
            }

            current = _state;
            AddExactReference(current);
        }
        try
        {
            long generation = checked(current.Generation + 1);
            AppendMergeRunDescriptor? replacement = current.Runs.Count == 0 ? null :
                await _compactionExecutor.ExecuteStreamingAsync(current.Runs, generation,
                    retentionFloorVersion, true, workspace,
                    (pages, token) => WriteReplacementPagesAsync(pages, validateRunRecords: true, token),
                    cancellationToken, retain)
                    .ConfigureAwait(false);
            await ValidateReplacementOverflowAsync(replacement, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<AppendMergeRunDescriptor> runs = replacement is null
                ? [] : [replacement];
            AppendMergeRetiredPages[] retired = RetirePublishedState(current);
            AppendMergeManifestImage manifest = _manifest.Encode(current.AccessPathId, generation,
                runs, retired, workspace);
            workspace.Complete(manifest.RootPageId);
            await WriteReplacementPagesAsync(
                [new(manifest.RootPageId, manifest.Root), .. manifest.Branches],
                validateRunRecords: false, cancellationToken)
                .ConfigureAwait(false);
            if (flushPages)
            {
                await _pages.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var state = new State(current.AccessPathId, generation, runs, retired,
                [manifest.RootPageId, .. manifest.AllBranches])
            {
                RunBranches = manifest.RunBranches
            };
            lock (_gate)
            {
                _replacements.Add(replacementId,
                new(current.Generation, state, workspace,
                    current.Runs.Select(run => run.Generation).ToHashSet()));
            }
        }
        finally
        {
            ReleaseExactReference(current.Generation);
        }
    }

    /// <summary>Builds one compact replacement directly from a complete caller-owned mutation set.</summary>
    public async ValueTask BuildReplacementAsync(Guid replacementId,
        IReadOnlyList<AppendMergeMutation<TKey, TValue>> mutations, long databaseVersion,
        PhysicalPageWorkspace workspace, bool flushPages = true,
        CancellationToken cancellationToken = default)
    {
        if (replacementId == Guid.Empty)
        {
            throw new ArgumentException("A replacement identity is required.", nameof(replacementId));
        }

        ArgumentNullException.ThrowIfNull(mutations);
        if (databaseVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(databaseVersion));
        }

        ArgumentNullException.ThrowIfNull(workspace);
        State current;
        lock (_gate)
        {
            if (_replacements.ContainsKey(replacementId))
            {
                throw new AppendMergeException("The append/merge replacement already exists.");
            }

            current = _state;
        }
        long generation = checked(current.Generation + 1);
        AppendMergeRunEntry<TKey, TValue>[] normalized = Normalize(mutations, databaseVersion);
        AppendMergeRunImage? run = normalized.Length == 0 ? null :
            _runWriter.Write(normalized, generation, workspace);
        if (run is not null)
        {
            await WriteReplacementPagesAsync(run.Pages.ToArray(), validateRunRecords: true,
                cancellationToken).ConfigureAwait(false);
        }

        await ValidateReplacementOverflowAsync(run?.Descriptor, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AppendMergeRunDescriptor> runs = run is null ? [] : [run.Descriptor];
        AppendMergeRetiredPages[] retired = RetirePublishedState(current);
        AppendMergeManifestImage manifest = _manifest.Encode(current.AccessPathId, generation,
            runs, retired, workspace);
        workspace.Complete(manifest.RootPageId);
        await WriteReplacementPagesAsync(
            [new(manifest.RootPageId, manifest.Root), .. manifest.Branches],
            validateRunRecords: false, cancellationToken).ConfigureAwait(false);
        if (flushPages)
        {
            await _pages.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var state = new State(current.AccessPathId, generation, runs, retired,
            [manifest.RootPageId, .. manifest.AllBranches])
        {
            RunBranches = manifest.RunBranches
        };
        lock (_gate)
        {
            if (_state.Generation != current.Generation)
            {
                throw new AppendMergeReplacementStaleException(current.Generation, _state.Generation);
            }

            _replacements.Add(replacementId, new(current.Generation, state, workspace,
                current.Runs.Select(item => item.Generation).ToHashSet()));
        }
    }

    /// <summary>Gets the generation against which a replacement must be adopted.</summary>
    public long GetReplacementExpectedGeneration(Guid replacementId)
    {
        lock (_gate)
        {
            return RequiredReplacement(replacementId).ExpectedGeneration;
        }
    }

    /// <summary>
    /// Extends an unpublished compact replacement with immutable runs committed after its source
    /// generation. Callers must prevent new live publications until replacement adoption.
    /// </summary>
    public async ValueTask CatchUpReplacementAsync(Guid replacementId,
        long retentionFloorVersion, bool flushPages = true,
        CancellationToken cancellationToken = default)
    {
        if (retentionFloorVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionFloorVersion));
        }

        using var lineage = PinActiveReplacementSource();
        ReplacementState replacement;
        State current;
        lock (_gate)
        {
            replacement = RequiredReplacement(replacementId);
            current = _state;
        }
        if (current.Generation == replacement.ExpectedGeneration)
        {
            return;
        }

        if (current.Generation < replacement.ExpectedGeneration)
        {
            throw new AppendMergeException("The append/merge replacement generation is ahead of the live state.");
        }

        if (!replacement.SourceRunGenerations.IsSubsetOf(
                current.Runs.Select(run => run.Generation).ToHashSet()))
        {
            throw new AppendMergeReplacementStaleException(replacement.ExpectedGeneration, current.Generation);
        }

        IReadOnlyList<AppendMergeRunDescriptor> deltaRuns = current.Runs
            .Where(run => run.Generation > replacement.ExpectedGeneration)
            .OrderByDescending(run => run.Generation)
            .ToArray();
        int allocationMark = replacement.Workspace.CaptureAllocationMark();
        PhysicalPageId previousRoot = replacement.Workspace.RootPage;
        replacement.Workspace.Reopen();
        try
        {
            long generation = checked(current.Generation + 1);
            AppendMergeRunDescriptor? delta = deltaRuns.Count == 0 ? null :
                await _compactionExecutor.ExecuteStreamingAsync(deltaRuns, generation,
                    retentionFloorVersion, false, replacement.Workspace,
                    (pages, token) => WriteReplacementPagesAsync(pages, validateRunRecords: true, token),
                    cancellationToken)
                    .ConfigureAwait(false);
            await ValidateReplacementOverflowAsync(delta, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<AppendMergeRunDescriptor> runs = delta is null
                ? replacement.State.Runs : [delta, .. replacement.State.Runs];
            AppendMergeRetiredPages[] retired = RetirePublishedState(current);
            AppendMergeManifestImage manifest = _manifest.Encode(current.AccessPathId, generation,
                runs, retired, replacement.Workspace);
            replacement.Workspace.Complete(manifest.RootPageId);
            await WriteReplacementPagesAsync(
                [new(manifest.RootPageId, manifest.Root), .. manifest.Branches],
                validateRunRecords: false, cancellationToken)
                .ConfigureAwait(false);
            if (flushPages)
            {
                await _pages.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var state = new State(current.AccessPathId, generation, runs, retired,
                [manifest.RootPageId, .. manifest.AllBranches])
            {
                RunBranches = manifest.RunBranches
            };
            lock (_gate)
            {
                if (_state.Generation != current.Generation && (_replacementPins.Count == 0 ||
                    !current.Runs.Select(run => run.Generation).ToHashSet().IsSubsetOf(
                        _state.Runs.Select(run => run.Generation).ToHashSet())))
                {
                    throw new AppendMergeReplacementStaleException(current.Generation, _state.Generation);
                }

                replacement.Workspace.ReleasePages(replacement.State.ManifestPages);
                _replacements[replacementId] = new(current.Generation, state, replacement.Workspace,
                    current.Runs.Select(run => run.Generation).ToHashSet());
            }
        }
        catch
        {
            replacement.Workspace.RollbackTo(allocationMark, previousRoot);
            throw;
        }
    }

    private async ValueTask ValidateReplacementOverflowAsync(AppendMergeRunDescriptor? run, CancellationToken token)
    {
        if (run is null || run.OverflowExtents.Count == 0)
        {
            return;
        }
        int count = 0;
        await foreach (var entry in _runReader.ReadAsync(run, token).ConfigureAwait(false))
        {
            count = checked(count + 1);
        }
        if (count != run.EntryCount)
        {
            throw new PhysicalPageCorruptionException("Replacement run entry count is invalid.");
        }
    }

    private async ValueTask WriteReplacementPagesAsync(
        IReadOnlyList<KeyValuePair<PhysicalPageId, ReadOnlyMemory<byte>>> images,
        bool validateRunRecords,
        CancellationToken cancellationToken)
    {
        if (images.Count == 0)
        {
            return;
        }

        long required = images.Max(item => item.Key.Value) + 1;
        // A budgeted store grows the file only after reserving and journalling
        // its batch. Do not extend unjournalled private output before a pause.
        if (_pages is not PhysicalMaintenancePageStore &&
            await _pages.GetPageCountAsync(cancellationToken).ConfigureAwait(false) < required)
        {
            await _pages.EnsureCapacityAsync(required, cancellationToken).ConfigureAwait(false);
        }

        foreach ((PhysicalPageId page, ReadOnlyMemory<byte> image) in images)
        {
            if (validateRunRecords)
            {
                foreach (ReadOnlyMemory<byte> record in SlottedDataPage.ReadOccupiedRecords(image, page))
                {
                    if (record.IsEmpty || record.Span[0] is not (PhysicalOverflowRecord.ReferenceKind or PhysicalOverflowRecord.ChunkKind))
                    {
                        _runs.Decode(record.Span);
                    }
                    // Overflow chains are validated with complete run ownership after all batches are written.
                }
            }

            if (_pages is not PhysicalMaintenancePageStore)
            {
                await _pages.WriteAsync(page, image, cancellationToken).ConfigureAwait(false);
            }
        }
        if (_pages is PhysicalMaintenancePageStore maintenance)
        {
            await maintenance.WriteBatchAsync(images, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Stages publication of an already flushed and adopted replacement.</summary>
    public AppendMergeStage PrepareReplacement(Guid transactionId, Guid replacementId)
    {
        lock (_gate)
        {
            ReplacementState replacement = RequiredReplacement(replacementId);
            if (_state.Generation != replacement.ExpectedGeneration)
            {
                throw new AppendMergeReplacementStaleException(
                    replacement.ExpectedGeneration, _state.Generation);
            }

            if (_staged.Count != 0)
            {
                throw new AppendMergeException("Only one append/merge mutation may be staged.");
            }

            _staged.Add(transactionId, new(replacement.State));
            _replacements.Remove(replacementId);
            return new(transactionId, replacement.State.Generation, []);
        }
    }

    /// <summary>Abandons an unpublished replacement.</summary>
    public void AbandonReplacement(Guid replacementId)
    {
        lock (_gate)
        {
            if (_replacements.Remove(replacementId, out ReplacementState? replacement))
            {
                replacement.Workspace.Dispose();
            }
        }
    }

    private ReplacementState RequiredReplacement(Guid replacementId) =>
        _replacements.GetValueOrDefault(replacementId) ??
        throw new AppendMergeException("The append/merge replacement is not available.");

    // Writes and maintenance publish the same immutable manifest structure.
    // Reclamation alone must not rewrite every surviving run descriptor, or
    // discard the branch sharing needed by the next foreground write.
    private (AppendMergeManifestImage Manifest, AppendMergeRetiredPages[] Retired) EncodeChangedManifest(
        State current, long generation, IReadOnlyList<AppendMergeRunDescriptor> visibleRuns,
        IReadOnlyList<AppendMergeRunDescriptor> selected, AppendMergeRetiredPages[] retainedRetired,
        IPhysicalPageAllocator allocation)
    {
        var selectedGenerations = selected.Count == 0 ? null : selected.Select(run => run.Generation).ToHashSet();
        var reusedPages = new HashSet<PhysicalPageId>();
        foreach (var item in current.RunBranches)
        {
            if (selectedGenerations?.Contains(item.Key) != true)
            {
                foreach (var page in item.Value)
                {
                    reusedPages.Add(page);
                }
            }
        }

        AppendMergeRetiredPages? RetireManifest(bool reuse)
        {
            if (current.Generation == 0)
            {
                return null;
            }

            IReadOnlyList<PhysicalPageId> pages = reuse
                ? current.ManifestPages.Where(page => !reusedPages.Contains(page)).ToArray()
                : current.ManifestPages;
            return pages.Count == 0 ? null : new(current.Generation, Coalesce(pages));
        }
        var manifestRetired = RetireManifest(reuse: true);
        int recordCount = AppendMergeManifestCodec.RetiredRecordCount(retainedRetired);
        if (manifestRetired is not null)
        {
            recordCount = checked(recordCount + AppendMergeManifestCodec.RetiredRecordCount(manifestRetired.Extents.Count));
        }

        foreach (var run in selected)
        {
            recordCount = checked(recordCount + AppendMergeManifestCodec.RetiredRecordCount(run.OwnedExtents.Count));
        }

        bool separate = AppendMergeManifestCodec.CanSeparateRuns(visibleRuns, recordCount);
        if (!separate)
        {
            manifestRetired = RetireManifest(reuse: false);
        }
        // Materialize the final retirement list once, after the dense fallback decision.
        var retired = new AppendMergeRetiredPages[checked(retainedRetired.Length + selected.Count(run => run.OwnedExtents.Count != 0) + (manifestRetired is null ? 0 : 1))];
        retainedRetired.CopyTo(retired, 0);
        int next = retainedRetired.Length;
        if (manifestRetired is not null)
        {
            retired[next++] = manifestRetired;
        }

        foreach (var run in selected)
        {
            if (run.OwnedExtents.Count != 0)
            {
                retired[next++] = new(current.Generation, run.OwnedExtents);
            }
        }

        return (_manifest.Encode(current.AccessPathId, generation, visibleRuns, retired,
            allocation, separate ? current.RunBranches : null, selectedGenerations,
            inlineRetirement: separate, inlineRuns: separate), retired);
    }

    private static AppendMergeRetiredPages[] RetirePublishedState(State state)
    {
        if (state.Generation == 0)
        {
            return state.Retired.ToArray();
        }

        PhysicalPageExtent[] extents = state.Runs.SelectMany(run => run.OwnedExtents)
            .Concat(state.ManifestPages.Count == 0 ? [] : Coalesce(state.ManifestPages)).ToArray();
        return extents.Length == 0 ? state.Retired.ToArray() :
            state.Retired.Concat([new AppendMergeRetiredPages(state.Generation, extents)]).ToArray();
    }

    private async ValueTask<AppendMergeMaintenanceStage?> PrepareMaintenanceCoreAsync(Guid transactionId,
        long retentionFloorVersion, PhysicalAccessPathAllocationScope allocation, bool forceCompaction,
        CancellationToken cancellationToken, Func<TKey, TValue, bool>? retain = null, bool reclaimOnly = false)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A transaction identity is required.", nameof(transactionId));
        }

        if (retentionFloorVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionFloorVersion));
        }

        ArgumentNullException.ThrowIfNull(allocation);
        State current;
        lock (_gate)
        {
            if (_staged.Count != 0)
            {
                throw new AppendMergeException("Only one append/merge mutation may be staged.");
            }

            current = _state;
        }
        IReadOnlyList<AppendMergeRunDescriptor> selected = forceCompaction
            ? current.Runs : !reclaimOnly ? SelectCompactionRuns(current) : [];
        try
        {
            long generation = checked(current.Generation + 1);
            if (allocation.TransactionId != transactionId || allocation.AccessPathId.Value != current.AccessPathId ||
                allocation.ProposedGeneration != generation)
            {
                throw new AppendMergeException("The allocation scope does not match the append/merge compaction.");
            }

            (AppendMergeRetiredPages[] retainedRetired, AppendMergeRetiredPages[] reclaimed) = Reclamation(current);
            if (selected.Count == 0 && reclaimed.Length == 0)
            {
                return null;
            }

            foreach (PhysicalPageId page in reclaimed.SelectMany(item => item.Pages))
            {
                allocation.ReleasePage(page);
            }

            bool containsOldest = selected.Count != 0 && selected.Count == current.Runs.Count;
            AppendMergeRunImage? replacement = selected.Count == 0 ? null :
                await _compactionExecutor.ExecuteAsync(selected, generation,
                    retentionFloorVersion, containsOldest, allocation, cancellationToken, retain).ConfigureAwait(false);
            var images = replacement?.Pages.ToDictionary(item => item.Key, item => item.Value) ?? [];
            HashSet<long> selectedGenerations = selected.Select(run => run.Generation).ToHashSet();
            var visibleRuns = current.Runs.Where(run => !selectedGenerations.Contains(run.Generation)).ToList();
            if (replacement is not null)
            {
                visibleRuns.Add(replacement.Descriptor);
            }

            visibleRuns.Sort((left, right) => right.Generation.CompareTo(left.Generation));
            var (manifest, retired) = EncodeChangedManifest(current, generation, visibleRuns,
                selected, retainedRetired, allocation);
            allocation.Complete(manifest.RootPageId);
            images.Add(manifest.RootPageId, manifest.Root);
            foreach ((PhysicalPageId page, ReadOnlyMemory<byte> image) in manifest.Branches)
            {
                images.Add(page, image);
            }

            IReadOnlyList<DatabasePhysicalPageMutation> physical = CreatePhysicalMutations(images);
            lock (_gate)
            {
                _staged.Add(transactionId, new(new(current.AccessPathId, generation, visibleRuns,
                retired, new[] { manifest.RootPageId }.Concat(
                    manifest.AllBranches).ToArray())
                {
                    RunBranches = manifest.RunBranches
                }));
            }

            return new(new(transactionId, generation, physical),
                selected.Count == 0 ? AppendMergeMaintenanceKind.Reclamation : AppendMergeMaintenanceKind.Compaction,
                selected.Count, reclaimed.Sum(item => checked((int)item.Extents.Sum(extent => extent.PageCount))));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PhysicalPageException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AppendMergeException("The append/merge compaction could not be staged.", exception);
        }
    }

    /// <summary>Publishes the prepared manifest in memory after durable page publication.</summary>
    public void Publish(Guid transactionId)
    {
        HashSet<long> retained;
        lock (_gate)
        {
            StagedState staged = _staged.GetValueOrDefault(transactionId) ??
                throw new AppendMergeException("The append/merge mutation is not staged.");
            _state = staged.State;
            _staged.Remove(transactionId);
            retained = _state.Runs.Select(run => run.Generation).ToHashSet();
        }
        _runReader.RetainBoundaries(retained);
    }

    /// <summary>Abandons prepared in-memory state.</summary>
    public void Abandon(Guid transactionId)
    {
        lock (_gate)
        {
            _staged.Remove(transactionId);
        }
    }

    private AppendMergeRunEntry<TKey, TValue>[] Normalize(
        IReadOnlyList<AppendMergeMutation<TKey, TValue>> mutations, long databaseVersion)
    {
        if (mutations.Count == 0)
        {
            return [];
        }

        int[] order = new int[mutations.Count];
        for (int index = 0; index < order.Length; index++)
        {
            order[index] = index;
        }

        Array.Sort(order, new NormalizationIndexComparer(mutations, _keys, _values));
        int distinctCount = 1;
        for (int index = 1; index < order.Length; index++)
        {
            if (!SameComposite(mutations[order[index - 1]], mutations[order[index]]))
            {
                distinctCount++;
            }
        }

        var normalized = new AppendMergeRunEntry<TKey, TValue>[distinctCount];
        int destination = 0;
        for (int index = 0; index < order.Length;)
        {
            int next = index + 1;
            while (next < order.Length &&
                   SameComposite(mutations[order[index]], mutations[order[next]]))
            {
                next++;
            }

            AppendMergeMutation<TKey, TValue> winner = mutations[order[next - 1]];
            normalized[destination++] = new(winner.Kind, databaseVersion, winner.Key, winner.Value);
            index = next;
        }
        return normalized;
    }

    private bool SameComposite(AppendMergeMutation<TKey, TValue> left,
        AppendMergeMutation<TKey, TValue> right) =>
        _keys.Compare(left.Key, right.Key) == 0 && _values.CompareAt(left.Key, left.Value, right.Value) == 0;

    private static IReadOnlyList<DatabasePhysicalPageMutation> CreatePhysicalMutations(
        IReadOnlyDictionary<PhysicalPageId, ReadOnlyMemory<byte>> images)
    {
        var result = new List<DatabasePhysicalPageMutation>(images.Count);
        foreach ((PhysicalPageId page, ReadOnlyMemory<byte> after) in images)
        {
            result.Add(DatabasePhysicalPageMutation.CreatePage(page.Value, after));
        }

        return result;
    }

    private static bool OwnedBy(PhysicalAccessPathDirectoryEntry entry, PhysicalPageId page) =>
        entry.Extents.Any(extent => extent.Contains(page));

    private (AppendMergeRetiredPages[] Retained, AppendMergeRetiredPages[] Reclaimed) Reclamation(State state)
    {
        IReadOnlyList<AppendMergeRetiredPages> reclaimed;
        lock (_gate)
        {
            reclaimed = _reclaimer.Select(state.Retired, _snapshotReferences.Keys.ToArray())
            .Where(item => !IsExactlyProtected(item)).ToArray();
        }

        HashSet<AppendMergeRetiredPages> selected = reclaimed.ToHashSet();
        return (state.Retired.Where(item => !selected.Contains(item)).ToArray(), reclaimed.ToArray());
    }

    private static IReadOnlyList<PhysicalPageExtent> Coalesce(IReadOnlyList<PhysicalPageId> pages)
    {
        PhysicalPageId[] ordered = pages.Distinct().OrderBy(page => page.Value).ToArray();
        var extents = new List<PhysicalPageExtent>();
        long first = ordered[0].Value, previous = first;
        for (int index = 1; index < ordered.Length; index++)
        {
            long current = ordered[index].Value;
            if (current != previous + 1)
            {
                extents.Add(new(new(first), previous - first + 1));
                first = current;
            }
            previous = current;
        }
        extents.Add(new(new(first), previous - first + 1));
        return extents;
    }

    private void ReleaseSnapshot(long generation)
    {
        lock (_gate)
        {
            int remaining = _snapshotReferences[generation] - 1;
            if (remaining == 0)
            {
                _snapshotReferences.Remove(generation);
            }
            else
            {
                _snapshotReferences[generation] = remaining;
            }
        }
    }

    private sealed record State(Guid AccessPathId, long Generation,
        IReadOnlyList<AppendMergeRunDescriptor> Runs,
        IReadOnlyList<AppendMergeRetiredPages> Retired,
        IReadOnlyList<PhysicalPageId> ManifestPages)
    {
        public IReadOnlyDictionary<long, IReadOnlyList<PhysicalPageId>> RunBranches
        {
            get; init;
        }
        =
            new Dictionary<long, IReadOnlyList<PhysicalPageId>>();
    }

    private sealed record StagedState(State State);

    private sealed record ReplacementState(long ExpectedGeneration, State State,
        PhysicalPageWorkspace Workspace, IReadOnlySet<long> SourceRunGenerations);

    private sealed class NormalizationIndexComparer(
        IReadOnlyList<AppendMergeMutation<TKey, TValue>> mutations,
        IComparer<TKey> keys, IComparer<TValue> values) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            AppendMergeMutation<TKey, TValue> left = mutations[x];
            AppendMergeMutation<TKey, TValue> right = mutations[y];
            int key = keys.Compare(left.Key, right.Key);
            if (key != 0)
            {
                return key;
            }

            int value = values.CompareAt(left.Key, left.Value, right.Value);
            return value != 0 ? value : x.CompareTo(y);
        }
    }
}
