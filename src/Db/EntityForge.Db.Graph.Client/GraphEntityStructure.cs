using EntityForge.Db.Mapping;

namespace EntityForge.Db.Graph;

/// <summary>A named property-graph structure independent of domain CLR classes.</summary>
public sealed class GraphEntityStructure
{
    internal GraphEntityStructure(GraphResolvedEntityMapping<Entity> mapping,
        Action<EntityStructureShape, string, bool> addReference)
    {
        Mapping = mapping;
        AddReference = addReference;
    }

    internal GraphResolvedEntityMapping<Entity> Mapping
    {
        get;
    }

    internal Action<EntityStructureShape, string, bool> AddReference
    {
        get;
    }

    /// <summary>Gets the same durable interpretation used by mapped POCOs.</summary>
    public EntityMappingManifest Manifest => Mapping.Manifest;

    /// <summary>Begins a named structure with a native vertex label.</summary>
    public static GraphEntityStructureBuilder For(EntityModelIdentity model,
        PersistentEntityTypeIdentity entityType, string vertexLabel) => new(model, entityType, vertexLabel);
}

/// <summary>Declares literal member names, scalar codecs and independent outgoing references.</summary>
public sealed class GraphEntityStructureBuilder
{
    private readonly EntityModelIdentity _model;
    private readonly PersistentEntityTypeIdentity _type;
    private readonly string _label;
    private readonly EntityStructureShape _shape = new();
    private readonly Dictionary<string, string> _bindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphRelationshipTargetBinding> _targets = new(StringComparer.Ordinal);
    private Action<EntityStructureShape, string, bool>? _addReference;

    internal GraphEntityStructureBuilder(EntityModelIdentity model, PersistentEntityTypeIdentity type, string label)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _type = type ?? throw new ArgumentNullException(nameof(type));
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        _label = label;
    }

    /// <summary>Declares a required identity stored under a vertex property.</summary>
    public GraphEntityStructureBuilder Identity<T>(string name, string property)
    {
        Add<T>(name, property, EntityMemberRole.Identity, false);
        _addReference = (shape, member, nullable) => shape.Add<T>(member, EntityMemberRole.Relationship, nullable);
        return this;
    }

    /// <summary>Declares one scalar property. Omitted optional values clear map-owned state.</summary>
    public GraphEntityStructureBuilder Property<T>(string name, string property, bool nullable = false) =>
        Add<T>(name, property, EntityMemberRole.Property, nullable);

    /// <summary>Declares a reference to a native Guid or GraphVertexId target.</summary>
    public GraphEntityStructureBuilder Reference<T>(string name, string edgeLabel, bool nullable = false) =>
        Add<T>(name, edgeLabel, EntityMemberRole.Relationship, nullable);

    /// <summary>Declares a reference whose key is interpreted in another named structure's identity scope.</summary>
    public GraphEntityStructureBuilder Reference(string name, string edgeLabel,
        GraphEntityStructure target, bool nullable = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(edgeLabel);
        var identity = target.Manifest.Members.Single(m => m.Kind == EntityMemberKind.Identity);
        if (identity.ValueCodec is not ("entityforge/string" or "entityforge/int32-as-int64" or
            "entityforge/int64" or "entityforge/uuid"))
        {
            throw new NotSupportedException("Mapped Graph reference keys support string, int, long and Guid.");
        }

        var native = target.Manifest.ProfileBindings.Single(m => m.MemberIdentity == identity.Identity);
        target.AddReference(_shape, name, nullable);
        _bindings.Add(name, edgeLabel);
        _targets.Add(name, new(target.Manifest.ProfileTypeBinding, native.StoredBinding, identity.ValueCodec,
            typeof(Entity), value => value, (_, _) => throw new InvalidOperationException("Named references materialize as keys.")));
        return this;
    }

    /// <summary>Freezes and validates this definition through the existing native map.</summary>
    public GraphEntityStructure Build()
    {
        var (map, materialize) = _shape.Build();
        var native = new GraphEntityMap<Entity>(map, _label, _bindings, materialize, _targets);
        return new(native.ResolveBinding(_model, _type), _addReference!);
    }

    private GraphEntityStructureBuilder Add<T>(string name, string binding, EntityMemberRole role, bool nullable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binding);
        _shape.Add<T>(name, role, nullable);
        _bindings.Add(name, binding);
        return this;
    }
}
