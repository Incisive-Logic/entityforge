using EntityForge.Db.Storage;

namespace EntityForge.Db.Storage.Pages;

/// <summary>Owns the durable directory and global allocator for one physical page file.</summary>
public sealed class PhysicalPageSpace
{
    private const int VacuumMinimumFreePagePercent = 20;
    private const int PercentageScale = 100;
    private readonly IResizablePhysicalPageStore _pages;
    private readonly PhysicalPageSpaceDirectoryCodec _codec = new();
    private readonly PhysicalPageSpaceAllocator _allocator;
    private readonly Dictionary<Guid, StagedState> _staged = [];
    private readonly object _gate = new();
    private long _compactedOwnedPages;
    private IReadOnlyList<PhysicalPageId> _directoryBranchPages;
    private IReadOnlyList<PhysicalPageId> _standbyDirectoryBranchPages;

    private PhysicalPageSpace(IResizablePhysicalPageStore pages, PhysicalPageSpaceDirectory directory,
        IReadOnlyCollection<PhysicalPageId> directoryBranchPages,
        IReadOnlyCollection<PhysicalPageId> standbyDirectoryBranchPages, long physicalPageCount)
    {
        _pages = pages;
        _allocator = new(directory, directoryBranchPages.Concat(standbyDirectoryBranchPages).ToArray(),
            physicalPageCount);
        _directoryBranchPages = directoryBranchPages.ToArray();
        _standbyDirectoryBranchPages = standbyDirectoryBranchPages.ToArray();
    }

    /// <summary>Opens the page-space authority, or creates an empty authority for a new file.</summary>
    public static async ValueTask<PhysicalPageSpace> OpenAsync(IResizablePhysicalPageStore pages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        try
        {
            long pageCount = await pages.GetPageCountAsync(cancellationToken).ConfigureAwait(false);
            if (pageCount == 0)
            {
                return new(pages, new PhysicalPageSpaceDirectory(0, []), [], [], pageCount);
            }

            var codec = new PhysicalPageSpaceDirectoryCodec();
            PhysicalPageSpaceDirectoryRoot root = codec.DecodeRoot((await pages.ReadAsync(
                new(PhysicalPageSpaceDirectory.DirectoryRootPageId), cancellationToken).ConfigureAwait(false)).Span);
            var branches = new List<(PhysicalPageId, ReadOnlyMemory<byte>)>();
            foreach (PhysicalPageId branch in root.BranchPages)
            {
                branches.Add((branch, await pages.ReadAsync(branch, cancellationToken).ConfigureAwait(false)));
            }

            IReadOnlyList<PhysicalAccessPathDirectoryEntry> entries = codec.DecodeBranches(branches).Concat(root.InlineEntries).ToArray();
            var directory = new PhysicalPageSpaceDirectory(root.Generation, entries);
            long requiredPageCount = RequiredPageCount(directory, root.BranchPages,
                root.StandbyBranchPages);
            if (requiredPageCount < pageCount)
            {
                await pages.TruncateAsync(requiredPageCount, cancellationToken).ConfigureAwait(false);
                await pages.FlushAsync(cancellationToken).ConfigureAwait(false);
                pageCount = requiredPageCount;
            }
            return new(pages, directory, root.BranchPages, root.StandbyBranchPages, pageCount);
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
            throw new PhysicalPageSpaceException("The physical page space could not be opened.", exception);
        }
    }

    /// <summary>Whether enough unused space warrants fenced checkpoint reclamation.</summary>
    public bool RequiresCheckpointMaintenance => _allocator.HasReclamationDebt(_pages.PageSize);

    /// <summary>Detects retained-data growth separately from free holes left by physical rewrites.
    /// Call after reclamation-only maintenance has released retired generations.</summary>
    public bool HasRetainedGrowthDebt
    {
        get
        {
            const long MinimumGrowthBytes = 64L * 1024 * 1024;
            long owned = _allocator.CaptureDirectory().Entries.Sum(e => e.Extents.Sum(x => x.PageCount));
            long growth = Math.Max(0, owned - _compactedOwnedPages);
            return growth >= (MinimumGrowthBytes + _pages.PageSize - 1) / _pages.PageSize &&
                growth * PercentageScale >= owned * VacuumMinimumFreePagePercent;
        }
    }

    /// <summary>Records the owned-space baseline after successful retained-data compaction.</summary>
    public void RecordCompactedOwnership() => _compactedOwnedPages =
        _allocator.CaptureDirectory().Entries.Sum(e => e.Extents.Sum(x => x.PageCount));

    /// <summary>Authorizes free-page reuse only after a durable checkpoint under writer exclusion.
    /// Private reservations remain excluded. If present, the caller must guarantee that
    /// private work is paused throughout checkpoint capture and authorization.</summary>
    public void AuthorizeCheckpointPageReuse(bool privateWorkspacesPaused = false) =>
        _allocator.AuthorizeCheckpointPageReuse(privateWorkspacesPaused);

    /// <summary>Captures the currently published immutable directory.</summary>
    public PhysicalPageSpaceDirectory CaptureDirectory() => _allocator.CaptureDirectory();

    /// <summary>
    /// Performs an explicit full scan of owned physical pages and reports exact record occupancy.
    /// </summary>
    public ValueTask<PhysicalPageOccupancySnapshot> AnalyzeOccupancyAsync(
        CancellationToken cancellationToken = default) => PhysicalPageOccupancyAnalyzer.AnalyzeAsync(
            _pages, _allocator.CaptureDirectory(),
            _directoryBranchPages.Concat(_standbyDirectoryBranchPages).ToArray(), cancellationToken);

    /// <summary>Returns whether interior free space is large enough to justify a full physical rewrite.</summary>
    public async ValueTask<bool> ShouldVacuumAsync(CancellationToken cancellationToken = default)
    {
        long pageCount = await _pages.GetPageCountAsync(cancellationToken).ConfigureAwait(false);
        if (pageCount <= PhysicalPageSpaceDirectory.FirstAccessMethodPageId)
        {
            return false;
        }

        PhysicalPageSpaceDirectory directory = _allocator.CaptureDirectory();
        long owned = directory.Entries.SelectMany(entry => entry.Extents).Sum(extent => extent.PageCount) +
                     _directoryBranchPages.Count + _standbyDirectoryBranchPages.Count +
                     PhysicalPageSpaceDirectory.FirstAccessMethodPageId;
        long free = Math.Max(0, pageCount - owned);
        return free * PercentageScale >= pageCount * VacuumMinimumFreePagePercent;
    }

    /// <summary>Begins the sole page-space-mutating transaction.</summary>
    public PhysicalPageSpaceTransaction BeginTransaction(Guid transactionId) =>
        _allocator.BeginTransaction(transactionId);

    /// <summary>Begins a transaction that fills the lowest reusable pages during a physical vacuum.</summary>
    public PhysicalPageSpaceTransaction BeginVacuumTransaction(Guid transactionId) =>
        _allocator.BeginTransaction(transactionId, preferLowestFreePage: true);

    /// <summary>Creates an isolated workspace without occupying the live directory transaction.</summary>
    public PhysicalPageWorkspace BeginReplacementWorkspace(Guid workspaceId) =>
        _allocator.BeginWorkspace(workspaceId);

    /// <summary>Stages the directory root and branches after all access paths have completed.</summary>
    public async ValueTask<PhysicalPageSpacePersistenceStage> PrepareAsync(
        PhysicalPageSpaceTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();
        PhysicalPageSpaceDirectory directory = transaction.PreviewDirectory();
        PhysicalPageSpaceDirectoryImage image = _codec.Encode(directory, transaction,
            _directoryBranchPages, _standbyDirectoryBranchPages, _allocator.CaptureDirectory(), inlineEntries: true);
        var directoryEntries = directory.Entries;
        if (image.BranchPageIds.Any(branch => directoryEntries.Any(entry =>
                entry.Extents.Any(extent => extent.Contains(branch)))))
        {
            throw new PhysicalPageSpaceException(
                "A directory branch page overlaps access-path ownership.");
        }

        PhysicalPageSpaceStage allocation = transaction.Prepare();
        var afterImages = image.Branches.ToDictionary(item => item.Key, item => item.Value);
        afterImages.Add(new(PhysicalPageSpaceDirectory.DirectoryRootPageId), image.Root);
        // Workspace pages already contain their replacement images. Keep their complete
        // redo evidence, but publish only the new directory and ordinary staged writes.
        // Replaying an older WAL prefix can still overwrite reused pages during recovery.
        var publicationImages = afterImages;
        IReadOnlyList<PhysicalPageId> reusedPages = transaction.AdoptedReusedPages;
        if (reusedPages.Count != 0)
        {
            afterImages = afterImages.ToDictionary(item => item.Key, item => item.Value);
        }

        foreach (PhysicalPageId reusedPage in reusedPages)
        {
            afterImages.Add(reusedPage,
                await _pages.ReadAsync(reusedPage, cancellationToken).ConfigureAwait(false));
        }

        IReadOnlyList<DatabasePhysicalPageMutation> mutations = CreateMutations(afterImages);
        lock (_gate)
        {
            _staged.Add(transaction.TransactionId,
            new(directory, publicationImages, image.BranchPageIds, image.StandbyBranchPageIds));
        }

        return new PhysicalPageSpacePersistenceStage(
            transaction.TransactionId, allocation.Directory, mutations);
    }

    /// <summary>Writes a prepared directory image to its page file.</summary>
    public async ValueTask PersistAsync(Guid transactionId,
        IReadOnlyList<DatabasePhysicalPageMutation> accessPathMutations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessPathMutations);
        StagedState staged;
        lock (_gate)
        {
            staged = _staged.GetValueOrDefault(transactionId) ??
            throw new PhysicalPageSpaceException("The page-space transaction is not staged.");
        }

        var images = staged.PublicationImages.ToDictionary(item => item.Key, item => item.Value);
        foreach (DatabasePhysicalPageMutation mutation in accessPathMutations)
        {
            if (mutation.Offset != 0 || mutation.Bytes.Length != _pages.PageSize)
            {
                throw new PhysicalPageSpaceException("An access-path publication must contain complete pages.");
            }

            images.Add(new(mutation.PageId), mutation.Bytes);
        }
        long required = Math.Max(
            images.Keys.Max(page => page.Value + 1),
            RequiredPageCount(staged.Directory, staged.DirectoryBranchPages,
                staged.StandbyDirectoryBranchPages));
        if (await _pages.GetPageCountAsync(cancellationToken).ConfigureAwait(false) < required)
        {
            await _pages.EnsureCapacityAsync(required, cancellationToken).ConfigureAwait(false);
        }

        foreach ((PhysicalPageId page, ReadOnlyMemory<byte> image) in images.OrderBy(item => item.Key.Value))
        {
            await _pages.WriteAsync(page, image, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Removes only the unowned tail after its directory publication is durable.</summary>
    public async ValueTask TrimUnownedTailAsync(CancellationToken cancellationToken = default)
    {
        using PhysicalPageSpaceTrimLease trim = _allocator.BeginTrim();
        PhysicalPageSpaceDirectory directory = trim.Directory;
        IReadOnlyCollection<PhysicalPageId> branchPages;
        IReadOnlyCollection<PhysicalPageId> standbyPages;
        lock (_gate)
        {
            branchPages = _directoryBranchPages.ToArray();
            standbyPages = _standbyDirectoryBranchPages.ToArray();
        }
        long requiredPageCount = RequiredPageCount(directory, branchPages, standbyPages);
        if (requiredPageCount >= await _pages.GetPageCountAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await _pages.TruncateAsync(requiredPageCount, cancellationToken).ConfigureAwait(false);
        await _pages.FlushAsync(cancellationToken).ConfigureAwait(false);
        trim.Complete(requiredPageCount, branchPages.Concat(standbyPages).ToArray());
    }

    /// <summary>Publishes a persisted directory generation.</summary>
    public void Publish(Guid transactionId)
    {
        StagedState staged;
        lock (_gate)
        {
            staged = _staged.GetValueOrDefault(transactionId) ??
            throw new PhysicalPageSpaceException("The page-space transaction is not staged.");
        }

        _allocator.Publish(transactionId, () =>
        {
            lock (_gate)
            {
                _directoryBranchPages = staged.DirectoryBranchPages;
                _standbyDirectoryBranchPages = staged.StandbyDirectoryBranchPages;
                _staged.Remove(transactionId);
            }
        });
    }

    /// <summary>Abandons an active or staged page-space transaction.</summary>
    /// <param name="transactionId">The active transaction.</param>
    /// <param name="reclaimUnsubmittedTail">True only when neither page writes nor durability
    /// submission have begun for this preview.</param>
    public void Abandon(Guid transactionId, bool reclaimUnsubmittedTail = false)
    {
        _allocator.Abandon(transactionId, reclaimUnsubmittedTail);
        lock (_gate)
        {
            _staged.Remove(transactionId);
        }
    }

    private static IReadOnlyList<DatabasePhysicalPageMutation> CreateMutations(
        IReadOnlyDictionary<PhysicalPageId, ReadOnlyMemory<byte>> images)
    {
        var result = new List<DatabasePhysicalPageMutation>(images.Count);
        foreach ((PhysicalPageId pageId, ReadOnlyMemory<byte> after) in images)
        {
            result.Add(DatabasePhysicalPageMutation.CreatePage(pageId.Value, after));
        }

        return result;
    }

    private static long RequiredPageCount(PhysicalPageSpaceDirectory directory,
        IReadOnlyCollection<PhysicalPageId> directoryBranchPages,
        IReadOnlyCollection<PhysicalPageId> standbyDirectoryBranchPages) =>
        Math.Max(PhysicalPageSpaceDirectory.FirstAccessMethodPageId,
            directory.Entries.SelectMany(entry => entry.Extents).Select(extent => extent.EndExclusive)
                .Concat(directoryBranchPages.Select(page => checked(page.Value + 1)))
                .Concat(standbyDirectoryBranchPages.Select(page => checked(page.Value + 1)))
                .DefaultIfEmpty(PhysicalPageSpaceDirectory.FirstAccessMethodPageId).Max());

    private sealed record StagedState(
        PhysicalPageSpaceDirectory Directory,
        IReadOnlyDictionary<PhysicalPageId, ReadOnlyMemory<byte>> PublicationImages,
        IReadOnlyList<PhysicalPageId> DirectoryBranchPages,
        IReadOnlyList<PhysicalPageId> StandbyDirectoryBranchPages);
}

/// <summary>Describes one prepared page-space directory publication.</summary>
/// <param name="TransactionId">The transaction owning the directory persistence stage.</param>
/// <param name="Directory">The prospective shared page directory.</param>
/// <param name="PhysicalMutations">The page mutations required to persist the directory.</param>
public sealed record PhysicalPageSpacePersistenceStage(Guid TransactionId,
    PhysicalPageSpaceDirectory Directory,
    IReadOnlyList<DatabasePhysicalPageMutation> PhysicalMutations);
