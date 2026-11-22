using EntityForge.Db.Storage;

namespace EntityForge.Db.Storage.Pages;

/// <summary>
/// Copy-on-write ordered access method whose published state contains descriptors rather than
/// decoded leaves. Leaf bytes are loaded through the supplied bounded page store on demand.
/// </summary>
/// <remarks>The owning transaction coordinator must serialize preparation, persistence, publication and abandonment.
/// Method-level state locks do not make overlapping asynchronous mutation workflows safe. The caller owns the page
/// store, allocation scopes and WAL/directory publication; this object does not independently commit transactions.</remarks>
public sealed class DiskResidentOrderedPageTree<TKey, TValue> where TKey : notnull
{
    private const int CompactionEvaluationGenerationInterval = 8;
    private const int MinimumCompactionPageSavingPercent = 20;
    private const int PercentageScale = 100;
    private readonly IResizablePhysicalPageStore _pages;
    private readonly OrderedPageTreeCodec<TKey, TValue> _leaves;
    private readonly DiskResidentOrderedCatalogCodec<TKey, TValue> _catalog;
    private readonly IComparer<TKey> _keys;
    private readonly IComparer<TValue> _values;
    private readonly object _gate = new();
    private TreeState _state;
    private readonly Dictionary<Guid, StagedState> _staged = [];
    private readonly Dictionary<Guid, ReplacementState> _replacements = [];
    private readonly Dictionary<long, int> _snapshotReferences = [];
    private long _retentionFloor;
    private long _lastCompactionEvaluationGeneration;

    private DiskResidentOrderedPageTree(IResizablePhysicalPageStore pages,
        OrderedPageTreeCodec<TKey, TValue> leaves,
        DiskResidentOrderedCatalogCodec<TKey, TValue> catalog,
        IComparer<TKey> keys, IComparer<TValue> values, TreeState state)
    {
        _pages = pages;
        _leaves = leaves;
        _catalog = catalog;
        _keys = keys;
        _values = values;
        _state = state;
    }

    /// <summary>Gets the number of key/value entries in the published generation.</summary>
    public long Count
    {
        get
        {
            lock (_gate)
            {
                return _state.EntryCount;
            }
        }
    }

    /// <summary>Gets the currently published generation.</summary>
    public OrderedPageTreeGeneration Generation
    {
        get
        {
            lock (_gate)
            {
                return _state.Generation;
            }
        }
    }

    /// <summary>Gets whether retired pages are reclaimable or the generation interval warrants evaluating compaction.</summary>
    /// <remarks>This is a scheduling hint; maintenance may determine that no rewrite is worthwhile.</remarks>
    public bool NeedsMaintenance
    {
        get
        {
            lock (_gate)
            {
                return _state.RetiredPages.Any(CanReclaim) ||
                _state.Generation.Value - _lastCompactionEvaluationGeneration >=
                CompactionEvaluationGenerationInterval;
            }
        }
    }

    /// <summary>Opens published catalog descriptors, or creates an empty in-memory tree when no directory entry is supplied.</summary>
    /// <param name="pages">Caller-owned store used for catalog reads and subsequent leaf access.</param>
    /// <param name="accessPathId">Expected access-path identity.</param>
    /// <param name="method">Expected method recorded in the page-space directory.</param>
    /// <param name="published">Published directory entry, or null for an empty tree.</param>
    /// <param name="keyCodec">Codec matching the stored key representation.</param>
    /// <param name="valueCodec">Codec matching the stored value representation.</param>
    /// <param name="keyComparer">Stored key ordering, or the default comparer.</param>
    /// <param name="valueComparer">Stored value ordering within each key, or the default comparer.</param>
    /// <param name="cancellationToken">Cancels catalog reads.</param>
    /// <returns>A tree with resident catalog descriptors and leaves read on demand.</returns>
    /// <remarks>Checks directory/root identity and descriptor consistency. Opening does not read every leaf or constitute
    /// a complete integrity scan. Codecs and comparers must retain the semantics used to create the index.</remarks>
    public static async ValueTask<DiskResidentOrderedPageTree<TKey, TValue>> OpenAsync(
        IResizablePhysicalPageStore pages, PhysicalAccessPathId accessPathId,
        PhysicalAccessMethodKind method, PhysicalAccessPathDirectoryEntry? published,
        IOrderedIndexCodec<TKey> keyCodec,
        IOrderedIndexCodec<TValue> valueCodec, IComparer<TKey>? keyComparer = null,
        IComparer<TValue>? valueComparer = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var leaves = new OrderedPageTreeCodec<TKey, TValue>(keyCodec, valueCodec);
        var catalog = new DiskResidentOrderedCatalogCodec<TKey, TValue>(keyCodec, valueCodec);
        try
        {
            TreeState state;
            if (published is null)
            {
                state = new(accessPathId.Value, OrderedPageTreeGeneration.Initial, 0, null, [], [], []);
            }
            else
            {
                if (published.AccessPathId != accessPathId || published.Method != method)
                {
                    throw new PhysicalPageCorruptionException(
                        "The ordered access path does not match its page-space directory entry.");
                }

                DiskResidentCatalogRoot root = catalog.DecodeRoot(
                    (await pages.ReadAsync(published.RootPage, cancellationToken).ConfigureAwait(false)).Span,
                    published.RootPage);
                if (root.AccessPathId != accessPathId.Value || root.Generation.Value != published.Generation)
                {
                    throw new PhysicalPageCorruptionException(
                        "The ordered catalog root does not match its page-space directory entry.");
                }

                var descriptors = new List<DiskResidentLeafDescriptor<TKey, TValue>>();
                var retired = new List<DiskResidentRetiredPage>();
                var contents = new Dictionary<PhysicalPageId, DiskResidentCatalogBranch<TKey, TValue>>();
                foreach (PhysicalPageId branchId in root.Branches)
                {
                    DiskResidentCatalogBranch<TKey, TValue> branch = catalog.DecodeBranch(
                        (await pages.ReadAsync(branchId, cancellationToken).ConfigureAwait(false)).Span, branchId);
                    contents.Add(branchId, branch);
                    descriptors.AddRange(branch.Leaves);
                    retired.AddRange(branch.RetiredPages);
                }
                ValidateDescriptors(descriptors, root.EntryCount,
                    keyComparer ?? Comparer<TKey>.Default, valueComparer ?? Comparer<TValue>.Default);
                state = new(root.AccessPathId, root.Generation, root.EntryCount,
                    published.RootPage, root.Branches, retired, descriptors)
                {
                    CatalogContents = contents
                };
            }
            return new(pages, leaves, catalog, keyComparer ?? Comparer<TKey>.Default,
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
            throw new OrderedPageTreeException("The disk-resident ordered page tree could not be opened.", exception);
        }
    }

    /// <summary>Captures a generation and retains its pages against reclamation until the snapshot is disposed.</summary>
    /// <param name="stagedTransactionId">Optional staged transaction identity. If absent or not found, captures published state.</param>
    /// <returns>A caller-owned snapshot that reads leaf images from the supplied page store.</returns>
    /// <remarks>A staged snapshot does not overlay unpersisted after-images; its referenced images must already be
    /// available in the page store before reading. Dispose snapshots promptly to allow reclamation.</remarks>
    public DiskResidentOrderedPageTreeSnapshot<TKey, TValue> CaptureSnapshot(Guid? stagedTransactionId = null)
    {
        lock (_gate)
        {
            TreeState state = stagedTransactionId is Guid transactionId &&
                              _staged.TryGetValue(transactionId, out StagedState? staged)
                ? staged.State : _state;
            _snapshotReferences[state.Generation.Value] =
                _snapshotReferences.GetValueOrDefault(state.Generation.Value) + 1;
            return new(state.AccessPathId, state.Generation, state.EntryCount, _pages, _leaves,
                state.Leaves, _keys, _values, () => ReleaseSnapshot(state.Generation));
        }
    }

    /// <summary>Advances the reclamation floor monotonically, no further than the current published generation.</summary>
    /// <param name="floor">Oldest generation retained by the caller's retention policy.</param>
    /// <remarks>Pages visible to outstanding snapshots remain protected. This does not reclaim pages immediately;
    /// subsequent mutation or maintenance preparation selects eligible pages. Publication advances the floor to its generation.</remarks>
    public void AdvanceRetentionFloor(OrderedPageTreeGeneration floor)
    {
        ArgumentNullException.ThrowIfNull(floor);
        lock (_gate)
        {
            if (floor.Value < _retentionFloor || floor.Value > _state.Generation.Value)
            {
                throw new ArgumentOutOfRangeException(nameof(floor));
            }

            _retentionFloor = floor.Value;
        }
    }

    /// <summary>Stages changed leaves and catalog images without writing or publishing them.</summary>
    /// <param name="transactionId">Nonempty transaction identity; no other mutation may be staged.</param>
    /// <param name="mutations">Changes to apply to the published tree.</param>
    /// <param name="allocation">Caller-owned allocation scope matching the transaction, access path and next generation.</param>
    /// <param name="cancellationToken">Cancels preparation and underlying reads.</param>
    /// <returns>Staged generation and physical recovery mutations. A logical no-op can retain the current generation.</returns>
    /// <remarks>The caller must durably protect the returned mutations and coordinate page-space publication.
    /// Failed or abandoned preparation does not independently roll back the caller's allocation scope.</remarks>
    public async ValueTask<OrderedPageTreeStage> PrepareAsync(Guid transactionId,
        IReadOnlyList<OrderedPageTreeMutation<TKey, TValue>> mutations,
        PhysicalAccessPathAllocationScope allocation,
        CancellationToken cancellationToken = default)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A transaction identity is required.", nameof(transactionId));
        }

        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(allocation);
        TreeState current;
        lock (_gate)
        {
            if (_staged.Count != 0)
            {
                throw new OrderedPageTreeException("Only one ordered mutation may be staged.");
            }

            current = _state;
        }
        try
        {
            long generation = checked(current.Generation.Value + 1);
            if (allocation.TransactionId != transactionId || allocation.AccessPathId.Value != current.AccessPathId ||
                allocation.ProposedGeneration != generation)
            {
                throw new OrderedPageTreeException("The ordered allocation scope does not match the staged mutation.");
            }

            DiskResidentRetiredPage[] reclaimable;
            lock (_gate)
            {
                reclaimable = current.RetiredPages.Where(CanReclaim).ToArray();
            }

            var (replacements, entryCount) = await PrepareLeafReplacementsAsync(
                current, mutations, generation, allocation, cancellationToken).ConfigureAwait(false);
            if (replacements.Count == 0)
            {
                lock (_gate)
                {
                    _staged.Add(transactionId, new(current,
                    Array.Empty<PhysicalPageImage>(),
                    Array.Empty<DatabasePhysicalPageMutation>()));
                }

                return new(transactionId, current.Generation,
                    Array.Empty<DatabasePhysicalPageMutation>());
            }
            // Reclaim only after a real change is established; a no-op keeps its existing allocation.
            foreach (DiskResidentRetiredPage retired in reclaimable)
            {
                allocation.ReleasePage(retired.PageId);
            }

            List<LeafReplacement> layout = BuildLeafLayout(current.Leaves, replacements);
            PhysicalPageId[] newlyRetired = replacements.Keys.Where(index => index >= 0)
                .Select(index => current.Leaves[index].PageId).ToArray();
            // Layout now owns the changed entries; release the duplicate references before encoding.
            replacements.Clear();
            var (descriptors, afterImages) = EncodeChangedLeaves(current, layout, generation);
            DiskResidentRetiredPage[] retainedRetired = current.RetiredPages.Except(reclaimable)
                .Concat(newlyRetired.Select(page => new DiskResidentRetiredPage(page, current.Generation.Value)))
                .ToArray();
            DiskResidentCatalogImage catalog = _catalog.Encode(current.AccessPathId, generation, entryCount,
                descriptors, retainedRetired, allocation, current.CatalogContents);
            if (allocation.Current is not null)
            {
                allocation.ReleasePage(allocation.Current.RootPage);
                foreach (PhysicalPageId branch in current.CatalogBranches.Except(catalog.BranchIds))
                {
                    allocation.ReleasePage(branch);
                }
            }
            allocation.Complete(catalog.RootPageId);
            // Staging records after-images; publication remains a separate transaction step.
            afterImages.EnsureCapacity(afterImages.Count + catalog.Branches.Count + 1);
            afterImages.Add(new(catalog.RootPageId, catalog.Root));
            foreach ((PhysicalPageId pageId, ReadOnlyMemory<byte> image) in catalog.Branches)
            {
                afterImages.Add(new(pageId, image));
            }

            IReadOnlyList<DatabasePhysicalPageMutation> physical = CreatePhysicalMutations(afterImages);
            var state = new TreeState(current.AccessPathId, new(generation), entryCount,
                catalog.RootPageId, catalog.BranchIds, retainedRetired, descriptors)
            {
                CatalogContents = CatalogContents(catalog, current.CatalogContents)
            };
            lock (_gate)
            {
                _staged.Add(transactionId, new(state, afterImages, physical));
            }

            return new(transactionId, state.Generation, physical);
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
            throw new OrderedPageTreeException("The disk-resident ordered mutation could not be staged.", exception);
        }
    }

    private async ValueTask<(Dictionary<int, IReadOnlyList<LeafReplacement>> Replacements, long EntryCount)>
        PrepareLeafReplacementsAsync(TreeState current,
            IReadOnlyList<OrderedPageTreeMutation<TKey, TValue>> mutations, long generation,
            PhysicalAccessPathAllocationScope allocation, CancellationToken cancellationToken)
    {
        IReadOnlyList<MutationGroup> groups = GroupMutations(current.Leaves, mutations);
        var replacements = new Dictionary<int, IReadOnlyList<LeafReplacement>>(groups.Count);
        long entryCount = current.EntryCount;
        foreach (MutationGroup group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int leafIndex = group.LeafIndex;
            IReadOnlyList<OrderedLeafEntry<TKey, TValue>> existing = leafIndex < 0
                ? Array.Empty<OrderedLeafEntry<TKey, TValue>>()
                : (await ReadLeafAsync(current.Leaves[leafIndex].PageId, cancellationToken).ConfigureAwait(false)).Entries;
            ApplyResult applied = Apply(existing, group, ref entryCount);
            if (!applied.Changed)
            {
                continue;
            }

            List<OrderedLeafEntry<TKey, TValue>> updated = applied.Entries;
            IReadOnlyList<IReadOnlyList<OrderedLeafEntry<TKey, TValue>>> chunks = Chunk(updated, generation);
            var replacementLeaves = new List<LeafReplacement>(chunks.Count);
            foreach (IReadOnlyList<OrderedLeafEntry<TKey, TValue>> chunk in chunks)
            {
                replacementLeaves.Add(new(allocation.AllocatePage(), chunk));
            }

            replacements[leafIndex] = replacementLeaves;
        }
        return (replacements, entryCount);
    }

    private static List<LeafReplacement> BuildLeafLayout(
        IReadOnlyList<DiskResidentLeafDescriptor<TKey, TValue>> existingLeaves,
        IReadOnlyDictionary<int, IReadOnlyList<LeafReplacement>> replacements)
    {
        var layout = new List<LeafReplacement>();
        for (int index = 0; index < existingLeaves.Count; index++)
        {
            if (replacements.TryGetValue(index, out IReadOnlyList<LeafReplacement>? replacement))
            {
                layout.AddRange(replacement);
            }
            else
            {
                layout.Add(new(existingLeaves[index].PageId, null));
            }
        }
        if (existingLeaves.Count == 0 && replacements.TryGetValue(-1, out IReadOnlyList<LeafReplacement>? initial))
        {
            layout.AddRange(initial);
        }

        return layout;
    }

    private (List<DiskResidentLeafDescriptor<TKey, TValue>> Descriptors, List<PhysicalPageImage> AfterImages)
        EncodeChangedLeaves(TreeState current, List<LeafReplacement> layout, long generation)
    {
        var descriptors = new List<DiskResidentLeafDescriptor<TKey, TValue>>(layout.Count);
        var existingDescriptors =
            new Dictionary<PhysicalPageId, DiskResidentLeafDescriptor<TKey, TValue>>(current.Leaves.Count);
        foreach (DiskResidentLeafDescriptor<TKey, TValue> descriptor in current.Leaves)
        {
            existingDescriptors.Add(descriptor.PageId, descriptor);
        }

        var afterImages = new List<PhysicalPageImage>(layout.Count + current.CatalogBranches.Count + 1);
        for (int index = 0; index < layout.Count; index++)
        {
            LeafReplacement item = layout[index];
            if (item.Entries is null)
            {
                descriptors.Add(existingDescriptors[item.PageId]);
                continue;
            }
            PhysicalPageId? previous = index == 0 ? null : layout[index - 1].PageId;
            PhysicalPageId? next = index + 1 == layout.Count ? null : layout[index + 1].PageId;
            afterImages.Add(new(item.PageId, _leaves.EncodeLeaf(item.PageId, generation, previous, next,
                item.Entries, generation)));
            descriptors.Add(new(item.PageId, item.Entries[0].Key, item.Entries[0].Value, item.Entries.Count));
            // Drop decoded entries once the page image owns their encoded representation.
            layout[index] = new(item.PageId, null);
        }
        return (descriptors, afterImages);
    }

    /// <summary>Stages eligible retired-page reclamation and, when worthwhile, leaf compaction.</summary>
    /// <param name="transactionId">Nonempty identity for this exclusively coordinated maintenance transaction.</param>
    /// <param name="allocation">Matching caller-owned allocation scope for the proposed next generation.</param>
    /// <param name="cancellationToken">Cancels maintenance preparation and reads.</param>
    /// <returns>A stage requiring durable protection and publication, or null when no maintenance is selected.</returns>
    public async ValueTask<OrderedPageTreeStage?> PrepareMaintenanceAsync(Guid transactionId,
        PhysicalAccessPathAllocationScope allocation, CancellationToken cancellationToken = default) =>
        await PrepareMaintenanceCoreAsync(transactionId, allocation, forceCompaction: false,
            cancellationToken).ConfigureAwait(false);

    /// <summary>Stages a full streaming leaf rewrite for physical-space vacuuming.</summary>
    public async ValueTask<OrderedPageTreeStage?> PrepareVacuumAsync(Guid transactionId,
        PhysicalAccessPathAllocationScope allocation, CancellationToken cancellationToken = default) =>
        await PrepareMaintenanceCoreAsync(transactionId, allocation, forceCompaction: true,
            cancellationToken).ConfigureAwait(false);

    /// <summary>Builds a compact replacement without entering live transaction staging.</summary>
    public async ValueTask BuildReplacementAsync(Guid replacementId, PhysicalPageWorkspace workspace,
        bool flushPages = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        TreeState current;
        lock (_gate)
        {
            if (_replacements.ContainsKey(replacementId))
            {
                throw new OrderedPageTreeException("The ordered replacement already exists.");
            }

            current = _state;
            _snapshotReferences[current.Generation.Value] =
                _snapshotReferences.GetValueOrDefault(current.Generation.Value) + 1;
        }
        try
        {
            long generation = checked(current.Generation.Value + 1);
            var images = new List<PhysicalPageImage>();
            IReadOnlyList<DiskResidentLeafDescriptor<TKey, TValue>> leaves =
                await BuildPackedLeavesAsync(current.Leaves, generation, workspace, images,
                    cancellationToken).ConfigureAwait(false);
            DiskResidentRetiredPage[] retired = RetirePublishedState(current);
            DiskResidentCatalogImage catalog = _catalog.Encode(current.AccessPathId, generation,
                current.EntryCount, leaves, retired, workspace);
            workspace.Complete(catalog.RootPageId);
            images.Add(new(catalog.RootPageId, catalog.Root));
            images.AddRange(catalog.Branches.Select(item => new PhysicalPageImage(item.Key, item.Value)));
            long required = images.Select(item => item.PageId.Value + 1).DefaultIfEmpty(0).Max();
            if (await _pages.GetPageCountAsync(cancellationToken).ConfigureAwait(false) < required)
            {
                await _pages.EnsureCapacityAsync(required, cancellationToken).ConfigureAwait(false);
            }

            foreach (PhysicalPageImage image in images)
            {
                await _pages.WriteAsync(image.PageId, image.Image, cancellationToken).ConfigureAwait(false);
            }

            if (flushPages)
            {
                await _pages.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var state = new TreeState(current.AccessPathId, new(generation), current.EntryCount,
                catalog.RootPageId, catalog.BranchIds, retired, leaves)
            {
                CatalogContents = CatalogContents(catalog)
            };
            lock (_gate)
            {
                _replacements.Add(replacementId,
                new(current.Generation, state, workspace));
            }
        }
        finally
        {
            ReleaseSnapshot(current.Generation);
        }
    }

    /// <summary>Gets the generation against which a replacement must be atomically adopted.</summary>
    public OrderedPageTreeGeneration GetReplacementExpectedGeneration(Guid replacementId)
    {
        lock (_gate)
        {
            return RequiredReplacement(replacementId).ExpectedGeneration;
        }
    }

    /// <summary>Stages only the in-memory publication for an already flushed and adopted replacement.</summary>
    public OrderedPageTreeStage PrepareReplacement(Guid transactionId, Guid replacementId)
    {
        lock (_gate)
        {
            ReplacementState replacement = RequiredReplacement(replacementId);
            if (_state.Generation != replacement.ExpectedGeneration)
            {
                throw new OrderedPageTreeReplacementStaleException(
                    replacement.ExpectedGeneration.Value, _state.Generation.Value);
            }

            if (_staged.Count != 0)
            {
                throw new OrderedPageTreeException("Only one ordered mutation may be staged.");
            }

            _staged.Add(transactionId, new(replacement.State, [], []));
            _replacements.Remove(replacementId);
            return new(transactionId, replacement.State.Generation, []);
        }
    }

    /// <summary>Abandons an unpublished replacement workspace.</summary>
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
        throw new OrderedPageTreeException("The ordered replacement is not available.");

    private async ValueTask<OrderedPageTreeStage?> PrepareMaintenanceCoreAsync(Guid transactionId,
        PhysicalAccessPathAllocationScope allocation, bool forceCompaction,
        CancellationToken cancellationToken)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A transaction identity is required.", nameof(transactionId));
        }

        ArgumentNullException.ThrowIfNull(allocation);
        TreeState current;
        DiskResidentRetiredPage[] reclaimable;
        bool evaluateCompaction;
        lock (_gate)
        {
            if (_staged.Count != 0)
            {
                throw new OrderedPageTreeException("Only one ordered mutation may be staged.");
            }

            current = _state;
            reclaimable = current.RetiredPages.Where(CanReclaim).ToArray();
            evaluateCompaction = forceCompaction || current.Generation.Value - _lastCompactionEvaluationGeneration >=
                                 CompactionEvaluationGenerationInterval;
        }
        if (reclaimable.Length == 0 && !evaluateCompaction)
        {
            return null;
        }

        try
        {
            long generation = checked(current.Generation.Value + 1);
            if (allocation.TransactionId != transactionId || allocation.AccessPathId.Value != current.AccessPathId ||
                allocation.ProposedGeneration != generation)
            {
                throw new OrderedPageTreeException("The ordered maintenance allocation scope is invalid.");
            }

            foreach (DiskResidentRetiredPage retired in reclaimable)
            {
                allocation.ReleasePage(retired.PageId);
            }

            int packedLeafCount = evaluateCompaction
                ? await CountPackedLeavesAsync(current.Leaves, cancellationToken).ConfigureAwait(false)
                : current.Leaves.Count;
            bool compact = forceCompaction || evaluateCompaction && packedLeafCount * (long)PercentageScale <=
                current.Leaves.Count * (long)(PercentageScale - MinimumCompactionPageSavingPercent);
            if (reclaimable.Length == 0 && !compact)
            {
                lock (_gate)
                {
                    _lastCompactionEvaluationGeneration = current.Generation.Value;
                }

                return null;
            }
            allocation.ReleasePage(allocation.Current!.RootPage);
            foreach (PhysicalPageId branch in current.CatalogBranches)
            {
                allocation.ReleasePage(branch);
            }

            var images = new List<PhysicalPageImage>();
            IReadOnlyList<DiskResidentLeafDescriptor<TKey, TValue>> leaves = current.Leaves;
            if (compact)
            {
                leaves = await BuildPackedLeavesAsync(current.Leaves, generation, allocation, images,
                    cancellationToken).ConfigureAwait(false);
            }

            DiskResidentRetiredPage[] retained = current.RetiredPages.Except(reclaimable)
                .Concat(compact
                    ? current.Leaves.Select(leaf => new DiskResidentRetiredPage(
                        leaf.PageId, current.Generation.Value))
                    : []).ToArray();
            DiskResidentCatalogImage catalog = _catalog.Encode(current.AccessPathId, generation,
                current.EntryCount, leaves, retained, allocation);
            allocation.Complete(catalog.RootPageId);
            images.EnsureCapacity(images.Count + catalog.Branches.Count + 1);
            images.Add(new(catalog.RootPageId, catalog.Root));
            images.AddRange(catalog.Branches.Select(item => new PhysicalPageImage(item.Key, item.Value)));
            IReadOnlyList<DatabasePhysicalPageMutation> physical = CreatePhysicalMutations(images);
            var state = new TreeState(current.AccessPathId, new(generation), current.EntryCount,
                catalog.RootPageId, catalog.BranchIds, retained, leaves)
            {
                CatalogContents = CatalogContents(catalog)
            };
            lock (_gate)
            {
                _staged.Add(transactionId, new(state, images, physical));
            }

            return new(transactionId, state.Generation, physical);
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
            throw new OrderedPageTreeException("The disk-resident ordered maintenance could not be staged.", exception);
        }
    }

    private async ValueTask<int> CountPackedLeavesAsync(
        IReadOnlyList<DiskResidentLeafDescriptor<TKey, TValue>> leaves,
        CancellationToken cancellationToken)
    {
        int pages = 0;
        int storedBytes = OrderedPageTreeCodec<TKey, TValue>.EmptyLeafStorageBytes;
        foreach (DiskResidentLeafDescriptor<TKey, TValue> leaf in leaves)
        {
            OrderedLeaf<TKey, TValue> decoded = await ReadLeafAsync(leaf.PageId, cancellationToken)
                .ConfigureAwait(false);
            foreach (OrderedLeafEntry<TKey, TValue> entry in decoded.Entries)
            {
                int entryBytes = _leaves.LeafEntryStorageBytes(entry);
                if (storedBytes + entryBytes > PhysicalPageFileOptions.DefaultPageSize)
                {
                    pages++;
                    storedBytes = OrderedPageTreeCodec<TKey, TValue>.EmptyLeafStorageBytes;
                }
                storedBytes += entryBytes;
            }
        }
        return storedBytes == OrderedPageTreeCodec<TKey, TValue>.EmptyLeafStorageBytes ? pages : pages + 1;
    }

    private async ValueTask<IReadOnlyList<DiskResidentLeafDescriptor<TKey, TValue>>> BuildPackedLeavesAsync(
        IReadOnlyList<DiskResidentLeafDescriptor<TKey, TValue>> leaves, long generation,
        IPhysicalPageAllocator allocation, List<PhysicalPageImage> images,
        CancellationToken cancellationToken)
    {
        var descriptors = new List<DiskResidentLeafDescriptor<TKey, TValue>>();
        var chunk = new List<OrderedLeafEntry<TKey, TValue>>();
        int storedBytes = OrderedPageTreeCodec<TKey, TValue>.EmptyLeafStorageBytes;
        PendingPackedLeaf? pending = null;
        foreach (DiskResidentLeafDescriptor<TKey, TValue> leaf in leaves)
        {
            OrderedLeaf<TKey, TValue> decoded = await ReadLeafAsync(leaf.PageId, cancellationToken)
                .ConfigureAwait(false);
            foreach (OrderedLeafEntry<TKey, TValue> entry in decoded.Entries)
            {
                int entryBytes = _leaves.LeafEntryStorageBytes(entry);
                if (storedBytes + entryBytes > PhysicalPageFileOptions.DefaultPageSize)
                {
                    pending = StagePackedChunk(chunk, generation, allocation, images, descriptors, pending);
                    chunk = [];
                    storedBytes = OrderedPageTreeCodec<TKey, TValue>.EmptyLeafStorageBytes;
                }
                chunk.Add(entry);
                storedBytes += entryBytes;
            }
        }
        if (chunk.Count != 0)
        {
            pending = StagePackedChunk(chunk, generation, allocation, images, descriptors, pending);
        }

        if (pending is not null)
        {
            images.Add(new(pending.PageId, _leaves.EncodeLeaf(pending.PageId, generation,
                pending.PreviousPageId, null, pending.Entries, generation)));
        }

        return descriptors;
    }

    private PendingPackedLeaf StagePackedChunk(IReadOnlyList<OrderedLeafEntry<TKey, TValue>> entries,
        long generation, IPhysicalPageAllocator allocation, List<PhysicalPageImage> images,
        List<DiskResidentLeafDescriptor<TKey, TValue>> descriptors, PendingPackedLeaf? pending)
    {
        PhysicalPageId pageId = allocation.AllocatePage();
        if (pending is not null)
        {
            images.Add(new(pending.PageId, _leaves.EncodeLeaf(pending.PageId, generation,
                pending.PreviousPageId, pageId, pending.Entries, generation)));
        }

        OrderedLeafEntry<TKey, TValue>[] owned = entries.ToArray();
        descriptors.Add(new(pageId, owned[0].Key, owned[0].Value, owned.Length));
        return new(pageId, pending?.PageId, owned);
    }

    /// <summary>Grows the page store as needed, writes staged after-images and flushes it without publishing in-memory state.</summary>
    /// <param name="transactionId">Identity of an existing staged mutation.</param>
    /// <param name="cancellationToken">Cancels page I/O where supported.</param>
    /// <remarks>The owning coordinator must establish the required durable recovery protection first.
    /// Cancellation or failure can leave some images written; this is not an atomic standalone commit.</remarks>
    public async ValueTask PersistAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        StagedState staged;
        lock (_gate)
        {
            staged = _staged.GetValueOrDefault(transactionId) ??
            throw new OrderedPageTreeException("The ordered mutation is not staged.");
        }

        long required = 0;
        foreach (PhysicalPageImage item in staged.AfterImages)
        {
            required = Math.Max(required, item.PageId.Value + 1);
        }

        if (await _pages.GetPageCountAsync(cancellationToken).ConfigureAwait(false) < required)
        {
            await _pages.EnsureCapacityAsync(required, cancellationToken).ConfigureAwait(false);
        }

        foreach (PhysicalPageImage item in staged.AfterImages)
        {
            await _pages.WriteAsync(item.PageId, item.Image, cancellationToken).ConfigureAwait(false);
        }

        await _pages.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adopts staged state in memory, advances its retention floor and removes the stage.</summary>
    /// <param name="transactionId">Identity of the staged mutation.</param>
    /// <remarks>Does not write pages, flush a WAL or publish the page-space directory. The owning coordinator must
    /// satisfy those durability and visibility requirements before adopting the generation.</remarks>
    public void Publish(Guid transactionId)
    {
        lock (_gate)
        {
            StagedState staged = _staged.GetValueOrDefault(transactionId) ??
                throw new OrderedPageTreeException("The ordered mutation is not staged.");
            _state = staged.State;
            _retentionFloor = _state.Generation.Value;
            _staged.Remove(transactionId);
        }
    }

    /// <summary>Persists and flushes staged pages, then publishes their in-memory state.</summary>
    /// <remarks>Combines <see cref="PersistAsync"/> and <see cref="Publish"/>; it does not supply the caller's WAL
    /// or page-space directory transaction. Their coordination requirements still apply.</remarks>
    public async ValueTask PublishAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        await PersistAsync(transactionId, cancellationToken).ConfigureAwait(false);
        Publish(transactionId);
    }

    /// <summary>Removes an unpublished in-memory stage if present.</summary>
    /// <remarks>Does not erase persisted images, undo a durable transaction or roll back the caller's allocation scope.</remarks>
    public void Abandon(Guid transactionId)
    {
        lock (_gate)
        {
            _staged.Remove(transactionId);
        }
    }

    private IReadOnlyList<MutationGroup> GroupMutations(
        IReadOnlyList<DiskResidentLeafDescriptor<TKey, TValue>> descriptors,
        IReadOnlyList<OrderedPageTreeMutation<TKey, TValue>> mutations)
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

        Array.Sort(order, new MutationIndexComparer(mutations, _keys, _values));
        var result = new List<MutationGroup>();
        int offset = 0;
        while (offset < order.Length)
        {
            OrderedPageTreeMutation<TKey, TValue> first = mutations[order[offset]];
            int leafIndex = FindLeaf(descriptors, first.Key, first.Value);
            int next = offset + 1;
            while (next < order.Length)
            {
                OrderedPageTreeMutation<TKey, TValue> candidate = mutations[order[next]];
                if (FindLeaf(descriptors, candidate.Key, candidate.Value) != leafIndex)
                {
                    break;
                }

                next++;
            }
            result.Add(new(leafIndex, mutations, order, offset, next - offset));
            offset = next;
        }
        return result;
    }

    private ApplyResult Apply(IReadOnlyList<OrderedLeafEntry<TKey, TValue>> existing,
        MutationGroup mutations, ref long count)
    {
        var entries = existing.ToList();
        var comparer = new EntryComparer(_keys, _values);
        bool changed = false;
        for (int index = 0; index < mutations.Count; index++)
        {
            OrderedPageTreeMutation<TKey, TValue> mutation = mutations[index];
            int position = entries.BinarySearch(new(mutation.Key, mutation.Value), comparer);
            if (mutation.Kind is OrderedPageTreeMutationKind.Add && position < 0)
            {
                entries.Insert(~position, new(mutation.Key, mutation.Value));
                count++;
                changed = true;
            }
            else if (mutation.Kind is OrderedPageTreeMutationKind.Remove && position >= 0)
            {
                entries.RemoveAt(position);
                count--;
                changed = true;
            }
        }
        return new(entries, changed);
    }

    private IReadOnlyList<IReadOnlyList<OrderedLeafEntry<TKey, TValue>>> Chunk(
        IReadOnlyList<OrderedLeafEntry<TKey, TValue>> entries, long generation)
    {
        if (entries.Count == 0)
        {
            return Array.Empty<IReadOnlyList<OrderedLeafEntry<TKey, TValue>>>();
        }

        var chunks = new List<IReadOnlyList<OrderedLeafEntry<TKey, TValue>>>();
        int offset = 0;
        while (offset < entries.Count)
        {
            int storedBytes = OrderedPageTreeCodec<TKey, TValue>.EmptyLeafStorageBytes;
            int selected = 0;
            while (offset + selected < entries.Count)
            {
                int entryBytes = _leaves.LeafEntryStorageBytes(entries[offset + selected]);
                if (storedBytes + entryBytes > PhysicalPageFileOptions.DefaultPageSize)
                {
                    break;
                }

                storedBytes += entryBytes;
                selected++;
            }
            if (selected == 0)
            {
                throw new PhysicalPageCapacityException("An ordered entry exceeds leaf capacity.");
            }

            chunks.Add(new EntryWindow(entries, offset, selected));
            offset += selected;
        }
        return chunks;
    }

    private int FindLeaf(IReadOnlyList<DiskResidentLeafDescriptor<TKey, TValue>> leaves, TKey key, TValue value)
    {
        if (leaves.Count == 0)
        {
            return -1;
        }

        int low = 0, high = leaves.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            int comparison = Compare(leaves[middle].FirstKey, leaves[middle].FirstValue, key, value);
            if (comparison <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return Math.Max(0, high);
    }

    private int Compare(TKey leftKey, TValue leftValue, TKey rightKey, TValue rightValue)
    {
        int key = _keys.Compare(leftKey, rightKey);
        return key != 0 ? key : _values.Compare(leftValue, rightValue);
    }

    private async ValueTask<OrderedLeaf<TKey, TValue>> ReadLeafAsync(PhysicalPageId pageId,
        CancellationToken cancellationToken) => _leaves.DecodeLeaf(
        (await _pages.ReadAsync(pageId, cancellationToken).ConfigureAwait(false)).Span, pageId);

    private static IReadOnlyList<DatabasePhysicalPageMutation> CreatePhysicalMutations(
        IReadOnlyList<PhysicalPageImage> images)
    {
        var result = new List<DatabasePhysicalPageMutation>(images.Count);
        foreach (PhysicalPageImage item in images)
        {
            result.Add(DatabasePhysicalPageMutation.CreatePage(item.PageId.Value, item.Image));
        }

        return result;
    }

    private static void ValidateDescriptors(IReadOnlyList<DiskResidentLeafDescriptor<TKey, TValue>> leaves,
        long count, IComparer<TKey> keys, IComparer<TValue> values)
    {
        if (leaves.Sum(item => (long)item.EntryCount) != count)
        {
            throw new PhysicalPageCorruptionException("The ordered catalog entry count is inconsistent.");
        }

        for (int index = 1; index < leaves.Count; index++)
        {
            int key = keys.Compare(leaves[index - 1].FirstKey, leaves[index].FirstKey);
            if (key > 0 || key == 0 && values.Compare(leaves[index - 1].FirstValue, leaves[index].FirstValue) >= 0)
            {
                throw new PhysicalPageCorruptionException("Ordered leaf descriptors are not strictly ordered.");
            }
        }
    }

    private void ReleaseSnapshot(OrderedPageTreeGeneration generation)
    {
        lock (_gate)
        {
            int remaining = _snapshotReferences[generation.Value] - 1;
            if (remaining == 0)
            {
                _snapshotReferences.Remove(generation.Value);
            }
            else
            {
                _snapshotReferences[generation.Value] = remaining;
            }
        }
    }

    private bool CanReclaim(DiskResidentRetiredPage retired)
    {
        if (retired.LastGeneration >= _retentionFloor)
        {
            return false;
        }

        // A page retired after generation N may be visible to any still-open snapshot at
        // generation N or earlier. Conservatively retain it until all such readers finish.
        return !_snapshotReferences.Keys.Any(generation => generation <= retired.LastGeneration);
    }

    private static DiskResidentRetiredPage[] RetirePublishedState(TreeState state)
    {
        if (state.Generation.Value == 0)
        {
            return state.RetiredPages.ToArray();
        }

        IEnumerable<PhysicalPageId> pages = state.Leaves.Select(leaf => leaf.PageId)
            .Concat(state.CatalogBranches);
        if (state.RootPage is PhysicalPageId root)
        {
            pages = pages.Append(root);
        }

        return state.RetiredPages.Concat(pages.Distinct().Select(page =>
            new DiskResidentRetiredPage(page, state.Generation.Value))).ToArray();
    }

    private sealed record TreeState(Guid AccessPathId, OrderedPageTreeGeneration Generation, long EntryCount,
        PhysicalPageId? RootPage, IReadOnlyList<PhysicalPageId> CatalogBranches,
        IReadOnlyList<DiskResidentRetiredPage> RetiredPages,
        IReadOnlyList<DiskResidentLeafDescriptor<TKey, TValue>> Leaves)
    {
        public IReadOnlyDictionary<PhysicalPageId, DiskResidentCatalogBranch<TKey, TValue>> CatalogContents
        {
            get; init;
        }
        =
            new Dictionary<PhysicalPageId, DiskResidentCatalogBranch<TKey, TValue>>();
    }

    private IReadOnlyDictionary<PhysicalPageId, DiskResidentCatalogBranch<TKey, TValue>> CatalogContents(
        DiskResidentCatalogImage image,
        IReadOnlyDictionary<PhysicalPageId, DiskResidentCatalogBranch<TKey, TValue>>? previous = null)
    {
        var contents = image.Branches.ToDictionary(pair => pair.Key,
            pair => _catalog.DecodeBranch(pair.Value.Span, pair.Key));
        foreach (PhysicalPageId id in image.BranchIds)
        {
            if (!contents.ContainsKey(id))
            {
                contents.Add(id, previous![id]);
            }
        }

        return contents;
    }

    private sealed record StagedState(TreeState State,
        IReadOnlyList<PhysicalPageImage> AfterImages,
        IReadOnlyList<DatabasePhysicalPageMutation> PhysicalMutations);

    private sealed record ReplacementState(OrderedPageTreeGeneration ExpectedGeneration,
        TreeState State, PhysicalPageWorkspace Workspace);

    private readonly record struct PhysicalPageImage(PhysicalPageId PageId, ReadOnlyMemory<byte> Image);

    private sealed record LeafReplacement(PhysicalPageId PageId,
        IReadOnlyList<OrderedLeafEntry<TKey, TValue>>? Entries);

    private sealed record ApplyResult(List<OrderedLeafEntry<TKey, TValue>> Entries, bool Changed);

    private sealed record PendingPackedLeaf(PhysicalPageId PageId, PhysicalPageId? PreviousPageId,
        IReadOnlyList<OrderedLeafEntry<TKey, TValue>> Entries);

    private sealed class EntryWindow(IReadOnlyList<OrderedLeafEntry<TKey, TValue>> entries,
        int offset, int count) : IReadOnlyList<OrderedLeafEntry<TKey, TValue>>
    {
        public int Count => count;

        public OrderedLeafEntry<TKey, TValue> this[int index] =>
            index >= 0 && index < count ? entries[offset + index] :
                throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<OrderedLeafEntry<TKey, TValue>> GetEnumerator()
        {
            for (int index = 0; index < count; index++)
            {
                yield return entries[offset + index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed record MutationGroup(int LeafIndex,
        IReadOnlyList<OrderedPageTreeMutation<TKey, TValue>> Mutations,
        int[] Order, int Offset, int Count)
    {
        public OrderedPageTreeMutation<TKey, TValue> this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                if (index >= Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return Mutations[Order[Offset + index]];
            }
        }
    }

    private sealed class MutationIndexComparer(
        IReadOnlyList<OrderedPageTreeMutation<TKey, TValue>> mutations,
        IComparer<TKey> keys, IComparer<TValue> values) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            OrderedPageTreeMutation<TKey, TValue> left = mutations[x];
            OrderedPageTreeMutation<TKey, TValue> right = mutations[y];
            int key = keys.Compare(left.Key, right.Key);
            if (key != 0)
            {
                return key;
            }

            int value = values.Compare(left.Value, right.Value);
            return value != 0 ? value : x.CompareTo(y);
        }
    }

    private sealed class EntryComparer(IComparer<TKey> keys, IComparer<TValue> values) :
        IComparer<OrderedLeafEntry<TKey, TValue>>
    {
        public int Compare(OrderedLeafEntry<TKey, TValue>? x, OrderedLeafEntry<TKey, TValue>? y)
        {
            int key = keys.Compare(x!.Key, y!.Key);
            return key != 0 ? key : values.Compare(x.Value, y.Value);
        }
    }
}
