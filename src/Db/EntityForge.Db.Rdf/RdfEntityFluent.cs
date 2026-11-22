using System.Collections;
using System.Globalization;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Query.Projection;
using EntityForge.Query.Values;
using EntityForge.Semantics;

namespace EntityForge.Db.Rdf;

using static EntityForge.Db.Rdf.RdfEntityMaterialization;

/// <summary>Advanced embedded mapped planning and synchronous reads.</summary>
public static class RdfEntityFluentExtensions
{
    internal static RdfEntityWriteSet PrepareSaveEntity<T>(IRdfDatabase database, T entity,
        RdfEntityMap<T> map, RdfSnapshotLease observation, DatabaseVersion readVersion,
        string? expectedConcurrencyToken = null) => PrepareSaveEntity(new LocalRdfMappingSnapshot(database, observation),
            entity, map, readVersion, expectedConcurrencyToken);

    internal static RdfEntityWriteSet PrepareSaveEntity<T>(IRdfMappingSnapshot snapshot, T entity,
        RdfEntityMap<T> map, DatabaseVersion readVersion, string? expectedConcurrencyToken = null)
    {
        RdfIri subject = Subject(map, entity);
        var desired = new List<RdfQuad> { new(subject, TypePredicate, new RdfIri(map.Class.AbsoluteUri)) };
        foreach (EntityMemberMap member in map.EntityMap.Members.Where(item => item.Role is not EntityMemberRole.Identity))
        {
            object? value = EntityMappedValueValidation.Validate(member, member.Read(entity!));
            if (value is null)
            {
                continue;
            }

            IEnumerable items = value is string or byte[]? new object[] { value } :
                value as IEnumerable ?? new object[] { value };
            foreach (object item in items)
            {
                desired.Add(new(subject, new RdfIri(map.MemberBindings[member.Name].AbsoluteUri),
                    member.Role is EntityMemberRole.Relationship ? ToResource(item) : ToLiteral(item)));
            }
        }
        IReadOnlyList<RdfQuad> subjectState = snapshot.ReadDefaultSubject(subject);
        IReadOnlySet<RdfIri> ownedPredicates = map.EntityMap.Members.Where(member => member.Role != EntityMemberRole.Identity).Select(member => new RdfIri(map.MemberBindings[member.Name].AbsoluteUri))
            .Append(TypePredicate).ToHashSet();
        RdfQuad[] current = subjectState.Where(quad => ownedPredicates.Contains(quad.Predicate) &&
            (quad.Predicate != TypePredicate || quad.Object.Equals(new RdfIri(map.Class.AbsoluteUri)))).ToArray();
        RdfMutation[] mutations = current.Except(desired).Select(quad => (RdfMutation)new RetractRdfQuad(quad))
            .Concat(desired.Except(current).Select(quad => (RdfMutation)new AssertRdfQuad(quad))).ToArray();
        RdfReadPredicate read = expectedConcurrencyToken is null
            ? RdfSubjectStateRead.Capture(subject, graph: null, matchDefaultGraphOnly: true, subjectState)
            : new RdfSubjectStateRead(subject, null, true, expectedConcurrencyToken);
        return new(readVersion, mutations, [read]);
    }

    internal static RdfEntityWriteSet PrepareDeleteEntity<T>(IRdfDatabase database,
        RdfEntityMap<T> map, object identity, RdfSnapshotLease observation, DatabaseVersion readVersion,
        string? expectedConcurrencyToken = null) => PrepareDeleteEntity(new LocalRdfMappingSnapshot(database, observation),
            map, identity, readVersion, expectedConcurrencyToken);

    internal static RdfEntityWriteSet PrepareDeleteEntity<T>(IRdfMappingSnapshot snapshot,
        RdfEntityMap<T> map, object identity, DatabaseVersion readVersion, string? expectedConcurrencyToken = null)
    {
        RdfIri subject = Subject(map, identity);
        IReadOnlyList<RdfQuad> subjectState = snapshot.ReadDefaultSubject(subject);
        IReadOnlySet<RdfIri> ownedPredicates = map.EntityMap.Members.Where(member => member.Role != EntityMemberRole.Identity).Select(member => new RdfIri(map.MemberBindings[member.Name].AbsoluteUri))
            .Append(TypePredicate).ToHashSet();
        RdfMutation[] mutations = subjectState.Where(quad => ownedPredicates.Contains(quad.Predicate) &&
                (quad.Predicate != TypePredicate || quad.Object.Equals(new RdfIri(map.Class.AbsoluteUri))))
            .Select(quad => (RdfMutation)new RetractRdfQuad(quad)).ToArray();
        RdfReadPredicate read = expectedConcurrencyToken is null
            ? RdfSubjectStateRead.Capture(subject, null, true, subjectState)
            : new RdfSubjectStateRead(subject, null, true, expectedConcurrencyToken);
        return new(readVersion, mutations, [read]);
    }

    internal sealed record RdfEntityWriteSet(DatabaseVersion ReadVersion,
        IReadOnlyList<RdfMutation> Mutations, IReadOnlyList<RdfReadPredicate> Reads);

    /// <summary>Asynchronously finds one mapped RDF entity.</summary>
    public static ValueTask<T?> FindEntityAsync<T>(this IRdfDatabase database, RdfEntityMap<T> map,
        object identity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(database.GetEntity(map, identity));
    }

    internal static EntityReadResult<T>? ReadEntity<T>(this IRdfDatabase database,
        RdfEntityMap<T> map, object identity)
    {
        using RdfSnapshotLease snapshot = database.AcquireSnapshot();
        T? value = database.GetEntity(map, identity, snapshot);
        if (value is null)
        {
            return null;
        }

        RdfIri subject = Subject(map, identity);
        IReadOnlyList<RdfQuad> state = database.Find(
            new RdfQuadPattern(Subject: subject, MatchDefaultGraphOnly: true), snapshot);
        string token = RdfSubjectStateRead.Capture(subject, null, true, state).ExpectedFingerprint;
        return new(value, snapshot.Version, token);
    }

    /// <summary>Finds and materialises one mapped POCO by its declared RDF subject identity.</summary>
    public static T? GetEntity<T>(this IRdfDatabase database, RdfEntityMap<T> map, object identity,
        RdfSnapshotLease? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(identity);
        using var ownedSnapshot = snapshot is null ? database.AcquireSnapshot() : null;
        return MaterializeEntity(new LocalRdfMappingSnapshot(database, snapshot ?? ownedSnapshot!), map, identity);
    }

    /// <summary>Projects one mapped RDF subject into the profile-neutral entity document model.</summary>
    public static EntityProjection? ProjectEntity<T>(this IRdfDatabase database, RdfEntityMap<T> map,
        object identity, RdfSnapshotLease? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(identity);
        using var ownedSnapshot = snapshot is null ? database.AcquireSnapshot() : null;
        return RdfEntityMaterialization.ProjectEntity(new LocalRdfMappingSnapshot(database, snapshot ?? ownedSnapshot!), map, identity);
    }
}
