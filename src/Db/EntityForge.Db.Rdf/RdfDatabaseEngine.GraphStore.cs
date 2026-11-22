using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

internal sealed partial class RdfDatabaseEngine
{
    internal IReadOnlyList<RdfTerm> ReadSparqlGraphNames(RdfSnapshotLease snapshot, int maximumNames,
        CancellationToken cancellation)
    {
        return _runtime.ReadSparqlGraphNames(RequireSnapshot(snapshot), maximumNames, cancellation);
    }

    internal IReadOnlyList<RdfQuad> ReadSparqlPrefix(RdfQuadPattern pattern, RdfSnapshotLease snapshot,
        int maximumResults, bool namedOnly, CancellationToken cancellation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumResults);
        cancellation.ThrowIfCancellationRequested();
        return _runtime.ReadSparqlPrefix(pattern, RequireSnapshot(snapshot), maximumResults, namedOnly, cancellation);
    }

    internal IReadOnlyList<RdfQuad> ReadGraphStore(RdfIri? graph, CancellationToken cancellation)
    {
        using var snapshot = AcquireSnapshot();
        var quads = ReadSparqlPrefix(new(Graph: graph, MatchDefaultGraphOnly: graph is null),
            snapshot, RdfSparqlUpdate.MaximumMutations + 1, false, cancellation);
        if (quads.Count > RdfSparqlUpdate.MaximumMutations)
        {
            throw new RdfSparqlWorkLimitException("Graph Store operations are limited to 4,096 triples.");
        }
        return quads;
    }

    internal async ValueTask<bool> WriteGraphStoreAsync(RdfIri? graph, IReadOnlyList<RdfQuad> input,
        bool replace, bool requireExisting, CancellationToken cancellation, Func<bool, bool>? precondition = null)
    {
        if (input.Count > RdfSparqlUpdate.MaximumMutations || input.Any(quad => quad.Graph is not null))
        {
            throw new ArgumentException("Graph Store payloads require at most 4,096 default-graph triples.");
        }
        await _reindexWriteAdmission.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var existing = ReadGraphStore(graph, cancellation);
            bool exists = graph is null || existing.Count != 0;
            if (requireExisting && !exists)
            {
                return false;
            }
            if (precondition is not null && !precondition(exists))
            {
                throw new RdfGraphStorePreconditionException();
            }
            var desired = input.Select(quad => quad with { Graph = graph }).ToHashSet();
            var prior = existing.ToHashSet();
            if (!replace && prior.Union(desired).Take(RdfSparqlUpdate.MaximumMutations + 1).Count() > RdfSparqlUpdate.MaximumMutations)
            {
                throw new RdfSparqlWorkLimitException("The resulting Graph Store graph exceeds 4,096 triples.");
            }
            var mutations = new List<RdfMutation>();
            if (replace)
            {
                mutations.AddRange(existing.Where(quad => !desired.Contains(quad)).Select(quad => new RetractRdfQuad(quad)));
            }
            mutations.AddRange(desired.Where(quad => !prior.Contains(quad)).Select(quad => new AssertRdfQuad(quad)));
            if (mutations.Count > RdfSparqlUpdate.MaximumMutations)
            {
                throw new RdfSparqlWorkLimitException("Graph Store changes exceed 4,096 native mutations.");
            }
            Guid identity = Guid.NewGuid();
            await _kernel.CommitAsync(identity, RdfProfileRuntime.Encode(new(identity, mutations)), cancellation).ConfigureAwait(false);
            return exists;
        }
        finally
        {
            _reindexWriteAdmission.Release();
        }
    }
}
