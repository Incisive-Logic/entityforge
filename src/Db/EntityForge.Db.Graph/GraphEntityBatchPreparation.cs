using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

internal static class GraphEntityBatchPreparation
{
    internal sealed record WriteSet(DatabaseVersion ReadVersion, IReadOnlyList<GraphMutation> Mutations,
        IReadOnlyList<GraphReadPredicate> Reads);

    internal static async ValueTask<WriteSet> PrepareSetAsync(IGraphEntityPlanningScope view,
        IReadOnlyList<EntityCapturedRequest> captured, int maximumMutations, CancellationToken cancellationToken)
    {
        var metadata = EntityCapturedBatch.Validate(captured);
        if (maximumMutations < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumMutations));
        }

        var compiler = GraphEntityInputCompiler.FromManifest(metadata.Manifest);
        var mutations = new List<GraphMutation>();
        var reads = new HashSet<GraphReadPredicate>();
        var roots = new HashSet<GraphVertexId>();
        foreach (var request in captured)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var write = await compiler.PrepareCapturedSetAsync(view, request, cancellationToken).ConfigureAwait(false);
            if (!roots.Add(write.Root))
            {
                throw new InvalidOperationException("A mapped write batch may contain only one operation per entity.");
            }

            if (write.Mutations.Count > maximumMutations - mutations.Count)
            {
                throw new InvalidOperationException("The mapped batch exceeds its native mutation bound.");
            }

            mutations.AddRange(write.Mutations);
            reads.UnionWith(write.Reads);
        }
        await view.ValidateAsync(cancellationToken).ConfigureAwait(false);
        return new(metadata.ExpectedVersion ?? view.Version, mutations.ToArray(), reads.ToArray());
    }

    internal static async ValueTask<GraphEntityWrite> PrepareAsync(IGraphDatabase database, IReadOnlyList<GraphEntityRequest> requests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        EntityCapturedBatch.ValidateCount(requests.Count);
        var captured = requests.Select(request => request?.Captured ?? throw new ArgumentException("A batch request cannot be null.", nameof(requests))).ToArray();
        var metadata = EntityCapturedBatch.Validate(captured);
        EntityRecordDeliveryCodec.RequireTarget(database, metadata.DatabaseIncarnation);
        cancellationToken.ThrowIfCancellationRequested();
        await using var view = new EmbeddedGraphEntityReadScope(database,
            await database.AcquireSnapshotAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
        var write = await PrepareSetAsync(new GraphEntityPlanningScope(view), captured,
            GraphEntityWrite.MaximumMutations * captured.Length, cancellationToken).ConfigureAwait(false);
        return new(metadata.Manifest, new(metadata.OperationId, write.Mutations, write.ReadVersion,
            write.Reads, InterpretationFingerprint: EntityRecordInterpretation.Fingerprint(metadata.Manifest, null, false, captured.Length)),
            metadata.DatabaseIncarnation, null, false, captured.Length);
    }
}
