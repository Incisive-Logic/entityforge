using EntityForge.Db.AccessPaths;
using EntityForge.Db.Storage.Pages;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

internal sealed partial class SemanticPhysicalAccessPath
{
    private static OrderedIndexRange<SemanticAccessKey> RecoveryFamily(SemanticAccessFamily family) =>
        OrderedIndexRange<SemanticAccessKey>.Between(new(family, string.Empty, string.Empty),
            new((SemanticAccessFamily)((byte)family + 1), string.Empty, string.Empty));

    // The merge stream resolves every run's removals and version ceiling. Each intersecting
    // physical page is decoded once per pass, including records not selected by this join.
    private async ValueTask<IReadOnlyDictionary<SemanticAtomId, SemanticAtomRecord>> ReadRecoveryAtomsAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot, IReadOnlySet<SemanticAtomId> ids, CancellationToken token)
    {
        var requested = ids.ToDictionary(AtomDirectoryKey);
        var seen = new HashSet<SemanticAtomId>();
        var directories = new Dictionary<string, (SemanticAtomId Id, int Chunks)>(StringComparer.Ordinal);
        var result = new Dictionary<SemanticAtomId, SemanticAtomRecord>(ids.Count);
        if (ids.Count == 0)
        {
            return result;
        }

        using (SemanticPhysicalReadTelemetry.Start("atom_directory_read"))
        {
            using (var work = AppendMergeLookupTelemetry.Begin(SemanticLookupRoles.AtomDirectory, ids.Count))
            {
                await foreach (var item in snapshot.ReadVisibleAsync(RecoveryFamily(SemanticAccessFamily.AtomDirectory), token).ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested();
                    if (!requested.TryGetValue(item.Key, out var id))
                    {
                        continue;
                    }

                    if (!seen.Add(id))
                    {
                        throw new InvalidDataException("Recovery atom has multiple visible directory values.");
                    }

                    var directory = DecodeAtomDirectory(item.Value);
                    if (!directories.TryAdd(directory.Ordinal.ToString("D20"), (id, directory.ChunkCount)))
                    {
                        throw new InvalidDataException("Recovery atom has a duplicate directory or ordinal.");
                    }
                }
                work?.Complete();
            }
        }
        if (seen.Count != ids.Count)
        {
            throw new InvalidDataException("Recovery atom directory is missing.");
        }

        requested.Clear();
        seen.Clear();
        string? priorOrdinal = null;
        var chunks = new List<SemanticAtomChunk>();
        using (SemanticPhysicalReadTelemetry.Start("atom_payload_read"))
        {
            using (var work = AppendMergeLookupTelemetry.Begin(SemanticLookupRoles.AtomPayload,
                    directories.Values.Sum(value => value.Chunks)))
            {
                await foreach (var item in snapshot.ReadVisibleAsync(RecoveryFamily(SemanticAccessFamily.AtomPayload), token).ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested();
                    if (!directories.TryGetValue(item.Key.Term, out var directory))
                    {
                        continue;
                    }

                    if (priorOrdinal != item.Key.Term)
                    {
                        if (chunks.Count != 0)
                        {
                            throw new InvalidDataException("Recovery atom payload is incomplete.");
                        }

                        priorOrdinal = item.Key.Term;
                    }
                    int sequence = chunks.Count;
                    if (sequence >= directory.Chunks || item.Key.Value != sequence.ToString("D8"))
                    {
                        throw new InvalidDataException("Recovery atom payload sequence is missing or duplicated.");
                    }

                    chunks.Add(_atomStorage.Decode(new(directory.Id, sequence), item.Value));
                    if (chunks.Count == directory.Chunks)
                    {
                        if (!result.TryAdd(directory.Id, _atomStorage.Reassemble(chunks)))
                        {
                            throw new InvalidDataException("Recovery atom has multiple visible payloads.");
                        }

                        chunks.Clear();
                    }
                }
                work?.Complete();
            }
        }
        if (chunks.Count != 0 || result.Count != ids.Count)
        {
            throw new InvalidDataException("Recovery atom payload is missing or incomplete.");
        }

        return result;
    }

    private async ValueTask<SemanticEvidenceSlice> ReadRecoveryEvidenceAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot,
        SemanticFieldAccessPaths.SemanticFieldSnapshot fields,
        IReadOnlySet<SemanticEntityIdentity> subjects, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var owned = subjects.Select(subject => Atom(subject).Id).ToHashSet();
        var fieldRows = new List<SemanticFieldAssertionRow>();
        var relationshipRows = new List<SemanticRelationshipAssertionRow>();
        long maximum = snapshot.MaximumVersion ?? long.MaxValue;
        bool Active(DatabaseVersion begin, DatabaseVersion? end) =>
            begin.Value <= maximum && (end is null || end.Value.Value > maximum);
        Guid? priorField = null;
        using (SemanticPhysicalReadTelemetry.Start("recovery_field_stream"))
        {
            await foreach (var item in fields.ReadVisibleEvidenceAsync(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                if (priorField == item.Key)
                {
                    throw new InvalidDataException("Recovery field has multiple visible rows.");
                }

                priorField = item.Key;
                var row = _rows.DecodeField(item.Value);
                if (row.AssertionId != item.Key)
                {
                    throw new InvalidDataException("Recovery field row identity differs from its key.");
                }

                if (owned.Contains(row.Subject) && Active(row.BeginVersion, row.EndVersion))
                {
                    fieldRows.Add(row);
                }
            }
        }

        SemanticAccessKey? priorRelationship = null;
        using (SemanticPhysicalReadTelemetry.Start("recovery_relationship_stream"))
        {
            await foreach (var item in snapshot.ReadVisibleAsync(
                RecoveryFamily(SemanticAccessFamily.RelationshipAssertion), token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                if (item.Key.Family != SemanticAccessFamily.RelationshipAssertion)
                {
                    continue;
                }

                if (priorRelationship == item.Key)
                {
                    throw new InvalidDataException("Recovery relationship has multiple visible rows.");
                }

                priorRelationship = item.Key;
                var row = _rows.DecodeRelationship(item.Value);
                if (AssertionKey(SemanticAccessFamily.RelationshipAssertion, row.AssertionId) != item.Key)
                {
                    throw new InvalidDataException("Recovery relationship row identity differs from its key.");
                }

                if (owned.Contains(row.Subject) && Active(row.BeginVersion, row.EndVersion))
                {
                    relationshipRows.Add(row);
                }
            }
        }

        var atomIds = new HashSet<SemanticAtomId>();
        foreach (var row in fieldRows)
        {
            token.ThrowIfCancellationRequested();
            atomIds.Add(row.Subject);
            atomIds.Add(row.Field);
            atomIds.Add(row.Value);
            atomIds.Add(row.Provenance);
        }
        foreach (var row in relationshipRows)
        {
            token.ThrowIfCancellationRequested();
            atomIds.Add(row.Subject);
            atomIds.Add(row.Relationship);
            atomIds.Add(row.Target);
            atomIds.Add(row.Provenance);
        }
        // Private to this recovery attempt. No validation or serialized-output cache is retained.
        var atoms = await ReadRecoveryAtomsAsync(snapshot, atomIds, token).ConfigureAwait(false);
        var hydratedFields = await HydrateFieldRowsAsync(snapshot, fieldRows, token, atoms).ConfigureAwait(false);
        var hydratedRelationships = new SemanticRelationshipEvidence[relationshipRows.Count];
        var decodedRelationships = new SemanticDecodedAtomScope(atoms);
        for (int index = 0; index < relationshipRows.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var row = relationshipRows[index];
            hydratedRelationships[index] = new(row.AssertionId,
                decodedRelationships.Identity(row.Subject), decodedRelationships.Term(row.Relationship),
                decodedRelationships.Identity(row.Target), decodedRelationships.Provenance(row.Provenance),
                row.BeginVersion, row.EndVersion);
        }
        return new(hydratedFields, hydratedRelationships);
    }
}
