namespace EntityForge.Db.Storage.Pages;

/// <summary>Allocates collision-free pages for either a live transaction or isolated workspace.</summary>
public interface IPhysicalPageAllocator
{
    /// <summary>Allocates one page.</summary>
    PhysicalPageId AllocatePage();
}

/// <summary>Describes pages allocated to one access path in a staged transaction.</summary>
/// <param name="AccessPathId">The access path for which pages are reserved.</param>
/// <param name="Extents">The physical page extents reserved for this access path.</param>
/// <param name="ProposedRootPage">The proposed root page after publication.</param>
/// <param name="ProposedGeneration">The access-path generation to publish with the reservation.</param>
/// <param name="ReleasedPages">Pages released by this reservation's transition.</param>
public sealed record PhysicalPageReservation(PhysicalAccessPathId AccessPathId,
    IReadOnlyList<PhysicalPageExtent> Extents, PhysicalPageId ProposedRootPage,
    long ProposedGeneration, IReadOnlyList<PhysicalPageId> ReleasedPages);

/// <summary>Contains one unpublished page-space directory generation and its allocations.</summary>
/// <param name="TransactionId">The transaction owning the private page-space stage.</param>
/// <param name="Directory">The prospective shared page directory.</param>
/// <param name="Reservations">The access-path page reservations grouped in this stage.</param>
/// <param name="ReleasedInfrastructurePages">Infrastructure pages made reclaimable by the staged transition.</param>
public sealed record PhysicalPageSpaceStage(Guid TransactionId,
    PhysicalPageSpaceDirectory Directory, IReadOnlyList<PhysicalPageReservation> Reservations,
    IReadOnlyList<PhysicalPageId> ReleasedInfrastructurePages);

/// <summary>Owns global collision-free allocation and immutable directory publication.</summary>
public sealed class PhysicalPageSpaceAllocator
{
    private readonly object _gate = new();
    private PhysicalPageSpaceDirectory _published;
    private PhysicalPageSpaceTransaction? _active;
    private PhysicalPageSpaceStage? _staged;
    private long _nextPageId;
    // Disjoint from quarantine: only a durable, fenced checkpoint can authorize
    // these pages for ordinary transactions. New releases stay quarantined.
    private PhysicalFreePageRanges _checkpointFreePages = new();
    private PhysicalFreePageRanges _freePages;
    private readonly List<long> _activeAllocations = [];
    private readonly Dictionary<Guid, WorkspaceAllocationState> _workspaces = [];
    private bool _trimming;

    /// <summary>Initializes an allocator from one validated published directory.</summary>
    public PhysicalPageSpaceAllocator(PhysicalPageSpaceDirectory published,
        IReadOnlyCollection<PhysicalPageId> directoryBranchPages, long physicalPageCount)
    {
        _published = published ?? throw new ArgumentNullException(nameof(published));
        ArgumentNullException.ThrowIfNull(directoryBranchPages);
        if (physicalPageCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalPageCount));
        }

        _nextPageId = Math.Max(PhysicalPageSpaceDirectory.FirstAccessMethodPageId,
            published.Entries.SelectMany(entry => entry.Extents).Select(extent => extent.EndExclusive)
                .Concat(directoryBranchPages.Select(page => checked(page.Value + 1)))
                .Append(physicalPageCount)
                .DefaultIfEmpty(PhysicalPageSpaceDirectory.FirstAccessMethodPageId).Max());
        _freePages = PhysicalFreePageRanges.Reconstruct(published, directoryBranchPages, physicalPageCount);
    }

    /// <summary>Reports published free space without scanning physical page contents.</summary>
    public bool HasReclamationDebt(int pageSize)
    {
        const long MinimumReclaimableBytes = 64L * 1024 * 1024;
        const int MinimumFreePercent = 20, PercentageScale = 100;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        lock (_gate)
        {
            long free = _freePages.PageCount;
            return free >= MinimumReclaimableBytes / pageSize &&
                free * PercentageScale >= _nextPageId * MinimumFreePercent;
        }
    }

    /// <summary>Authorizes currently free pages after a durable checkpoint under writer exclusion.</summary>
    public void AuthorizeCheckpointPageReuse(bool privateWorkspacesPaused = false)
    {
        lock (_gate)
        {
            if (_trimming || _active is not null || _staged is not null || !privateWorkspacesPaused && _workspaces.Count != 0)
            {
                throw new PhysicalPageSpaceException("Checkpoint page reuse requires exclusive published ownership and paused private work.");
            }

            _checkpointFreePages.Include(_freePages);
            _freePages = new();
        }
    }

    private bool TryTakeFreePage(long? preferredPageId, out long page)
    {
        var first = _checkpointFreePages.FirstPage;
        var quarantined = _freePages.FirstPage;
        return first is not null && (quarantined is null || first < quarantined)
            ? _checkpointFreePages.TryTake(preferredPageId, true, out page)
            : _freePages.TryTake(preferredPageId, true, out page);
    }

    /// <summary>Captures the current immutable published directory.</summary>
    public PhysicalPageSpaceDirectory CaptureDirectory()
    {
        lock (_gate)
        {
            return _published;
        }
    }

    /// <summary>Begins the sole directory-mutating physical transaction.</summary>
    public PhysicalPageSpaceTransaction BeginTransaction(Guid transactionId,
        bool preferLowestFreePage = false)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A transaction identity is required.", nameof(transactionId));
        }

        lock (_gate)
        {
            if (_trimming)
            {
                throw new PhysicalPageSpaceException("A page-space tail trim is in progress.");
            }

            if (_active is not null)
            {
                throw new PhysicalPageSpaceException("Only one page-space directory transaction may be active.");
            }

            _active = new(this, transactionId, _published, preferLowestFreePage);
            return _active;
        }
    }

    /// <summary>Creates an isolated tail-allocated workspace that does not occupy the live transaction.</summary>
    public PhysicalPageWorkspace BeginWorkspace(Guid workspaceId)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("A workspace identity is required.", nameof(workspaceId));
        }

        lock (_gate)
        {
            if (_trimming)
            {
                throw new PhysicalPageSpaceException("A page-space tail trim is in progress.");
            }

            if (_workspaces.ContainsKey(workspaceId))
            {
                throw new PhysicalPageSpaceException("The physical workspace already exists.");
            }

            var workspace = new PhysicalPageWorkspace(this, workspaceId);
            _workspaces.Add(workspaceId, new(workspace, [], [], [], []));
            return workspace;
        }
    }

    internal PhysicalPageId AllocateWorkspace(PhysicalPageWorkspace suppliedWorkspace)
    {
        lock (_gate)
        {
            if (!_workspaces.TryGetValue(suppliedWorkspace.WorkspaceId,
                    out WorkspaceAllocationState? workspace) ||
                !ReferenceEquals(workspace.Owner, suppliedWorkspace))
            {
                throw new PhysicalPageSpaceException("The physical workspace is not active.");
            }

            long page;
            bool reused = TryTakeFreePage(null, out page);
            if (!reused)
            {
                page = _nextPageId++;
            }

            if (_workspaces.Values.Any(reserved => reserved.OwnedPages.Contains(page)) ||
                _activeAllocations.Contains(page))
            {
                throw new PhysicalPageSpaceException(
                    $"Physical page {page} is already reserved by another unpublished allocation.");
            }

            workspace.Pages.Add(page);
            workspace.OwnedPages.Add(page);
            if (reused)
            {
                workspace.ReusedPages.Add(page);
            }

            return new(page);
        }
    }

    internal PhysicalWorkspaceAdoption AdoptWorkspace(PhysicalPageSpaceTransaction transaction,
        PhysicalPageWorkspace suppliedWorkspace)
    {
        lock (_gate)
        {
            RequireActive(transaction);
            Guid workspaceId = suppliedWorkspace.WorkspaceId;
            if (!_workspaces.TryGetValue(workspaceId, out WorkspaceAllocationState? workspace) ||
                !ReferenceEquals(workspace.Owner, suppliedWorkspace))
            {
                throw new PhysicalPageSpaceException("The physical workspace is not available for adoption.");
            }

            _workspaces.Remove(workspaceId);
            _activeAllocations.AddRange(workspace.Pages);
            suppliedWorkspace.MarkAdopted();
            return new(workspace.Pages.Select(page => new PhysicalPageId(page)).ToArray(),
                workspace.ReusedPages.Where(page => !workspace.JournaledPages.Contains(page))
                    .Select(page => new PhysicalPageId(page)).ToArray());
        }
    }

    internal void AbandonWorkspace(PhysicalPageWorkspace suppliedWorkspace)
    {
        lock (_gate)
        {
            if (!_workspaces.TryGetValue(suppliedWorkspace.WorkspaceId,
                    out WorkspaceAllocationState? workspace) ||
                !ReferenceEquals(workspace.Owner, suppliedWorkspace))
            {
                return;
            }

            _workspaces.Remove(suppliedWorkspace.WorkspaceId);
            foreach (long page in workspace.Pages)
            {
                _freePages.Add(page);
            }
        }
    }

    internal void CompleteWorkspace(PhysicalPageWorkspace suppliedWorkspace, PhysicalPageId rootPage)
    {
        lock (_gate)
        {
            if (!_workspaces.TryGetValue(suppliedWorkspace.WorkspaceId,
                    out WorkspaceAllocationState? workspace) ||
                !ReferenceEquals(workspace.Owner, suppliedWorkspace) ||
                !workspace.OwnedPages.Contains(rootPage.Value))
            {
                throw new PhysicalPageSpaceException("The workspace root was not allocated by this workspace.");
            }
        }
    }

    /// <summary>Publishes the prepared directory for the supplied transaction.</summary>
    internal void Publish(Guid transactionId, Action publishInfrastructure)
    {
        ArgumentNullException.ThrowIfNull(publishInfrastructure);
        lock (_gate)
        {
            PhysicalPageSpaceStage staged = RequiredStage(transactionId);
            _published = staged.Directory;
            publishInfrastructure();
            List<long> retired = staged.Reservations.SelectMany(item => item.ReleasedPages)
                .Concat(staged.ReleasedInfrastructurePages).Select(page => page.Value).Distinct().ToList();
            // Access methods release pages only after their generation-level snapshot references
            // have drained. Once the replacement directory is published those pages are therefore
            // safe for a later vacuum/replacement workspace to reuse.
            foreach (long page in retired)
            {
                _freePages.Add(page);
            }

            _activeAllocations.Clear();
            _staged = null;
            _active = null;
        }
    }

    /// <summary>Abandons active or prepared state belonging to the supplied transaction.</summary>
    /// <param name="transactionId">The active transaction.</param>
    /// <param name="reclaimUnsubmittedTail">True only for a preview whose page mutations have
    /// never been written or submitted to a durability log. Ordinary failures retain quarantine.</param>
    public void Abandon(Guid transactionId, bool reclaimUnsubmittedTail = false)
    {
        lock (_gate)
        {
            if (_active is null || _active.TransactionId != transactionId)
            {
                throw new PhysicalPageSpaceException(
                    $"Transaction '{transactionId}' has no active page-space directory state.");
            }
            // Admission can prepare and abandon a complete physical plan before
            // the durable commit. Return its contiguous tail reservations directly;
            // otherwise every dry preparation permanently advances the file tail.
            // Only this transaction's pages qualify: a concurrent workspace or
            // published page stops the rewind, even if older free holes exist.
            var abandoned = _activeAllocations.ToHashSet();
            if (reclaimUnsubmittedTail)
            {
                while (abandoned.Remove(_nextPageId - 1))
                {
                    _nextPageId--;
                }
            }

            foreach (long pageId in abandoned)
            {
                _freePages.Add(pageId);
            }

            _activeAllocations.Clear();
            _staged = null;
            _active = null;
        }
    }

    internal PhysicalPageId Allocate(PhysicalPageSpaceTransaction transaction, long? preferredPageId)
    {
        lock (_gate)
        {
            RequireActive(transaction);
            long pageId;
            bool reused = transaction.PreferLowestFreePage
                ? TryTakeFreePage(preferredPageId, out pageId)
                : _checkpointFreePages.TryTake(preferredPageId, true, out pageId);
            if (!reused)
            {
                pageId = _nextPageId++;
            }

            _activeAllocations.Add(pageId);
            return new(pageId);
        }
    }

    internal PhysicalPageId AllocateInfrastructure(PhysicalPageSpaceTransaction transaction)
    {
        lock (_gate)
        {
            RequireActive(transaction);
            long pageId = _nextPageId++;
            _activeAllocations.Add(pageId);
            return new(pageId);
        }
    }

    internal PhysicalPageSpaceStage Stage(PhysicalPageSpaceTransaction transaction,
        PhysicalPageSpaceDirectory directory, IReadOnlyList<PhysicalPageReservation> reservations)
    {
        lock (_gate)
        {
            RequireActive(transaction);
            if (_staged is not null)
            {
                throw new PhysicalPageSpaceException("The page-space transaction is already prepared.");
            }

            _staged = new(transaction.TransactionId, directory, reservations,
                transaction.ReleasedInfrastructurePages);
            return _staged;
        }
    }

    private void RequireActive(PhysicalPageSpaceTransaction transaction)
    {
        if (!ReferenceEquals(_active, transaction))
        {
            throw new PhysicalPageSpaceException("The page-space transaction is not active.");
        }
    }

    private PhysicalPageSpaceStage RequiredStage(Guid transactionId)
    {
        if (_staged is null || _staged.TransactionId != transactionId)
        {
            throw new PhysicalPageSpaceException(
                $"Transaction '{transactionId}' has no prepared page-space directory.");
        }

        return _staged;
    }

    internal PhysicalPageSpaceTrimLease BeginTrim()
    {
        lock (_gate)
        {
            if (_trimming || _active is not null || _staged is not null || _workspaces.Count != 0)
            {
                throw new PhysicalPageSpaceException(
                    "The unowned tail cannot be trimmed while unpublished physical ownership exists.");
            }

            _trimming = true;
            return new(this, _published);
        }
    }

    internal void EndTrim()
    {
        lock (_gate)
        {
            _trimming = false;
        }
    }

    internal void CompleteTrim(long pageCount, IReadOnlyCollection<PhysicalPageId> infrastructure)
    {
        lock (_gate)
        {
            if (!_trimming)
            {
                throw new PhysicalPageSpaceException("No exclusive tail trim is active.");
            }

            _freePages = PhysicalFreePageRanges.Reconstruct(_published, infrastructure, pageCount);
            _checkpointFreePages = new(); // A new checkpoint must cover the trimmed directory.
            _nextPageId = Math.Max(PhysicalPageSpaceDirectory.FirstAccessMethodPageId, pageCount);
        }
    }

    internal int CaptureWorkspaceAllocationMark(PhysicalPageWorkspace suppliedWorkspace)
    {
        lock (_gate)
        {
            return RequiredWorkspace(suppliedWorkspace).Pages.Count;
        }
    }

    internal void RollbackWorkspace(PhysicalPageWorkspace suppliedWorkspace, int allocationMark)
    {
        lock (_gate)
        {
            WorkspaceAllocationState workspace = RequiredWorkspace(suppliedWorkspace);
            if (allocationMark < 0 || allocationMark > workspace.Pages.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(allocationMark));
            }

            for (int index = workspace.Pages.Count - 1; index >= allocationMark; index--)
            {
                long page = workspace.Pages[index];
                workspace.Pages.RemoveAt(index);
                workspace.OwnedPages.Remove(page);
                workspace.ReusedPages.Remove(page);
                workspace.JournaledPages.Remove(page);
                _freePages.Add(page);
            }
        }
    }

    internal void ReleaseWorkspacePages(PhysicalPageWorkspace suppliedWorkspace,
        IReadOnlyCollection<PhysicalPageId> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        lock (_gate)
        {
            WorkspaceAllocationState workspace = RequiredWorkspace(suppliedWorkspace);
            foreach (long page in pages.Select(item => item.Value).Distinct())
            {
                if (!workspace.Pages.Remove(page))
                {
                    throw new PhysicalPageSpaceException("Only a page owned by this workspace can be released.");
                }

                workspace.OwnedPages.Remove(page);
                workspace.ReusedPages.Remove(page);
                workspace.JournaledPages.Remove(page);
                _freePages.Add(page);
            }
        }
    }

    private WorkspaceAllocationState RequiredWorkspace(PhysicalPageWorkspace suppliedWorkspace)
    {
        if (!_workspaces.TryGetValue(suppliedWorkspace.WorkspaceId,
                out WorkspaceAllocationState? workspace) ||
            !ReferenceEquals(workspace.Owner, suppliedWorkspace))
        {
            throw new PhysicalPageSpaceException("The physical workspace is not active.");
        }

        return workspace;
    }

    internal void ValidateWorkspacePages(PhysicalPageWorkspace owner, IReadOnlyList<PhysicalPageId> pages)
    {
        lock (_gate)
        {
            var workspace = RequiredWorkspace(owner);
            if (pages.Any(page => !workspace.OwnedPages.Contains(page.Value)))
            {
                throw new PhysicalPageSpaceException("A private write must name only its workspace's pages.");
            }
            // A failed rewrite must never inherit an earlier journal acknowledgement.
            foreach (var page in pages)
            {
                workspace.JournaledPages.Remove(page.Value);
            }
        }
    }

    internal void AcknowledgeWorkspacePages(PhysicalPageWorkspace owner, IReadOnlyList<PhysicalPageId> pages)
    {
        lock (_gate)
        {
            var workspace = RequiredWorkspace(owner);
            if (pages.Any(page => !workspace.OwnedPages.Contains(page.Value)))
            {
                throw new PhysicalPageSpaceException("Journalled workspace pages changed ownership.");
            }

            foreach (var page in pages)
            {
                workspace.JournaledPages.Add(page.Value);
            }
        }
    }

    private sealed record WorkspaceAllocationState(PhysicalPageWorkspace Owner,
        List<long> Pages, HashSet<long> OwnedPages, HashSet<long> ReusedPages, HashSet<long> JournaledPages);

}

internal sealed class PhysicalPageSpaceTrimLease(
    PhysicalPageSpaceAllocator allocator, PhysicalPageSpaceDirectory directory) : IDisposable
{
    private int _disposed;

    public PhysicalPageSpaceDirectory Directory { get; } = directory;

    public void Complete(long pageCount, IReadOnlyCollection<PhysicalPageId> infrastructure) =>
        allocator.CompleteTrim(pageCount, infrastructure);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            allocator.EndTrim();
        }
    }
}

internal sealed record PhysicalWorkspaceAdoption(IReadOnlyList<PhysicalPageId> Pages,
    IReadOnlyList<PhysicalPageId> ReusedPages);

/// <summary>Coordinates exact page allocation for all access methods in one physical transaction.</summary>
public sealed class PhysicalPageSpaceTransaction
{
    private readonly PhysicalPageSpaceAllocator _allocator;
    private readonly PhysicalPageSpaceDirectory _baseDirectory;
    private readonly Dictionary<PhysicalAccessPathId, PhysicalAccessPathAllocationScope> _scopes = [];
    private readonly HashSet<PhysicalPageId> _releasedInfrastructurePages = [];
    private readonly HashSet<PhysicalPageId> _adoptedReusedPages = [];
    private bool _prepared;
    private (PhysicalPageSpaceDirectory Directory, PhysicalPageReservation[] Reservations)? _preview;

    internal PhysicalPageSpaceTransaction(PhysicalPageSpaceAllocator allocator, Guid transactionId,
        PhysicalPageSpaceDirectory baseDirectory, bool preferLowestFreePage)
    {
        _allocator = allocator;
        TransactionId = transactionId;
        _baseDirectory = baseDirectory;
        PreferLowestFreePage = preferLowestFreePage;
    }

    /// <summary>Gets the physical transaction identifier.</summary>
    public Guid TransactionId
    {
        get;
    }

    internal bool PreferLowestFreePage
    {
        get;
    }

    /// <summary>Opens one access method's allocation scope at an expected published generation.</summary>
    public PhysicalAccessPathAllocationScope OpenAccessPath(PhysicalAccessPathId accessPathId,
        PhysicalAccessMethodKind method, long expectedGeneration)
    {
        ObjectDisposedException.ThrowIf(_prepared, this);
        ArgumentNullException.ThrowIfNull(method);
        if (expectedGeneration < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        }

        if (_scopes.ContainsKey(accessPathId))
        {
            throw new PhysicalPageSpaceException("The transaction already opened this physical access path.");
        }

        PhysicalAccessPathDirectoryEntry? current = _baseDirectory.Find(accessPathId);
        ValidateExpected(accessPathId, method, expectedGeneration, current);
        var scope = new PhysicalAccessPathAllocationScope(this, accessPathId, method, current);
        _scopes.Add(accessPathId, scope);
        _preview = null;
        return scope;
    }

    /// <summary>Opens an access path and transfers a completed isolated workspace into it.</summary>
    public PhysicalAccessPathAllocationScope OpenReplacementAccessPath(
        PhysicalAccessPathId accessPathId, PhysicalAccessMethodKind method, long expectedGeneration,
        PhysicalPageWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        PhysicalAccessPathAllocationScope scope = OpenAccessPath(accessPathId, method, expectedGeneration);
        scope.Adopt(workspace);
        return scope;
    }

    /// <summary>Removes an optional access-path scope that performed no allocation or release work.</summary>
    public void CancelAccessPath(PhysicalAccessPathAllocationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ObjectDisposedException.ThrowIf(_prepared, this);
        if (!_scopes.TryGetValue(scope.AccessPathId, out PhysicalAccessPathAllocationScope? current) ||
            !ReferenceEquals(current, scope))
        {
            throw new PhysicalPageSpaceException("The access-path allocation scope is not active.");
        }

        if (!scope.IsUntouched)
        {
            throw new PhysicalPageSpaceException("An access-path allocation scope with staged work cannot be cancelled.");
        }

        _scopes.Remove(scope.AccessPathId);
        _preview = null;
    }

    /// <summary>Freezes all completed scopes into an immutable replacement directory.</summary>
    public PhysicalPageSpaceStage Prepare()
    {
        ObjectDisposedException.ThrowIf(_prepared, this);
        if (_scopes.Count == 0)
        {
            throw new PhysicalPageSpaceException("The page-space transaction contains no access paths.");
        }

        if (_scopes.Values.Any(scope => !scope.IsCompleted))
        {
            throw new PhysicalPageSpaceException("Every access-path allocation scope must publish a root.");
        }

        var (directory, reservations) = CaptureCompletedDirectory();
        _prepared = true;
        return _allocator.Stage(this, directory, reservations);
    }

    internal PhysicalPageSpaceDirectory PreviewDirectory() => CaptureCompletedDirectory().Directory;

    private (PhysicalPageSpaceDirectory Directory, PhysicalPageReservation[] Reservations) CaptureCompletedDirectory()
    {
        // Reservation requires completed scopes, which reject further allocation/release.
        // Infrastructure allocation changes neither these scopes nor their ownership.
        // Opening/cancelling a scope invalidates the preview, including failed attempts.
        if (_preview is { } captured)
        {
            return captured;
        }

        var directory = CreateDirectory(out var reservations);
        _preview = (directory, reservations);
        return _preview.Value;
    }

    internal PhysicalPageId AllocateDirectoryPage() => _allocator.AllocateInfrastructure(this);

    internal IReadOnlyList<PhysicalPageId> AdoptWorkspace(PhysicalPageWorkspace workspace)
    {
        PhysicalWorkspaceAdoption adoption = _allocator.AdoptWorkspace(this, workspace);
        _adoptedReusedPages.UnionWith(adoption.ReusedPages);
        return adoption.Pages;
    }

    internal IReadOnlyList<PhysicalPageId> AdoptedReusedPages =>
        _adoptedReusedPages.OrderBy(page => page.Value).ToArray();

    internal IReadOnlyList<PhysicalPageId> ReleasedInfrastructurePages =>
        _releasedInfrastructurePages.OrderBy(page => page.Value).ToArray();

    internal void ReleaseInfrastructurePages(IEnumerable<PhysicalPageId> pages)
    {
        ObjectDisposedException.ThrowIf(_prepared, this);
        foreach (PhysicalPageId page in pages)
        {
            if (page.Value < PhysicalPageSpaceDirectory.FirstAccessMethodPageId)
            {
                throw new PhysicalPageSpaceException("A reserved directory page cannot be released.");
            }

            _releasedInfrastructurePages.Add(page);
        }
    }

    private PhysicalPageSpaceDirectory CreateDirectory(out PhysicalPageReservation[] reservations)
    {
        var entries = _baseDirectory.Entries.ToDictionary(entry => entry.AccessPathId);
        reservations = _scopes.Values.Select(scope => scope.Reservation()).ToArray();
        foreach (PhysicalPageReservation reservation in reservations)
        {
            PhysicalAccessPathAllocationScope scope = _scopes[reservation.AccessPathId];
            entries[reservation.AccessPathId] = PhysicalAccessPathDirectoryEntry.Combine(reservation.AccessPathId, scope.Method,
                reservation.ProposedRootPage, reservation.ProposedGeneration,
                Remaining(scope.Current?.Extents ?? [], reservation.ReleasedPages), reservation.Extents);
        }
        return new PhysicalPageSpaceDirectory(checked(_baseDirectory.Generation + 1),
            entries.Values.ToArray());
    }

    private static IReadOnlyList<PhysicalPageExtent> Remaining(IReadOnlyList<PhysicalPageExtent> extents,
        IReadOnlyList<PhysicalPageId> released)
    {
        if (released.Count == 0)
        {
            return extents;
        }

        long[] removed = released.Select(page => page.Value).Distinct().OrderBy(page => page).ToArray();
        var result = new List<PhysicalPageExtent>();
        int removedIndex = 0;
        // Published directory extents and released pages are both ordered. Each release
        // is visited once instead of rescanning the entire release set for every extent.
        foreach (PhysicalPageExtent extent in extents)
        {
            long first = extent.FirstPage.Value;
            while (removedIndex < removed.Length && removed[removedIndex] < first)
            {
                removedIndex++;
            }

            while (removedIndex < removed.Length && removed[removedIndex] < extent.EndExclusive)
            {
                long page = removed[removedIndex++];
                if (page > first)
                {
                    result.Add(new(new(first), page - first));
                }

                first = page + 1;
            }
            if (first < extent.EndExclusive)
            {
                result.Add(new(new(first), extent.EndExclusive - first));
            }
        }
        return result;
    }

    internal PhysicalPageId Allocate(long? preferredPageId) => _allocator.Allocate(this, preferredPageId);

    private static void ValidateExpected(PhysicalAccessPathId accessPathId, PhysicalAccessMethodKind method,
        long expectedGeneration, PhysicalAccessPathDirectoryEntry? current)
    {
        if (current is null)
        {
            if (expectedGeneration != -1)
            {
                throw new PhysicalPageSpaceException(
                    $"Access path '{accessPathId.Value}' does not exist at expected generation {expectedGeneration}.");
            }

            return;
        }
        if (expectedGeneration != current.Generation)
        {
            throw new PhysicalPageSpaceException(
                $"Access path '{accessPathId.Value}' is at generation {current.Generation}, not {expectedGeneration}.");
        }

        if (method != current.Method)
        {
            throw new PhysicalPageSpaceException(
                $"Access path '{accessPathId.Value}' cannot change physical method in place.");
        }
    }
}

/// <summary>Allocates exact pages and selects a root for one access method.</summary>
public sealed class PhysicalAccessPathAllocationScope : IPhysicalPageAllocator
{
    private readonly PhysicalPageSpaceTransaction _transaction;
    private readonly List<PhysicalPageId> _pages = [];
    private readonly HashSet<PhysicalPageId> _released = [];
    private PhysicalPageId? _root;

    internal PhysicalAccessPathAllocationScope(PhysicalPageSpaceTransaction transaction,
        PhysicalAccessPathId accessPathId, PhysicalAccessMethodKind method,
        PhysicalAccessPathDirectoryEntry? current)
    {
        _transaction = transaction;
        AccessPathId = accessPathId;
        Method = method;
        Current = current;
    }

    /// <summary>Gets the access-path identifier.</summary>
    public PhysicalAccessPathId AccessPathId
    {
        get;
    }

    /// <summary>Gets the owning physical transaction identifier.</summary>
    public Guid TransactionId => _transaction.TransactionId;

    /// <summary>Gets the selected physical access method.</summary>
    public PhysicalAccessMethodKind Method
    {
        get;
    }

    /// <summary>Gets the current published entry when updating an existing path.</summary>
    public PhysicalAccessPathDirectoryEntry? Current
    {
        get;
    }

    /// <summary>Gets the proposed generation.</summary>
    public long ProposedGeneration => Current is null ? 1 : checked(Current.Generation + 1);

    internal bool IsCompleted => _root is not null;

    internal bool IsUntouched => _pages.Count == 0 && _released.Count == 0 && _root is null;

    /// <summary>Allocates one globally unique page to this access path.</summary>
    public PhysicalPageId AllocatePage()
    {
        if (IsCompleted)
        {
            throw new PhysicalPageSpaceException("The access-path allocation scope is complete.");
        }

        long? preferredPageId = _pages.Count == 0 ? null : checked(_pages[^1].Value + 1);
        PhysicalPageId page = _transaction.Allocate(preferredPageId);
        _pages.Add(page);
        return page;
    }

    /// <summary>Removes a formerly owned page from the proposed directory generation.</summary>
    public void ReleasePage(PhysicalPageId page)
    {
        if (IsCompleted)
        {
            throw new PhysicalPageSpaceException("The access-path allocation scope is complete.");
        }

        if (!OwnsCurrentPage(page))
        {
            throw new PhysicalPageSpaceException("Only a currently owned page can be released.");
        }

        _released.Add(page);
    }

    private bool OwnsCurrentPage(PhysicalPageId page)
    {
        if (Current is null)
        {
            return false;
        }

        IReadOnlyList<PhysicalPageExtent> extents = Current.Extents;
        int low = 0, high = extents.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            PhysicalPageExtent extent = extents[middle];
            if (page.Value < extent.FirstPage.Value)
            {
                high = middle - 1;
            }
            else if (page.Value >= extent.EndExclusive)
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Selects an allocated page as the proposed published root.</summary>
    public void Complete(PhysicalPageId rootPage)
    {
        if (IsCompleted)
        {
            throw new PhysicalPageSpaceException("The access-path allocation scope is complete.");
        }

        if (!_pages.Contains(rootPage))
        {
            throw new PhysicalPageSpaceException("The proposed root was not allocated by this access path.");
        }

        _root = rootPage;
    }

    internal void Adopt(PhysicalPageWorkspace workspace)
    {
        if (!workspace.IsCompleted)
        {
            throw new PhysicalPageSpaceException("Only a completed physical workspace can be adopted.");
        }

        IReadOnlyList<PhysicalPageId> pages = _transaction.AdoptWorkspace(workspace);
        _pages.AddRange(pages);
        Complete(workspace.RootPage);
    }

    internal PhysicalPageReservation Reservation()
    {
        if (_root is null)
        {
            throw new PhysicalPageSpaceException("The access-path allocation scope has no root.");
        }

        return new(AccessPathId, Coalesce(_pages), _root.Value, ProposedGeneration,
            _released.OrderBy(page => page.Value).ToArray());
    }

    internal static IReadOnlyList<PhysicalPageExtent> Coalesce(IReadOnlyList<PhysicalPageId> pages)
    {
        if (pages.Count == 0)
        {
            return [];
        }
        // Sort a private copy and discard duplicates during the scan. The caller's
        // allocation order stays intact; no temporary hash set or LINQ sort buffers.
        var ordered = new PhysicalPageId[pages.Count];
        for (int i = 0; i < pages.Count; i++)
        {
            ordered[i] = pages[i];
        }

        Array.Sort(ordered, static (left, right) => left.Value.CompareTo(right.Value));
        int count = 1;
        long previous = ordered[0].Value;
        for (int i = 1; i < ordered.Length; i++)
        {
            long current = ordered[i].Value;
            if (current == previous)
            {
                continue;
            }

            if (current - previous != 1)
            {
                count++;
            }

            previous = current;
        }
        var extents = new PhysicalPageExtent[count];
        int next = 0;
        long first = ordered[0].Value;
        previous = first;
        for (int i = 1; i < ordered.Length; i++)
        {
            long current = ordered[i].Value;
            if (current == previous)
            {
                continue;
            }

            if (current - previous != 1)
            {
                extents[next++] = new(new(first), previous - first + 1);
                first = current;
            }
            previous = current;
        }
        extents[next] = new(new(first), previous - first + 1);
        return extents;
    }

}

/// <summary>Owns collision-free unpublished pages until abandonment or atomic directory adoption.</summary>
public sealed class PhysicalPageWorkspace : IPhysicalPageAllocator, IDisposable
{
    private readonly PhysicalPageSpaceAllocator _allocator;
    private bool _adopted;
    private bool _disposed;

    internal PhysicalPageWorkspace(PhysicalPageSpaceAllocator allocator, Guid workspaceId)
    {
        _allocator = allocator;
        WorkspaceId = workspaceId;
    }

    /// <summary>Gets the stable workspace identity.</summary>
    public Guid WorkspaceId
    {
        get;
    }

    /// <summary>Gets the selected replacement root after completion.</summary>
    public PhysicalPageId RootPage
    {
        get; private set;
    }

    /// <summary>Gets whether the workspace has selected its replacement root.</summary>
    public bool IsCompleted
    {
        get; private set;
    }

    /// <summary>Reserves a unique unpublished tail page.</summary>
    public PhysicalPageId AllocatePage()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsCompleted)
        {
            throw new PhysicalPageSpaceException("The physical workspace is complete.");
        }

        return _allocator.AllocateWorkspace(this);
    }

    /// <summary>Completes the workspace with one of its allocated pages as root.</summary>
    public void Complete(PhysicalPageId rootPage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsCompleted)
        {
            throw new PhysicalPageSpaceException("The physical workspace is complete.");
        }

        _allocator.CompleteWorkspace(this, rootPage);
        RootPage = rootPage;
        IsCompleted = true;
    }

    /// <summary>Reopens an unpublished completed workspace so a replacement can append catch-up pages.</summary>
    internal void Reopen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_adopted)
        {
            throw new PhysicalPageSpaceException("An adopted physical workspace cannot be reopened.");
        }

        if (!IsCompleted)
        {
            throw new PhysicalPageSpaceException("Only a completed physical workspace can be reopened.");
        }

        IsCompleted = false;
    }

    internal void MarkAdopted() => _adopted = true;

    /// <summary>Journals a complete private batch before writing it. The supplied journal
    /// must be the owning database's durable physical log, ordered with its checkpoints.
    /// Only successfully flushed batches may omit duplicate redo at root publication.</summary>
    public async ValueTask WriteJournaledAsync(IResizablePhysicalPageStore pages,
        IReadOnlyList<KeyValuePair<PhysicalPageId, ReadOnlyMemory<byte>>> images,
        Func<IReadOnlyList<DatabasePhysicalPageMutation>, CancellationToken, ValueTask> journal,
        CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_adopted)
        {
            throw new PhysicalPageSpaceException("A published workspace cannot be rewritten.");
        }

        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(journal);
        if (images.Count == 0)
        {
            return;
        }

        if (images.Any(image => image.Value.Length != pages.PageSize))
        {
            throw new PhysicalPageSpaceException("Private batches must contain complete page images.");
        }

        var ids = images.Select(image => image.Key).ToArray();
        _allocator.ValidateWorkspacePages(this, ids);
        var mutations = images.Select(image => DatabasePhysicalPageMutation.CreatePage(image.Key.Value, image.Value)).ToArray();
        await journal(mutations, token).ConfigureAwait(false);
        // The log is durable: finish applying this batch even if the caller cancels.
        // An I/O failure must be handled as recovery-required by the owning kernel.
        long required = ids.Max(page => page.Value) + 1;
        if (await pages.GetPageCountAsync(CancellationToken.None).ConfigureAwait(false) < required)
        {
            await pages.EnsureCapacityAsync(required, CancellationToken.None).ConfigureAwait(false);
        }

        foreach (var image in images)
        {
            await pages.WriteAsync(image.Key, image.Value, CancellationToken.None).ConfigureAwait(false);
        }

        await pages.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        _allocator.AcknowledgeWorkspacePages(this, ids);
    }

    internal int CaptureAllocationMark() => _allocator.CaptureWorkspaceAllocationMark(this);

    internal void RollbackTo(int allocationMark, PhysicalPageId rootPage)
    {
        _allocator.RollbackWorkspace(this, allocationMark);
        RootPage = rootPage;
        IsCompleted = true;
    }

    internal void ReleasePages(IReadOnlyCollection<PhysicalPageId> pages) =>
        _allocator.ReleaseWorkspacePages(this, pages);

    /// <summary>Abandons all reserved pages unless ownership was transferred to a transaction.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_adopted)
        {
            _allocator.AbandonWorkspace(this);
        }
    }
}
