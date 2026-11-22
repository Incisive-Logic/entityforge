using EntityForge.Db.Mapping;
using EntityForge.Query.Projection;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>A named Semantic entity structure, independent of domain CLR classes.</summary>
public sealed class SemanticEntityStructure
{
    internal SemanticEntityStructure(AggregateNode node) => Node = node;

    internal AggregateNode Node
    {
        get;
    }

    /// <summary>Captures executable target policy without producer CLR types or accessors.</summary>
    public SemanticEntityDefinition ExportDefinition() => SemanticEntityDefinition.Capture(Node);

    /// <summary>Reconstructs the named frontend and execution policy from a transported definition.</summary>
    public static SemanticEntityStructure FromDefinition(SemanticEntityDefinition definition) =>
        (definition ?? throw new ArgumentNullException(nameof(definition))).CreateStructure();

    /// <summary>Begins a structure with stable application, type and native concept identities.</summary>
    public static SemanticEntityStructureBuilder For(EntityModelIdentity model,
        PersistentEntityTypeIdentity entityType, SemanticTerm concept) => new(model, entityType, concept);
}

/// <summary>Declares literal member names and codecs for values supplied by any client.</summary>
public sealed class SemanticEntityStructureBuilder
{
    private readonly EntityModelIdentity _model;
    private readonly PersistentEntityTypeIdentity _type;
    private readonly SemanticTerm _concept;
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly List<Scalar> _properties = [];
    private readonly List<Func<EntityMappingManifest, AggregateLink>> _links = [];
    private Scalar? _identity;
    private SemanticIdentityScheme? _scheme;
    private Func<string, object>? _readKey;

    internal SemanticEntityStructureBuilder(EntityModelIdentity model, PersistentEntityTypeIdentity type, SemanticTerm concept)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _type = type ?? throw new ArgumentNullException(nameof(type));
        _concept = concept ?? throw new ArgumentNullException(nameof(concept));
        if (concept.Role != SemanticTermRole.Concept)
        {
            throw new ArgumentException("A structure requires a concept term.", nameof(concept));
        }
    }

    /// <summary>Declares one required string, integral, Guid or absolute-URI identity.</summary>
    public SemanticEntityStructureBuilder Identity<T>(string name, SemanticIdentityScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        if (_identity is not null)
        {
            throw new InvalidOperationException("A structure has exactly one identity.");
        }

        Type type = typeof(T);
        if (type != typeof(string) && type != typeof(Guid) && type != typeof(Uri) &&
            type != typeof(byte) && type != typeof(sbyte) && type != typeof(short) && type != typeof(ushort) &&
            type != typeof(int) && type != typeof(uint) && type != typeof(long))
        {
            throw new NotSupportedException("Identity codecs support strings, integral values, Guid and absolute URI.");
        }

        if (SemanticEntityMap<Entity>.SemanticKind(type) != scheme.ValueKind)
        {
            throw new ArgumentException("The identity codec must match its Semantic scheme.", nameof(scheme));
        }

        AddName(name);
        _identity = new(name, type, false, null!, null!);
        _scheme = scheme;
        _readKey = lexical => new EntityMaterializationContext(lexical, new Dictionary<string, object?>()).IdentityAs<T>()!;
        return this;
    }

    /// <summary>Declares a scalar codec. Optional values are represented by null or an omitted member.</summary>
    public SemanticEntityStructureBuilder Property<T>(string name, SemanticTerm property, bool nullable = false)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (property.Role != SemanticTermRole.Property)
        {
            throw new ArgumentException("A scalar requires a property term.", nameof(property));
        }

        if (Nullable.GetUnderlyingType(typeof(T)) is not null)
        {
            throw new ArgumentException("Use the underlying codec type and the nullable argument.");
        }

        _ = SemanticEntityMap<Entity>.SemanticKind(typeof(T));
        AddName(name);
        _properties.Add(new(name, typeof(T), nullable, property, context =>
        {
            if (!nullable)
            {
                return context.Required<T>(name);
            }

            var value = context.Optional<T>(name);
            return value.HasValue ? value.Value : null;
        }));
        return this;
    }

    /// <summary>Declares a child whose state and deletion follow its root.</summary>
    public SemanticEntityStructureBuilder Owned(string name, SemanticTerm relationship,
        Func<SemanticEntityStructure> target, bool nullable = false) => Link(name, relationship, target, nullable, EntityMemberKind.Owned);

    /// <summary>Declares an independently persisted target, supplied as an EntityReference.</summary>
    public SemanticEntityStructureBuilder Reference(string name, SemanticTerm relationship,
        Func<SemanticEntityStructure> target, bool nullable = false) => Link(name, relationship, target, nullable, EntityMemberKind.Reference);

    private SemanticEntityStructureBuilder Link(string name, SemanticTerm term,
        Func<SemanticEntityStructure> target, bool nullable, EntityMemberKind kind)
    {
        ValidateLink(term, target);
        AddName(name);
        _links.Add(_ => new(Member(name, kind, EntityCollectionKind.None), name, nullable, term, () => target().Node,
            (owner, value) => ((Entity)owner).SetMember(name, ReferenceValue(value, kind))));
        return this;
    }

    /// <summary>Declares non-null scalar elements with explicit set or sequence semantics.</summary>
    public SemanticEntityStructureBuilder ScalarCollection<T>(string name, SemanticTerm relationship,
        EntityCollectionKind kind, bool nullable = false) => Collection<T>(name, relationship, kind, nullable, EntityMemberKind.Scalar, null);

    /// <summary>Declares owned elements with explicit set or sequence semantics.</summary>
    public SemanticEntityStructureBuilder OwnedCollection(string name, SemanticTerm relationship,
        EntityCollectionKind kind, Func<SemanticEntityStructure> target, bool nullable = false)
    {
        ValidateLink(relationship, target);
        return Collection<Entity>(name, relationship, kind, nullable, EntityMemberKind.Owned, target);
    }

    /// <summary>Declares independent reference keys with explicit set or sequence semantics.</summary>
    public SemanticEntityStructureBuilder ReferenceCollection(string name, SemanticTerm relationship,
        EntityCollectionKind kind, Func<SemanticEntityStructure> target, bool nullable = false)
    {
        ValidateLink(relationship, target);
        return Collection<Entity>(name, relationship, kind, nullable, EntityMemberKind.Reference, target);
    }

    private SemanticEntityStructureBuilder Collection<T>(string name, SemanticTerm term, EntityCollectionKind collection,
        bool nullable, EntityMemberKind kind, Func<SemanticEntityStructure>? target)
    {
        ArgumentNullException.ThrowIfNull(term);
        if (term.Role != SemanticTermRole.Relationship)
        {
            throw new ArgumentException("A collection requires a relationship term.");
        }

        if (collection is not (EntityCollectionKind.Set or EntityCollectionKind.OrderedSequence))
        {
            throw new ArgumentOutOfRangeException(nameof(collection));
        }

        if (kind == EntityMemberKind.Scalar && (Nullable.GetUnderlyingType(typeof(T)) is not null || !EntityConvention<Entity>.Scalar(typeof(T))))
        {
            throw new NotSupportedException("Collection elements require a supported non-nullable scalar codec.");
        }

        AddName(name);
        _links.Add(manifest => AggregateCollection.Create<T>(Member(name, kind, collection), name, nullable, _ => null,
            (owner, values) => ((Entity)owner).SetMember(name, values is null ? null : new EntityCollection(collection,
                values.Select(value => ReferenceValue(value, kind)).ToArray())),
            term, kind, collection, target is null ? null : () => target().Node, manifest));
        return this;
    }

    /// <summary>Freezes this structure; recursive target factories resolve when creating its store.</summary>
    public SemanticEntityStructure Build()
    {
        Scalar identity = _identity ?? throw new InvalidOperationException("A structure requires an identity.");
        SemanticIdentityScheme scheme = _scheme!;
        Func<string, object> readKey = _readKey!;
        Scalar[] fields = _properties.ToArray();
        var manifest = new EntityMappingManifest(_model, AggregateNode.Convention, _type, DatabaseProfileId.Semantic,
            _concept.Identifier.AbsoluteUri,
            fields.Select(field => Contract(field, EntityMemberKind.Scalar, SemanticEntityMap<Entity>.SemanticKind(field.Type)))
                .Prepend(Contract(identity, EntityMemberKind.Identity, scheme.ValueKind)).ToArray(),
            fields.Select(field => new EntityProfileMemberContract(field.Name, field.Term.Identifier.AbsoluteUri, "assertion-property"))
                .Prepend(new(identity.Name, scheme.Identifier.AbsoluteUri, "identity-scheme")).ToArray());
        SemanticEntityIdentity Key(object value)
        {
            EntityRecordShape<Entity>.RequireType(value, identity.Type);
            if (value is string text && string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("A mapped identity cannot be empty.");
            }

            return new(_concept, scheme, SemanticMappedValue.Convert(value, scheme.ValueKind));
        }
        SemanticFieldChange[] Fields(Entity entity) => fields.Select(field =>
        {
            entity.Members.TryGetValue(field.Name, out object? value);
            if (value is null && !field.Nullable)
            {
                throw new InvalidOperationException($"Required scalar '{field.Name}' is null.");
            }

            if (value is not null)
            {
                EntityRecordShape<Entity>.RequireType(value, field.Type);
            }

            return (field, value);
        }).Where(pair => pair.value is not null).Select(pair => new SemanticFieldChange(pair.field.Term,
            SemanticFieldChangeKind.Set, SemanticMappedValue.Convert(pair.value!))).ToArray();
        return new(new(typeof(Entity), manifest, new(_concept, fields.Select(field => new EntityFieldProjection(field.Term, field.Name))),
            scheme, vocabulary => new(new Uri("urn:entityforge:aggregate:scalar:" + manifest.Fingerprint), AggregateNode.Convention,
                vocabulary, _concept, scheme, fields.Select(field => new SemanticTargetMemberBinding(field.Name, field.Term,
                    SemanticEntityMap<Entity>.SemanticKind(field.Type))).ToArray()),
            _links.Select(link => link(manifest)).ToArray(), entity => Key(((Entity)entity).Members[identity.Name]!), Key,
            (node, key) =>
            {
                var context = new SemanticEntityMaterializationContext(node, key);
                var values = fields.ToDictionary(field => field.Name, field => field.Read(context), StringComparer.Ordinal);
                values.Add(identity.Name, readKey(key));
                return new Entity(values) { MaterializedIdentity = values[identity.Name] };
            }, entity => Fields((Entity)entity))
        {
            InputIdentity = Key,
            InputFields = Fields,
            CaptureKey = entity => ((Entity)entity).Members[identity.Name]!,
            CaptureScalars = entity => new Entity(manifest.Members.ToDictionary(member => member.Identity,
                member => ((Entity)entity).Members.GetValueOrDefault(member.Identity), StringComparer.Ordinal))
        });
    }

    private static EntityMemberContract Contract(Scalar scalar, EntityMemberKind kind, SemanticValueKind codec) =>
        new(scalar.Name, scalar.Name, kind, EntityCollectionKind.None, scalar.Nullable, SemanticEntityMap<Entity>.ScalarCodec(scalar.Type, codec), 1);

    private static AggregateMember Member(string name, EntityMemberKind kind, EntityCollectionKind collection) =>
        new(name, kind, collection, entity => ((Entity)entity).Members.GetValueOrDefault(name));

    private static object? ReferenceValue(object? value, EntityMemberKind kind) =>
        value is null || kind != EntityMemberKind.Reference ? value :
            new EntityReference(((Entity)value).MaterializedIdentity!);

    private static void ValidateLink(SemanticTerm term, Func<SemanticEntityStructure> target)
    {
        ArgumentNullException.ThrowIfNull(term);
        ArgumentNullException.ThrowIfNull(target);
        if (term.Role != SemanticTermRole.Relationship)
        {
            throw new ArgumentException("A link requires a relationship term.");
        }
    }

    private void AddName(string name)
    {
        EntityMemberName.Validate(name);
        if (!_names.Add(name))
        {
            throw new ArgumentException("A member may have only one mapping.", nameof(name));
        }
    }

    private sealed record Scalar(string Name, Type Type, bool Nullable, SemanticTerm Term,
        Func<SemanticEntityMaterializationContext, object?> Read);
}
