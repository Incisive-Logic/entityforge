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
    public async ValueTask<bool> ContainsIdentityAsync(SemanticEntityIdentity identity,
        CancellationToken cancellationToken = default)
    {
        await using AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot = _shared.CaptureSnapshot();
        return await ContainsIdentityAsync(snapshot, identity, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> ContainsIdentityAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot, SemanticEntityIdentity identity,
        CancellationToken cancellationToken)
    {
        string identityAtom = AtomKey(identity);
        var key = (snapshot.Generation, snapshot.MaximumVersion, identityAtom);
        if (_identityMembershipCache.TryGetValue(key, out bool cached))
        {
            return cached;
        }

        bool exists = (await snapshot.FindAsync(
            new(SemanticAccessFamily.Identity, string.Empty, identityAtom), cancellationToken)
            .ConfigureAwait(false)).Count != 0;
        if (_identityMembershipCache.Count + 1 > MaximumCachedIdentityMemberships)
        {
            _identityMembershipCache.Clear();
        }

        _identityMembershipCache[key] = exists;
        return exists;
    }

    public async ValueTask<IReadOnlyList<SemanticEntityIdentity>> FindConceptMembersAsync(SemanticTerm concept,
        int? maximumResults = null, CancellationToken cancellationToken = default)
    {
        await using AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot = _concepts.CaptureSnapshot();
        return (await snapshot.FindAsync(ConceptKey(concept), cancellationToken, maximumResults).ConfigureAwait(false))
            .Select(DecodeIdentity).ToArray();
    }

    public async ValueTask<long> CountConceptMembersAsync(SemanticTerm concept,
        CancellationToken cancellationToken = default)
    {
        await using AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot = _concepts.CaptureSnapshot();
        return (await snapshot.FindAsync(ConceptKey(concept), cancellationToken).ConfigureAwait(false)).Count;
    }

    public async ValueTask<IReadOnlyList<Guid>> FindFieldAssertionsAsync(SemanticTerm field, SemanticValue value,
        CancellationToken cancellationToken = default)
    {
        using SemanticFieldAccessPaths.SemanticFieldSnapshot snapshot = _fields.CaptureSnapshot();
        return await FindFieldAssertionsAsync(snapshot, field, value, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<SemanticFieldCandidate>> FindFieldCandidatesAsync(
        SemanticTerm field, SemanticValue value, CancellationToken cancellationToken = default)
    {
        await using AppendMergeSnapshot<SemanticAccessKey, byte[]> tree = _shared.CaptureSnapshot();
        using SemanticFieldAccessPaths.SemanticFieldSnapshot fields = _fields.CaptureSnapshot();
        return await FindFieldCandidatesAsync(tree, fields, field, value, cancellationToken)
            .ConfigureAwait(false);
    }

    public PhysicalMaintenanceTarget GetMaintenanceTarget()
    {
        if (_ranges.GetMaintenanceStatus().NeedsMaintenance)
        {
            return PhysicalMaintenanceTarget.Ordered;
        }

        if (_shared.GetMaintenanceStatus().NeedsMaintenance)
        {
            return PhysicalMaintenanceTarget.Shared;
        }

        if (_concepts.GetMaintenanceStatus().NeedsMaintenance)
        {
            return PhysicalMaintenanceTarget.Concept;
        }

        PhysicalMaintenanceTarget[] remaining =
        [PhysicalMaintenanceTarget.OutgoingRelationship,
            PhysicalMaintenanceTarget.IncomingRelationship,
            PhysicalMaintenanceTarget.FieldEvidence,
            PhysicalMaintenanceTarget.FieldEquality,
            PhysicalMaintenanceTarget.SubjectField];
        return remaining.First(target => target switch
        {
            PhysicalMaintenanceTarget.OutgoingRelationship => _relationships.MaintenanceStatuses[0].NeedsMaintenance,
            PhysicalMaintenanceTarget.IncomingRelationship => _relationships.MaintenanceStatuses[1].NeedsMaintenance,
            PhysicalMaintenanceTarget.FieldEvidence => _fields.MaintenanceStatuses[0].NeedsMaintenance,
            PhysicalMaintenanceTarget.FieldEquality => _fields.MaintenanceStatuses[1].NeedsMaintenance,
            _ => _fields.MaintenanceStatuses[2].NeedsMaintenance
        });
    }

    public async ValueTask<IReadOnlyList<IReadOnlyList<Guid>>> FindFieldAssertionsAsync(
        IReadOnlyList<SemanticFieldEqualityQuery> queries, CancellationToken cancellationToken = default)
    {
        using SemanticFieldAccessPaths.SemanticFieldSnapshot snapshot = _fields.CaptureSnapshot();
        return await FindFieldAssertionsAsync(snapshot, queries, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<Guid>> FindStringAssertionsAsync(SemanticTerm field,
        string? lowerInclusive, string? upperInclusive, CancellationToken cancellationToken = default)
    {
        await using AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot = _ranges.CaptureSnapshot();
        return await FindStringAssertionsAsync(snapshot, field, lowerInclusive, upperInclusive,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<IReadOnlyList<SemanticFieldEvidence>>> FindFieldEvidenceAsync(
        IReadOnlyList<SemanticFieldEqualityQuery> queries, CancellationToken cancellationToken = default)
    {
        await using AppendMergeSnapshot<SemanticAccessKey, byte[]> shared = _shared.CaptureSnapshot();
        using SemanticFieldAccessPaths.SemanticFieldSnapshot fields = _fields.CaptureSnapshot();
        return await FindFieldEvidenceAsync(shared, fields, queries, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<SemanticFieldEvidence>> ScanFieldEvidenceAsync(
        SemanticTerm field, CancellationToken cancellationToken = default)
    {
        await using AppendMergeSnapshot<SemanticAccessKey, byte[]> shared = _shared.CaptureSnapshot();
        using SemanticFieldAccessPaths.SemanticFieldSnapshot fields = _fields.CaptureSnapshot();
        return await ScanFieldEvidenceAsync(shared, fields, field, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<Guid>> FindStringAssertionsAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot, SemanticTerm field,
        string? lowerInclusive, string? upperInclusive, CancellationToken cancellationToken)
    {
        const int readSize = 512;
        const string maximumIdentity = "\uffff";
        string term = AtomKey(field);
        SemanticAccessKey lower = new(SemanticAccessFamily.StringRange, term,
            EncodeOrderedValue(lowerInclusive ?? string.Empty, string.Empty));
        SemanticAccessKey upper = new(SemanticAccessFamily.StringRange, term,
            EncodeOrderedValue(upperInclusive ?? maximumIdentity, maximumIdentity));
        OrderedIndexRange<SemanticAccessKey> requested = OrderedIndexRange<SemanticAccessKey>.Between(lower, upper);
        AppendMergeCursor<SemanticAccessKey, byte[]>? cursor = null;
        var result = new List<Guid>();
        do
        {
            AppendMergePage<SemanticAccessKey, byte[]> page = await snapshot.ReadPageAsync(
                readSize, requested, cursor: cursor, cancellationToken: cancellationToken).ConfigureAwait(false);
            result.AddRange(page.Items.Select(item => DecodeAssertion(item.Value)));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return result;
    }

    public async ValueTask<IReadOnlyList<SemanticFieldEvidence>> ReadFieldEvidenceAsync(
        IReadOnlyList<Guid> assertionIds, CancellationToken cancellationToken = default)
    {
        await using AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot = _shared.CaptureSnapshot();
        using SemanticFieldAccessPaths.SemanticFieldSnapshot fields = _fields.CaptureSnapshot();
        return await ReadFieldEvidenceAsync(snapshot, fields, assertionIds, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(
        SemanticEntityIdentity entity, SemanticRelationshipDirection direction, SemanticTerm? relationship,
        CancellationToken cancellationToken = default)
    {
        using SemanticDirectionalRelationshipAccessPaths.SemanticDirectionalRelationshipSnapshot snapshot =
            _relationships.CaptureSnapshot();
        return await FindRelationshipAssertionsAsync(snapshot, entity, direction, relationship, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> ReadRelationshipEvidenceAsync(
        IReadOnlyList<Guid> assertionIds, CancellationToken cancellationToken = default)
    {
        await using AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot = _shared.CaptureSnapshot();
        return await ReadRelationshipEvidenceAsync(snapshot, assertionIds, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SemanticEvidenceSlice> ReadEntityEvidenceAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken = default)
    {
        await using AppendMergeSnapshot<SemanticAccessKey, byte[]> tree = _shared.CaptureSnapshot();
        using SemanticDirectionalRelationshipAccessPaths.SemanticDirectionalRelationshipSnapshot relationships =
            _relationships.CaptureSnapshot();
        using SemanticFieldAccessPaths.SemanticFieldSnapshot fields = _fields.CaptureSnapshot();
        return await ReadEntityEvidenceAsync(tree, relationships, fields, entities, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<SemanticEntitySupport>> ReadEntitySupportAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken = default)
    {
        await using AppendMergeSnapshot<SemanticAccessKey, byte[]> tree = _shared.CaptureSnapshot();
        using SemanticDirectionalRelationshipAccessPaths.SemanticDirectionalRelationshipSnapshot relationships =
            _relationships.CaptureSnapshot();
        using SemanticFieldAccessPaths.SemanticFieldSnapshot fields = _fields.CaptureSnapshot();
        return await _supportReader.ReadAsync(tree, relationships, fields, entities, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<SemanticRelationshipCandidate>> FindRelationshipCandidatesAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
        SemanticTerm relationship, CancellationToken cancellationToken = default)
    {
        await using AppendMergeSnapshot<SemanticAccessKey, byte[]> tree = _shared.CaptureSnapshot();
        using SemanticDirectionalRelationshipAccessPaths.SemanticDirectionalRelationshipSnapshot relationships =
            _relationships.CaptureSnapshot();
        return await ReadCompactRelationshipCandidatesAsync(tree, relationships, entities, direction,
            relationship, cancellationToken).ConfigureAwait(false);
    }
}
