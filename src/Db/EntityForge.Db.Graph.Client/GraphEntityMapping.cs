using System.Linq.Expressions;
using System.Collections.ObjectModel;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Mapping;

/// <summary>Property-graph representation attached to a reusable CLR map.</summary>
public sealed class GraphEntityMap<T>
{
    /// <summary>Creates a validated Graph binding over a neutral CLR entity map.</summary>
    public GraphEntityMap(EntityMap<T> entityMap, string vertexLabel,
        IReadOnlyDictionary<string, string> memberBindings,
        Func<EntityMaterializationContext, T>? materializer = null)
        : this(entityMap, vertexLabel, memberBindings, materializer, null)
    {
    }

    internal GraphEntityMap(EntityMap<T> entityMap, string vertexLabel,
        IReadOnlyDictionary<string, string> memberBindings,
        Func<EntityMaterializationContext, T>? materializer,
        IReadOnlyDictionary<string, GraphRelationshipTargetBinding>? relationshipTargets)
    {
        ArgumentNullException.ThrowIfNull(entityMap);
        entityMap = new(entityMap.Members.Select(member => member.Role == EntityMemberRole.Property && member.IsCollection &&
            member.CollectionKind == EntityCollectionKind.Unspecified ? member with
            {
                CollectionKind = EntityCollectionKind.OrderedSequence
            } : member));
        EntityMap = entityMap;
        ValidateLogicalShape(entityMap);
        ArgumentException.ThrowIfNullOrWhiteSpace(vertexLabel);
        VertexLabel = vertexLabel;
        MemberBindings = CopyBindings(entityMap, memberBindings);
        RelationshipTargets = CopyRelationshipTargets(entityMap, relationshipTargets);
        Materializer = materializer;
    }

    /// <summary>Gets the neutral CLR entity map.</summary>
    public EntityMap<T> EntityMap
    {
        get;
    }

    /// <summary>Gets the Graph vertex label.</summary>
    public string VertexLabel
    {
        get;
    }

    /// <summary>Gets the immutable CLR-member to Graph-representation bindings.</summary>
    public IReadOnlyDictionary<string, string> MemberBindings
    {
        get;
    }

    /// <summary>Gets explicit target identity bindings for relationships whose CLR identities are not native Graph IDs.</summary>
    public IReadOnlyDictionary<string, GraphRelationshipTargetBinding> RelationshipTargets
    {
        get;
    }

    /// <summary>Gets the optional CLR materializer.</summary>
    public Func<EntityMaterializationContext, T>? Materializer
    {
        get;
    }

    /// <summary>Creates the durable contract represented by this explicit Graph mapping.</summary>
    public EntityMappingManifest CreateManifest(EntityModelIdentity model,
        PersistentEntityTypeIdentity entityType, string convention = "explicit-v1",
        IReadOnlyDictionary<string, string>? persistentMemberIdentities = null)
    {
        var bindings = EntityMap.Members.ToDictionary(member => member.Name, member =>
        {
            RelationshipTargets.TryGetValue(member.Name, out GraphRelationshipTargetBinding? target);
            return new EntityProfileMemberContract(member.Name, MemberBindings[member.Name],
                member.Role is EntityMemberRole.Relationship ? "outgoing-edge" :
                    member.Role is EntityMemberRole.Identity ? "vertex-identity-v3" : "vertex-property",
                member.Role is EntityMemberRole.Relationship
                    ? target?.VertexLabel ?? "entityforge:graph/native-vertex-id" : null,
                target?.IdentityProperty,
                member.Role is EntityMemberRole.Relationship ? "outgoing" : null);
        }, StringComparer.Ordinal);
        return EntityMappingManifestFactory.Create(EntityMap, model, entityType,
            DatabaseProfileId.PropertyGraph, VertexLabel, bindings, Codec, convention,
            persistentMemberIdentities);
    }

    private static void ValidateLogicalShape(EntityMap<T> map)
    {
        EntityMemberMap? unsupported = map.Members.FirstOrDefault(item =>
            item.Kind is EntityMemberKind.Owned ||
            item.CollectionKind is EntityCollectionKind.OrderedSequence && item.Role != EntityMemberRole.Property);
        if (unsupported is not null)
        {
            throw new NotSupportedException(
                $"Graph member '{unsupported.Name}' requires an owned-value or ordered-sequence encoding that is not yet supported.");
        }
    }

    private static IReadOnlyDictionary<string, string> CopyBindings(EntityMap<T> map,
        IReadOnlyDictionary<string, string> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (EntityMemberMap member in map.Members)
        {
            if (!bindings.TryGetValue(member.Name, out string? value))
            {
                throw new ArgumentException($"Graph binding for CLR member '{member.Name}' is missing.", nameof(bindings));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            copy.Add(member.Name, value);
        }
        if (copy.Count != bindings.Count)
        {
            throw new ArgumentException("Graph bindings contain a member absent from the CLR map.", nameof(bindings));
        }

        EntityMemberMap[] properties = map.Members.Where(member =>
            member.Role is not EntityMemberRole.Relationship).ToArray();
        EntityMemberMap[] relationships = map.Members.Where(member =>
            member.Role is EntityMemberRole.Relationship).ToArray();
        RejectAliases(properties, "vertex property");
        RejectAliases(relationships, "outgoing edge label");
        return new ReadOnlyDictionary<string, string>(copy);

        void RejectAliases(IEnumerable<EntityMemberMap> members, string storageNamespace)
        {
            IGrouping<string, EntityMemberMap>? collision = members
                .GroupBy(member => copy[member.Name], StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (collision is not null)
            {
                throw new ArgumentException(
                    $"Graph {storageNamespace} '{collision.Key}' is bound to more than one writable member.",
                    nameof(bindings));
            }
        }
    }

    private static IReadOnlyDictionary<string, GraphRelationshipTargetBinding> CopyRelationshipTargets(
        EntityMap<T> map, IReadOnlyDictionary<string, GraphRelationshipTargetBinding>? targets)
    {
        var copy = new Dictionary<string, GraphRelationshipTargetBinding>(StringComparer.Ordinal);
        if (targets is not null)
        {
            foreach ((string memberName, GraphRelationshipTargetBinding target) in targets)
            {
                EntityMemberMap member = map.Members.SingleOrDefault(item => item.Name == memberName) ??
                    throw new ArgumentException($"Graph relationship target '{memberName}' is absent from the CLR map.", nameof(targets));
                if (member.Role is not EntityMemberRole.Relationship)
                {
                    throw new ArgumentException($"Graph relationship target '{memberName}' is not a relationship.", nameof(targets));
                }

                copy.Add(memberName, target ?? throw new ArgumentException("A Graph relationship target cannot be null.", nameof(targets)));
            }
        }

        foreach (EntityMemberMap member in map.Members)
        {
            if (member.Role is EntityMemberRole.Property && member.IsCollection && member.CollectionKind != EntityCollectionKind.OrderedSequence)
            {
                throw new ArgumentException($"Graph array property '{member.Name}' requires ordered-sequence semantics.", nameof(map));
            }
            if (member.Role == EntityMemberRole.Property && member.IsCollection)
            {
                Type element = EntityMappingManifestFactory.ValueType(member);
                if (element == typeof(Uri) || element == typeof(DateTimeOffset) || element == typeof(byte[]) || Nullable.GetUnderlyingType(element) is not null)
                {
                    throw new NotSupportedException($"Graph array property '{member.Name}' requires non-null scalar elements supported by DatabaseValue.Array.");
                }
            }

            if (member.Role is EntityMemberRole.Relationship && !copy.ContainsKey(member.Name))
            {
                Type identityType = ElementType(member.ValueType) ?? member.ValueType;
                identityType = Nullable.GetUnderlyingType(identityType) ?? identityType;
                if (identityType != typeof(Guid) && identityType != typeof(GraphVertexId))
                {
                    throw new ArgumentException(
                        $"Graph relationship '{member.Name}' requires Guid/GraphVertexId values or an explicit target map.", nameof(map));
                }
            }
        }
        return new ReadOnlyDictionary<string, GraphRelationshipTargetBinding>(copy);
    }

    private static Type? ElementType(Type type)
    {
        if (type == typeof(string) || type == typeof(byte[]))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        Type? enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(candidate => candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    private string Codec(EntityMemberMap member)
    {
        Type type = EntityMappingManifestFactory.ValueType(member);
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (member.Role == EntityMemberRole.Identity && type != typeof(string) && type != typeof(int) &&
            type != typeof(long) && type != typeof(Guid))
        {
            throw new NotSupportedException("Mapped Graph identity codecs support string, int, long and Guid.");
        }

        if (member.Role is EntityMemberRole.Relationship)
        {
            return RelationshipTargets.ContainsKey(member.Name) ? RelationshipTargetsCodec(member) :
                type == typeof(Guid) ? "entityforge/guid" :
                type == typeof(GraphVertexId) ? "entityforge/graph-vertex-id" :
                RelationshipTargetsCodec(member);
        }

        return type == typeof(string) ? "entityforge/string" :
            type == typeof(int) ? "entityforge/int32-as-int64" :
            type == typeof(long) ? "entityforge/int64" :
            type == typeof(float) ? "entityforge/float32-as-float64" :
            type == typeof(double) ? "entityforge/float64" :
            type == typeof(decimal) ? "entityforge/decimal" :
            type == typeof(bool) ? "entityforge/bool" :
            type == typeof(DateTimeOffset) ? "entityforge/datetime-offset" :
            type == typeof(EntityForge.Values.CalendarDate) ? "entityforge/calendar-date" :
            type == typeof(EntityForge.Values.CalendarDuration) ? "entityforge/calendar-duration" :
            type == typeof(EntityForge.Values.SpatialPoint) ? "entityforge/spatial-point" :
            type == typeof(EntityForge.Values.LocalTime) ? "entityforge/local-time" :
            type == typeof(EntityForge.Values.ZonedTime) ? "entityforge/zoned-time" :
            type == typeof(EntityForge.Values.ZonedDateTime) ? "entityforge/zoned-datetime" :
            type == typeof(EntityForge.Values.LocalDateTime) ? "entityforge/local-datetime" :
            type == typeof(Uri) ? "entityforge/absolute-uri" :
            type == typeof(Guid) ? "entityforge/uuid" :
            type == typeof(byte[]) ? "entityforge/binary" :
            throw new NotSupportedException(
                $"Graph member '{member.Name}' has no supported property codec for CLR type '{type.Name}'.");

        string RelationshipTargetsCodec(EntityMemberMap mapped) =>
            RelationshipTargets.TryGetValue(mapped.Name, out GraphRelationshipTargetBinding? target)
                ? target.IdentityCodec
                : throw new NotSupportedException(
                    $"Graph relationship '{mapped.Name}' requires a native Graph identity or explicit target map.");
    }
}

/// <summary>Identifies the target vertex scope and stored identity property for a mapped Graph relationship.</summary>
public sealed class GraphRelationshipTargetBinding
{
    internal GraphRelationshipTargetBinding(string vertexLabel, string identityProperty, string identityCodec,
        Type targetType,
        Func<object, object> identity, Func<IGraphMappingSnapshot, object, object?> materialize,
        Func<IGraphEntityReadScope, object, CancellationToken, ValueTask<object?>>? materializeAsync = null,
        Func<GraphReadRecipeBuilder, int>? readShape = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vertexLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityProperty);
        VertexLabel = vertexLabel;
        IdentityProperty = identityProperty;
        IdentityCodec = identityCodec;
        TargetType = targetType;
        Identity = identity;
        Materialize = materialize;
        MaterializeAsync = materializeAsync;
        ReadShape = readShape;
    }

    /// <summary>Gets the target vertex label.</summary>
    public string VertexLabel
    {
        get;
    }

    /// <summary>Gets the target vertex identity property.</summary>
    public string IdentityProperty
    {
        get;
    }

    internal string IdentityCodec
    {
        get;
    }

    internal Type TargetType
    {
        get;
    }

    internal Func<object, object> Identity
    {
        get;
    }

    internal Func<IGraphMappingSnapshot, object, object?> Materialize
    {
        get;
    }

    internal Func<IGraphEntityReadScope, object, CancellationToken, ValueTask<object?>>? MaterializeAsync
    {
        get;
    }

    internal Func<GraphReadRecipeBuilder, int>? ReadShape
    {
        get;
    }
}

/// <summary>Starts a Graph binding that also defines its reusable neutral CLR map.</summary>
public static class GraphEntityMap
{
    /// <summary>Begins a Graph mapping while defining its neutral CLR shape.</summary>
    public static GraphEntityMapBuilder<T> For<T>() => new();

    /// <summary>Binds an existing neutral CLR shape to Graph labels and properties.</summary>
    public static GraphEntityMapBuilder<T> BindGraph<T>(this EntityMap<T> map) => new(map);
}

/// <summary>Builds a property-graph binding.</summary>
public sealed class GraphEntityMapBuilder<T>
{
    private readonly EntityMapBuilder<T>? _newMap;
    private readonly EntityMap<T>? _map;
    private readonly Dictionary<string, string> _bindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphRelationshipTargetBinding> _relationshipTargets = new(StringComparer.Ordinal);
    private string? _label;
    private Func<EntityMaterializationContext, T>? _materializer;

    internal GraphEntityMapBuilder() => _newMap = EntityMap.For<T>();

    internal GraphEntityMapBuilder(EntityMap<T> map) => _map = map ?? throw new ArgumentNullException(nameof(map));

    /// <summary>Sets the vertex label represented by this CLR type.</summary>
    public GraphEntityMapBuilder<T> VertexLabel(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        _label = label;
        return this;
    }

    /// <summary>Binds the stable CLR identity to a Graph property.</summary>
    public GraphEntityMapBuilder<T> Identity<TValue>(Expression<Func<T, TValue>> member, string property) => Bind(member, property, EntityMemberRole.Identity);

    /// <summary>Binds a CLR scalar or homogeneous collection to a Graph property. Property collections preserve their input order and duplicates as native arrays; elements cannot be null.</summary>
    public GraphEntityMapBuilder<T> Property<TValue>(Expression<Func<T, TValue>> member, string property) => Bind(member, property, EntityMemberRole.Property);

    /// <summary>Binds a CLR relationship to an outgoing Graph edge label.</summary>
    public GraphEntityMapBuilder<T> Edge<TValue>(Expression<Func<T, TValue>> member, string label) => Bind(member, label, EntityMemberRole.Relationship);

    /// <summary>Binds an outgoing edge whose target identities use another mapped vertex scope.</summary>
    public GraphEntityMapBuilder<T> Edge<TValue, TTarget>(Expression<Func<T, TValue>> member, string label,
        GraphEntityMap<TTarget> targetMap)
    {
        ArgumentNullException.ThrowIfNull(targetMap);
        EntityMemberMap targetIdentity = targetMap.EntityMap.Identity ??
            throw new ArgumentException("A Graph relationship target map requires an identity member.", nameof(targetMap));
        Bind(member, label, EntityMemberRole.Relationship);
        _relationshipTargets.Add(EntityMapExpression.Member(member).Name,
            new(targetMap.VertexLabel, targetMap.MemberBindings[targetIdentity.Name],
                targetMap.IdentityCodec(), typeof(TTarget),
                value => value is TTarget entity
                    ? targetIdentity.Read(entity!) ?? throw new InvalidOperationException(
                        "A Graph relationship target identity cannot be null.")
                    : value,
                (snapshot, identity) => GraphEntityMaterialization.MaterializeEntity(snapshot, targetMap, identity),
                async (scope, identity, token) => await GraphEntityMaterialization.MaterializeEntityAsync(scope, targetMap, identity, token).ConfigureAwait(false),
                builder => builder.Add(targetMap)));
        return this;
    }

    /// <summary>Sets the CLR materializer and completes the mapping.</summary>
    public GraphEntityMap<T> Materialize(Func<EntityMaterializationContext, T> materializer)
    {
        _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
        return Build();
    }

    /// <summary>Completes the mapping.</summary>
    public GraphEntityMap<T> Build() => new(_map ?? _newMap!.Build(), _label ?? throw new InvalidOperationException("A Graph vertex label is required."), new Dictionary<string, string>(_bindings, StringComparer.Ordinal), _materializer, new Dictionary<string, GraphRelationshipTargetBinding>(_relationshipTargets, StringComparer.Ordinal));

    private GraphEntityMapBuilder<T> Bind<TValue>(Expression<Func<T, TValue>> member, string representation, EntityMemberRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(representation);
        EntityMemberMap mapped = _map is null ? Add(member, role) : _map.RequireMember(member);
        if (mapped.Role != role)
        {
            throw new ArgumentException($"CLR member '{mapped.Name}' is mapped as {mapped.Role}, not {role}.", nameof(member));
        }

        _bindings.Add(mapped.Name, representation);
        return this;
    }

    private EntityMemberMap Add<TValue>(Expression<Func<T, TValue>> member, EntityMemberRole role)
    {
        _newMap!.Add(member, role);
        return _newMap.Members[^1];
    }
}

internal static class GraphEntityMapCodecExtensions
{
    internal static string IdentityCodec<T>(this GraphEntityMap<T> map)
    {
        EntityMemberMap identity = map.EntityMap.Identity ?? throw new InvalidOperationException(
            "A Graph relationship target map requires an identity member.");
        Type type = Nullable.GetUnderlyingType(identity.ValueType) ?? identity.ValueType;
        return type == typeof(string) ? "entityforge/string" :
            type == typeof(int) ? "entityforge/int32-as-int64" :
            type == typeof(long) ? "entityforge/int64" :
            type == typeof(Guid) ? "entityforge/uuid" :
            throw new NotSupportedException(
                $"Graph target identity '{identity.Name}' uses unsupported CLR type '{type.Name}'.");
    }
}
