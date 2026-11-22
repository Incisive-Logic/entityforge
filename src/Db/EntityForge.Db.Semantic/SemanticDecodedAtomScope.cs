using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

// One read operation, after its exact snapshot's atom records have been validated.
// Never retained by the store or used to skip loading/validating another snapshot.
internal sealed class SemanticDecodedAtomScope(IReadOnlyDictionary<SemanticAtomId, SemanticAtomRecord> atoms)
{
    private readonly SemanticCanonicalAtomCodec _codec = new();
    private readonly Dictionary<SemanticAtomId, (SemanticAtomRecord Record, object Value)> _decoded = new();

    internal SemanticEntityIdentity Identity(SemanticAtomId id) => (SemanticEntityIdentity)Get(id, SemanticAtomKind.Identity);

    internal SemanticTerm Term(SemanticAtomId id) => (SemanticTerm)Get(id, SemanticAtomKind.Term);

    internal SemanticValue Value(SemanticAtomId id) => (SemanticValue)Get(id, SemanticAtomKind.Value);

    internal SemanticProvenance Provenance(SemanticAtomId id) => (SemanticProvenance)Get(id, SemanticAtomKind.Provenance);

    private object Get(SemanticAtomId id, SemanticAtomKind kind)
    {
        // Check the supplied record before a hit, so a missing/replaced/wrong-kind
        // record cannot be hidden even if a caller supplies a mutable dictionary.
        var record = atoms[id];
        if (record.Id != id || record.Kind != kind)
        {
            throw new InvalidDataException("Decoded Semantic atom association differs.");
        }

        if (_decoded.TryGetValue(id, out var cached) && ReferenceEquals(record, cached.Record))
        {
            return cached.Value;
        }

        object value = kind switch
        {
            SemanticAtomKind.Identity => _codec.DecodeIdentity(record),
            SemanticAtomKind.Term => _codec.DecodeTerm(record),
            SemanticAtomKind.Value => _codec.DecodeValue(record),
            SemanticAtomKind.Provenance => _codec.DecodeProvenance(record),
            _ => throw new InvalidDataException("Unknown Semantic atom kind.")
        };
        _decoded[id] = (record, value);
        return value;
    }
}
