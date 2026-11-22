using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

internal static class RdfEmbeddedChangeExecution
{
    internal static ValueTask<DatabaseCommitReceipt> ApplyAsync(this RdfChangeSession session, IRdfDatabase database, CancellationToken cancellationToken)
    {
        var incarnation = ((IEntityMappingReplayHost)database).DatabaseIncarnation;
        return EntityNativeChangeOperation.ApplyAsync(database, incarnation, session.OperationId, DatabaseProfileId.Rdf,
            RdfProfileRuntime.TransactionCodecVersion, session.RequestBytes(), () => PrepareAsync(session, database, cancellationToken),
            payload => database.CommitWithReceiptAsync(RdfProfileRuntime.DecodeTransaction(session.OperationId, payload), cancellationToken), cancellationToken);
    }

    private static async ValueTask<byte[]> PrepareAsync(RdfChangeSession session, IRdfDatabase database, CancellationToken cancellationToken)
    {
        var definition = session.ReadDefinition();
        if (definition.Incremental is not null)
        {
            return definition.Incremental;
        }

        using var snapshot = await database.AcquireSnapshotAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var mutations = new List<RdfMutation>();
        var reads = new List<RdfReadPredicate>();
        foreach (var replacement in definition.Replacements!)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = database.Find(new(replacement.Subject, Graph: replacement.Graph, MatchDefaultGraphOnly: replacement.Graph is null), snapshot);
            reads.Add(RdfSubjectStateRead.Capture(replacement.Subject, replacement.Graph, replacement.Graph is null, current));
            mutations.AddRange(current.Except(replacement.Quads).Select(q => new RetractRdfQuad(q)));
            mutations.AddRange(replacement.Quads.Except(current).Select(q => new AssertRdfQuad(q)));
            if (mutations.Count > RdfChangeSession.MaximumChanges)
            {
                throw new InvalidOperationException("The expanded RDF replacement exceeds its mutation limit.");
            }
        }
        return RdfProfileRuntime.Encode(new(session.OperationId, mutations, ReadVersion: snapshot.Version, ReadPredicates: reads));
    }
}
