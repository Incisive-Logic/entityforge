using System.Linq.Expressions;
using System.Collections.ObjectModel;

namespace EntityForge.Db.Mapping;

/// <summary>RDF representation attached to a reusable CLR map.</summary>
public sealed class RdfEntityMap<T>
{
    /// <summary>Creates a validated RDF binding over a neutral CLR entity map.</summary>
    public RdfEntityMap(EntityMap<T> entityMap, Uri @class,
        IReadOnlyDictionary<string, Uri> memberBindings,
        Func<EntityMaterializationContext, T>? materializer = null)
    {
        EntityMap = entityMap ?? throw new ArgumentNullException(nameof(entityMap));
        ValidateLogicalShape(entityMap);
        Class = @class is { IsAbsoluteUri: true } ? @class : throw new ArgumentException(
            "An RDF class binding requires an absolute IRI.", nameof(@class));
        MemberBindings = CopyBindings(entityMap, memberBindings);
        Materializer = materializer;
    }

    /// <summary>Gets the neutral CLR entity map.</summary>
    public EntityMap<T> EntityMap
    {
        get;
    }

    /// <summary>Gets the absolute RDF class IRI.</summary>
    public Uri Class
    {
        get;
    }

    /// <summary>Gets the immutable CLR-member to RDF-term bindings.</summary>
    public IReadOnlyDictionary<string, Uri> MemberBindings
    {
        get;
    }

    /// <summary>Gets the optional CLR materializer.</summary>
    public Func<EntityMaterializationContext, T>? Materializer
    {
        get;
    }

    /// <summary>Creates the durable contract represented by this explicit RDF mapping.</summary>
    public EntityMappingManifest CreateManifest(EntityModelIdentity model,
        PersistentEntityTypeIdentity entityType, string convention = "explicit-v1",
        IReadOnlyDictionary<string, string>? persistentMemberIdentities = null) =>
        EntityMappingManifestFactory.Create(EntityMap, model, entityType, DatabaseProfileId.Rdf,
            Class.AbsoluteUri, EntityMap.Members.ToDictionary(member => member.Name, member =>
                new EntityProfileMemberContract(member.Name, MemberBindings[member.Name].AbsoluteUri,
                    member.Role is EntityMemberRole.Identity ? "subject-base" : "predicate",
                    member.Role is EntityMemberRole.Relationship ? "entityforge:rdf/resource" : null,
                    member.Role is EntityMemberRole.Relationship ? Codec(member) : null,
                    member.Role is EntityMemberRole.Relationship ? "outgoing" : null),
                StringComparer.Ordinal), Codec, convention, persistentMemberIdentities);

    private static void ValidateLogicalShape(EntityMap<T> map)
    {
        EntityMemberMap? unsupported = map.Members.FirstOrDefault(item =>
            item.Kind is EntityMemberKind.Owned ||
            item.CollectionKind is EntityCollectionKind.OrderedSequence);
        if (unsupported is not null)
        {
            throw new NotSupportedException(
                $"RDF member '{unsupported.Name}' requires an owned-value or ordered-sequence encoding that is not yet supported.");
        }
    }

    private static IReadOnlyDictionary<string, Uri> CopyBindings(EntityMap<T> map,
        IReadOnlyDictionary<string, Uri> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var copy = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (EntityMemberMap member in map.Members)
        {
            if (!bindings.TryGetValue(member.Name, out Uri? value))
            {
                throw new ArgumentException($"RDF binding for CLR member '{member.Name}' is missing.", nameof(bindings));
            }

            if (!value.IsAbsoluteUri)
            {
                throw new ArgumentException($"RDF binding for CLR member '{member.Name}' must be absolute.", nameof(bindings));
            }

            copy.Add(member.Name, value);
        }
        if (copy.Count != bindings.Count)
        {
            throw new ArgumentException("RDF bindings contain a member absent from the CLR map.", nameof(bindings));
        }

        IGrouping<string, EntityMemberMap>? collision = map.Members
            .Where(member => member.Role is not EntityMemberRole.Identity)
            .GroupBy(member => copy[member.Name].AbsoluteUri, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (collision is not null)
        {
            throw new ArgumentException(
                $"RDF predicate '{collision.Key}' is bound to more than one writable member.", nameof(bindings));
        }

        return new ReadOnlyDictionary<string, Uri>(copy);
    }

    private static string Codec(EntityMemberMap member)
    {
        Type type = EntityMappingManifestFactory.ValueType(member);
        if (member.Role is EntityMemberRole.Relationship)
        {
            return type == typeof(Uri) ? "entityforge/absolute-uri" :
                throw new NotSupportedException(
                    $"RDF relationship '{member.Name}' currently requires Uri identities for a lossless mapped round trip.");
        }

        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(bool) ? "entityforge/rdf-xsd-boolean" :
            type == typeof(int) ? "entityforge/rdf-xsd-integer-int32" :
            type == typeof(long) ? "entityforge/rdf-xsd-integer-int64" :
            type == typeof(double) ? "entityforge/rdf-xsd-double" :
            type == typeof(string) ? "entityforge/rdf-string" :
            type == typeof(Uri) ? "entityforge/rdf-xsd-any-uri" :
            type == typeof(Guid) ? "entityforge/rdf-string-guid" :
            type == typeof(decimal) ? "entityforge/rdf-string-decimal" :
            throw new NotSupportedException(
                $"RDF member '{member.Name}' has no validated lossless codec for CLR type '{type.Name}'.");
    }
}

/// <summary>Starts an RDF binding that also defines its reusable neutral CLR map.</summary>
public static class RdfEntityMap
{
    /// <summary>Begins an RDF mapping while defining its neutral CLR shape.</summary>
    public static RdfEntityMapBuilder<T> For<T>() => new();

    /// <summary>Binds an existing neutral CLR shape to RDF terms.</summary>
    public static RdfEntityMapBuilder<T> BindRdf<T>(this EntityMap<T> map) => new(map);
}

/// <summary>Builds an RDF binding.</summary>
public sealed class RdfEntityMapBuilder<T>
{
    private readonly EntityMapBuilder<T>? _newMap;
    private readonly EntityMap<T>? _map;
    private readonly Dictionary<string, Uri> _bindings = new(StringComparer.Ordinal);
    private Uri? _class;
    private Func<EntityMaterializationContext, T>? _materializer;

    internal RdfEntityMapBuilder() => _newMap = EntityMap.For<T>();

    internal RdfEntityMapBuilder(EntityMap<T> map) => _map = map ?? throw new ArgumentNullException(nameof(map));

    /// <summary>Sets the RDF class represented by this CLR type.</summary>
    public RdfEntityMapBuilder<T> Class(Uri value)
    {
        _class = Absolute(value, nameof(value));
        return this;
    }

    /// <summary>Binds the stable CLR identity to an RDF subject IRI base.</summary>
    public RdfEntityMapBuilder<T> Subject<TValue>(Expression<Func<T, TValue>> member, Uri iri) => Bind(member, iri, EntityMemberRole.Identity);

    /// <summary>Binds a CLR property to an RDF predicate.</summary>
    public RdfEntityMapBuilder<T> Predicate<TValue>(Expression<Func<T, TValue>> member, Uri iri) => Bind(member, iri, EntityMemberRole.Property);

    /// <summary>Binds a CLR relationship to an RDF predicate.</summary>
    public RdfEntityMapBuilder<T> Relationship<TValue>(Expression<Func<T, TValue>> member, Uri iri) => Bind(member, iri, EntityMemberRole.Relationship);

    /// <summary>Sets the CLR materializer and completes the mapping.</summary>
    public RdfEntityMap<T> Materialize(Func<EntityMaterializationContext, T> materializer)
    {
        _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
        return Build();
    }

    /// <summary>Completes the mapping.</summary>
    public RdfEntityMap<T> Build() => new(_map ?? _newMap!.Build(), _class ?? throw new InvalidOperationException("An RDF class IRI is required."), new Dictionary<string, Uri>(_bindings, StringComparer.Ordinal), _materializer);

    private RdfEntityMapBuilder<T> Bind<TValue>(Expression<Func<T, TValue>> member, Uri iri, EntityMemberRole role)
    {
        EntityMemberMap mapped = _map is null ? Add(member, role) : _map.RequireMember(member);
        if (mapped.Role != role)
        {
            throw new ArgumentException($"CLR member '{mapped.Name}' is mapped as {mapped.Role}, not {role}.", nameof(member));
        }

        _bindings.Add(mapped.Name, Absolute(iri, nameof(iri)));
        return this;
    }

    private EntityMemberMap Add<TValue>(Expression<Func<T, TValue>> member, EntityMemberRole role)
    {
        _newMap!.Add(member, role);
        return _newMap.Members[^1];
    }

    private static Uri Absolute(Uri value, string parameter) => value is { IsAbsoluteUri: true } ? value : throw new ArgumentException("An RDF binding requires an absolute IRI.", parameter);
}
