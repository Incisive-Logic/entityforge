using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

// Indexes belong to one owned immutable relation, never to a version number alone.
// Larger relations retain the scan adapter. At most four posting sets are retained.
internal sealed class IndexedRdfRelation(IReadOnlyList<RdfQuad> relation)
{
    internal const int MaximumIndexedQuads = 100_000;
    private readonly RdfQuad[] _relation = relation.ToArray();
    private readonly object _gate = new();
    private readonly Column?[] _columns = new Column?[4];

    private sealed record Column(Dictionary<RdfTerm, RdfQuad[]> Postings, RdfQuad[] DefaultGraph, RdfTerm[] Keys);

    internal RdfSparqlDatasetSnapshot Snapshot(DatabaseVersion version) => new RelationRdfSparqlDatasetSnapshot(_relation, version, this);

    internal IReadOnlyList<RdfQuad> Candidates(RdfQuadPattern pattern, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_relation.Length > MaximumIndexedQuads)
        {
            return _relation;
        }
        int column;
        RdfTerm? key;
        if (pattern.Subject is { } subject)
        {
            column = 0;
            key = subject;
        }
        else if (pattern.Object is { } value)
        {
            column = 2;
            key = value;
        }
        else if (pattern.Predicate is { } predicate)
        {
            column = 1;
            key = predicate;
        }
        else if (pattern.Graph is { } graph)
        {
            column = 3;
            key = graph;
        }
        else if (pattern.MatchDefaultGraphOnly)
        {
            column = 3;
            key = null;
        }
        else
        {
            return _relation;
        }
        var index = GetColumn(column, token);
        return key is null ? index.DefaultGraph : index.Postings.GetValueOrDefault(key) ?? [];
    }

    internal IReadOnlyList<RdfTerm>? NamedGraphs(int maximum, CancellationToken token)
    {
        if (_relation.Length > MaximumIndexedQuads)
        {
            return null;
        }
        return GetColumn(3, token).Keys.Take(maximum).ToArray();
    }

    private Column GetColumn(int column, CancellationToken token)
    {
        if (Volatile.Read(ref _columns[column]) is { } cached)
        {
            return cached;
        }
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (_columns[column] is { } existing)
            {
                return existing;
            }
            var postings = new Dictionary<RdfTerm, List<RdfQuad>>();
            var defaults = new List<RdfQuad>();
            var keys = new List<RdfTerm>();
            foreach (var quad in _relation)
            {
                token.ThrowIfCancellationRequested();
                RdfTerm? key = column switch
                {
                    0 => quad.Subject,
                    1 => quad.Predicate,
                    2 => quad.Object,
                    _ => quad.Graph
                };
                if (key is null)
                {
                    defaults.Add(quad);
                }
                else
                {
                    if (!postings.TryGetValue(key, out var matches))
                    {
                        postings.Add(key, matches = []);
                        keys.Add(key);
                    }
                    matches.Add(quad);
                }
            }
            var result = new Column(postings.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()), defaults.ToArray(), keys.ToArray());
            token.ThrowIfCancellationRequested();
            Volatile.Write(ref _columns[column], result);
            return result;
        }
    }
}
