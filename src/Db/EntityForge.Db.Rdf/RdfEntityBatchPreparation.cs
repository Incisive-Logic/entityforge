using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

internal static class RdfEntityBatchPreparation
{
    internal static async ValueTask<RdfEntityWrite> PrepareAsync(IRdfDatabase database, IReadOnlyList<RdfEntityRequest> requests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        EntityCapturedBatch.ValidateCount(requests.Count);
        var captured = requests.Select(request => request?.Captured ?? throw new ArgumentException("A batch request cannot be null.", nameof(requests))).ToArray();
        var metadata = EntityCapturedBatch.Validate(captured);
        EntityRecordDeliveryCodec.RequireTarget(database, metadata.DatabaseIncarnation);
        cancellationToken.ThrowIfCancellationRequested();
        var compiler = RdfEntityInputCompiler.FromManifest(metadata.Manifest);
        using var snapshot = await database.AcquireSnapshotAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var view = new LocalRdfMappingSnapshot(database, snapshot);
        var mutations = new List<RdfMutation>();
        var reads = new List<RdfReadPredicate>();
        var roots = new HashSet<RdfTerm>();
        foreach (var request in captured)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var write = compiler.PrepareCapturedSet(view, request, snapshot.Version);
            if (!roots.Add(((RdfSubjectStateRead)write.Reads.Single()).Subject))
            {
                throw new InvalidOperationException("A mapped write batch may contain only one operation per entity.");
            }

            mutations.AddRange(write.Mutations);
            reads.AddRange(write.Reads);
        }
        return new(metadata.Manifest, new(metadata.OperationId, mutations, ReadVersion: metadata.ExpectedVersion ?? snapshot.Version,
            ReadPredicates: reads.Distinct().ToArray(), InterpretationFingerprint: EntityRecordInterpretation.Fingerprint(metadata.Manifest, null, false, captured.Length)),
            metadata.DatabaseIncarnation, null, false, captured.Length);
    }
}
