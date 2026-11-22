using EntityForge.Db.Storage.Pages;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

internal sealed class SemanticEntitySupportReader
{
    private readonly SemanticAssertionRowCodec _rows;
    private readonly Func<SemanticEntityIdentity, string> _identityAtom;
    private readonly Func<byte[], Guid> _decodeAssertion;

    public SemanticEntitySupportReader(SemanticAssertionRowCodec rows,
        Func<SemanticEntityIdentity, string> identityAtom, Func<byte[], Guid> decodeAssertion)
    {
        _rows = rows ?? throw new ArgumentNullException(nameof(rows));
        _identityAtom = identityAtom ?? throw new ArgumentNullException(nameof(identityAtom));
        _decodeAssertion = decodeAssertion ?? throw new ArgumentNullException(nameof(decodeAssertion));
    }

    public async ValueTask<IReadOnlyList<SemanticEntitySupport>> ReadAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot,
        SemanticDirectionalRelationshipAccessPaths.SemanticDirectionalRelationshipSnapshot relationships,
        SemanticFieldAccessPaths.SemanticFieldSnapshot fields,
        IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entities);
        SemanticEntityIdentity[] unique = entities.Distinct().ToArray();
        string[] identityAtoms = unique.Select(_identityAtom).ToArray();
        SemanticAccessKey[] subjectKeys = identityAtoms.Select(atom => new SemanticAccessKey(
            SemanticAccessFamily.SubjectField, atom, string.Empty)).ToArray();
        IReadOnlyDictionary<SemanticAccessKey, IReadOnlyList<byte[]>> subjectFields =
            await fields.FindSubjectsAsync(subjectKeys, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, IReadOnlyList<Guid>> outgoing = await relationships.FindManyAsync(
            identityAtoms, SemanticRelationshipDirection.Outgoing, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, IReadOnlyList<Guid>> incoming = await relationships.FindManyAsync(
            identityAtoms, SemanticRelationshipDirection.Incoming, cancellationToken).ConfigureAwait(false);
        Guid[] fieldIds = subjectFields.Values.SelectMany(values => values.Select(_decodeAssertion)).Distinct().ToArray();
        Guid[] relationshipIds = outgoing.Values.Concat(incoming.Values).SelectMany(values => values).Distinct().ToArray();
        IReadOnlyDictionary<Guid, SemanticAssertionSupport> fieldSupport =
            await ReadFieldSupportAsync(fields, fieldIds, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<Guid, SemanticAssertionSupport> relationshipSupport =
            await ReadRelationshipSupportAsync(snapshot, relationshipIds, cancellationToken).ConfigureAwait(false);
        var result = new SemanticEntitySupport[unique.Length];
        for (int index = 0; index < unique.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<Guid> fieldsForEntity = subjectFields.TryGetValue(subjectKeys[index], out var values)
                ? values.Select(_decodeAssertion) : [];
            IEnumerable<Guid> relationshipsForEntity = outgoing[identityAtoms[index]]
                .Concat(incoming[identityAtoms[index]]);
            result[index] = new(unique[index], fieldsForEntity.Select(id => fieldSupport[id])
                .Concat(relationshipsForEntity.Select(id => relationshipSupport[id]))
                .DistinctBy(item => item.AssertionId).ToArray());
        }
        return result;
    }

    private async ValueTask<IReadOnlyDictionary<Guid, SemanticAssertionSupport>> ReadFieldSupportAsync(
        SemanticFieldAccessPaths.SemanticFieldSnapshot fields, IReadOnlyList<Guid> assertionIds,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, byte[]> encoded = await fields.FindEvidenceAsync(assertionIds, cancellationToken)
            .ConfigureAwait(false);
        return assertionIds.ToDictionary(id => id, id =>
        {
            if (!encoded.TryGetValue(id, out byte[]? value))
            {
                throw new InvalidDataException($"Field assertion '{id:N}' has no physical row.");
            }

            SemanticFieldAssertionRow row = _rows.DecodeField(value);
            return new SemanticAssertionSupport(row.AssertionId, row.BeginVersion, row.EndVersion);
        });
    }

    private async ValueTask<IReadOnlyDictionary<Guid, SemanticAssertionSupport>> ReadRelationshipSupportAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot,
        IReadOnlyList<Guid> assertionIds, CancellationToken cancellationToken)
    {
        SemanticAccessKey[] keys = assertionIds.Select(id => new SemanticAccessKey(
            SemanticAccessFamily.RelationshipAssertion, id.ToString("N"), string.Empty)).ToArray();
        IReadOnlyDictionary<SemanticAccessKey, byte[]> encoded = await snapshot.FindUniqueAsync(keys,
            cancellationToken).ConfigureAwait(false);
        return keys.ToDictionary(key => Guid.ParseExact(key.Term, "N"), key =>
        {
            if (!encoded.TryGetValue(key, out byte[]? value))
            {
                throw new InvalidDataException($"Relationship assertion '{key.Term}' has no physical row.");
            }

            SemanticRelationshipAssertionRow row = _rows.DecodeRelationship(value);
            return new SemanticAssertionSupport(row.AssertionId, row.BeginVersion, row.EndVersion);
        });
    }
}
