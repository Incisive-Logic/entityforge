using EntityForge.Db.Query.Planning;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static Dictionary<RdfTerm, List<IReadOnlyDictionary<string, RdfTerm>>>? BuildJoinLookup(
        IReadOnlyList<Dictionary<string, RdfTerm>> rows,
        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> candidates, CancellationToken cancellation,
        out string? variable)
    {
        cancellation.ThrowIfCancellationRequested();
        variable = null;
        // Avoid building a lookup for tiny joins. Store references to existing
        // solutions, preserving candidate order and duplicate multiplicity.
        if (rows.Count < 8 || candidates.Count < 32)
        {
            return null;
        }
        variable = rows[0].Keys.FirstOrDefault(candidates[0].ContainsKey);
        if (variable is null)
        {
            return null;
        }
        var lookup = new Dictionary<RdfTerm, List<IReadOnlyDictionary<string, RdfTerm>>>();
        foreach (var candidate in QueryCancellation.Observe(candidates, cancellation))
        {
            if (!candidate.TryGetValue(variable, out RdfTerm? key))
            {
                // An absent binding is compatible with every outer value.
                // Keep the ordinary join rather than dropping wildcard rows.
                return null;
            }
            if (!lookup.TryGetValue(key, out var matches))
            {
                matches = [];
                lookup.Add(key, matches);
            }
            matches.Add(candidate);
        }
        return lookup;
    }
}
