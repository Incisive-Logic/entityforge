using EntityForge.Db.Rdf;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

internal sealed partial class RdfProfileRuntime : IDatabaseProfileScopedReads
{
    DatabaseTransactionFootprint IDatabaseProfilePartitionReservations.DescribeTransaction(Guid id, ReadOnlyMemory<byte> payload)
    {
        if (EntityOperationCodec.IsCommand(payload.Span))
        {
            return DatabaseTransactionFootprint.OperationMetadata;
        }

        if (IsMetadata(payload))
        {
            return DatabaseTransactionFootprint.WholePartition;
        }

        var transaction = Decode(id, payload.Span);
        if (transaction.IsInitialBulkLoad || transaction.SourceCommandHash is not null && !(transaction.ReadPredicates is { Count: > 0 } scoped && scoped.All(p => p is RdfQueryStateRead)))
        {
            return DatabaseTransactionFootprint.WholePartition;
        }

        if ((transaction.ReadPredicates ?? []).Any(read => read is not RdfSubjectStateRead and not RdfQueryStateRead))
        {
            return DatabaseTransactionFootprint.WholePartition;
        }

        // Subject identity includes term kind and all quoted-triple structure. Deliberately
        // coarser than graph/predicate identity, so absence and graph changes are protected.
        return new(transaction.ReadVersion, (transaction.ReadPredicates ?? []).SelectMany(read => read switch
        {
            RdfSubjectStateRead subject => new[] { Key(subject.Subject) },
            RdfQueryStateRead query => query.Subjects.Select(Key).Append(RdfQueryStateRead.PredicateResource(query.Predicate)),
            _ => throw new InvalidDataException("Unsupported reservation predicate.")
        }), transaction.Mutations.Select(mutation => Key(mutation.Quad.Subject)), transaction.Mutations.Select(mutation => RdfQueryStateRead.PredicateResource(mutation.Quad.Predicate)));
    }

    private static string Key(RdfTerm subject) => RdfPhysicalCodec.ResourceIdentity(subject);

    // RDF validates retained fingerprints against current subject contents, including after
    // reopen, rather than consulting an evictable historical write index.
    IDisposable IDatabaseProfilePartitionReservations.EnterPreparedCompletion(Guid id) => CompletionScope.Instance;

    private sealed class CompletionScope : IDisposable
    {
        internal static readonly CompletionScope Instance = new();

        public void Dispose()
        {
        }
    }
}
