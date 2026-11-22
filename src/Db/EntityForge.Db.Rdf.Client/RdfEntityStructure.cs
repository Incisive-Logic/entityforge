using EntityForge.Db.Mapping;

namespace EntityForge.Db.Rdf;

/// <summary>A named default-graph RDF structure independent of domain CLR classes.</summary>
public sealed class RdfEntityStructure
{
    internal RdfEntityStructure(RdfResolvedEntityMapping<Entity> mapping) => Mapping = mapping;

    internal RdfResolvedEntityMapping<Entity> Mapping
    {
        get;
    }

    /// <summary>Gets the same durable interpretation used by mapped POCOs.</summary>
    public EntityMappingManifest Manifest => Mapping.Manifest;

    /// <summary>Begins a named structure with an RDF class IRI.</summary>
    public static RdfEntityStructureBuilder For(EntityModelIdentity model,
        PersistentEntityTypeIdentity entityType, Uri @class) => new(model, entityType, @class);
}

/// <summary>Declares subject identity, literal predicates and independent resource references.</summary>
public sealed class RdfEntityStructureBuilder
{
    private readonly EntityModelIdentity _model;
    private readonly PersistentEntityTypeIdentity _type;
    private readonly Uri _class;
    private readonly EntityStructureShape _shape = new();
    private readonly Dictionary<string, Uri> _bindings = new(StringComparer.Ordinal);

    internal RdfEntityStructureBuilder(EntityModelIdentity model, PersistentEntityTypeIdentity type, Uri @class)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _type = type ?? throw new ArgumentNullException(nameof(type));
        _class = Absolute(@class);
    }

    /// <summary>Declares a required key resolved against the native subject IRI base.</summary>
    public RdfEntityStructureBuilder Identity<T>(string name, Uri subjectBase) =>
        Add<T>(name, subjectBase, EntityMemberRole.Identity, false);

    /// <summary>Declares one literal predicate. Its datatype comes from the codec.</summary>
    public RdfEntityStructureBuilder Property<T>(string name, Uri predicate, bool nullable = false) =>
        Add<T>(name, predicate, EntityMemberRole.Property, nullable);

    /// <summary>Declares an independent absolute resource IRI, supplied as an EntityReference.</summary>
    public RdfEntityStructureBuilder Reference(string name, Uri predicate, bool nullable = false) =>
        Add<Uri>(name, predicate, EntityMemberRole.Relationship, nullable);

    /// <summary>Freezes and validates the definition through the existing default-graph planner.</summary>
    public RdfEntityStructure Build()
    {
        var (map, materialize) = _shape.Build();
        return new(new RdfEntityMap<Entity>(map, _class, _bindings, materialize).ResolveBinding(_model, _type));
    }

    private RdfEntityStructureBuilder Add<T>(string name, Uri binding, EntityMemberRole role, bool nullable)
    {
        binding = Absolute(binding);
        _shape.Add<T>(name, role, nullable);
        _bindings.Add(name, binding);
        return this;
    }

    private static Uri Absolute(Uri value) => value is { IsAbsoluteUri: true } ? value :
        throw new ArgumentException("An RDF binding requires an absolute IRI.", nameof(value));
}
