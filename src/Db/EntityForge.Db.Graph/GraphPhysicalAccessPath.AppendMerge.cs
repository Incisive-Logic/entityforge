using EntityForge.Db.AccessPaths;
using EntityForge.Db.Graph.Contracts;
using static EntityForge.Db.Graph.GraphPhysicalEntries;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Graph;

internal sealed class GraphPhysicalAccessPath
{
    public bool RequiresCheckpointMaintenance => _pageSpace.RequiresCheckpointMaintenance;

    public void AuthorizeCheckpointPageReuse() => _pageSpace.AuthorizeCheckpointPageReuse();

    private static readonly PhysicalAccessPathId AccessPathId =
        new(new Guid("8f8510ae-f350-488a-b5b7-15cfc450a370"));

    private static readonly PhysicalAccessMethodKind AccessMethod = new("append-merge.graph-ordered", 2);
    private readonly AppendMergeAccessPath<GraphAccessKey, Guid> _access;
    private readonly PhysicalPageSpace _pageSpace;
    private readonly IResizablePhysicalPageStore _pages;
    private readonly HashSet<Guid> _staged = [];
    private readonly Dictionary<Guid, IReadOnlyList<DatabasePhysicalPageMutation>> _stagedMutations = [];
    private readonly Dictionary<Guid, PhysicalPageWorkspace> _replacements = [];

    private GraphPhysicalAccessPath(AppendMergeAccessPath<GraphAccessKey, Guid> access,
        PhysicalPageSpace pageSpace, IResizablePhysicalPageStore pages)
    {
        _access = access;
        _pageSpace = pageSpace;
        _pages = pages;
    }

    public static async ValueTask<GraphPhysicalAccessPath> OpenAsync(IResizablePhysicalPageStore pages,
        CancellationToken cancellationToken = default)
    {
        const byte LegacyOrderedCatalogRecord = 1;
        if (await pages.GetPageCountAsync(cancellationToken).ConfigureAwait(false) != 0)
        {
            var root = SlottedDataPage.Decode((await pages.ReadAsync(new(0), cancellationToken).ConfigureAwait(false)).Span, new(0));
            if (root.FirstOccupiedRecord.Span[0] == LegacyOrderedCatalogRecord)
            {
                throw new NotSupportedException("This Graph image uses the unreleased ordered-tree format. The append/merge format requires a fresh database; the existing image has not been modified.");
            }
        }
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages, cancellationToken).ConfigureAwait(false);
        PhysicalAccessPathDirectoryEntry? published = pageSpace.CaptureDirectory().Entries
            .SingleOrDefault(entry => entry.AccessPathId == AccessPathId);
        AppendMergeAccessPath<GraphAccessKey, Guid> access = await AppendMergeAccessPath<GraphAccessKey, Guid>.OpenAsync(
            pages, AccessPathId, AccessMethod, published, new KeyCodec(), new GuidCodec(),
            GraphAccessKeyComparer.Instance, GuidByteComparer.Instance, options: new(compactOnWrite: true), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (published is null && pageSpace.CaptureDirectory().Entries.Count != 0)
        {
            throw new InvalidDataException("The physical directory does not contain the Graph access path.");
        }

        return new(access, pageSpace, pages);
    }

    internal static AppendMergeMutation<GraphAccessKey, Guid>[] Compile(GraphCommittedEffects effects) =>
        GraphPhysicalEntries.Compile(effects).Select(m => new AppendMergeMutation<GraphAccessKey, Guid>(
            m.Kind == OrderedPageTreeMutationKind.Add ? AppendMergeMutationKind.Add : AppendMergeMutationKind.Remove,
            m.Key, m.Value)).ToArray();

    public ValueTask<OrderedPageTreeStage?> PrepareAsync(Guid transactionId, long databaseVersion,
        GraphCommittedEffects effects, CancellationToken cancellationToken) =>
        PrepareCompiledAsync(transactionId, databaseVersion, Compile(effects), cancellationToken);

    internal async ValueTask<OrderedPageTreeStage?> PrepareCompiledAsync(Guid transactionId, long databaseVersion,
        AppendMergeMutation<GraphAccessKey, Guid>[] mutations, CancellationToken cancellationToken)
    {
        if (mutations.Length == 0)
        {
            return null;
        }

        AppendMergeMutationPlan<GraphAccessKey, Guid> plan = _access.PlanMutations(
            mutations, databaseVersion, cancellationToken);
        return await PreparePlanAsync(transactionId, plan, cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask<OrderedPageTreeStage?> PrepareGroupAsync(Guid transactionId,
        IReadOnlyList<(long Version, AppendMergeMutation<GraphAccessKey, Guid>[] Mutations)> members, CancellationToken token)
    {
        var plans = members.Select(member => _access.PlanMutations(
            member.Mutations, member.Version, token)).ToArray();
        return PreparePlanAsync(transactionId, _access.PlanMutationGroup(plans), token);
    }

    private async ValueTask<OrderedPageTreeStage?> PreparePlanAsync(Guid transactionId,
        AppendMergeMutationPlan<GraphAccessKey, Guid> plan, CancellationToken cancellationToken)
    {
        if (plan.Count == 0)
        {
            return null;
        }

        PhysicalPageSpaceTransaction physical = _pageSpace.BeginTransaction(transactionId);
        try
        {
            PhysicalAccessPathDirectoryEntry? current = _pageSpace.CaptureDirectory().Entries
                .SingleOrDefault(entry => entry.AccessPathId == AccessPathId);
            AppendMergeStage stage = await _access.PrepareAsync(transactionId, plan,
                physical.OpenAccessPath(AccessPathId, AccessMethod, current?.Generation ?? -1),
                cancellationToken).ConfigureAwait(false);
            PhysicalPageSpacePersistenceStage directory = await _pageSpace.PrepareAsync(
                physical, cancellationToken).ConfigureAwait(false);
            _staged.Add(transactionId);
            _stagedMutations.Add(transactionId, stage.PhysicalMutations);
            return new(transactionId, new(stage.Generation),
                stage.PhysicalMutations.Concat(directory.PhysicalMutations).ToArray());
        }
        catch
        {
            _access.Abandon(transactionId);
            _pageSpace.Abandon(transactionId);
            throw;
        }
    }

    public ValueTask PersistAsync(Guid transactionId, CancellationToken cancellationToken) =>
        _pageSpace.PersistAsync(transactionId, _stagedMutations[transactionId], cancellationToken);

    public ValueTask FlushAsync(CancellationToken cancellationToken) => _pages.FlushAsync(cancellationToken);

    public void Publish(Guid transactionId)
    {
        if (_staged.Remove(transactionId))
        {
            _access.Publish(transactionId);
        }

        _pageSpace.Publish(transactionId);
        _stagedMutations.Remove(transactionId);
    }

    public void Abandon(Guid transactionId)
    {
        if (_staged.Remove(transactionId))
        {
            _access.Abandon(transactionId);
            _pageSpace.Abandon(transactionId);
        }
        _stagedMutations.Remove(transactionId);
    }

    public async ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PrepareMaintenanceAsync(Guid id,
        long version, bool compact, CancellationToken token)
    {
        var physical = _pageSpace.BeginVacuumTransaction(id);
        try
        {
            PhysicalAccessPathDirectoryEntry? current = _pageSpace.CaptureDirectory().Entries
                .SingleOrDefault(entry => entry.AccessPathId == AccessPathId);
            var allocation = physical.OpenAccessPath(AccessPathId, AccessMethod, current?.Generation ?? -1);
            var stage = compact
                ? await _access.PrepareVacuumAsync(id, version, allocation, token).ConfigureAwait(false)
                : await _access.PrepareMaintenanceAsync(id, version, allocation, token).ConfigureAwait(false);
            if (stage is null)
            {
                _pageSpace.Abandon(id);
                return [];
            }
            var directory = await _pageSpace.PrepareAsync(physical, token).ConfigureAwait(false);
            _staged.Add(id);
            _stagedMutations.Add(id, stage.Stage.PhysicalMutations);
            return stage.Stage.PhysicalMutations.Concat(directory.PhysicalMutations).ToArray();
        }
        catch
        {
            _access.Abandon(id);
            _pageSpace.Abandon(id);
            throw;
        }
    }

    public ValueTask TrimAsync(CancellationToken token) => _pageSpace.TrimUnownedTailAsync(token);

    public async ValueTask BuildReplacementAsync(Guid replacementId, GraphReadView snapshot,
        long databaseVersion, CancellationToken cancellationToken)
    {
        AppendMergeMutation<GraphAccessKey, Guid>[] mutations = ReplacementEntries(snapshot)
            .Select(entry => new AppendMergeMutation<GraphAccessKey, Guid>(AppendMergeMutationKind.Add,
                entry.Key, entry.Value)).ToArray();
        PhysicalPageWorkspace workspace = _pageSpace.BeginReplacementWorkspace(replacementId);
        try
        {
            await _access.BuildReplacementAsync(replacementId, mutations, Math.Max(1, databaseVersion),
                workspace, cancellationToken: cancellationToken).ConfigureAwait(false);
            _replacements.Add(replacementId, workspace);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public async ValueTask<OrderedPageTreeStage> PrepareReplacementAsync(Guid transactionId,
        Guid replacementId, CancellationToken cancellationToken)
    {
        PhysicalPageWorkspace workspace = _replacements.GetValueOrDefault(replacementId) ??
            throw new AppendMergeException("The Graph append/merge replacement is unavailable.");
        long expected = _access.GetReplacementExpectedGeneration(replacementId);
        long expectedDirectoryGeneration = _pageSpace.CaptureDirectory().Entries
            .SingleOrDefault(entry => entry.AccessPathId == AccessPathId)?.Generation ?? -1;
        PhysicalPageSpaceTransaction physical = _pageSpace.BeginTransaction(transactionId);
        try
        {
            physical.OpenReplacementAccessPath(AccessPathId, AccessMethod,
                expectedDirectoryGeneration, workspace);
            AppendMergeStage stage = _access.PrepareReplacement(transactionId, replacementId);
            PhysicalPageSpacePersistenceStage directory = await _pageSpace.PrepareAsync(
                physical, cancellationToken).ConfigureAwait(false);
            _replacements.Remove(replacementId);
            _staged.Add(transactionId);
            _stagedMutations.Add(transactionId, stage.PhysicalMutations);
            return new(transactionId, new(stage.Generation), directory.PhysicalMutations);
        }
        catch
        {
            _access.Abandon(transactionId);
            _pageSpace.Abandon(transactionId);
            _replacements.Remove(replacementId);
            workspace.Dispose();
            long actual = _access.Generation;
            if (actual != expected)
            {
                throw new AppendMergeReplacementStaleException(expected, actual);
            }

            throw;
        }
    }

    public long Generation => _access.Generation;

    internal AppendMergeSnapshot<GraphAccessKey, Guid> CaptureSnapshot(long? maximumVersion = null) =>
        _access.CaptureSnapshot(maximumVersion: maximumVersion);

    public long GetReplacementExpectedGeneration(Guid replacementId) =>
        _access.GetReplacementExpectedGeneration(replacementId);

    public void AbandonReplacement(Guid replacementId)
    {
        _access.AbandonReplacement(replacementId);
        if (_replacements.Remove(replacementId, out var workspace))
        {
            workspace.Dispose();
        }
    }

}
