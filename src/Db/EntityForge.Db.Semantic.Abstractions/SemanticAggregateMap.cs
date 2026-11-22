using System.Linq.Expressions;
using System.Reflection;
using EntityForge.Db.Mapping;
using EntityForge.Query.Projection;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>Composes existing scalar Semantic maps with explicitly owned or referenced single objects.</summary>
public sealed class SemanticAggregateMap<T> where T : class
{
    internal SemanticAggregateMap(AggregateNode node) => Node = node;

    internal AggregateNode Node
    {
        get;
    }

    /// <summary>Captures executable target policy without producer CLR types or accessors.</summary>
    public SemanticEntityDefinition ExportDefinition() => SemanticEntityDefinition.Capture(Node);
}

/// <summary>Begins explicit aggregate mapping over a writable scalar map.</summary>
public static class SemanticAggregateMap
{
    /// <summary>Starts an aggregate mapping from an existing writable scalar map and durable model identities.</summary>
    /// <param name="scalarMap">The scalar-only map that supplies identity and root values.</param>
    /// <param name="model">Stable application model identity.</param>
    /// <param name="entityType">Stable persisted entity-type identity.</param>
    /// <typeparam name="T">The root CLR entity type.</typeparam>
    /// <returns>A builder for explicitly owned and referenced single-object members.</returns>
    /// <exception cref="NotSupportedException">The starting map contains non-scalar or collection members.</exception>
    public static SemanticAggregateMapBuilder<T> For<T>(SemanticEntityMap<T> scalarMap,
        EntityModelIdentity model, PersistentEntityTypeIdentity entityType) where T : class => new(scalarMap, model, entityType);
}

/// <summary>Declares lifecycle and assignment explicitly; target factories allow recursive reference maps.</summary>
public sealed partial class SemanticAggregateMapBuilder<T> where T : class
{
    private readonly SemanticEntityMap<T> _scalar;
    private readonly EntityMappingManifest _manifest;
    private readonly List<AggregateLink> _links = [];

    internal SemanticAggregateMapBuilder(SemanticEntityMap<T> scalar, EntityModelIdentity model, PersistentEntityTypeIdentity type, IReadOnlyDictionary<string, string>? memberIdentities = null)
    {
        _scalar = scalar ?? throw new ArgumentNullException(nameof(scalar));
        if (scalar.EntityMap.Members.Any(member => member.Kind is not (EntityMemberKind.Identity or EntityMemberKind.Scalar) || member.IsCollection))
        {
            throw new NotSupportedException("An aggregate starts from a scalar-only writable map.");
        }

        _manifest = scalar.CreateManifest(model, type, AggregateNode.Convention,
            memberIdentities ?? scalar.EntityMap.Members.ToDictionary(member => member.Name,
                member => member.Member?.GetCustomAttribute<EntityMemberAttribute>()?.Identity ?? member.Name));
    }

    /// <summary>Maps a single-object member as an owned child whose lifecycle follows the root.</summary>
    /// <param name="member">Property expression with explicit CLR nullability.</param>
    /// <param name="relationship">Semantic term with the Relationship role.</param>
    /// <param name="target">Deferred factory for the target aggregate map; supports recursive references.</param>
    /// <param name="assign">Assigns the materialized child to its owning CLR object.</param>
    /// <param name="persistentMemberIdentity">Optional stable member identity; otherwise uses the member attribute or property name.</param>
    /// <typeparam name="TChild">The child entity type.</typeparam>
    /// <returns>This builder for further declarations.</returns>
    /// <exception cref="ArgumentException">The relationship role or member identity is invalid, or the member already has a mapping.</exception>
    /// <exception cref="NotSupportedException">The member is not a single object property or its CLR nullability is unknown.</exception>
    public SemanticAggregateMapBuilder<T> Owned<TChild>(Expression<Func<T, TChild?>> member, SemanticTerm relationship,
        Func<SemanticAggregateMap<TChild>> target, Action<T, TChild?> assign, string? persistentMemberIdentity = null) where TChild : class =>
        Add(member, relationship, target, assign, EntityMemberKind.Owned, persistentMemberIdentity);

    /// <summary>Maps a single-object member as a reference to an independently identified entity.</summary>
    /// <param name="member">Property expression with explicit CLR nullability.</param>
    /// <param name="relationship">Semantic term with the Relationship role.</param>
    /// <param name="target">Deferred factory for the target aggregate map; supports recursive references.</param>
    /// <param name="assign">Assigns the materialized child to its owning CLR object.</param>
    /// <param name="persistentMemberIdentity">Optional stable member identity; otherwise uses the member attribute or property name.</param>
    /// <typeparam name="TChild">The child entity type.</typeparam>
    /// <returns>This builder for further declarations.</returns>
    /// <exception cref="ArgumentException">The relationship role or member identity is invalid, or the member already has a mapping.</exception>
    /// <exception cref="NotSupportedException">The member is not a single object property or its CLR nullability is unknown.</exception>
    public SemanticAggregateMapBuilder<T> Reference<TChild>(Expression<Func<T, TChild?>> member, SemanticTerm relationship,
        Func<SemanticAggregateMap<TChild>> target, Action<T, TChild?> assign, string? persistentMemberIdentity = null) where TChild : class =>
        Add(member, relationship, target, assign, EntityMemberKind.Reference, persistentMemberIdentity);

    private SemanticAggregateMapBuilder<T> Add<TChild>(Expression<Func<T, TChild?>> expression, SemanticTerm relationship,
        Func<SemanticAggregateMap<TChild>> target, Action<T, TChild?> assign, EntityMemberKind kind, string? persistentIdentity) where TChild : class
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(assign);
        MemberInfo member = EntityMapExpression.Member(expression);
        if (member is not PropertyInfo property || property.GetIndexParameters().Length != 0 ||
            typeof(System.Collections.IEnumerable).IsAssignableFrom(typeof(TChild)))
        {
            throw new NotSupportedException("Aggregate links must be single object properties; collections are not enabled.");
        }

        if (relationship.Role != SemanticTermRole.Relationship)
        {
            throw new ArgumentException("An aggregate link requires a relationship term.");
        }

        NullabilityState state = new NullabilityInfoContext().Create(property).ReadState;
        if (state == NullabilityState.Unknown)
        {
            throw new NotSupportedException("Aggregate links require explicit CLR nullability.");
        }

        string id = persistentIdentity ?? property.GetCustomAttribute<EntityMemberAttribute>()?.Identity ?? property.Name;
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (_scalar.EntityMap.Members.Any(item => item.Name == member.Name) || _links.Any(item => item.Member.Name == member.Name))
        {
            throw new ArgumentException("A member may have only one mapping.");
        }

        var mapped = new EntityMemberMap(member.Name, typeof(TChild), EntityMemberRole.Relationship, false, member, expression.Compile()) { Kind = kind };
        _links.Add(new(mapped, id, state == NullabilityState.Nullable, relationship, () => target().Node,
            (owner, child) => assign((T)owner, (TChild?)child)));
        return this;
    }

    /// <summary>Captures the scalar mapping and declared aggregate links as an executable aggregate map.</summary>
    /// <returns>The aggregate map used for capture and materialization.</returns>
    public SemanticAggregateMap<T> Build()
    {
        var shape = _scalar.EntityMap;
        return new(new(typeof(T), _manifest, _scalar.Projection, _scalar.IdentityBinding!.Scheme,
            vocabulary => _scalar.CreateTargetBinding(new Uri("urn:entityforge:aggregate:scalar:" + _manifest.Fingerprint),
                AggregateNode.Convention, vocabulary, _manifest.Members.ToDictionary(member => member.ClrName, member => member.Identity)), _links.ToArray(),
            entity => _scalar.GetIdentity((T)entity), identity => _scalar.GetIdentityValue(identity),
            (node, identity) => _scalar.Materialize(node, identity),
            entity => shape.Members.Where(member => member.Kind == EntityMemberKind.Scalar && !member.IsCollection).Select(member =>
            {
                object? value = member.Read(entity);
                if (value is null && !_manifest.Members.Single(item => item.ClrName == member.Name).IsNullable)
                {
                    throw new InvalidOperationException($"Required scalar '{member.Name}' is null.");
                }

                return (Member: member, Value: value);
            }).Where(item => item.Value is not null).Select(item => new SemanticFieldChange(
                _scalar.MemberBindings.Single(binding => binding.MemberName == item.Member.Name).Term,
                SemanticFieldChangeKind.Set, SemanticMappedValue.Convert(item.Value!))).ToArray())
        {
            CaptureScalars = entity => new Entity(shape.Members.ToDictionary(
                member => _manifest.Members.Single(contract => contract.ClrName == member.Name).Identity,
                member => member.Read(entity), StringComparer.Ordinal)),
            CaptureKey = entity => shape.Identity!.Read(entity)!,
            InputIdentity = key => _scalar.GetIdentityValue(EntityRecordShape<T>.RequireType(key, _scalar.EntityMap.Identity!.ValueType)),
            InputFields = input => _scalar.EntityMap.Members.Where(member => member.Kind == EntityMemberKind.Scalar)
                .Select(member =>
                {
                    var contract = _manifest.Members.Single(m => m.ClrName == member.Name);
                    input.Members.TryGetValue(contract.Identity, out object? value);
                    if (value is null && !contract.IsNullable)
                    {
                        throw new InvalidOperationException($"Required scalar '{contract.Identity}' is null.");
                    }

                    if (value is not null)
                    {
                        value = EntityRecordShape<T>.RequireType(value, member.ValueType);
                    }

                    return (member, value);
                }).Where(pair => pair.value is not null).Select(pair => new SemanticFieldChange(
                    _scalar.MemberBindings.Single(binding => binding.MemberName == pair.member.Name).Term,
                    SemanticFieldChangeKind.Set, SemanticMappedValue.Convert(pair.value!))).ToArray()
        });
    }
}

internal sealed record AggregateMember(string Name, EntityMemberKind Kind, EntityCollectionKind CollectionKind,
    Func<object, object?> Read)
{
    internal bool IsCollection => CollectionKind != EntityCollectionKind.None;

    public static implicit operator AggregateMember(EntityMemberMap member) =>
        new(member.Name, member.Kind, member.CollectionKind, member.Read);
}

internal sealed record AggregateLink(AggregateMember Member, string Identity, bool Nullable, SemanticTerm Term,
    Func<AggregateNode> Target, Action<object, object?> Assign, string? CollectionCodec = null)
{
    internal Func<object, int, bool, object?>? Capture
    {
        get; init;
    }

    internal Func<AggregateNode>? CollectionElementTarget
    {
        get; init;
    }

    internal Type? CollectionElementType
    {
        get; init;
    }

    internal bool Cascades => Member.Kind == EntityMemberKind.Owned || Member.IsCollection;
}

internal sealed record AggregateNode(Type ClrType, EntityMappingManifest ScalarManifest, EntityProjectionDefinition ScalarProjection,
    SemanticIdentityScheme Scheme, Func<SemanticModelReference, SemanticTargetBinding> ScalarTargetBinding,
    IReadOnlyList<AggregateLink> Links, Func<object, SemanticEntityIdentity> Identity, Func<object, SemanticEntityIdentity> IdentityValue,
    Func<EntityProjectionNode, string, object> Materialize, Func<object, SemanticFieldChange[]> Fields)
{
    internal Func<object, SemanticEntityIdentity> InputIdentity { get; init; } = null!;

    internal Func<Entity, SemanticFieldChange[]> InputFields { get; init; } = null!;

    internal Func<object, Entity> CaptureScalars { get; init; } = null!;

    internal Func<object, object> CaptureKey { get; init; } = null!;

    internal SemanticEntityIdentity ReadIdentity(object value)
    {
        if (value is EntityReference reference)
        {
            return InputIdentity(reference.Identity);
        }

        if (value is not Entity input)
        {
            return Identity(value);
        }

        var allowed = ScalarManifest.Members.Select(m => m.Identity).Concat(Links.Select(l => l.Identity)).ToHashSet(StringComparer.Ordinal);
        if (input.Members.Keys.Any(key => !allowed.Contains(key)))
        {
            throw new ArgumentException("Input contains an unknown mapped member.");
        }

        var identity = ScalarManifest.Members.Single(m => m.Kind == EntityMemberKind.Identity);
        if (!input.Members.TryGetValue(identity.Identity, out object? key) || key is null)
        {
            throw new InvalidOperationException("A mapped input requires an identity.");
        }

        return InputIdentity(key);
    }

    internal SemanticFieldChange[] ReadFields(object value) =>
        value is Entity input ? InputFields(input) : Fields(value);

    // Infrastructure collection nodes consume the entity budget, but not domain relationship depth.
    internal int DepthCost { get; init; } = 1;

    internal const string Convention = "semantic-single-object-aggregate-v2";
}
