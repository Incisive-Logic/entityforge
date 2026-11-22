using System.Collections.Immutable;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Core;

/// <summary>Associates a source owner with its source partition and provenance.</summary>
/// <param name="SourcePartition">The source partition containing the owner.</param>
/// <param name="Provenance">The provenance retained for this source contribution.</param>
public sealed record SourceMembership(Uri SourcePartition, SemanticProvenance Provenance);

internal sealed record SourceMembershipMutation(SourceOwner Owner, Uri? SourcePartition,
    SemanticProvenance? Provenance)
{
    public bool IsRemoval => SourcePartition is null;

    public static SourceMembershipMutation Remove(SourceOwner owner) => new(owner, null, null);

    public static SourceMembershipMutation Set(SourceOwner owner, Uri partition,
        SemanticProvenance provenance) => new(owner, partition, provenance);
}

/// <summary>
/// Immutable source ownership index with direct owner lookup and partition-local ordered traversal.
/// Normal snapshot reconciliation therefore scales with the addressed partition, not the database.
/// </summary>
public sealed class SourceMembershipIndex
{
    private readonly ImmutableDictionary<SourceOwner, SourceMembership> _byOwner;
    private readonly ImmutableDictionary<SemanticEntityIdentity, ImmutableDictionary<SourceOwner, SourceMembership>> _bySubject;
    private readonly ImmutableDictionary<SourcePartitionKey,
        ImmutableSortedDictionary<SourceOwner, SemanticProvenance>> _byPartition;

    private SourceMembershipIndex(
        ImmutableDictionary<SourceOwner, SourceMembership> byOwner,
        ImmutableDictionary<SemanticEntityIdentity, ImmutableDictionary<SourceOwner, SourceMembership>> bySubject,
        ImmutableDictionary<SourcePartitionKey,
            ImmutableSortedDictionary<SourceOwner, SemanticProvenance>> byPartition)
    {
        _byOwner = byOwner;
        _bySubject = bySubject;
        _byPartition = byPartition;
    }

    /// <summary>Gets an immutable index without source owners.</summary>
    public static SourceMembershipIndex Empty
    {
        get;
    }
    = new(
        ImmutableDictionary<SourceOwner, SourceMembership>.Empty,
        ImmutableDictionary<SemanticEntityIdentity, ImmutableDictionary<SourceOwner, SourceMembership>>.Empty,
        ImmutableDictionary<SourcePartitionKey,
            ImmutableSortedDictionary<SourceOwner, SemanticProvenance>>.Empty);

    /// <summary>Gets the number of indexed source owners.</summary>
    public int Count => _byOwner.Count;

    /// <summary>Enumerates owners with their partition and provenance.</summary>
    public IEnumerable<KeyValuePair<SourceOwner, SourceMembership>> Entries => _byOwner;

    internal bool TryGetMembership(SourceOwner owner, out SourceMembership? membership)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return _byOwner.TryGetValue(owner, out membership);
    }

    /// <summary>Checks whether any source owner contributes membership for the subject.</summary>
    public bool ContainsSubject(SemanticEntityIdentity subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return _bySubject.ContainsKey(subject);
    }

    /// <summary>Returns the ordered owners and provenance for one source-system partition, or an empty view when absent.</summary>
    public IReadOnlyDictionary<SourceOwner, SemanticProvenance> GetPartition(
        Uri sourceSystem, Uri sourcePartition)
    {
        ArgumentNullException.ThrowIfNull(sourceSystem);
        ArgumentNullException.ThrowIfNull(sourcePartition);
        return _byPartition.GetValueOrDefault(new(sourceSystem, sourcePartition)) ??
            ImmutableSortedDictionary.Create<SourceOwner, SemanticProvenance>(SourceOwnerComparer.Instance);
    }

    /// <summary>Returns a new index with the owner's membership set, updating partition and subject indexes consistently.</summary>
    public SourceMembershipIndex SetItem(
        SourceOwner owner, Uri sourcePartition, SemanticProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(sourcePartition);
        ArgumentNullException.ThrowIfNull(provenance);
        ImmutableDictionary<SourceOwner, SourceMembership> byOwner = _byOwner;
        ImmutableDictionary<SemanticEntityIdentity, ImmutableDictionary<SourceOwner, SourceMembership>> bySubject = _bySubject;
        ImmutableDictionary<SourcePartitionKey,
            ImmutableSortedDictionary<SourceOwner, SemanticProvenance>> byPartition = _byPartition;
        if (byOwner.TryGetValue(owner, out SourceMembership? existing) &&
            existing.SourcePartition != sourcePartition)
        {
            byPartition = RemoveFromPartition(byPartition,
                new(owner.SourceSystem, existing.SourcePartition), owner);
        }

        SourcePartitionKey key = new(owner.SourceSystem, sourcePartition);
        ImmutableSortedDictionary<SourceOwner, SemanticProvenance> partition =
            byPartition.GetValueOrDefault(key) ??
            ImmutableSortedDictionary.Create<SourceOwner, SemanticProvenance>(SourceOwnerComparer.Instance);
        bySubject = SetSubject(bySubject, owner, new(sourcePartition, provenance));
        return new(byOwner.SetItem(owner, new(sourcePartition, provenance)), bySubject,
            byPartition.SetItem(key, partition.SetItem(owner, provenance)));
    }

    /// <summary>Returns a new index without the specified owner; returns the same index when absent.</summary>
    public SourceMembershipIndex Remove(SourceOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!_byOwner.TryGetValue(owner, out SourceMembership? existing))
        {
            return this;
        }

        return new(_byOwner.Remove(owner), RemoveSubject(_bySubject, owner), RemoveFromPartition(_byPartition,
            new(owner.SourceSystem, existing.SourcePartition), owner));
    }

    internal SourceMembershipIndex Apply(IReadOnlyList<SourceMembershipMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        if (mutations.Count == 0)
        {
            return this;
        }

        ImmutableDictionary<SourceOwner, SourceMembership>.Builder byOwner = _byOwner.ToBuilder();
        ImmutableDictionary<SemanticEntityIdentity, ImmutableDictionary<SourceOwner, SourceMembership>>.Builder bySubject = _bySubject.ToBuilder();
        ImmutableDictionary<SourcePartitionKey,
            ImmutableSortedDictionary<SourceOwner, SemanticProvenance>>.Builder byPartition =
            _byPartition.ToBuilder();
        var touchedPartitions = new Dictionary<SourcePartitionKey,
            ImmutableSortedDictionary<SourceOwner, SemanticProvenance>.Builder>();

        ImmutableSortedDictionary<SourceOwner, SemanticProvenance>.Builder Partition(
            SourcePartitionKey key)
        {
            if (touchedPartitions.TryGetValue(key, out var existing))
            {
                return existing;
            }

            ImmutableSortedDictionary<SourceOwner, SemanticProvenance>.Builder builder =
                (byPartition.GetValueOrDefault(key) ??
                 ImmutableSortedDictionary.Create<SourceOwner, SemanticProvenance>(
                     SourceOwnerComparer.Instance)).ToBuilder();
            touchedPartitions.Add(key, builder);
            return builder;
        }

        foreach (SourceMembershipMutation mutation in mutations)
        {
            SourceOwner owner = mutation.Owner;
            if (byOwner.TryGetValue(owner, out SourceMembership? existing) &&
                (mutation.IsRemoval || existing.SourcePartition != mutation.SourcePartition))
            {
                Partition(new(owner.SourceSystem, existing.SourcePartition)).Remove(owner);
            }

            if (mutation.IsRemoval)
            {
                if (byOwner.Remove(owner))
                {
                    RemoveSubject(bySubject, owner);
                }

                continue;
            }
            Uri partition = mutation.SourcePartition!;
            SemanticProvenance provenance = mutation.Provenance!;
            bySubject[owner.Subject] = (bySubject.GetValueOrDefault(owner.Subject) ?? ImmutableDictionary<SourceOwner, SourceMembership>.Empty).SetItem(owner, new(partition, provenance));
            byOwner[owner] = new(partition, provenance);
            Partition(new(owner.SourceSystem, partition))[owner] = provenance;
        }
        foreach ((SourcePartitionKey key,
                  ImmutableSortedDictionary<SourceOwner, SemanticProvenance>.Builder partition) in touchedPartitions)
        {
            if (partition.Count == 0)
            {
                byPartition.Remove(key);
            }
            else
            {
                byPartition[key] = partition.ToImmutable();
            }
        }

        return new(byOwner.ToImmutable(), bySubject.ToImmutable(), byPartition.ToImmutable());
    }

    /// <summary>Returns the subjects represented in the selected source membership scope.</summary>
    public IEnumerable<KeyValuePair<SourceOwner, SourceMembership>> GetSubjects(
        IEnumerable<SemanticEntityIdentity> subjects) => subjects.Distinct()
        .SelectMany(subject => _bySubject.GetValueOrDefault(subject) ??
            ImmutableDictionary<SourceOwner, SourceMembership>.Empty);

    private static ImmutableDictionary<SemanticEntityIdentity, ImmutableDictionary<SourceOwner, SourceMembership>> SetSubject(
        ImmutableDictionary<SemanticEntityIdentity, ImmutableDictionary<SourceOwner, SourceMembership>> subjects,
        SourceOwner owner, SourceMembership membership) => subjects.SetItem(owner.Subject,
            (subjects.GetValueOrDefault(owner.Subject) ?? ImmutableDictionary<SourceOwner, SourceMembership>.Empty)
                .SetItem(owner, membership));

    private static ImmutableDictionary<SemanticEntityIdentity, ImmutableDictionary<SourceOwner, SourceMembership>> RemoveSubject(
        ImmutableDictionary<SemanticEntityIdentity, ImmutableDictionary<SourceOwner, SourceMembership>> subjects, SourceOwner owner)
    {
        var members = subjects[owner.Subject].Remove(owner);
        return members.Count == 0 ? subjects.Remove(owner.Subject) : subjects.SetItem(owner.Subject, members);
    }

    private static void RemoveSubject(
        ImmutableDictionary<SemanticEntityIdentity, ImmutableDictionary<SourceOwner, SourceMembership>>.Builder subjects,
        SourceOwner owner)
    {
        var members = subjects[owner.Subject].Remove(owner);
        if (members.Count == 0)
        {
            subjects.Remove(owner.Subject);
        }
        else
        {
            subjects[owner.Subject] = members;
        }
    }

    private static ImmutableDictionary<SourcePartitionKey,
        ImmutableSortedDictionary<SourceOwner, SemanticProvenance>> RemoveFromPartition(
            ImmutableDictionary<SourcePartitionKey,
                ImmutableSortedDictionary<SourceOwner, SemanticProvenance>> partitions,
            SourcePartitionKey key, SourceOwner owner)
    {
        if (!partitions.TryGetValue(key, out ImmutableSortedDictionary<SourceOwner,
            SemanticProvenance>? partition))
        {
            return partitions;
        }

        partition = partition.Remove(owner);
        return partition.Count == 0 ? partitions.Remove(key) : partitions.SetItem(key, partition);
    }
}
