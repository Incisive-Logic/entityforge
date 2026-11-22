using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Diagnostics;
using EntityForge.Db.Storage.Pages;
using EntityForge.Db.Storage;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.AccessPaths;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Kernel;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

internal sealed partial class SemanticPhysicalAccessPath
{
    private readonly ConcurrentDictionary<(long Generation, Guid AssertionId),
        SemanticRelationshipAssertionRow> _relationshipRowCache = new();

    private readonly ConcurrentDictionary<(long Generation, Guid AssertionId),
        SemanticRelationshipEvidence> _relationshipEvidenceCache = new();

    private readonly ConcurrentDictionary<(long SharedGeneration, long? SharedMaximumVersion,
        long EvidenceGeneration, long? EvidenceMaximumVersion, Guid AssertionId),
        SemanticFieldEvidence> _fieldEvidenceCache = new();

    private readonly ConcurrentDictionary<(long Generation, long? MaximumVersion, string EntityAtom,
        SemanticRelationshipDirection Direction, Guid RelationshipAtom),
        IReadOnlyList<SemanticDirectionalRelationshipAccessPaths.SemanticRelationshipAdjacencyValue>>
        _relationshipAdjacencyCache = new();

    private readonly ConcurrentDictionary<(long Generation, long? MaximumVersion, string EntityAtom,
        SemanticRelationshipDirection Direction, Guid? RelationshipAtom), IReadOnlyList<Guid>>
        _relationshipAssertionCache = new();

    private readonly ConcurrentDictionary<(long Generation, long? MaximumVersion, SemanticAccessKey Equality),
        IReadOnlyList<SemanticFieldCandidate>> _fieldCandidateCache = new();

    private readonly ConcurrentDictionary<(long Generation, long? MaximumVersion, SemanticAccessKey Equality),
        IReadOnlyList<Guid>> _fieldAssertionCache = new();

    private readonly ConcurrentDictionary<object, string> _atomKeyCache = new();
    private readonly ConcurrentDictionary<SemanticAtomId, SemanticEntityIdentity> _decodedIdentityCache = new();
    private readonly ConcurrentDictionary<(long Generation, long? MaximumVersion, string IdentityAtom), bool>
        _identityMembershipCache = new();

    private async ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(
        SemanticDirectionalRelationshipAccessPaths.SemanticDirectionalRelationshipSnapshot snapshot,
        SemanticEntityIdentity entity, SemanticRelationshipDirection direction,
        SemanticTerm? relationship, CancellationToken cancellationToken)
    {
        string entityAtom = AtomKey(entity);
        Guid? relationshipAtom = relationship is null ? null : Atom(relationship).Id.Value;
        long generation = snapshot.Generation(direction);
        long? maximumVersion = snapshot.MaximumVersion(direction);
        if (_relationshipAssertionCache.TryGetValue(
                (generation, maximumVersion, entityAtom, direction, relationshipAtom), out var cached))
        {
            return cached;
        }

        IReadOnlyList<Guid> result = await snapshot.FindAsync(entityAtom, direction, relationshipAtom,
            cancellationToken).ConfigureAwait(false);
        CacheRelationshipAssertions((generation, maximumVersion, entityAtom, direction, relationshipAtom), result);
        return result;
    }

    private async ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> ReadRelationshipEvidenceAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot,
        IReadOnlyList<Guid> assertionIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assertionIds);
        cancellationToken.ThrowIfCancellationRequested();
        var cachedResult = new SemanticRelationshipEvidence[assertionIds.Count];
        bool allCached = true;
        for (int index = 0; index < assertionIds.Count; index++)
        {
            if (!_relationshipEvidenceCache.TryGetValue(
                    (snapshot.Generation, assertionIds[index]), out SemanticRelationshipEvidence? cached))
            {
                allCached = false;
                break;
            }
            cachedResult[index] = cached;
        }
        if (allCached)
        {
            return cachedResult;
        }

        Guid[] ids = assertionIds.ToArray();
        var evidenceById = new Dictionary<Guid, SemanticRelationshipEvidence>(ids.Length);
        Guid[] evidenceMissing = ids.Distinct().Where(id =>
        {
            if (!_relationshipEvidenceCache.TryGetValue((snapshot.Generation, id), out var cached))
            {
                return true;
            }

            evidenceById.Add(id, cached);
            return false;
        }).ToArray();
        if (evidenceMissing.Length == 0)
        {
            return ids.Select(id => evidenceById[id]).ToArray();
        }

        var rowsById = new Dictionary<Guid, SemanticRelationshipAssertionRow>(evidenceMissing.Length);
        var missingIds = new List<Guid>();
        foreach (Guid id in evidenceMissing)
        {
            if (_relationshipRowCache.TryGetValue((snapshot.Generation, id),
                    out SemanticRelationshipAssertionRow? cached))
            {
                rowsById.Add(id, cached);
            }
            else
            {
                missingIds.Add(id);
            }
        }

        Guid[] missing = missingIds.ToArray();
        if (missing.Length != 0)
        {
            SemanticAccessKey[] keys = missing.Select(id => AssertionKey(
                SemanticAccessFamily.RelationshipAssertion, id)).ToArray();
            IReadOnlyDictionary<SemanticAccessKey, byte[]> encoded = await snapshot.FindUniqueAsync(keys,
                cancellationToken).ConfigureAwait(false);
            if (_relationshipRowCache.Count + missing.Length > MaximumCachedRelationshipRows)
            {
                _relationshipRowCache.Clear();
            }

            foreach ((Guid id, SemanticAccessKey key) in missing.Zip(keys))
            {
                SemanticRelationshipAssertionRow row = encoded.TryGetValue(key, out byte[]? value)
                    ? _rows.DecodeRelationship(value) : throw new InvalidDataException(
                        $"Relationship assertion '{key.Term}' has no physical row.");
                rowsById.Add(id, row);
                _relationshipRowCache[(snapshot.Generation, id)] = row;
            }
        }
        SemanticRelationshipAssertionRow[] rows = evidenceMissing.Select(id => rowsById[id]).ToArray();
        IReadOnlyDictionary<SemanticAtomId, SemanticAtomRecord> atoms = await ReadAtomsAsync(snapshot,
            rows.SelectMany(row => new[] { row.Subject, row.Relationship, row.Target, row.Provenance }).ToArray(),
            cancellationToken).ConfigureAwait(false);
        if (_relationshipEvidenceCache.Count + rows.Length > MaximumCachedRelationshipEvidence)
        {
            _relationshipEvidenceCache.Clear();
        }

        foreach (SemanticRelationshipAssertionRow row in rows)
        {
            var evidence = new SemanticRelationshipEvidence(row.AssertionId,
                DecodeIdentity(row.Subject, atoms), _atoms.DecodeTerm(atoms[row.Relationship]),
                DecodeIdentity(row.Target, atoms), _atoms.DecodeProvenance(atoms[row.Provenance]),
                row.BeginVersion, row.EndVersion);
            evidenceById.Add(row.AssertionId, evidence);
            _relationshipEvidenceCache[(snapshot.Generation, row.AssertionId)] = evidence;
        }
        return ids.Select(id => evidenceById[id]).ToArray();
    }

    private async ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(
        SemanticDirectionalRelationshipAccessPaths.SemanticDirectionalRelationshipSnapshot snapshot,
        IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
        CancellationToken cancellationToken)
        => await FindRelationshipAssertionsAsync(snapshot, entities, direction, null,
            cancellationToken).ConfigureAwait(false);

    private async ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(
        SemanticDirectionalRelationshipAccessPaths.SemanticDirectionalRelationshipSnapshot snapshot,
        IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
        SemanticTerm? relationship, CancellationToken cancellationToken)
    {
        string[] atoms = entities.Distinct().Select(AtomKey).ToArray();
        Guid? relationshipAtom = relationship is null ? null : Atom(relationship).Id.Value;
        long generation = snapshot.Generation(direction);
        long? maximumVersion = snapshot.MaximumVersion(direction);
        var matches = new Dictionary<string, IReadOnlyList<Guid>>(atoms.Length, StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (string atom in atoms)
        {
            if (_relationshipAssertionCache.TryGetValue(
                    (generation, maximumVersion, atom, direction, relationshipAtom), out var cached))
            {
                matches.Add(atom, cached);
            }
            else
            {
                missing.Add(atom);
            }
        }

        if (missing.Count != 0)
        {
            IReadOnlyDictionary<string, IReadOnlyList<Guid>> loaded;
            if (relationshipAtom is null)
            {
                loaded = await snapshot.FindManyAsync(missing, direction, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                IReadOnlyDictionary<string,
                    IReadOnlyList<SemanticDirectionalRelationshipAccessPaths.SemanticRelationshipAdjacencyValue>>
                    values = await snapshot.FindManyCandidatesAsync(missing, direction, relationshipAtom.Value,
                        cancellationToken).ConfigureAwait(false);
                loaded = values.ToDictionary(item => item.Key,
                    item => (IReadOnlyList<Guid>)item.Value.Select(value => value.AssertionId).ToArray(),
                    StringComparer.Ordinal);
            }
            foreach (string atom in missing)
            {
                IReadOnlyList<Guid> ids = loaded.TryGetValue(atom, out var found) ? found : Array.Empty<Guid>();
                matches.Add(atom, ids);
                CacheRelationshipAssertions((generation, maximumVersion, atom, direction, relationshipAtom), ids);
            }
        }
        return atoms.SelectMany(atom => matches[atom]).Distinct().ToArray();
    }

    private void CacheRelationshipAssertions((long Generation, long? MaximumVersion, string EntityAtom,
        SemanticRelationshipDirection Direction, Guid? RelationshipAtom) key, IReadOnlyList<Guid> assertions)
    {
        if (_relationshipAssertionCache.Count + 1 > MaximumCachedRelationshipAssertionSets)
        {
            _relationshipAssertionCache.Clear();
        }

        _relationshipAssertionCache[key] = assertions;
    }

    private async ValueTask<IReadOnlyList<SemanticFieldEvidence>> ReadFieldEvidenceAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot,
        SemanticFieldAccessPaths.SemanticFieldSnapshot fields,
        IReadOnlyList<Guid> assertionIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assertionIds);
        cancellationToken.ThrowIfCancellationRequested();
        var cacheGeneration = (snapshot.Generation, snapshot.MaximumVersion,
            fields.EvidenceGeneration, fields.EvidenceMaximumVersion);
        var cachedResult = new SemanticFieldEvidence[assertionIds.Count];
        bool allCached = true;
        for (int index = 0; index < assertionIds.Count; index++)
        {
            if (!_fieldEvidenceCache.TryGetValue(
                    (cacheGeneration.Generation, cacheGeneration.MaximumVersion,
                        cacheGeneration.EvidenceGeneration, cacheGeneration.EvidenceMaximumVersion,
                        assertionIds[index]),
                    out SemanticFieldEvidence? cached))
            {
                allCached = false;
                break;
            }
            cachedResult[index] = cached;
        }
        if (allCached)
        {
            return cachedResult;
        }

        Guid[] keys = assertionIds.ToArray();
        var evidenceById = new Dictionary<Guid, SemanticFieldEvidence>(keys.Length);
        Guid[] missing = keys.Distinct().Where(id =>
        {
            if (!_fieldEvidenceCache.TryGetValue(
                    (cacheGeneration.Generation, cacheGeneration.MaximumVersion,
                        cacheGeneration.EvidenceGeneration, cacheGeneration.EvidenceMaximumVersion, id),
                    out var cached))
            {
                return true;
            }

            evidenceById.Add(id, cached);
            return false;
        }).ToArray();
        if (missing.Length == 0)
        {
            return keys.Select(id => evidenceById[id]).ToArray();
        }

        IReadOnlyDictionary<Guid, byte[]> encoded = await fields.FindEvidenceAsync(missing,
            cancellationToken).ConfigureAwait(false);
        SemanticFieldAssertionRow[] rows;
        using (SemanticPhysicalReadTelemetry.Start("evidence_row_decoding"))
        {
            rows = missing.Select(key => encoded.TryGetValue(key, out byte[]? row)
                ? _rows.DecodeField(row) : throw new InvalidDataException(
                    $"Field assertion '{key:N}' has no physical row.")).ToArray();
        }

        IReadOnlyList<SemanticFieldEvidence> hydrated = await HydrateFieldRowsAsync(
            snapshot, rows, cancellationToken).ConfigureAwait(false);
        if (_fieldEvidenceCache.Count + hydrated.Count > MaximumCachedFieldEvidence)
        {
            _fieldEvidenceCache.Clear();
        }

        foreach (SemanticFieldEvidence evidence in hydrated)
        {
            evidenceById.Add(evidence.AssertionId, evidence);
            _fieldEvidenceCache[(cacheGeneration.Generation, cacheGeneration.MaximumVersion,
                cacheGeneration.EvidenceGeneration, cacheGeneration.EvidenceMaximumVersion,
                evidence.AssertionId)] = evidence;
        }
        return keys.Select(id => evidenceById[id]).ToArray();
    }

    private async ValueTask<IReadOnlyList<SemanticFieldEvidence>> ScanFieldEvidenceAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot,
        SemanticFieldAccessPaths.SemanticFieldSnapshot fields, SemanticTerm field,
        CancellationToken cancellationToken)
    {
        const int pageSize = 4096;
        SemanticAtomId fieldId = Atom(field).Id;
        var rows = new List<SemanticFieldAssertionRow>();
        AppendMergeCursor<Guid, byte[]>? cursor = null;
        do
        {
            AppendMergePage<Guid, byte[]> page = await fields.ReadEvidencePageAsync(
                pageSize, cursor, cancellationToken).ConfigureAwait(false);
            foreach (KeyValuePair<Guid, byte[]> item in page.Items)
            {
                SemanticFieldAssertionRow row = _rows.DecodeField(item.Value);
                if (row.Field == fieldId)
                {
                    rows.Add(row);
                }
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
        return await HydrateFieldRowsAsync(snapshot, rows, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<SemanticFieldEvidence>> HydrateFieldRowsAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot,
        IReadOnlyList<SemanticFieldAssertionRow> rows, CancellationToken cancellationToken,
        IReadOnlyDictionary<SemanticAtomId, SemanticAtomRecord>? recoveryAtoms = null)
    {
        var atomIds = new HashSet<SemanticAtomId>();
        foreach (SemanticFieldAssertionRow row in rows)
        {
            atomIds.Add(row.Subject);
            atomIds.Add(row.Field);
            atomIds.Add(row.Value);
            atomIds.Add(row.Provenance);
        }
        IReadOnlyDictionary<SemanticAtomId, SemanticAtomRecord> atoms;
        using (SemanticPhysicalReadTelemetry.Start("evidence_atom_read"))
        {
            atoms = recoveryAtoms ?? await ReadAtomsAsync(snapshot, atomIds.ToArray(), cancellationToken).ConfigureAwait(false);
        }

        var identities = new Dictionary<SemanticAtomId, SemanticEntityIdentity>();
        var terms = new Dictionary<SemanticAtomId, SemanticTerm>();
        var values = new Dictionary<SemanticAtomId, SemanticValue>();
        var provenances = new Dictionary<SemanticAtomId, SemanticProvenance>();
        var evidence = new SemanticFieldEvidence[rows.Count];
        using (SemanticPhysicalReadTelemetry.Start("evidence_domain_decoding"))
        {
            for (int index = 0; index < rows.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SemanticFieldAssertionRow row = rows[index];
                if (!identities.TryGetValue(row.Subject, out SemanticEntityIdentity? identity))
                {
                    identities.Add(row.Subject, identity = _atoms.DecodeIdentity(atoms[row.Subject]));
                }

                if (!terms.TryGetValue(row.Field, out SemanticTerm? term))
                {
                    terms.Add(row.Field, term = _atoms.DecodeTerm(atoms[row.Field]));
                }

                if (!values.TryGetValue(row.Value, out SemanticValue? value))
                {
                    values.Add(row.Value, value = _atoms.DecodeValue(atoms[row.Value]));
                }

                if (!provenances.TryGetValue(row.Provenance, out SemanticProvenance? provenance))
                {
                    provenances.Add(row.Provenance, provenance = _atoms.DecodeProvenance(atoms[row.Provenance]));
                }

                evidence[index] = new(row.AssertionId, identity, term, value, provenance,
                    row.BeginVersion, row.EndVersion);
            }
        }

        return evidence;
    }

    private async ValueTask<IReadOnlyList<IReadOnlyList<Guid>>> FindFieldAssertionsAsync(
        SemanticFieldAccessPaths.SemanticFieldSnapshot snapshot,
        IReadOnlyList<SemanticFieldEqualityQuery> queries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queries);
        SemanticAccessKey[] keys = queries.Select(query => FieldKey(query.Field, query.Value)).ToArray();
        IReadOnlyDictionary<SemanticAccessKey, IReadOnlyList<byte[]>> matches =
            await snapshot.FindEqualitiesAsync(keys, cancellationToken).ConfigureAwait(false);
        return keys.Select(key => matches.TryGetValue(key, out IReadOnlyList<byte[]>? values)
            ? (IReadOnlyList<Guid>)values.Select(value => _rows.DecodeField(value).AssertionId).ToArray()
            : Array.Empty<Guid>()).ToArray();
    }

    private async ValueTask<IReadOnlyList<Guid>> FindFieldAssertionsAsync(
        SemanticFieldAccessPaths.SemanticFieldSnapshot fields, SemanticTerm field, SemanticValue value,
        CancellationToken cancellationToken)
    {
        SemanticAccessKey equality = FieldKey(field, value);
        var cacheKey = (fields.EqualityGeneration, fields.EqualityMaximumVersion, equality);
        if (_fieldAssertionCache.TryGetValue(cacheKey, out IReadOnlyList<Guid>? cached))
        {
            return cached;
        }

        Guid[] result = (await fields.FindEqualityAsync(equality, cancellationToken).ConfigureAwait(false))
            .Select(encoded => _rows.DecodeField(encoded).AssertionId).ToArray();
        if (_fieldAssertionCache.Count + 1 > MaximumCachedFieldAssertionSets)
        {
            _fieldAssertionCache.Clear();
        }

        _fieldAssertionCache[cacheKey] = result;
        return result;
    }

    private async ValueTask<IReadOnlyList<IReadOnlyList<SemanticFieldEvidence>>> FindFieldEvidenceAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> shared,
        SemanticFieldAccessPaths.SemanticFieldSnapshot fields,
        IReadOnlyList<SemanticFieldEqualityQuery> queries, CancellationToken cancellationToken)
    {
        SemanticAccessKey[] keys = queries.Select(query => FieldKey(query.Field, query.Value)).ToArray();
        IReadOnlyDictionary<SemanticAccessKey, IReadOnlyList<byte[]>> matches =
            await fields.FindEqualitiesAsync(keys, cancellationToken).ConfigureAwait(false);
        SemanticFieldAssertionRow[][] rowGroups = keys.Select(key => matches.TryGetValue(key, out var values)
            ? values.Select(value => _rows.DecodeField(value)).ToArray() : []).ToArray();
        SemanticFieldAssertionRow[] uniqueRows = rowGroups.SelectMany(group => group)
            .DistinctBy(row => row.AssertionId).ToArray();
        IReadOnlyList<SemanticFieldEvidence> hydrated = await HydrateFieldRowsAsync(
            shared, uniqueRows, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<Guid, SemanticFieldEvidence> byId = hydrated.ToDictionary(item => item.AssertionId);
        return rowGroups.Select(group => (IReadOnlyList<SemanticFieldEvidence>)group
            .Select(row => byId[row.AssertionId]).ToArray()).ToArray();
    }

    private async ValueTask<IReadOnlyList<SemanticFieldCandidate>> FindFieldCandidatesAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> tree,
        SemanticFieldAccessPaths.SemanticFieldSnapshot fields, SemanticTerm field, SemanticValue value,
        CancellationToken cancellationToken)
    {
        SemanticAccessKey equality = FieldKey(field, value);
        if (_fieldCandidateCache.TryGetValue((tree.Generation, tree.MaximumVersion, equality), out var cached))
        {
            return cached;
        }

        SemanticFieldAssertionRow[] rows = (await fields.FindEqualityAsync(equality,
                cancellationToken).ConfigureAwait(false)).Select(encoded => _rows.DecodeField(encoded)).ToArray();
        IReadOnlyDictionary<SemanticAtomId, SemanticAtomRecord> atoms = await ReadAtomsAsync(tree,
            rows.Select(row => row.Subject).Distinct().ToArray(), cancellationToken).ConfigureAwait(false);
        SemanticFieldCandidate[] result = rows.Select(row => new SemanticFieldCandidate(DecodeIdentity(row.Subject, atoms),
            row.AssertionId, row.BeginVersion, row.EndVersion)).ToArray();
        if (_fieldCandidateCache.Count + 1 > MaximumCachedFieldCandidateSets)
        {
            _fieldCandidateCache.Clear();
        }

        _fieldCandidateCache[(tree.Generation, tree.MaximumVersion, equality)] = result;
        return result;
    }

    private async ValueTask<IReadOnlyList<SemanticRelationshipCandidate>> ReadCompactRelationshipCandidatesAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> tree,
        SemanticDirectionalRelationshipAccessPaths.SemanticDirectionalRelationshipSnapshot relationships,
        IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
        SemanticTerm relationship, CancellationToken cancellationToken)
    {
        SemanticEntityIdentity[] unique = entities.Distinct().ToArray();
        (string Atom, SemanticEntityIdentity Entity)[] requested = unique
            .Select(entity => (AtomKey(entity), entity)).ToArray();
        Guid relationshipAtom = Atom(relationship).Id.Value;
        var matches = new IReadOnlyList<SemanticDirectionalRelationshipAccessPaths.SemanticRelationshipAdjacencyValue>
            [requested.Length];
        var missing = new List<string>(requested.Length);
        for (int index = 0; index < requested.Length; index++)
        {
            if (_relationshipAdjacencyCache.TryGetValue(
                    (tree.Generation, tree.MaximumVersion, requested[index].Atom, direction, relationshipAtom),
                    out var cached))
            {
                matches[index] = cached;
            }
            else
            {
                missing.Add(requested[index].Atom);
            }
        }
        if (missing.Count != 0)
        {
            IReadOnlyDictionary<string,
                IReadOnlyList<SemanticDirectionalRelationshipAccessPaths.SemanticRelationshipAdjacencyValue>>
                loaded = await relationships.FindManyCandidatesAsync(missing, direction,
                    relationshipAtom, cancellationToken).ConfigureAwait(false);
            if (_relationshipAdjacencyCache.Count + missing.Count > MaximumCachedRelationshipAdjacencies)
            {
                _relationshipAdjacencyCache.Clear();
            }

            for (int index = 0; index < requested.Length; index++)
            {
                string entityAtom = requested[index].Atom;
                if (matches[index] is not null)
                {
                    continue;
                }

                IReadOnlyList<SemanticDirectionalRelationshipAccessPaths.SemanticRelationshipAdjacencyValue>
                    candidates = loaded[entityAtom];
                matches[index] = candidates;
                _relationshipAdjacencyCache[(tree.Generation, tree.MaximumVersion, entityAtom, direction,
                    relationshipAtom)] = candidates;
            }
        }
        SemanticAtomId[] relatedAtomIds = matches.SelectMany(item => item)
            .Select(item => new SemanticAtomId(item.RelatedEntityAtom)).Distinct().ToArray();
        var related = new Dictionary<Guid, SemanticEntityIdentity>(relatedAtomIds.Length);
        foreach (SemanticAtomId id in relatedAtomIds)
        {
            if (!_decodedIdentityCache.TryGetValue(id, out SemanticEntityIdentity? identity))
            {
                break;
            }

            related.Add(id.Value, identity);
        }
        if (related.Count != relatedAtomIds.Length)
        {
            related.Clear();
            IReadOnlyDictionary<SemanticAtomId, SemanticAtomRecord> atoms = await ReadAtomsAsync(
                tree, relatedAtomIds, cancellationToken).ConfigureAwait(false);
            foreach (SemanticAtomId id in relatedAtomIds)
            {
                related.Add(id.Value, DecodeIdentity(id, atoms));
            }
        }
        var result = new List<SemanticRelationshipCandidate>(matches.Sum(item => item.Count));
        for (int index = 0; index < matches.Length; index++)
        {
            foreach (SemanticDirectionalRelationshipAccessPaths.SemanticRelationshipAdjacencyValue item in matches[index])
            {
                result.Add(new(requested[index].Entity, related[item.RelatedEntityAtom], relationship,
                    item.AssertionId));
            }
        }
        return result;
    }

    private SemanticEntityIdentity DecodeIdentity(SemanticAtomId id,
        IReadOnlyDictionary<SemanticAtomId, SemanticAtomRecord> atoms)
    {
        if (_decodedIdentityCache.TryGetValue(id, out SemanticEntityIdentity? identity))
        {
            return identity;
        }

        identity = _atoms.DecodeIdentity(atoms[id]);
        if (_decodedIdentityCache.Count + 1 > MaximumCachedDecodedIdentities)
        {
            _decodedIdentityCache.Clear();
        }

        _decodedIdentityCache.TryAdd(id, identity);
        return identity;
    }

    public async ValueTask<IReadOnlyList<SemanticFieldEvidence>> ReadEntityFieldsAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, IReadOnlySet<SemanticTerm> properties,
        CancellationToken cancellationToken = default)
    {
        await using var shared = _shared.CaptureSnapshot();
        using var fields = _fields.CaptureSnapshot();
        return await ReadSubjectFieldsAsync(shared, fields, entities, properties, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<SemanticFieldEvidence>> ReadSubjectFieldsAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> shared,
        SemanticFieldAccessPaths.SemanticFieldSnapshot fields,
        IReadOnlyList<SemanticEntityIdentity> entities, IReadOnlySet<SemanticTerm> properties,
        CancellationToken token)
    {
        var wanted = properties.Select(property => Atom(property).Id).ToHashSet();
        var keys = entities.Distinct().Select(entity => new SemanticAccessKey(
            SemanticAccessFamily.SubjectField, AtomKey(entity), string.Empty)).ToArray();
        var subjects = await fields.FindSubjectsAsync(keys, token).ConfigureAwait(false);
        Guid[] ids = subjects.Values.SelectMany(values => values).Select(DecodeAssertion).Distinct().ToArray();
        var encoded = await fields.FindEvidenceAsync(ids, token).ConfigureAwait(false);
        var rows = new List<SemanticFieldAssertionRow>();
        foreach (Guid id in ids)
        {
            token.ThrowIfCancellationRequested();
            SemanticFieldAssertionRow row = _rows.DecodeField(encoded[id]);
            if (wanted.Contains(row.Field))
            {
                rows.Add(row);
            }
        }
        return await HydrateFieldRowsAsync(shared, rows, token).ConfigureAwait(false);
    }

    private async ValueTask<SemanticEvidenceSlice> ReadEntityEvidenceAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot,
        SemanticDirectionalRelationshipAccessPaths.SemanticDirectionalRelationshipSnapshot relationships,
        SemanticFieldAccessPaths.SemanticFieldSnapshot fields,
        IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entities);
        var fieldIds = new HashSet<Guid>();
        var relationshipIds = new HashSet<Guid>();
        SemanticEntityIdentity[] unique = entities.Distinct().ToArray();
        string[] identityAtoms = unique.Select(AtomKey).ToArray();
        SemanticAccessKey[] subjectKeys = identityAtoms.Select(atom => new SemanticAccessKey(
            SemanticAccessFamily.SubjectField, atom, string.Empty)).ToArray();
        IReadOnlyDictionary<SemanticAccessKey, IReadOnlyList<byte[]>> subjectFields =
            await fields.FindSubjectsAsync(subjectKeys, cancellationToken).ConfigureAwait(false);
        foreach (SemanticAccessKey key in subjectKeys)
        {
            if (subjectFields.TryGetValue(key, out IReadOnlyList<byte[]>? values))
            {
                fieldIds.UnionWith(values.Select(DecodeAssertion));
            }
        }

        IReadOnlyDictionary<string, IReadOnlyList<Guid>> outgoing = await relationships.FindManyAsync(
            identityAtoms, SemanticRelationshipDirection.Outgoing, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, IReadOnlyList<Guid>> incoming = await relationships.FindManyAsync(
            identityAtoms, SemanticRelationshipDirection.Incoming, cancellationToken).ConfigureAwait(false);
        foreach (string identityAtom in identityAtoms)
        {
            relationshipIds.UnionWith(outgoing[identityAtom]);
            relationshipIds.UnionWith(incoming[identityAtom]);
        }
        return new(await ReadFieldEvidenceAsync(snapshot, fields, fieldIds.ToArray(), cancellationToken).ConfigureAwait(false),
            await ReadRelationshipEvidenceAsync(snapshot, relationshipIds.ToArray(), cancellationToken)
                .ConfigureAwait(false));
    }

    private async ValueTask<SemanticEvidenceSlice> ReadOwnedEntityEvidenceAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot,
        SemanticDirectionalRelationshipAccessPaths.SemanticDirectionalRelationshipSnapshot relationships,
        SemanticFieldAccessPaths.SemanticFieldSnapshot fields,
        IReadOnlyList<SemanticEntityIdentity> subjects, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        SemanticEntityIdentity[] unique = subjects.Distinct().ToArray();
        string[] identityAtoms = unique.Select(AtomKey).ToArray();
        SemanticAccessKey[] subjectKeys = identityAtoms.Select(atom => new SemanticAccessKey(
            SemanticAccessFamily.SubjectField, atom, string.Empty)).ToArray();
        IReadOnlyDictionary<SemanticAccessKey, IReadOnlyList<byte[]>> subjectFields =
            await fields.FindSubjectsAsync(subjectKeys, cancellationToken).ConfigureAwait(false);
        Guid[] fieldIds = subjectKeys.SelectMany(key => subjectFields.TryGetValue(key, out var values)
            ? values.Select(DecodeAssertion) : []).Distinct().ToArray();
        IReadOnlyDictionary<string, IReadOnlyList<Guid>> outgoing = await relationships.FindManyAsync(
            identityAtoms, SemanticRelationshipDirection.Outgoing, cancellationToken).ConfigureAwait(false);
        Guid[] relationshipIds = identityAtoms.SelectMany(atom => outgoing[atom]).Distinct().ToArray();
        return new(await ReadFieldEvidenceAsync(snapshot, fields, fieldIds, cancellationToken).ConfigureAwait(false),
            await ReadRelationshipEvidenceAsync(snapshot, relationshipIds, cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<IReadOnlyDictionary<SemanticAtomId, SemanticAtomRecord>> ReadAtomsAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot,
        IReadOnlyList<SemanticAtomId> atomIds, CancellationToken cancellationToken)
    {
        SemanticAtomId[] ids = atomIds.Distinct().ToArray();
        var result = new Dictionary<SemanticAtomId, SemanticAtomRecord>();
        var missingAtoms = new List<SemanticAtomId>();
        foreach (SemanticAtomId id in ids)
        {
            if (_atomCache.TryGet(id, out SemanticAtomRecord? atom))
            {
                result.Add(id, atom!);
            }
            else
            {
                missingAtoms.Add(id);
            }
        }
        SemanticAtomId[] missing = missingAtoms.ToArray();
        if (missing.Length == 0)
        {
            return result;
        }

        SemanticAccessKey[] directoryKeys = missing.Select(AtomDirectoryKey).ToArray();
        IReadOnlyDictionary<SemanticAccessKey, byte[]> directories;
        using (SemanticPhysicalReadTelemetry.Start("atom_directory_read"))
        {
            using (var work = AppendMergeLookupTelemetry.Begin(SemanticLookupRoles.AtomDirectory, directoryKeys.Length))
            {
                directories = await snapshot.FindUniqueAsync(directoryKeys, cancellationToken).ConfigureAwait(false);
                work?.Complete();
            }
        }
        var chunks = new Dictionary<SemanticAtomId, List<SemanticAtomChunk>>();
        var payloadOwners = new Dictionary<SemanticAccessKey, SemanticAtomChunkKey>();
        foreach ((SemanticAtomId id, SemanticAccessKey key) in missing.Zip(directoryKeys))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!directories.TryGetValue(key, out byte[]? bytes))
            {
                throw new InvalidDataException($"Semantic atom '{id.Value}' has no directory entry.");
            }

            AtomDirectoryEntry directory = DecodeAtomDirectory(bytes);
            chunks[id] = [];
            for (int sequence = 0; sequence < directory.ChunkCount; sequence++)
            {
                payloadOwners.Add(AtomPayloadKey(directory.Ordinal, sequence), new(id, sequence));
            }
        }
        SemanticAccessKey[] payloadKeys = payloadOwners.Keys.ToArray();
        IReadOnlyDictionary<SemanticAccessKey, byte[]> payloads;
        using (SemanticPhysicalReadTelemetry.Start("atom_payload_read"))
        {
            using (var work = AppendMergeLookupTelemetry.Begin(SemanticLookupRoles.AtomPayload, payloadKeys.Length))
            {
                payloads = await snapshot.FindUniqueAsync(payloadKeys, cancellationToken).ConfigureAwait(false);
                work?.Complete();
            }
        }
        using (SemanticPhysicalReadTelemetry.Start("atom_reassemble_cache"))
        {
            foreach (SemanticAccessKey key in payloadKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!payloads.TryGetValue(key, out byte[]? bytes))
                {
                    throw new InvalidDataException("A semantic atom payload chunk is missing.");
                }

                SemanticAtomChunkKey owner = payloadOwners[key];
                chunks[owner.AtomId].Add(_atomStorage.Decode(owner, bytes));
            }
            foreach ((SemanticAtomId id, List<SemanticAtomChunk> value) in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SemanticAtomRecord atom = _atomStorage.Reassemble(
                    value.OrderBy(chunk => chunk.Key.Sequence).ToArray());
                _atomCache.Add(atom);
                result.Add(id, atom);
            }
        }
        return result;
    }

    private readonly record struct AtomDirectoryEntry(long Ordinal, int ChunkCount);
}
