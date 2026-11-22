using System.Collections.ObjectModel;

namespace EntityForge.Db.Mapping;

/// <summary>Evaluated complete-record values keyed by stable mapped member identity, independent of any producer framework.</summary>
public sealed class Entity
{
    private readonly Dictionary<string, object?> _members;

    /// <summary>Captures mapped members with ordinal names and copies direct byte-array values.</summary>
    /// <param name="members">Evaluated values keyed by stable mapping member names. Other referenced objects are retained.</param>
    /// <exception cref="ArgumentNullException">The member dictionary is null.</exception>
    /// <exception cref="ArgumentException">A mapped member name is invalid or names collide under ordinal comparison.</exception>
    public Entity(IReadOnlyDictionary<string, object?> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        foreach (string name in members.Keys)
        {
            EntityMemberName.Validate(name);
        }

        _members = members.ToDictionary(p => p.Key,
            p => p.Value is byte[] bytes ? bytes.ToArray() : p.Value, StringComparer.Ordinal);
        Members = new ReadOnlyDictionary<string, object?>(_members);
    }

    /// <summary>Gets the read-only view of captured mapped values; null values remain distinct from omitted members.</summary>
    public IReadOnlyDictionary<string, object?> Members
    {
        get;
    }

    // Only the database materializer populates relationships, before publishing the completed result.
    internal void SetMember(string name, object? value) => _members[name] = value;

    internal object? MaterializedIdentity
    {
        get; init;
    }
}

/// <summary>An independent reference key. Its type and identity domain come from the target mapping.</summary>
public sealed record EntityReference
{
    /// <summary>Creates a mapped reference without loading the referenced entity.</summary>
    /// <param name="identity">The non-null identity interpreted by the target mapping.</param>
    /// <exception cref="ArgumentNullException">The identity is null.</exception>
    public EntityReference(object identity) => Identity = identity ?? throw new ArgumentNullException(nameof(identity));

    /// <summary>Gets the retained identity value; its domain is defined by the target mapping.</summary>
    public object Identity
    {
        get;
    }
}

/// <summary>Explicit set or sequence values. Null collections are represented by null member values.</summary>
public sealed class EntityCollection
{
    /// <summary>Captures an explicit set or ordered sequence, copying the list and direct byte-array items.</summary>
    /// <param name="kind">Set or ordered-sequence semantics. Scalar semantics are not a collection.</param>
    /// <param name="items">The item values, including explicit null entries where permitted by the mapping.</param>
    /// <exception cref="ArgumentNullException">The item list is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The kind is not a supported collection kind.</exception>
    public EntityCollection(EntityCollectionKind kind, IReadOnlyList<object?> items)
    {
        if (kind is not (EntityCollectionKind.Set or EntityCollectionKind.OrderedSequence))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentNullException.ThrowIfNull(items);
        Kind = kind;
        Items = Array.AsReadOnly(items.Select(value => value is byte[] bytes ? bytes.ToArray() : value).ToArray());
    }

    /// <summary>Gets the requested set or sequence semantics.</summary>
    public EntityCollectionKind Kind
    {
        get;
    }

    /// <summary>Gets the captured items in supplied order; set canonicalization is performed by mapping interpretation, not this constructor.</summary>
    public IReadOnlyList<object?> Items
    {
        get;
    }
}
