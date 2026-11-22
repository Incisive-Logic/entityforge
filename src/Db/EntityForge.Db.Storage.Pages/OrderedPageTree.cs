using EntityForge.Db.AccessPaths;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Storage.Pages;

/// <summary>
/// Incremental selected-format ordered access method. Mutations are staged as page redo, then
/// published only after the owning logical transaction is durable.
/// </summary>
/// <remarks>This implementation retains decoded leaves in memory. The caller owns the page store and must coordinate
/// mutation preparation, durable redo, page persistence and publication as one serialized transaction workflow.</remarks>
public sealed class OrderedPageTree<TKey, TValue> where TKey : notnull
{
    internal const long DefaultMaximumInitialImageBytes = 64L * 1024 * 1024;

    internal long MaximumInitialImageBytes { get; set; } = DefaultMaximumInitialImageBytes;

    private readonly IResizablePhysicalPageStore _pages;
    private readonly OrderedPageTreeCodec<TKey, TValue> _codec;
    private readonly IComparer<TKey> _keys;
    private readonly IComparer<TValue> _values;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _allocation = new(1, 1);
    private readonly PhysicalPageTelemetry? _telemetry;
    private TreeState _state;
    private readonly Dictionary<Guid, StagedState> _staged = [];
    private readonly Dictionary<Guid, ReplacementState> _replacements = [];
    private readonly HashSet<PhysicalPageId> _abandonedReplacementPages = [];
    private readonly HashSet<PhysicalPageId> _replacementReservations = [];
    private Guid? _allocationOwner;

    private OrderedPageTree(IResizablePhysicalPageStore pages, OrderedPageTreeCodec<TKey, TValue> codec,
        IComparer<TKey> keys, IComparer<TValue> values, TreeState state)
    {
        _pages = pages;
        _codec = codec;
        _keys = keys;
        _values = values;
        _state = state;
        _telemetry = (pages as IPhysicalPageTelemetrySource)?.Telemetry;
    }

    /// <summary>Gets the persistent identity of the published access path.</summary>
    public Guid AccessPathId
    {
        get
        {
            lock (_gate)
            {
                return _state.AccessPathId;
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

    /// <summary>Gets the total number of key/value entries in the published decoded leaves.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _state.Leaves.Sum(item => item.Entries.Count);
            }
        }
    }

    /// <summary>Captures immutable decoded leaves from a staged transaction, if found, or from published state.</summary>
    /// <param name="stagedTransactionId">Optional staged transaction identity; an unknown identity falls back to published state.</param>
    /// <returns>A snapshot whose reads remain on the captured generation after later publication.</returns>
    /// <remarks>Unlike the disk-resident tree, staged reads use decoded in-memory leaves and do not require after-images
    /// to have been persisted. Retaining a snapshot retains those leaf objects in memory.</remarks>
    public OrderedPageTreeSnapshot<TKey, TValue> CaptureSnapshot(Guid? stagedTransactionId = null)
    {
        lock (_gate)
        {
            TreeState state = stagedTransactionId is { } transactionId &&
                              _staged.TryGetValue(transactionId, out StagedState? staged)
                ? staged.State : _state;
            return new(state.AccessPathId, state.Generation, state.Leaves, _keys, _values);
        }
    }

    /// <summary>Loads the catalog and all decoded leaves from the supplied store, or creates an empty tree if it has no pages.</summary>
    /// <param name="pages">Caller-owned store dedicated to this tree, whose catalog root occupies page zero.</param>
    /// <param name="keyCodec">Codec matching the persisted key representation.</param>
    /// <param name="valueCodec">Codec matching the persisted value representation.</param>
    /// <param name="keyComparer">Persisted key ordering, or the default comparer.</param>
    /// <param name="valueComparer">Persisted value ordering within each key, or the default comparer.</param>
    /// <param name="cancellationToken">Cancels opening and page reads.</param>
    /// <returns>A tree containing the validated decoded topology.</returns>
    /// <remarks>Opening an empty store assigns an in-memory access-path identity but does not persist it.
    /// Memory use grows with the decoded index; use the disk-resident implementation when leaves must be read on demand.</remarks>
    public static async ValueTask<OrderedPageTree<TKey, TValue>> OpenAsync(
        IResizablePhysicalPageStore pages, IOrderedIndexCodec<TKey> keyCodec,
        IOrderedIndexCodec<TValue> valueCodec, IComparer<TKey>? keyComparer = null,
        IComparer<TValue>? valueComparer = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var codec = new OrderedPageTreeCodec<TKey, TValue>(keyCodec, valueCodec);
        try
        {
            long pageCount = await pages.GetPageCountAsync(cancellationToken).ConfigureAwait(false);
            TreeState state;
            if (pageCount == 0)
            {
                state = new(Guid.NewGuid(), OrderedPageTreeGeneration.Initial, [], [], []);
            }
            else
            {
                OrderedCatalogRoot catalog = codec.DecodeCatalogRoot(
                    (await pages.ReadAsync(new(0), cancellationToken).ConfigureAwait(false)).Span);
                var descriptors = new List<OrderedLeafDescriptor<TKey>>();
                var freePages = new List<PhysicalPageId>();
                foreach (PhysicalPageId branch in catalog.Branches)
                {
                    OrderedCatalogBranchState<TKey> branchState = codec.DecodeCatalogBranchState(
                        (await pages.ReadAsync(branch, cancellationToken).ConfigureAwait(false)).Span, branch);
                    descriptors.AddRange(branchState.Leaves);
                    freePages.AddRange(branchState.FreePages);
                }
                var leaves = new List<OrderedLeaf<TKey, TValue>>(descriptors.Count);
                foreach (OrderedLeafDescriptor<TKey> descriptor in descriptors)
                {
                    leaves.Add(codec.DecodeLeaf(
                        (await pages.ReadAsync(descriptor.PageId, cancellationToken).ConfigureAwait(false)).Span,
                        descriptor.PageId));
                }

                var ownedPages = descriptors.Select(item => item.PageId.Value)
                    .Concat(catalog.Branches.Select(item => item.Value)).Append(0).ToHashSet();
                for (long page = 1; page < pageCount; page++)
                {
                    if (!ownedPages.Contains(page))
                    {
                        freePages.Add(new(page));
                    }
                }

                freePages = freePages.Distinct().OrderBy(item => item.Value).ToList();
                state = new(catalog.AccessPathId, catalog.Generation, catalog.Branches, freePages, leaves);
                ValidateTopology(state, keyComparer ?? Comparer<TKey>.Default,
                    valueComparer ?? Comparer<TValue>.Default);
            }
            return new(pages, codec, keyComparer ?? Comparer<TKey>.Default,
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
            throw new OrderedPageTreeException("The ordered page tree could not be opened.", exception);
        }
    }

    /// <summary>Returns all visible values matching an exact key in the currently published generation.</summary>
    /// <remarks>Uses the configured comparers and materialises the complete match list. Use paging for bounded result counts.</remarks>
    public IReadOnlyList<TValue> Find(TKey key)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_gate)
        {
            TreeState state = _state;
            var result = new List<TValue>();
            AppendMatches(state, key, result);
            _telemetry?.AccessSeek(System.Diagnostics.Stopwatch.GetElapsedTime(started));
            return result;
        }
    }

    /// <summary>Maps matching values directly into a caller-owned result without an intermediate value list.</summary>
    public IReadOnlyList<TResult> FindMapped<TResult>(TKey key, Func<TValue, TResult> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_gate)
        {
            TreeState state = _state;
            var result = new List<TResult>();
            AppendMappedMatches(state, key, map, result);
            _telemetry?.AccessSeek(System.Diagnostics.Stopwatch.GetElapsedTime(started));
            return result;
        }
    }

    /// <summary>Returns the flattened values for several keys using one access-path operation.</summary>
    public IReadOnlyList<TValue> FindMany(IReadOnlyList<TKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return [];
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_gate)
        {
            TreeState state = _state;
            var result = new List<TValue>();
            foreach (TKey key in keys)
            {
                AppendMatches(state, key, result);
            }

            _telemetry?.AccessSeek(System.Diagnostics.Stopwatch.GetElapsedTime(started));
            return result;
        }
    }

    /// <summary>Maps flattened matches directly into a caller-owned result.</summary>
    public IReadOnlyList<TResult> FindManyMapped<TResult>(IReadOnlyList<TKey> keys, Func<TValue, TResult> map)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(map);
        if (keys.Count == 0)
        {
            return [];
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_gate)
        {
            TreeState state = _state;
            var result = new List<TResult>();
            foreach (TKey key in keys)
            {
                AppendMappedMatches(state, key, map, result);
            }

            _telemetry?.AccessSeek(System.Diagnostics.Stopwatch.GetElapsedTime(started));
            return result;
        }
    }

    private void AppendMatches(TreeState state, TKey key, List<TValue> result)
    {
        if (state.Leaves.Count == 0)
        {
            return;
        }

        int leafIndex = FindLeaf(state.Leaves, key);
        while (leafIndex > 0 && _keys.Compare(state.Leaves[leafIndex - 1].Entries[^1].Key, key) >= 0)
        {
            leafIndex--;
        }

        for (int index = leafIndex; index < state.Leaves.Count; index++)
        {
            foreach (OrderedLeafEntry<TKey, TValue> item in state.Leaves[index].Entries)
            {
                int comparison = _keys.Compare(item.Key, key);
                if (comparison > 0)
                {
                    return;
                }

                if (comparison == 0)
                {
                    result.Add(item.Value);
                }
            }
        }
    }

    private void AppendMappedMatches<TResult>(TreeState state, TKey key, Func<TValue, TResult> map,
        List<TResult> result)
    {
        if (state.Leaves.Count == 0)
        {
            return;
        }

        int leafIndex = FindLeaf(state.Leaves, key);
        while (leafIndex > 0 && _keys.Compare(state.Leaves[leafIndex - 1].Entries[^1].Key, key) >= 0)
        {
            leafIndex--;
        }

        for (int index = leafIndex; index < state.Leaves.Count; index++)
        {
            foreach (OrderedLeafEntry<TKey, TValue> item in state.Leaves[index].Entries)
            {
                int comparison = _keys.Compare(item.Key, key);
                if (comparison > 0)
                {
                    return;
                }

                if (comparison == 0)
                {
                    result.Add(map(item.Value));
                }
            }
        }
    }

    /// <summary>Reads a bounded forward or reverse page from the currently published generation.</summary>
    /// <param name="pageSize">Positive result limit below <see cref="int.MaxValue"/>; one extra entry is examined for continuation.</param>
    /// <param name="range">Inclusive key bounds, or null for the entire index. Keep unchanged between pages.</param>
    /// <param name="direction">Scan direction, unchanged between pages.</param>
    /// <param name="cursor">Exclusive continuation from the same access path, generation and direction, or null to start.</param>
    /// <returns>Entries and a continuation cursor only when another matching entry exists.</returns>
    /// <remarks>Publication between calls invalidates an old cursor. Capture a snapshot for a stable multipage read.
    /// Cursor validation is not authentication of client-provided data.</remarks>
    public OrderedPageTreePage<TKey, TValue> ReadPage(int pageSize,
        OrderedIndexRange<TKey>? range = null, OrderedScanDirection direction = OrderedScanDirection.Forward,
        OrderedPageTreeCursor<TKey, TValue>? cursor = null)
    {
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        lock (_gate)
        {
            TreeState state = _state;
            if (cursor is not null && (cursor.AccessPathId != state.AccessPathId ||
                                       cursor.Generation != state.Generation || cursor.Direction != direction))
            {
                throw new ArgumentException("The cursor does not belong to this access-path snapshot.", nameof(cursor));
            }

            range ??= OrderedIndexRange<TKey>.All;
            IEnumerable<OrderedLeafEntry<TKey, TValue>> query = state.Leaves.SelectMany(item => item.Entries);
            query = query.Where(item => (!range.HasLowerBound || _keys.Compare(item.Key, range.LowerInclusive!) >= 0) &&
                                        (!range.HasUpperBound || _keys.Compare(item.Key, range.UpperInclusive!) <= 0));
            if (direction is OrderedScanDirection.Reverse)
            {
                query = query.Reverse();
            }

            if (cursor is not null)
            {
                query = query.Where(item => direction is OrderedScanDirection.Forward
                    ? Compare(item, cursor.Key, cursor.Value) > 0 : Compare(item, cursor.Key, cursor.Value) < 0);
            }

            OrderedLeafEntry<TKey, TValue>[] selected = query.Take(checked(pageSize + 1)).ToArray();
            bool more = selected.Length > pageSize;
            OrderedLeafEntry<TKey, TValue>[] visible = more ? selected[..pageSize] : selected;
            OrderedPageTreeCursor<TKey, TValue>? next = more && visible.Length != 0
                ? new(state.AccessPathId, state.Generation, visible[^1].Key, visible[^1].Value, direction) : null;
            return new(visible.Select(item => new KeyValuePair<TKey, TValue>(item.Key, item.Value)).ToArray(), next);
        }
    }

    /// <summary>Stages a new decoded generation and physical redo without publishing it.</summary>
    /// <param name="transactionId">Nonempty identity for the exclusively coordinated transaction.</param>
    /// <param name="mutations">Changes to apply to published state.</param>
    /// <param name="cancellationToken">Cancels allocation admission, preparation and page reads where supported.</param>
    /// <returns>The staged generation and mutations requiring durable recovery protection.</returns>
    /// <remarks>Complete the workflow by writing and publishing the stage, or abandon it. Allocation ownership can
    /// remain held until staged writing or abandonment; preparing another mutation is not an independent concurrent transaction.</remarks>
    public async ValueTask<OrderedPageTreeStage> PrepareAsync(Guid transactionId,
        IReadOnlyList<OrderedPageTreeMutation<TKey, TValue>> mutations,
        CancellationToken cancellationToken = default)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A transaction identity is required.", nameof(transactionId));
        }

        ArgumentNullException.ThrowIfNull(mutations);
        cancellationToken.ThrowIfCancellationRequested();
        TreeState current;
        lock (_gate)
        {
            if (_staged.Count != 0)
            {
                throw new OrderedPageTreeException("Only one ordered page-tree mutation may be staged.");
            }

            current = _state;
        }
        try
        {
            await _allocation.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                long pageCount = await _pages.GetPageCountAsync(cancellationToken).ConfigureAwait(false);
                long nextPageId = Math.Max(1, pageCount);
                var freePages = new SortedSet<long>(current.FreePages
                    .Where(item => !_replacementReservations.Contains(item)).Select(item => item.Value));
                long generation = checked(current.Generation.Value + 1);
                // Leaves and their entry collections are immutable. Copy only the topology list here;
                // mutation replaces the affected leaf records and therefore naturally preserves any
                // snapshot that still references the previous generation.
                var leaves = current.Leaves.ToList();
                var changed = new HashSet<long>();
                int splits = 0;
                int merges = 0;
                if (mutations.Count > 1 && mutations.All(item => item.Kind is OrderedPageTreeMutationKind.Add))
                {
                    ApplyAddBatch(mutations, leaves, changed, freePages, ref nextPageId, generation, ref splits);
                }
                else
                {
                    foreach (OrderedPageTreeMutation<TKey, TValue> mutation in mutations)
                    {
                        Apply(mutation, leaves, changed, freePages, ref nextPageId, generation, ref splits, ref merges);
                    }
                }

                Relink(leaves);
                var afterImages = new Dictionary<PhysicalPageId, ReadOnlyMemory<byte>>();
                foreach (OrderedLeaf<TKey, TValue> leaf in leaves.Where(item => changed.Contains(item.PageId.Value)))
                {
                    afterImages.Add(leaf.PageId, _codec.EncodeLeaf(leaf.PageId, generation, leaf.Previous,
                        leaf.Next, leaf.Entries, generation));
                }

                var descriptors = leaves.Select(item => new OrderedLeafDescriptor<TKey>(item.PageId, item.Entries[0].Key)).ToArray();
                OrderedCatalogImage catalog = _codec.EncodeCatalog(current.AccessPathId, generation, descriptors,
                    current.CatalogBranches,
                    freePages.Select(item => new PhysicalPageId(item)).ToArray(),
                    ref nextPageId, generation);
                afterImages.Add(new(0), catalog.Root);
                foreach (KeyValuePair<PhysicalPageId, ReadOnlyMemory<byte>> branch in catalog.Branches)
                {
                    afterImages.Add(branch.Key, branch.Value);
                }

                IReadOnlyList<DatabasePhysicalPageMutation> physical = CreatePhysicalMutations(afterImages);
                var next = new TreeState(current.AccessPathId, new(generation), catalog.BranchIds,
                    freePages.Select(item => new PhysicalPageId(item)).ToArray(), leaves);
                lock (_gate)
                {
                    _staged.Add(transactionId, new(next, afterImages, physical, []));
                }

                _allocationOwner = transactionId;
                _telemetry?.AccessMutation(System.Diagnostics.Stopwatch.GetElapsedTime(started),
                    afterImages.Values.Sum(image => (long)image.Length),
                    physical.Sum(item => (long)item.Bytes.Length + item.BeforeImage.Length),
                    afterImages.Count, splits, merges);
                return new(transactionId, next.Generation, physical);
            }
            finally
            {
                if (_allocationOwner != transactionId)
                {
                    _allocation.Release();
                }
            }
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
            throw new OrderedPageTreeException("Ordered page-tree mutations could not be staged.", exception);
        }
    }

    /// <summary>Builds and durably flushes an unreachable complete replacement generation.</summary>
    public ValueTask BuildReplacementAsync(Guid replacementId,
        IReadOnlyList<KeyValuePair<TKey, TValue>> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var comparer = new EntryComparer(_keys, _values);
        OrderedLeafEntry<TKey, TValue>[] ordered = NormalizeReplacement(entries, comparer);
        return BuildNormalizedReplacementAsync(replacementId, ordered, cancellationToken);
    }

    /// <summary>
    /// Builds a replacement from a bounded stream without first materializing a second caller-owned entry array.
    /// </summary>
    public ValueTask BuildReplacementAsync(Guid replacementId,
        IEnumerable<KeyValuePair<TKey, TValue>> entries, int expectedCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (expectedCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedCount));
        }

        var comparer = new EntryComparer(_keys, _values);
        OrderedLeafEntry<TKey, TValue>[] ordered = NormalizeReplacement(entries, expectedCount, comparer);
        return BuildNormalizedReplacementAsync(replacementId, ordered, cancellationToken);
    }

    private async ValueTask BuildNormalizedReplacementAsync(Guid replacementId,
        OrderedLeafEntry<TKey, TValue>[] ordered, CancellationToken cancellationToken)
    {
        if (replacementId == Guid.Empty)
        {
            throw new ArgumentException("A replacement identity is required.", nameof(replacementId));
        }

        TreeState current;
        lock (_gate)
        {
            if (_replacements.ContainsKey(replacementId))
            {
                throw new OrderedPageTreeException("The ordered replacement already exists.");
            }

            current = _state;
        }
        long generation = checked(current.Generation.Value + 1);
        IReadOnlyList<IReadOnlyList<OrderedLeafEntry<TKey, TValue>>> chunks = ChunkReplacement(ordered, generation);
        await _allocation.WaitAsync(cancellationToken).ConfigureAwait(false);
        long nextPage;
        List<OrderedLeaf<TKey, TValue>> leaves;
        OrderedCatalogImage catalog;
        PhysicalPageId[] freePages;
        bool initialBuild;
        try
        {
            long pageCount = await _pages.GetPageCountAsync(cancellationToken).ConfigureAwait(false);
            initialBuild = pageCount == 0;
            nextPage = Math.Max(1, pageCount);
            var reusablePages = new SortedSet<long>();
            lock (_gate)
            {
                reusablePages.UnionWith(current.FreePages.Concat(_abandonedReplacementPages)
                    .Where(page => !_replacementReservations.Contains(page)).Select(page => page.Value));
            }

            var leafPageIds = new PhysicalPageId[chunks.Count];
            for (int index = 0; index < leafPageIds.Length; index++)
            {
                if (reusablePages.Count == 0)
                {
                    leafPageIds[index] = new(nextPage++);
                }
                else
                {
                    long page = reusablePages.Min;
                    reusablePages.Remove(page);
                    leafPageIds[index] = new(page);
                }
            }
            leaves = new List<OrderedLeaf<TKey, TValue>>(chunks.Count);
            for (int index = 0; index < chunks.Count; index++)
            {
                PhysicalPageId page = leafPageIds[index];
                leaves.Add(new(page, generation,
                    index == 0 ? null : leafPageIds[index - 1],
                    index + 1 == chunks.Count ? null : leafPageIds[index + 1], chunks[index]));
            }
            (catalog, PhysicalPageId[] unusedReusable, nextPage) = EncodeReplacementCatalog(
                current.AccessPathId, generation,
                leaves.Select(item => new OrderedLeafDescriptor<TKey>(item.PageId, item.Entries[0].Key)).ToArray(),
                reusablePages.Select(page => new PhysicalPageId(page)).ToArray(), nextPage);
            freePages = unusedReusable.Concat(current.CatalogBranches)
                .Concat(current.Leaves.Select(item => item.PageId))
                .Distinct().OrderBy(item => item.Value).ToArray();
            if (!initialBuild)
            {
                await _pages.EnsureCapacityAsync(nextPage, cancellationToken).ConfigureAwait(false);
            }

            if (!initialBuild)
            {
                lock (_gate)
                {
                    foreach (PhysicalPageId page in leaves.Select(item => item.PageId).Concat(catalog.BranchIds))
                    {
                        _replacementReservations.Add(page);
                        _abandonedReplacementPages.Remove(page);
                    }
                }
            }
        }
        finally
        {
            _allocation.Release();
        }
        // With no durable root, speculative file growth would make reopen interpret an
        // uninitialized page zero as an established tree. Keep first-build images private;
        // the root and every referenced page enter the same committing redo group.
        if (initialBuild && checked(((long)leaves.Count + catalog.Branches.Count + 1) * _pages.PageSize) >
            MaximumInitialImageBytes)
        {
            throw new OrderedPageTreeException("The first replacement exceeds its private page-image budget.");
        }

        Dictionary<PhysicalPageId, ReadOnlyMemory<byte>>? initialImages = initialBuild ? new() : null;
        try
        {
            foreach (OrderedLeaf<TKey, TValue> leaf in leaves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlyMemory<byte> image = _codec.EncodeLeaf(leaf.PageId, generation,
                    leaf.Previous, leaf.Next, leaf.Entries, generation);
                if (initialImages is not null)
                {
                    initialImages.Add(leaf.PageId, image);
                }
                else
                {
                    await _pages.WriteAsync(leaf.PageId, image, cancellationToken).ConfigureAwait(false);
                }
            }
            foreach ((PhysicalPageId page, ReadOnlyMemory<byte> image) in catalog.Branches)
            {
                if (initialImages is not null)
                {
                    initialImages.Add(page, image);
                }
                else
                {
                    await _pages.WriteAsync(page, image, cancellationToken).ConfigureAwait(false);
                }
            }

            if (initialImages is not null)
            {
                initialImages.Add(new(0), catalog.Root);
            }
            else
            {
                await _pages.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            if (!initialBuild)
            {
                lock (_gate)
                {
                    foreach (PhysicalPageId page in leaves.Select(item => item.PageId).Concat(catalog.BranchIds))
                    {
                        _replacementReservations.Remove(page);
                        _abandonedReplacementPages.Add(page);
                    }
                }
            }

            throw;
        }
        var state = new TreeState(current.AccessPathId, new(generation), catalog.BranchIds, freePages, leaves);
        PhysicalPageId[] ownedReplacementPages = initialBuild ? [] : leaves.Select(item => item.PageId)
            .Concat(catalog.BranchIds).ToArray();
        lock (_gate)
        {
            _replacements.Add(replacementId,
            new(current.Generation, state, catalog.Root, ownedReplacementPages, initialImages));
        }
    }

    private (OrderedCatalogImage Catalog, PhysicalPageId[] FreePages, long NextPage)
        EncodeReplacementCatalog(Guid accessPathId, long generation,
            IReadOnlyList<OrderedLeafDescriptor<TKey>> leaves,
            PhysicalPageId[] reusablePages, long initialNextPage)
    {
        for (int reusableBranchCount = 0; reusableBranchCount <= reusablePages.Length;
             reusableBranchCount++)
        {
            PhysicalPageId[] branches = reusablePages.Take(reusableBranchCount).ToArray();
            PhysicalPageId[] free = reusablePages.Skip(reusableBranchCount).ToArray();
            long nextPage = initialNextPage;
            OrderedCatalogImage candidate = _codec.EncodeCatalog(accessPathId, generation, leaves,
                branches, free, ref nextPage, generation);
            int branchesTaken = Math.Min(candidate.BranchIds.Count, reusablePages.Length);
            if (branchesTaken == reusableBranchCount)
            {
                return (candidate, free, nextPage);
            }
        }
        throw new OrderedPageTreeException("A stable replacement catalog allocation could not be produced.");
    }

    /// <summary>Stages the root switch for a previously flushed replacement.</summary>
    public OrderedPageTreeStage PrepareReplacement(Guid transactionId, Guid replacementId)
    {
        lock (_gate)
        {
            ReplacementState replacement = _replacements.GetValueOrDefault(replacementId) ??
                throw new OrderedPageTreeException("The ordered replacement is not available.");
            if (_state.Generation != replacement.ExpectedGeneration)
            {
                throw new OrderedPageTreeReplacementStaleException(
                    replacement.ExpectedGeneration.Value, _state.Generation.Value);
            }

            if (_staged.Count != 0)
            {
                throw new OrderedPageTreeException("Only one ordered page-tree mutation may be staged.");
            }

            IReadOnlyDictionary<PhysicalPageId, ReadOnlyMemory<byte>> images = replacement.InitialImages
                ?? new Dictionary<PhysicalPageId, ReadOnlyMemory<byte>> { [new(0)] = replacement.RootImage };
            IReadOnlyList<DatabasePhysicalPageMutation> physical = images.OrderBy(pair => pair.Key.Value)
                .Select(pair => DatabasePhysicalPageMutation.CreatePage(pair.Key.Value, pair.Value)).ToArray();
            _staged.Add(transactionId, new(replacement.State, images, physical, replacement.OwnedPages));
            _replacements.Remove(replacementId);
            _abandonedReplacementPages.Clear();
            return new(transactionId, replacement.State.Generation, physical);
        }
    }

    /// <summary>Gets the live generation captured when an unpublished replacement was built.</summary>
    public OrderedPageTreeGeneration GetReplacementExpectedGeneration(Guid replacementId)
    {
        lock (_gate)
        {
            return _replacements.GetValueOrDefault(replacementId)?.ExpectedGeneration ??
                throw new OrderedPageTreeException("The ordered replacement is not available.");
        }
    }

    /// <summary>Discards an unpublished replacement if present and makes its reserved pages available for later reuse.</summary>
    /// <remarks>Does not erase the replacement's physical images or undo an adopted generation.</remarks>
    public void AbandonReplacement(Guid replacementId)
    {
        lock (_gate)
        {
            if (_replacements.Remove(replacementId, out ReplacementState? replacement))
            {
                _replacementReservations.ExceptWith(replacement.OwnedPages);
                _abandonedReplacementPages.UnionWith(replacement.OwnedPages);
            }
        }
    }

    private IReadOnlyList<IReadOnlyList<OrderedLeafEntry<TKey, TValue>>> ChunkReplacement(
        IReadOnlyList<OrderedLeafEntry<TKey, TValue>> entries, long generation)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var result = new List<IReadOnlyList<OrderedLeafEntry<TKey, TValue>>>();
        var chunk = new List<OrderedLeafEntry<TKey, TValue>>();
        int occupiedBytes = OrderedPageTreeCodec<TKey, TValue>.EmptyLeafStorageBytes;
        foreach (OrderedLeafEntry<TKey, TValue> entry in entries)
        {
            int entryBytes = _codec.LeafEntryStorageBytes(entry);
            if (entryBytes > _pages.PageSize - OrderedPageTreeCodec<TKey, TValue>.EmptyLeafStorageBytes)
            {
                throw new PhysicalPageCapacityException("An ordered entry exceeds page capacity.");
            }

            if (occupiedBytes + entryBytes > _pages.PageSize)
            {
                result.Add(chunk.ToArray());
                chunk.Clear();
                occupiedBytes = OrderedPageTreeCodec<TKey, TValue>.EmptyLeafStorageBytes;
            }
            chunk.Add(entry);
            occupiedBytes = checked(occupiedBytes + entryBytes);
        }
        if (chunk.Count != 0)
        {
            result.Add(chunk.ToArray());
        }

        return result;
    }

    private static OrderedLeafEntry<TKey, TValue>[] NormalizeReplacement(
        IReadOnlyList<KeyValuePair<TKey, TValue>> entries,
        IComparer<OrderedLeafEntry<TKey, TValue>> comparer)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        OrderedLeafEntry<TKey, TValue>[] ordered = entries
            .Select(item => new OrderedLeafEntry<TKey, TValue>(item.Key, item.Value)).ToArray();
        Array.Sort(ordered, comparer);
        int distinct = 1;
        for (int index = 1; index < ordered.Length; index++)
        {
            if (comparer.Compare(ordered[index], ordered[distinct - 1]) != 0)
            {
                ordered[distinct++] = ordered[index];
            }
        }

        if (distinct != ordered.Length)
        {
            Array.Resize(ref ordered, distinct);
        }

        return ordered;
    }

    private static OrderedLeafEntry<TKey, TValue>[] NormalizeReplacement(
        IEnumerable<KeyValuePair<TKey, TValue>> entries, int expectedCount,
        IComparer<OrderedLeafEntry<TKey, TValue>> comparer)
    {
        if (expectedCount == 0)
        {
            using IEnumerator<KeyValuePair<TKey, TValue>> empty = entries.GetEnumerator();
            if (empty.MoveNext())
            {
                throw new ArgumentException("Replacement entry count is incorrect.", nameof(entries));
            }

            return [];
        }

        var ordered = new OrderedLeafEntry<TKey, TValue>[expectedCount];
        int count = 0;
        foreach (KeyValuePair<TKey, TValue> entry in entries)
        {
            if (count == ordered.Length)
            {
                throw new ArgumentException("Replacement entry count is incorrect.", nameof(entries));
            }

            ordered[count++] = new(entry.Key, entry.Value);
        }
        if (count != ordered.Length)
        {
            throw new ArgumentException("Replacement entry count is incorrect.", nameof(entries));
        }

        Array.Sort(ordered, comparer);
        int distinct = 1;
        for (int index = 1; index < ordered.Length; index++)
        {
            if (comparer.Compare(ordered[index], ordered[distinct - 1]) != 0)
            {
                ordered[distinct++] = ordered[index];
            }
        }

        if (distinct != ordered.Length)
        {
            Array.Resize(ref ordered, distinct);
        }

        return ordered;
    }

    /// <summary>Writes staged after-images, growing the page store as needed, and flushes them without publishing decoded state.</summary>
    /// <param name="transactionId">Identity of an existing stage.</param>
    /// <param name="cancellationToken">Cancels page I/O where supported.</param>
    /// <remarks>The caller must first durably protect the mutations. Failure can leave some images written;
    /// allocation ownership is released even on failure, while the stage remains for coordinated recovery or cleanup.</remarks>
    public async ValueTask PersistAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        await WriteStagedCoreAsync(transactionId, flushToDisk: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a WAL-protected staged generation without forcing the page file to disk.</summary>
    public async ValueTask WriteStagedAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        await WriteStagedCoreAsync(transactionId, flushToDisk: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Forces all buffered generations to disk before their protecting redo can be reclaimed.</summary>
    public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
        _pages.FlushAsync(cancellationToken);

    private async ValueTask WriteStagedCoreAsync(Guid transactionId, bool flushToDisk,
        CancellationToken cancellationToken)
    {
        StagedState staged;
        lock (_gate)
        {
            staged = _staged.TryGetValue(transactionId, out StagedState? value) ? value :
                throw new OrderedPageTreeException("The ordered page-tree transaction is not staged.");
        }

        try
        {
            long required = staged.AfterImages.Keys.Select(item => item.Value + 1).DefaultIfEmpty(0).Max();
            if (await _pages.GetPageCountAsync(cancellationToken).ConfigureAwait(false) < required)
            {
                await _pages.EnsureCapacityAsync(required, cancellationToken).ConfigureAwait(false);
            }

            foreach ((PhysicalPageId pageId, ReadOnlyMemory<byte> image) in staged.AfterImages)
            {
                await _pages.WriteAsync(pageId, image, cancellationToken).ConfigureAwait(false);
            }

            if (flushToDisk)
            {
                await _pages.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
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
            throw new OrderedPageTreeException("The durable ordered page-tree generation could not be published.", exception);
        }
        finally
        {
            ReleaseAllocation(transactionId);
        }
    }

    /// <summary>Adopts staged decoded state in memory and removes the stage.</summary>
    /// <remarks>Does not write or flush pages or a WAL. The caller must establish durability and complete staged
    /// writing before publication; this method alone does not release ordinary staged allocation ownership.</remarks>
    public void Publish(Guid transactionId)
    {
        lock (_gate)
        {
            StagedState staged = _staged.TryGetValue(transactionId, out StagedState? value) ? value :
                throw new OrderedPageTreeException("The ordered page-tree transaction is not staged.");
            _state = staged.State;
            _staged.Remove(transactionId);
            _replacementReservations.ExceptWith(staged.ReservedPages);
        }
    }

    /// <summary>Persists and flushes staged after-images, then publishes their decoded state.</summary>
    /// <remarks>Combines <see cref="PersistAsync"/> and <see cref="Publish"/>; the caller must still provide durable redo
    /// and transaction coordination. This method is not an independent transaction protocol.</remarks>
    public async ValueTask PublishAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        await PersistAsync(transactionId, cancellationToken).ConfigureAwait(false);
        Publish(transactionId);
    }

    /// <summary>Removes an unpublished stage if present, releases its page reservations and allocation ownership.</summary>
    /// <remarks>Does not undo physical writes or a durable logical transaction. Coordinate abandonment with the owner’s recovery protocol.</remarks>
    public void Abandon(Guid transactionId)
    {
        lock (_gate)
        {
            if (_staged.Remove(transactionId, out StagedState? staged))
            {
                _replacementReservations.ExceptWith(staged.ReservedPages);
                _abandonedReplacementPages.UnionWith(staged.ReservedPages);
            }
        }

        ReleaseAllocation(transactionId);
    }

    private void ReleaseAllocation(Guid transactionId)
    {
        if (_allocationOwner != transactionId)
        {
            return;
        }

        _allocationOwner = null;
        _allocation.Release();
    }

    private static IReadOnlyList<DatabasePhysicalPageMutation> CreatePhysicalMutations(
        IReadOnlyDictionary<PhysicalPageId, ReadOnlyMemory<byte>> afterImages)
    {
        var result = new List<DatabasePhysicalPageMutation>(afterImages.Count);
        foreach ((PhysicalPageId pageId, ReadOnlyMemory<byte> after) in afterImages)
        {
            result.Add(DatabasePhysicalPageMutation.CreatePage(pageId.Value, after));
        }

        return result;
    }

    private void Apply(OrderedPageTreeMutation<TKey, TValue> mutation,
        List<OrderedLeaf<TKey, TValue>> leaves, HashSet<long> changed, SortedSet<long> freePages,
        ref long nextPageId, long generation,
        ref int splits, ref int merges)
    {
        if (leaves.Count == 0)
        {
            if (mutation.Kind is OrderedPageTreeMutationKind.Remove)
            {
                return;
            }

            var pageId = AllocatePage(freePages, ref nextPageId);
            leaves.Add(new(pageId, generation, null, null, [new(mutation.Key, mutation.Value)]));
            changed.Add(pageId.Value);
            return;
        }
        int leafIndex = FindLeaf(leaves, mutation.Key);
        OrderedLeaf<TKey, TValue> leaf = leaves[leafIndex];
        var entries = leaf.Entries.ToList();
        int position = entries.BinarySearch(new(mutation.Key, mutation.Value), new EntryComparer(_keys, _values));
        if (mutation.Kind is OrderedPageTreeMutationKind.Remove)
        {
            if (position < 0)
            {
                return;
            }

            entries.RemoveAt(position);
            if (entries.Count == 0)
            {
                leaves.RemoveAt(leafIndex);
                changed.Add(leaf.PageId.Value);
                freePages.Add(leaf.PageId.Value);
            }
            else
            {
                leaves[leafIndex] = leaf with
                {
                    Generation = generation,
                    Entries = entries
                };
                changed.Add(leaf.PageId.Value);
            }
            if (MergeIfPossible(leaves, Math.Min(leafIndex, leaves.Count - 1), changed, freePages, generation))
            {
                merges++;
            }

            MarkNeighbors(leaves, leafIndex, changed);
            return;
        }
        if (position >= 0)
        {
            return;
        }

        entries.Insert(~position, new(mutation.Key, mutation.Value));
        if (_codec.Fits(leaf.PageId, generation, leaf.Previous, leaf.Next, entries))
        {
            leaves[leafIndex] = leaf with
            {
                Generation = generation,
                Entries = entries
            };
            changed.Add(leaf.PageId.Value);
            return;
        }
        int midpoint = entries.Count / 2;
        var rightId = AllocatePage(freePages, ref nextPageId);
        leaves[leafIndex] = leaf with
        {
            Generation = generation,
            Entries = entries[..midpoint]
        };
        leaves.Insert(leafIndex + 1, new(rightId, generation, leaf.PageId, leaf.Next, entries[midpoint..]));
        splits++;
        changed.Add(leaf.PageId.Value);
        changed.Add(rightId.Value);
        MarkNeighbors(leaves, leafIndex + 1, changed);
    }

    private static void Relink(List<OrderedLeaf<TKey, TValue>> leaves)
    {
        for (int index = 0; index < leaves.Count; index++)
        {
            leaves[index] = leaves[index] with
            {
                Previous = index == 0 ? null : leaves[index - 1].PageId,
                Next = index + 1 == leaves.Count ? null : leaves[index + 1].PageId
            };
        }
    }

    private bool MergeIfPossible(List<OrderedLeaf<TKey, TValue>> leaves, int index,
        HashSet<long> changed, SortedSet<long> freePages, long generation)
    {
        if (leaves.Count < 2 || index < 0)
        {
            return false;
        }

        int leftIndex = index > 0 ? index - 1 : index;
        int rightIndex = leftIndex + 1;
        OrderedLeaf<TKey, TValue> left = leaves[leftIndex];
        OrderedLeaf<TKey, TValue> right = leaves[rightIndex];
        OrderedLeafEntry<TKey, TValue>[] combined = left.Entries.Concat(right.Entries).ToArray();
        if (!_codec.Fits(left.PageId, generation, left.Previous, right.Next, combined))
        {
            return false;
        }

        leaves[leftIndex] = left with
        {
            Generation = generation,
            Next = right.Next,
            Entries = combined
        };
        leaves.RemoveAt(rightIndex);
        changed.Add(left.PageId.Value);
        changed.Add(right.PageId.Value);
        freePages.Add(right.PageId.Value);
        return true;
    }

    private static PhysicalPageId AllocatePage(SortedSet<long> freePages, ref long nextPageId)
    {
        if (freePages.Count == 0)
        {
            return new PhysicalPageId(nextPageId++);
        }

        long value = freePages.Min;
        freePages.Remove(value);
        return new(value);
    }

    private static void MarkNeighbors(IReadOnlyList<OrderedLeaf<TKey, TValue>> leaves, int index, HashSet<long> changed)
    {
        if (index > 0)
        {
            changed.Add(leaves[index - 1].PageId.Value);
        }

        if (index + 1 < leaves.Count)
        {
            changed.Add(leaves[index + 1].PageId.Value);
        }
    }

    private int FindLeaf(IReadOnlyList<OrderedLeaf<TKey, TValue>> leaves, TKey key)
    {
        int low = 0, high = leaves.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            int comparison = _keys.Compare(leaves[middle].Entries[0].Key, key);
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

    private int Compare(OrderedLeafEntry<TKey, TValue> entry, TKey key, TValue value)
    {
        int keyResult = _keys.Compare(entry.Key, key);
        return keyResult != 0 ? keyResult : _values.Compare(entry.Value, value);
    }

    private static void ValidateTopology(TreeState state, IComparer<TKey> keys, IComparer<TValue> values)
    {
        var comparer = new EntryComparer(keys, values);
        var allocated = state.Leaves.Select(item => item.PageId.Value)
            .Concat(state.CatalogBranches.Select(item => item.Value)).Append(0).ToHashSet();
        if (state.FreePages.Any(item => item.Value <= 0 || allocated.Contains(item.Value)) ||
            state.FreePages.Select(item => item.Value).Distinct().Count() != state.FreePages.Count)
        {
            throw new PhysicalPageCorruptionException("The ordered free-page catalog overlaps allocated topology.");
        }

        for (int index = 0; index < state.Leaves.Count; index++)
        {
            OrderedLeaf<TKey, TValue> leaf = state.Leaves[index];
            if (leaf.Entries.Count == 0)
            {
                throw new PhysicalPageCorruptionException("An ordered leaf is empty.");
            }

            if (leaf.Previous != (index == 0 ? null : state.Leaves[index - 1].PageId) ||
                leaf.Next != (index + 1 == state.Leaves.Count ? null : state.Leaves[index + 1].PageId))
            {
                throw new PhysicalPageCorruptionException("Ordered leaf links are not contiguous.");
            }

            for (int item = 1; item < leaf.Entries.Count; item++)
            {
                if (comparer.Compare(leaf.Entries[item - 1], leaf.Entries[item]) >= 0)
                {
                    throw new PhysicalPageCorruptionException("Ordered leaf entries are not strictly ordered.");
                }
            }
        }
    }

    private sealed record TreeState(Guid AccessPathId, OrderedPageTreeGeneration Generation,
        IReadOnlyList<PhysicalPageId> CatalogBranches,
        IReadOnlyList<PhysicalPageId> FreePages,
        IReadOnlyList<OrderedLeaf<TKey, TValue>> Leaves);

    private sealed record StagedState(TreeState State,
        IReadOnlyDictionary<PhysicalPageId, ReadOnlyMemory<byte>> AfterImages,
        IReadOnlyList<DatabasePhysicalPageMutation> PhysicalMutations,
        IReadOnlyList<PhysicalPageId> ReservedPages);

    private sealed record ReplacementState(OrderedPageTreeGeneration ExpectedGeneration,
        TreeState State, ReadOnlyMemory<byte> RootImage, IReadOnlyList<PhysicalPageId> OwnedPages,
        IReadOnlyDictionary<PhysicalPageId, ReadOnlyMemory<byte>>? InitialImages);

    private sealed class EntryComparer(IComparer<TKey> keys, IComparer<TValue> values) :
        IComparer<OrderedLeafEntry<TKey, TValue>>
    {
        public int Compare(OrderedLeafEntry<TKey, TValue>? x, OrderedLeafEntry<TKey, TValue>? y)
        {
            int key = keys.Compare(x!.Key, y!.Key);
            return key != 0 ? key : values.Compare(x.Value, y.Value);
        }
    }

    private void ApplyAddBatch(IReadOnlyList<OrderedPageTreeMutation<TKey, TValue>> mutations,
        List<OrderedLeaf<TKey, TValue>> leaves, HashSet<long> changed, SortedSet<long> freePages,
        ref long nextPageId, long generation, ref int splits)
    {
        var comparer = new EntryComparer(_keys, _values);
        OrderedLeafEntry<TKey, TValue>[] additions = DistinctAdditions(mutations, comparer);
        if (leaves.Count != 0)
        {
            // Preserve untouched leaf images. Repacking the complete tree for an
            // ordinary add-only transaction makes ingestion quadratic in its size.
            var incrementalLeaves = new List<OrderedLeaf<TKey, TValue>>(leaves.Count);
            int first = 0;
            for (int index = 0; index < leaves.Count; index++)
            {
                OrderedLeaf<TKey, TValue> leaf = leaves[index];
                int last = first;
                while (last < additions.Length && (index + 1 == leaves.Count ||
                    _keys.Compare(additions[last].Key, leaves[index + 1].Entries[0].Key) < 0))
                {
                    last++;
                }

                if (last == first)
                {
                    incrementalLeaves.Add(leaf);
                    continue;
                }
                var mergedLeaf = MergeDistinct(leaf.Entries, additions[first..last], comparer);
                first = last;
                if (mergedLeaf.Length == leaf.Entries.Count)
                {
                    incrementalLeaves.Add(leaf);
                    continue;
                }
                IReadOnlyList<IReadOnlyList<OrderedLeafEntry<TKey, TValue>>> leafChunks =
                    _codec.Fits(leaf.PageId, generation, leaf.Previous, leaf.Next, mergedLeaf)
                        ? [mergedLeaf] : ChunkAddBatch(mergedLeaf, generation);
                for (int chunk = 0; chunk < leafChunks.Count; chunk++)
                {
                    PhysicalPageId page = chunk == 0 ? leaf.PageId : AllocatePage(freePages, ref nextPageId);
                    incrementalLeaves.Add(new(page, generation, null, null, leafChunks[chunk]));
                    changed.Add(page.Value);
                }
                splits += leafChunks.Count - 1;
                if (leafChunks.Count > 1 && index + 1 < leaves.Count)
                {
                    changed.Add(leaves[index + 1].PageId.Value);
                }
            }
            leaves.Clear();
            leaves.AddRange(incrementalLeaves);
            return;
        }
        OrderedLeafEntry<TKey, TValue>[] existing = leaves.SelectMany(item => item.Entries).ToArray();
        OrderedLeafEntry<TKey, TValue>[] merged = MergeDistinct(existing, additions, comparer);
        if (merged.Length == existing.Length)
        {
            return;
        }

        IReadOnlyList<IReadOnlyList<OrderedLeafEntry<TKey, TValue>>> chunks =
            ChunkAddBatch(merged, generation);
        PhysicalPageId[] existingPages = leaves.Select(item => item.PageId).ToArray();
        var replacement = new List<OrderedLeaf<TKey, TValue>>(chunks.Count);
        for (int index = 0; index < chunks.Count; index++)
        {
            PhysicalPageId pageId = index < existingPages.Length
                ? existingPages[index]
                : AllocatePage(freePages, ref nextPageId);
            replacement.Add(new(pageId, generation, null, null, chunks[index]));
            changed.Add(pageId.Value);
        }
        for (int index = chunks.Count; index < existingPages.Length; index++)
        {
            changed.Add(existingPages[index].Value);
            freePages.Add(existingPages[index].Value);
        }
        splits += Math.Max(0, replacement.Count - leaves.Count);
        leaves.Clear();
        leaves.AddRange(replacement);
    }

    private static OrderedLeafEntry<TKey, TValue>[] DistinctAdditions(
        IEnumerable<OrderedPageTreeMutation<TKey, TValue>> mutations,
        IComparer<OrderedLeafEntry<TKey, TValue>> comparer)
    {
        OrderedLeafEntry<TKey, TValue>[] entries = mutations
            .Select(item => new OrderedLeafEntry<TKey, TValue>(item.Key, item.Value)).ToArray();
        Array.Sort(entries, comparer);
        if (entries.Length < 2)
        {
            return entries;
        }

        int distinct = 1;
        for (int index = 1; index < entries.Length; index++)
        {
            if (comparer.Compare(entries[index], entries[distinct - 1]) != 0)
            {
                entries[distinct++] = entries[index];
            }
        }

        if (distinct != entries.Length)
        {
            Array.Resize(ref entries, distinct);
        }

        return entries;
    }

    private IReadOnlyList<IReadOnlyList<OrderedLeafEntry<TKey, TValue>>> ChunkAddBatch(
        IReadOnlyList<OrderedLeafEntry<TKey, TValue>> entries, long generation)
    {
        IReadOnlyList<IReadOnlyList<OrderedLeafEntry<TKey, TValue>>> dense =
            ChunkReplacement(entries, generation);
        var balanced = new List<IReadOnlyList<OrderedLeafEntry<TKey, TValue>>>(dense.Count * 2);
        foreach (IReadOnlyList<OrderedLeafEntry<TKey, TValue>> chunk in dense)
        {
            if (chunk.Count < 2)
            {
                balanced.Add(chunk);
            }
            else
            {
                int midpoint = chunk.Count / 2;
                while (midpoint < chunk.Count &&
                       _keys.Compare(chunk[midpoint - 1].Key, chunk[midpoint].Key) == 0)
                {
                    midpoint++;
                }

                if (midpoint == chunk.Count)
                {
                    midpoint = chunk.Count / 2;
                    while (midpoint > 0 && midpoint < chunk.Count &&
                           _keys.Compare(chunk[midpoint - 1].Key, chunk[midpoint].Key) == 0)
                    {
                        midpoint--;
                    }
                }
                if (midpoint == 0 || midpoint == chunk.Count)
                {
                    balanced.Add(chunk);
                }
                else
                {
                    balanced.Add(chunk.Take(midpoint).ToArray());
                    balanced.Add(chunk.Skip(midpoint).ToArray());
                }
            }
        }
        return balanced;
    }

    private static OrderedLeafEntry<TKey, TValue>[] MergeDistinct(
        IReadOnlyList<OrderedLeafEntry<TKey, TValue>> existing,
        IReadOnlyList<OrderedLeafEntry<TKey, TValue>> additions,
        IComparer<OrderedLeafEntry<TKey, TValue>> comparer)
    {
        var merged = new OrderedLeafEntry<TKey, TValue>[existing.Count + additions.Count];
        int left = 0, right = 0, written = 0;
        while (left < existing.Count || right < additions.Count)
        {
            if (right == additions.Count)
            {
                merged[written++] = existing[left++];
            }
            else if (left == existing.Count)
            {
                merged[written++] = additions[right++];
            }
            else
            {
                int comparison = comparer.Compare(existing[left], additions[right]);
                if (comparison <= 0)
                {
                    merged[written++] = existing[left++];
                    if (comparison == 0)
                    {
                        right++;
                    }
                }
                else
                {
                    merged[written++] = additions[right++];
                }
            }
        }
        if (written != merged.Length)
        {
            Array.Resize(ref merged, written);
        }

        return merged;
    }
}
