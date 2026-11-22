using System.Diagnostics.Metrics;

namespace EntityForge.Db.Semantic;

internal sealed partial class SemanticPhysicalAccessPath
{
    private IEnumerable<Measurement<long>> ObserveCacheCounts()
    {
        static Measurement<long> Count(string name, long count) => new(count, new KeyValuePair<string, object?>("cache", name));
        // No cache-value enumeration or retained-object traversal. Counts across caches
        // are sampled separately and may change while traffic is running.
        yield return Count("atoms", _atomCache.Observe().Count);
        yield return Count("atom_keys", _atomKeyCache.Count);
        yield return Count("decoded_identities", _decodedIdentityCache.Count);
        yield return Count("identity_memberships", _identityMembershipCache.Count);
        yield return Count("relationship_rows", _relationshipRowCache.Count);
        yield return Count("relationship_evidence", _relationshipEvidenceCache.Count);
        yield return Count("field_evidence", _fieldEvidenceCache.Count);
        yield return Count("relationship_adjacencies", _relationshipAdjacencyCache.Count);
        yield return Count("relationship_assertions", _relationshipAssertionCache.Count);
        yield return Count("field_candidates", _fieldCandidateCache.Count);
        yield return Count("field_assertions", _fieldAssertionCache.Count);
    }
}
