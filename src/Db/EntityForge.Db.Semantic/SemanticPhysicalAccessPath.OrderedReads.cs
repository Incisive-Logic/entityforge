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
    public async ValueTask<SemanticOrderedEntityPage> FindOrderedStringPageAsync(
        SemanticTerm concept, SemanticTerm field, IReadOnlyList<SemanticEntityIdentity>? candidates,
        SemanticOrderValueSelection selection, SemanticOrderedEntityPosition? after,
        int pageSize, DatabaseVersion snapshotVersion, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        if (candidates is { Count: 0 })
        {
            return new([], null, 0);
        }

        HashSet<SemanticEntityIdentity>? allowed = candidates?.ToHashSet();
        string term = OrderedTermKey(concept, field);
        SemanticAccessKey lower = new(SemanticAccessFamily.EntityStringOrder, term,
            after is null ? string.Empty : EncodeOrderedValue(after.Value, after.Identity));
        await using AppendMergeSnapshot<SemanticAccessKey, byte[]> ordered = _ranges.CaptureSnapshot();
        await using AppendMergeSnapshot<SemanticAccessKey, byte[]> shared = _shared.CaptureSnapshot();
        using SemanticFieldAccessPaths.SemanticFieldSnapshot fields = _fields.CaptureSnapshot();
        AppendMergeCursor<SemanticAccessKey, byte[]>? cursor = null;
        var accepted = new List<(SemanticEntityIdentity Entity, SemanticOrderedEntityPosition Position)>();
        var acceptedSubjects = new HashSet<SemanticEntityIdentity>();
        long scanned = 0;
        bool exhausted = false;
        int readSize = Math.Max(32, checked(pageSize * 4));
        while (accepted.Count <= pageSize && !exhausted)
        {
            AppendMergePage<SemanticAccessKey, byte[]> physical = await ordered.ReadPageAsync(readSize,
                OrderedIndexRange<SemanticAccessKey>.From(lower), cursor: cursor,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            cursor = physical.NextCursor;
            exhausted = cursor is null;
            var entries = new List<(KeyValuePair<SemanticAccessKey, byte[]> Entry, string Value, string Identity)>();
            foreach (KeyValuePair<SemanticAccessKey, byte[]> item in physical.Items)
            {
                if (item.Key.Family != SemanticAccessFamily.EntityStringOrder || item.Key.Term != term)
                {
                    exhausted = true;
                    break;
                }
                (string value, string identity) = DecodeOrderedValue(item.Key.Value);
                scanned++;
                if (after is not null && ComparePosition(value, identity, after) <= 0)
                {
                    continue;
                }

                entries.Add((item, value, identity));
            }
            if (entries.Count == 0)
            {
                continue;
            }

            IReadOnlyList<SemanticFieldEvidence> evidence = await ReadFieldEvidenceAsync(shared, fields,
                entries.Select(item => DecodeAssertion(item.Entry.Value)).ToArray(), cancellationToken)
                .ConfigureAwait(false);
            SemanticEntityIdentity[] subjects = evidence.Where(item => Visible(item, snapshotVersion) &&
                    item.Subject.Concept == concept && (allowed is null || allowed.Contains(item.Subject)))
                .Select(item => item.Subject).Distinct().ToArray();
            if (subjects.Length == 0)
            {
                continue;
            }

            var subjectFields = await ReadSubjectFieldsAsync(shared, fields, subjects, new HashSet<SemanticTerm> { field },
                cancellationToken).ConfigureAwait(false);
            Dictionary<SemanticEntityIdentity, string> selectedValues = subjectFields
                .Where(item => Visible(item, snapshotVersion) && item.Field == field &&
                    item.Value is StringSemanticValue && item.Subject.Concept == concept &&
                    (allowed is null || allowed.Contains(item.Subject)))
                .GroupBy(item => item.Subject).ToDictionary(group => group.Key, group =>
                    selection is SemanticOrderValueSelection.Minimum
                        ? group.Select(item => ((StringSemanticValue)item.Value).Value).Min(StringComparer.Ordinal)!
                        : group.Select(item => ((StringSemanticValue)item.Value).Value).Max(StringComparer.Ordinal)!);
            var visibleEvidenceByIdentity = evidence.Where(item => Visible(item, snapshotVersion) &&
                    item.Subject.Concept == concept && (allowed is null || allowed.Contains(item.Subject)))
                .DistinctBy(item => OrderIdentity(item.Subject)).ToDictionary(item => OrderIdentity(item.Subject));
            // A subject can have several indexed values. Emit it only at the selected
            // minimum/maximum and only once, preserving the physical index order.
            foreach ((_, string value, string identity) in entries)
            {
                SemanticFieldEvidence? assertion = visibleEvidenceByIdentity.GetValueOrDefault(identity);
                if (assertion is null || !selectedValues.TryGetValue(assertion.Subject, out string? chosen) ||
                    !StringComparer.Ordinal.Equals(value, chosen) ||
                    !acceptedSubjects.Add(assertion.Subject))
                {
                    continue;
                }

                accepted.Add((assertion.Subject, new(value, identity)));
                if (accepted.Count > pageSize)
                {
                    break;
                }
            }
        }
        SemanticOrderedEntityPosition? next = accepted.Count > pageSize ? accepted[pageSize - 1].Position : null;
        return new(accepted.Take(pageSize).Select(item => item.Entity).ToArray(), next, scanned);
    }

    public async ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
        CancellationToken cancellationToken = default)
    {
        using SemanticDirectionalRelationshipAccessPaths.SemanticDirectionalRelationshipSnapshot snapshot =
            _relationships.CaptureSnapshot();
        return await FindRelationshipAssertionsAsync(snapshot, entities, direction, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
        SemanticTerm relationship, CancellationToken cancellationToken = default)
    {
        using SemanticDirectionalRelationshipAccessPaths.SemanticDirectionalRelationshipSnapshot snapshot =
            _relationships.CaptureSnapshot();
        return await FindRelationshipAssertionsAsync(snapshot, entities, direction, relationship,
            cancellationToken).ConfigureAwait(false);
    }
}
