using System.Globalization;
using System.Diagnostics;
using System.Linq.Expressions;
using EntityForge.Db.Mapping;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>Classifies a failure to materialise a neutral entity document as a C# value.</summary>
public enum SemanticEntityMaterializationFailure
{
    /// <summary>A required projected field has no value.</summary>
    MissingField,

    /// <summary>A scalar projection contains more than one value.</summary>
    MultipleValues,

    /// <summary>A projected value cannot be materialized as the requested CLR type.</summary>
    TypeMismatch
}

/// <summary>Reports an explicit typed-materialisation policy violation.</summary>
public sealed class SemanticEntityMaterializationException : EntityForgeDbException
{
    internal SemanticEntityMaterializationException(
        SemanticEntityMaterializationFailure failure, string fieldName, string message)
        : base(message)
    {
        Failure = failure;
        FieldName = fieldName;
    }

    /// <summary>Gets the category of materialisation failure.</summary>
    public SemanticEntityMaterializationFailure Failure
    {
        get;
    }

    /// <summary>Gets the projection field that failed.</summary>
    public string FieldName
    {
        get;
    }
}

/// <summary>Pairs a typed projected value with the evidence and semantic terms that support it.</summary>
/// <param name="Value">Materialized CLR value.</param>
/// <param name="SourceTerm">Original semantic term supporting the value.</param>
/// <param name="ProjectedTerm">Term under which the value is projected.</param>
/// <param name="Evidence">References to supporting evidence records.</param>
public sealed record SemanticEntityValue<T>(
    T Value,
    SemanticTerm SourceTerm,
    SemanticTerm ProjectedTerm,
    IReadOnlyList<EvidenceReference> Evidence);

/// <summary>Represents an explicitly present or absent typed semantic value.</summary>
/// <param name="HasValue">Whether a value is explicitly present; independent of the CLR default value.</param>
/// <param name="Value">Present value, or default when absent.</param>
public sealed record SemanticOptional<T>(bool HasValue, T? Value)
{
    /// <summary>Creates an absent value.</summary>
    public static SemanticOptional<T> None { get; } = new(false, default);

    /// <summary>Creates a present value.</summary>
    public static SemanticOptional<T> Some(T value) => new(true, value);
}

/// <summary>Provides deliberate scalar and evidence-aware access while constructing one C# object.</summary>
public sealed class SemanticEntityMaterializationContext
{
    private readonly EntityProjectionNode _node;
    private readonly string? _mappedIdentity;

    internal SemanticEntityMaterializationContext(EntityProjectionNode node, string? mappedIdentity = null)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _mappedIdentity = mappedIdentity;
    }

    /// <summary>Gets the stable textual identity of the projected entity.</summary>
    public string Identity => _mappedIdentity ?? (_node.Identity switch
    {
        IdentifierValue identifier => identifier.Identifier,
        BlankNodeValue blank => blank.Identifier,
        _ => throw new InvalidOperationException("The projected entity has an unsupported identity shape.")
    });

    /// <summary>Gets the semantic concepts asserted for the projected entity.</summary>
    public IReadOnlyList<SemanticTerm> Types => _node.Types;

    /// <summary>Returns every value of a projected field, preserving evidence and semantic-term metadata.</summary>
    public IReadOnlyList<SemanticEntityValue<T>> Values<T>(string fieldName) =>
        Field(fieldName).Values.Select(value => new SemanticEntityValue<T>(
            Convert<T>(value.Value, fieldName), value.SourceTerm, value.ProjectedTerm, value.Evidence)).ToArray();

    /// <summary>Returns exactly one value or fails explicitly when the field is missing or ambiguous.</summary>
    public T Required<T>(string fieldName)
    {
        IReadOnlyList<SemanticEntityValue<T>> values = Values<T>(fieldName);
        if (values.Count == 0)
        {
            throw Failure(SemanticEntityMaterializationFailure.MissingField, fieldName,
                "A required projected field has no value.");
        }

        if (values.Count != 1)
        {
            throw Failure(SemanticEntityMaterializationFailure.MultipleValues, fieldName,
                $"A scalar projected field has {values.Count} competing values.");
        }

        return values[0].Value;
    }

    /// <summary>Returns zero or one value or fails explicitly when the field is ambiguous.</summary>
    public SemanticOptional<T> Optional<T>(string fieldName)
    {
        IReadOnlyList<SemanticEntityValue<T>> values = Values<T>(fieldName);
        if (values.Count > 1)
        {
            throw Failure(SemanticEntityMaterializationFailure.MultipleValues, fieldName,
                $"An optional scalar projected field has {values.Count} competing values.");
        }

        return values.Count == 0 ? SemanticOptional<T>.None : SemanticOptional<T>.Some(values[0].Value);
    }

    /// <summary>Returns the identities projected through one relationship, including unresolved references.</summary>
    public IReadOnlyList<string> RelationshipIdentities(string relationshipName) =>
        Relationship(relationshipName).Items.Select(item => item switch
        {
            EntityProjectionNode node => IdentityOf(node.Identity),
            EntityProjectionReference reference => IdentityOf(reference.Identity),
            _ => throw new InvalidOperationException("The projected relationship has an unsupported item shape.")
        }).ToArray();

    /// <summary>Materialises every expanded entity projected through one relationship.</summary>
    public IReadOnlyList<T> Related<T>(string relationshipName, SemanticEntityMap<T> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return Relationship(relationshipName).Items.Select(item => item switch
        {
            EntityProjectionNode node => map.Materialize(node),
            EntityProjectionReference => throw new InvalidOperationException(
                $"Relationship '{relationshipName}' contains a reference rather than an expanded entity."),
            _ => throw new InvalidOperationException("The projected relationship has an unsupported item shape.")
        }).ToArray();
    }

    private EntityProjectionField Field(string fieldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        return _node.Fields.SingleOrDefault(field => String.Equals(field.Name, fieldName, StringComparison.Ordinal))
            ?? new EntityProjectionField(fieldName, []);
    }

    private EntityProjectionRelationship Relationship(string relationshipName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationshipName);
        return _node.Relationships.SingleOrDefault(relationship =>
                   String.Equals(relationship.Name, relationshipName, StringComparison.Ordinal))
            ?? new EntityProjectionRelationship(relationshipName, []);
    }

    private static string IdentityOf(GraphElementIdentity identity) => identity switch
    {
        IdentifierValue identifier => identifier.Identifier,
        BlankNodeValue blank => blank.Identifier,
        _ => throw new InvalidOperationException("The projected entity has an unsupported identity shape.")
    };

    private static T Convert<T>(QueryValue value, string fieldName)
    {
        object? converted;
        try
        {
            converted = value switch
            {
                LiteralValue literal when typeof(T) == typeof(string) => literal.LexicalValue,
                LiteralValue literal when typeof(T) == typeof(bool) => Boolean.Parse(literal.LexicalValue),
                LiteralValue literal when typeof(T) == typeof(byte) => Byte.Parse(literal.LexicalValue, CultureInfo.InvariantCulture),
                LiteralValue literal when typeof(T) == typeof(sbyte) => SByte.Parse(literal.LexicalValue, CultureInfo.InvariantCulture),
                LiteralValue literal when typeof(T) == typeof(short) => Int16.Parse(literal.LexicalValue, CultureInfo.InvariantCulture),
                LiteralValue literal when typeof(T) == typeof(ushort) => UInt16.Parse(literal.LexicalValue, CultureInfo.InvariantCulture),
                LiteralValue literal when typeof(T) == typeof(uint) => UInt32.Parse(literal.LexicalValue, CultureInfo.InvariantCulture),
                LiteralValue literal when typeof(T) == typeof(long) => Int64.Parse(literal.LexicalValue, CultureInfo.InvariantCulture),
                LiteralValue literal when typeof(T) == typeof(int) => Int32.Parse(literal.LexicalValue, CultureInfo.InvariantCulture),
                LiteralValue literal when typeof(T) == typeof(float) => Single.Parse(literal.LexicalValue, CultureInfo.InvariantCulture),
                LiteralValue literal when typeof(T) == typeof(double) => Double.Parse(literal.LexicalValue, CultureInfo.InvariantCulture),
                LiteralValue literal when typeof(T) == typeof(Guid) => Guid.Parse(literal.LexicalValue),
                LiteralValue literal when typeof(T) == typeof(Uri) => new Uri(literal.LexicalValue, UriKind.Absolute),
                IdentifierValue identifier when typeof(T) == typeof(Uri) => new Uri(identifier.Identifier, UriKind.Absolute),
                LiteralValue literal when typeof(T) == typeof(decimal) => Decimal.Parse(literal.LexicalValue, CultureInfo.InvariantCulture),
                LiteralValue literal when typeof(T) == typeof(DateOnly) => DateOnly.Parse(literal.LexicalValue, CultureInfo.InvariantCulture),
                LiteralValue literal when typeof(T) == typeof(TimeOnly) => TimeOnly.Parse(literal.LexicalValue, CultureInfo.InvariantCulture),
                LiteralValue literal when typeof(T) == typeof(DateTimeOffset) => DateTimeOffset.Parse(literal.LexicalValue, CultureInfo.InvariantCulture),
                LiteralValue literal when typeof(T) == typeof(DateTime) => DateTimeOffset.Parse(literal.LexicalValue, CultureInfo.InvariantCulture).UtcDateTime,
                LiteralValue literal when typeof(T) == typeof(TimeSpan) => TimeSpan.Parse(literal.LexicalValue, CultureInfo.InvariantCulture),
                LiteralValue literal when typeof(T) == typeof(byte[]) => System.Convert.FromBase64String(literal.LexicalValue),
                IdentifierValue identifier when typeof(T) == typeof(string) => identifier.Identifier,
                JsonValue json when typeof(T) == typeof(string) => json.Value.ToString(),
                _ => null
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw Failure(SemanticEntityMaterializationFailure.TypeMismatch, fieldName,
                $"Projected value cannot be materialised as '{typeof(T).Name}'.");
        }
        if (converted is T typed)
        {
            return typed;
        }

        throw Failure(SemanticEntityMaterializationFailure.TypeMismatch, fieldName,
            $"Projected value '{value.GetType().Name}' cannot be materialised as '{typeof(T).Name}'.");
    }

    private static SemanticEntityMaterializationException Failure(
        SemanticEntityMaterializationFailure failure, string fieldName, string message) =>
        new(failure, fieldName, $"Field '{fieldName}': {message}");
}

/// <summary>Defines one explicit concept-to-C# mapping over a neutral entity projection.</summary>
public sealed class SemanticEntityMap<T>
{
    private readonly Func<SemanticEntityMaterializationContext, T> _materialize;

    /// <summary>Creates an explicit mapping from one semantic concept and projection to a C# type.</summary>
    public SemanticEntityMap(
        SemanticTerm concept,
        IEnumerable<EntityFieldProjection> fields,
        Func<SemanticEntityMaterializationContext, T> materialize,
        IEnumerable<EntityRelationshipProjection>? relationships = null)
        : this(EntityForge.Db.Mapping.EntityMap.For<T>().Build(), concept, fields, materialize,
            relationships, null, [])
    {
    }

    internal SemanticEntityMap(
        EntityMap<T> entityMap,
        SemanticTerm concept,
        IEnumerable<EntityFieldProjection> fields,
        Func<SemanticEntityMaterializationContext, T> materialize,
        IEnumerable<EntityRelationshipProjection>? relationships,
        SemanticIdentityMemberBinding? identityBinding,
        IEnumerable<SemanticMemberBinding> memberBindings)
    {
        EntityMap = entityMap ?? throw new ArgumentNullException(nameof(entityMap));
        EntityMemberMap? unsupported = entityMap.Members.FirstOrDefault(item =>
            item.Kind is EntityMemberKind.Owned ||
            item.CollectionKind is EntityCollectionKind.OrderedSequence);
        if (unsupported is not null)
        {
            throw new NotSupportedException(
                $"Semantic member '{unsupported.Name}' requires an owned-value or ordered-sequence encoding that is not yet supported.");
        }

        Projection = new EntityProjectionDefinition(concept, fields, relationships);
        _materialize = materialize ?? throw new ArgumentNullException(nameof(materialize));
        IdentityBinding = identityBinding;
        SemanticMemberBinding[] bindingCopy = memberBindings.ToArray();
        IGrouping<(EntityMemberRole Role, string Term), SemanticMemberBinding>? collision = bindingCopy
            .GroupBy(item => (item.Role, item.Term.Identifier.AbsoluteUri))
            .FirstOrDefault(group => group.Count() > 1);
        if (collision is not null)
        {
            throw new ArgumentException(
                $"Semantic term '{collision.Key.Term}' is bound to more than one writable {collision.Key.Role} member.",
                nameof(memberBindings));
        }

        MemberBindings = Array.AsReadOnly(bindingCopy);
    }

    /// <summary>Gets the immutable profile-neutral CLR mapping reusable by other profile bindings.</summary>
    public EntityMap<T> EntityMap
    {
        get;
    }

    /// <summary>Gets the neutral projection used by this mapping.</summary>
    public EntityProjectionDefinition Projection
    {
        get;
    }

    /// <summary>Gets the Semantic identity binding when the map supports persistence.</summary>
    public SemanticIdentityMemberBinding? IdentityBinding
    {
        get;
    }

    /// <summary>Gets the Semantic field and relationship bindings keyed by neutral CLR member.</summary>
    public IReadOnlyList<SemanticMemberBinding> MemberBindings
    {
        get;
    }

    /// <summary>Creates the durable contract represented by this writable Semantic mapping.</summary>
    public EntityMappingManifest CreateManifest(EntityModelIdentity model,
        PersistentEntityTypeIdentity entityType, string convention = "explicit-v1",
        IReadOnlyDictionary<string, string>? persistentMemberIdentities = null)
    {
        SemanticIdentityMemberBinding identity = IdentityBinding ?? throw new InvalidOperationException(
            "A query-only Semantic map cannot define a persistence manifest.");
        var bindings = MemberBindings.ToDictionary(item => item.MemberName, item =>
            new EntityProfileMemberContract(item.MemberName, item.Term.Identifier.AbsoluteUri,
                item.Role is EntityMemberRole.Property ? "assertion-property" : "assertion-relationship",
                item.TargetConceptBinding, item.TargetIdentityBinding,
                item.Role is EntityMemberRole.Relationship ? DirectionName(item.Direction) : null),
            StringComparer.Ordinal);
        bindings.Add(identity.MemberName, new(identity.MemberName, identity.Scheme.Identifier.AbsoluteUri,
            "identity-scheme"));
        return EntityMappingManifestFactory.Create(EntityMap, model, entityType, DatabaseProfileId.Semantic,
            Projection.Concept.Identifier.AbsoluteUri, bindings, Codec, convention, persistentMemberIdentities);
    }

    /// <summary>
    /// Projects this writable POCO map into the same source-neutral Semantic target binding used by
    /// Pipeline transforms. CLR accessors and construction policy remain outside the returned contract.
    /// </summary>
    public SemanticTargetBinding CreateTargetBinding(
        Uri identifier,
        string version,
        SemanticModelReference targetVocabulary,
        IReadOnlyDictionary<string, string>? persistentMemberIdentities = null)
    {
        SemanticIdentityMemberBinding identity = IdentityBinding ?? throw new InvalidOperationException(
            "A query-only Semantic map cannot define a target binding.");
        if (persistentMemberIdentities is not null && persistentMemberIdentities.Keys.Any(name =>
                EntityMap.Members.All(member => member.Name != name)))
        {
            throw new ArgumentException("Persistent member identities contain a member absent from the logical map.",
                nameof(persistentMemberIdentities));
        }

        string Slot(string memberName) => persistentMemberIdentities is not null &&
            persistentMemberIdentities.TryGetValue(memberName, out string? slot) ? slot : memberName;
        SemanticTargetMemberBinding[] members = MemberBindings.Select(binding =>
        {
            EntityMemberMap member = EntityMap.Members.Single(item => item.Name == binding.MemberName);
            return binding.Role switch
            {
                EntityMemberRole.Property => new SemanticTargetMemberBinding(
                    Slot(binding.MemberName), binding.Term, SemanticKind(member)),
                EntityMemberRole.Relationship => new SemanticTargetMemberBinding(
                    Slot(binding.MemberName), binding.Term, targetConcept: binding.TargetConcept,
                    targetIdentityScheme: binding.TargetIdentityScheme),
                _ => throw new InvalidOperationException(
                    $"Semantic member '{binding.MemberName}' has unsupported role '{binding.Role}'.")
            };
        }).ToArray();
        return new SemanticTargetBinding(identifier, version, targetVocabulary, Projection.Concept,
            identity.Scheme, members);
    }

    private string Codec(EntityMemberMap member)
    {
        if (member.Role is EntityMemberRole.Identity)
        {
            return ScalarCodec(member, IdentityBinding?.Scheme.ValueKind ?? throw new InvalidOperationException(
                "A writable Semantic mapping requires an identity scheme."));
        }

        if (member.Role is EntityMemberRole.Relationship)
        {
            return MemberBindings.Single(item => item.MemberName == member.Name).TargetIdentityCodec ??
                throw new InvalidOperationException(
                    $"Semantic relationship '{member.Name}' requires a writable target identity mapping.");
        }

        return ScalarCodec(member, SemanticKind(member));
    }

    internal static string ScalarCodec(EntityMemberMap member, SemanticValueKind kind)
        => ScalarCodec(EntityMappingManifestFactory.ValueType(member), kind);

    internal static string ScalarCodec(Type type, SemanticValueKind kind)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        string range = type == typeof(byte) ? "/uint8" : type == typeof(sbyte) ? "/int8" :
            type == typeof(short) ? "/int16" : type == typeof(ushort) ? "/uint16" :
            type == typeof(int) ? "/int32" : type == typeof(uint) ? "/uint32" :
            type == typeof(long) ? "/int64" : type == typeof(float) ? "/float32" :
            type == typeof(double) ? "/float64" : type == typeof(DateTime) ? "/datetime" :
            type == typeof(DateTimeOffset) ? "/datetime-offset" : string.Empty;
        return SemanticCodec(kind) + range;
    }

    internal static SemanticValueKind SemanticKind(EntityMemberMap member)
        => SemanticKind(EntityMappingManifestFactory.ValueType(member));

    internal static SemanticValueKind SemanticKind(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(bool) ? SemanticValueKind.Boolean :
            type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) ||
            type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(long)
                ? SemanticValueKind.Integer :
            type == typeof(float) || type == typeof(double) ? SemanticValueKind.FloatingPoint :
            type == typeof(decimal) ? SemanticValueKind.Decimal :
            type == typeof(string) ? SemanticValueKind.String :
            type == typeof(DateOnly) ? SemanticValueKind.Date :
            type == typeof(TimeOnly) ? SemanticValueKind.Time :
            type == typeof(DateTime) || type == typeof(DateTimeOffset) ? SemanticValueKind.DateTime :
            type == typeof(TimeSpan) ? SemanticValueKind.Duration :
            type == typeof(byte[]) ? SemanticValueKind.Binary :
            type == typeof(Uri) ? SemanticValueKind.AbsoluteUri :
            type == typeof(Guid) ? SemanticValueKind.Guid :
            throw new NotSupportedException(
                $"No supported Semantic codec for CLR type '{type.Name}'.");
    }

    private static string SemanticCodec(SemanticValueKind kind) =>
        $"entityforge/semantic/{kind.ToString().ToLowerInvariant()}";

    private static string DirectionName(EntityRelationshipDirection direction) => direction switch
    {
        EntityRelationshipDirection.Outgoing => "outgoing",
        EntityRelationshipDirection.Incoming => "incoming",
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    internal T Materialize(EntityProjectionNode node) =>
        _materialize(new SemanticEntityMaterializationContext(node, MappedIdentity(node)));

    internal T Materialize(EntityProjectionNode node, string mappedIdentity) =>
        _materialize(new SemanticEntityMaterializationContext(node, mappedIdentity));

    private string? MappedIdentity(EntityProjectionNode node)
    {
        if (IdentityBinding is null || node.Identity is not IdentifierValue identifier)
        {
            return null;
        }

        string prefix = $"{Projection.Concept.Identifier.AbsoluteUri}|{IdentityBinding.Scheme.Identifier.AbsoluteUri}|";
        return identifier.Identifier.StartsWith(prefix, StringComparison.Ordinal)
            ? identifier.Identifier[prefix.Length..] : null;
    }

    /// <summary>Gets the canonical semantic identity produced by this map for an entity.</summary>
    public SemanticEntityIdentity GetIdentity(T entity)
    {
        SemanticIdentityMemberBinding binding = IdentityBinding ?? throw new InvalidOperationException(
            "A Semantic entity map must bind an identity before it can persist POCOs.");
        EntityMemberMap member = EntityMap.Identity ?? throw new InvalidOperationException(
            "The neutral entity map does not define an identity member.");
        object value = member.Read(entity!) ?? throw new InvalidOperationException(
            "A mapped Semantic identity cannot be null.");
        return new(Projection.Concept, binding.Scheme, SemanticMappedValue.Convert(value, binding.Scheme.ValueKind));
    }

    internal SemanticEntityIdentity GetIdentityValue(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        SemanticIdentityMemberBinding binding = IdentityBinding ?? throw new InvalidOperationException(
            "A Semantic entity map must bind an identity before it can persist POCOs.");
        return new(Projection.Concept, binding.Scheme, SemanticMappedValue.Convert(value, binding.Scheme.ValueKind));
    }

    internal SemanticTerm Property<TValue>(Expression<Func<T, TValue>> expression)
    {
        EntityMemberMap member = EntityMap.RequireMember(expression);
        SemanticMemberBinding binding = MemberBindings.SingleOrDefault(item =>
            StringComparer.Ordinal.Equals(item.MemberName, member.Name)) ?? throw new ArgumentException(
                $"CLR member '{member.Name}' has no Semantic binding.", nameof(expression));
        if (binding.Role is not EntityMemberRole.Property)
        {
            throw new ArgumentException($"CLR member '{member.Name}' is not a Semantic property.", nameof(expression));
        }

        return binding.Term;
    }
}

/// <summary>Starts readable, explicit concept-to-C# mapping definitions.</summary>
public static class SemanticEntityMap
{
    /// <summary>Begins a mapping for one C# type and semantic concept.</summary>
    public static SemanticEntityMapBuilder<T> For<T>(SemanticTerm concept) => new(concept);

    /// <summary>Starts a Semantic binding over an existing neutral CLR mapping.</summary>
    public static SemanticEntityMapBuilder<T> Bind<T>(EntityMap<T> map, SemanticTerm concept) =>
        new(map, concept);
}

/// <summary>Binds the neutral identity member to a governed Semantic identity scheme.</summary>
/// <param name="MemberName">CLR identity member name.</param>
/// <param name="Scheme">Semantic identity scheme assigned to the member.</param>
public sealed record SemanticIdentityMemberBinding(string MemberName, SemanticIdentityScheme Scheme);

/// <summary>Binds one neutral CLR property or relationship to a Semantic term.</summary>
public sealed class SemanticMemberBinding
{
    internal SemanticMemberBinding(string memberName, SemanticTerm term, EntityMemberRole role,
        string projectionName, Func<object, SemanticEntityIdentity>? targetIdentity = null,
        SemanticTerm? targetConcept = null, SemanticIdentityScheme? targetScheme = null,
        EntityRelationshipDirection direction = EntityRelationshipDirection.Outgoing)
    {
        MemberName = memberName;
        Term = term;
        Role = role;
        ProjectionName = projectionName;
        TargetIdentity = targetIdentity;
        TargetConcept = targetConcept;
        TargetIdentityScheme = targetScheme;
        TargetConceptBinding = targetConcept?.Identifier.AbsoluteUri;
        TargetIdentityBinding = targetScheme?.Identifier.AbsoluteUri;
        TargetIdentityCodec = targetScheme is null ? null :
            $"entityforge/semantic/{targetScheme.ValueKind.ToString().ToLowerInvariant()}";
        Direction = direction;
    }

    /// <summary>Gets the bound CLR member name.</summary>
    public string MemberName
    {
        get;
    }

    /// <summary>Gets the semantic term assigned to the member.</summary>
    public SemanticTerm Term
    {
        get;
    }

    /// <summary>Gets whether the member represents a property or relationship.</summary>
    public EntityMemberRole Role
    {
        get;
    }

    /// <summary>Gets the field name used in the neutral entity projection.</summary>
    public string ProjectionName
    {
        get;
    }

    internal string? TargetConceptBinding
    {
        get;
    }

    internal string? TargetIdentityBinding
    {
        get;
    }

    internal SemanticTerm? TargetConcept
    {
        get;
    }

    internal SemanticIdentityScheme? TargetIdentityScheme
    {
        get;
    }

    internal EntityRelationshipDirection Direction
    {
        get;
    }

    internal Func<object, SemanticEntityIdentity>? TargetIdentity
    {
        get;
    }

    internal string? TargetIdentityCodec
    {
        get;
    }
}

internal static class SemanticMappedValue
{
    public static SemanticValue Convert(object value, SemanticValueKind? requiredKind = null)
        => SemanticValueConverter.FromClr(value, requiredKind);

    public static string Lexical(SemanticValue value) => SemanticValueConverter.ToLexical(value);

    public static QueryValue QueryValue(object value)
    {
        if (value is QueryValue query)
        {
            return query;
        }

        SemanticValue semantic = Convert(value);
        return semantic switch
        {
            BooleanSemanticValue item => new LiteralValue(item.Value ? "true" : "false", QueryScalarKind.Boolean),
            IntegerSemanticValue item => new LiteralValue(item.Value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer),
            FloatingPointSemanticValue item => new LiteralValue(item.Value.ToString("R", CultureInfo.InvariantCulture), QueryScalarKind.FloatingPoint),
            DecimalSemanticValue item => new LiteralValue(item.Value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Decimal),
            StringSemanticValue item => new LiteralValue(item.Value),
            DateSemanticValue item => new LiteralValue(item.Value.ToString("O", CultureInfo.InvariantCulture), QueryScalarKind.Date),
            TimeSemanticValue item => new LiteralValue(item.Value.ToString("O", CultureInfo.InvariantCulture), QueryScalarKind.Time),
            DateTimeSemanticValue item => new LiteralValue(item.Value.ToString("O", CultureInfo.InvariantCulture), QueryScalarKind.DateTime),
            DurationSemanticValue item => new LiteralValue(item.Value.ToString("c", CultureInfo.InvariantCulture), QueryScalarKind.Duration),
            BinarySemanticValue item => new LiteralValue(System.Convert.ToBase64String(item.Value.Span), QueryScalarKind.Binary),
            AbsoluteUriSemanticValue item => new LiteralValue(item.Value.AbsoluteUri, QueryScalarKind.AbsoluteUri),
            GuidSemanticValue item => new LiteralValue(item.Value.ToString("D"), QueryScalarKind.Guid),
            _ => throw new NotSupportedException($"Semantic value '{semantic.Kind}' cannot be used in a typed comparison.")
        };
    }
}

/// <summary>Builds a concept projection before attaching its C# materialiser.</summary>
public sealed class SemanticEntityMapBuilder<T>
{
    private readonly SemanticTerm _concept;
    private readonly EntityMapBuilder<T>? _newEntityMap;
    private readonly EntityMap<T>? _existingEntityMap;
    private readonly List<EntityFieldProjection> _fields = [];
    private readonly List<EntityRelationshipProjection> _relationships = [];
    private readonly List<SemanticMemberBinding> _memberBindings = [];
    private SemanticIdentityMemberBinding? _identityBinding;

    internal SemanticEntityMapBuilder(SemanticTerm concept)
    {
        ArgumentNullException.ThrowIfNull(concept);
        _concept = concept.Role is SemanticTermRole.Concept ? concept : throw new ArgumentException(
            "A typed entity map requires a concept term.", nameof(concept));
        _newEntityMap = EntityMap.For<T>();
    }

    internal SemanticEntityMapBuilder(EntityMap<T> map, SemanticTerm concept) : this(concept)
    {
        _existingEntityMap = map ?? throw new ArgumentNullException(nameof(map));
        _newEntityMap = null;
    }

    /// <summary>Binds the neutral identity selected from the CLR type.</summary>
    public SemanticEntityMapBuilder<T> Identity<TValue>(
        Expression<Func<T, TValue>> member, SemanticIdentityScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        EntityMemberMap mapped = Map(member, EntityMemberRole.Identity);
        _identityBinding = _identityBinding is null
            ? new(mapped.Name, scheme)
            : throw new InvalidOperationException("A Semantic entity map can bind only one identity member.");
        return this;
    }

    /// <summary>Adds one explicitly named field to the neutral projection.</summary>
    public SemanticEntityMapBuilder<T> Property(
        SemanticTerm property,
        string name,
        EntityFieldValueSelection valueSelection = EntityFieldValueSelection.AllCandidates,
        IEnumerable<Uri>? preferredProviders = null)
    {
        _fields.Add(new EntityFieldProjection(property, name, valueSelection, preferredProviders));
        return this;
    }

    /// <summary>Adds and binds one CLR property while deriving its projection name from the member.</summary>
    public SemanticEntityMapBuilder<T> Property<TValue>(
        Expression<Func<T, TValue>> member,
        SemanticTerm property,
        EntityFieldValueSelection valueSelection = EntityFieldValueSelection.AllCandidates,
        IEnumerable<Uri>? preferredProviders = null)
    {
        EntityMemberMap mapped = Map(member, EntityMemberRole.Property);
        string name = mapped.Name;
        _fields.Add(new(property, name, valueSelection, preferredProviders));
        _memberBindings.Add(new(mapped.Name, property, mapped.Role, name));
        return this;
    }

    /// <summary>Adds one expanded typed relationship to the neutral projection.</summary>
    public SemanticEntityMapBuilder<T> Relationship<TRelated>(
        SemanticTerm relationship,
        string name,
        SemanticEntityMap<TRelated> target,
        EntityRelationshipDirection direction = EntityRelationshipDirection.Outgoing)
    {
        ArgumentNullException.ThrowIfNull(target);
        _relationships.Add(new EntityRelationshipProjection(relationship, name, target.Projection, direction));
        return this;
    }

    /// <summary>Adds and binds one CLR relationship while reusing the target's neutral mapping.</summary>
    public SemanticEntityMapBuilder<T> Relationship<TValue, TRelated>(
        Expression<Func<T, TValue>> member,
        SemanticTerm relationship,
        SemanticEntityMap<TRelated> target,
        EntityRelationshipDirection direction = EntityRelationshipDirection.Outgoing)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (direction is not EntityRelationshipDirection.Outgoing)
        {
            throw new NotSupportedException(
                "A CLR-bound Semantic relationship must be outgoing because entity contributions own outgoing assertions. Incoming relationships remain available to query-only projections.");
        }

        EntityMemberMap mapped = Map(member, EntityMemberRole.Relationship);
        string name = mapped.Name;
        _relationships.Add(new(relationship, name, target.Projection, direction));
        _memberBindings.Add(new(mapped.Name, relationship, mapped.Role, name,
            value => target.GetIdentity((TRelated)value), target.Projection.Concept,
            target.IdentityBinding?.Scheme, direction));
        return this;
    }

    /// <summary>Completes the mapping with an explicit C# construction function.</summary>
    public SemanticEntityMap<T> Materialize(Func<SemanticEntityMaterializationContext, T> materialize) =>
        new(_existingEntityMap ?? _newEntityMap!.Build(), _concept, _fields, materialize, _relationships,
            _identityBinding, _memberBindings);

    private EntityMemberMap Map<TValue>(Expression<Func<T, TValue>> member, EntityMemberRole role)
    {
        if (_existingEntityMap is not null)
        {
            EntityMemberMap mapped = _existingEntityMap.RequireMember(member);
            if (mapped.Role != role)
            {
                throw new ArgumentException(
                    $"CLR member '{mapped.Name}' is mapped as {mapped.Role}, not {role}.", nameof(member));
            }

            return mapped;
        }
        _newEntityMap!.Add(member, role);
        return _newEntityMap.Members[^1];
    }
}

/// <summary>Starts a Semantic binding from an independently declared neutral map.</summary>
public static class SemanticEntityMapBindingExtensions
{
    /// <summary>Begins binding a neutral entity map to a Semantic concept.</summary>
    /// <param name="map">Neutral CLR-member mapping to bind.</param>
    /// <param name="concept">Semantic concept represented by the mapped entity.</param>
    /// <typeparam name="T">The mapped CLR type.</typeparam>
    /// <returns>A builder for the concept-specific identity, property and relationship bindings.</returns>
    public static SemanticEntityMapBuilder<T> BindSemantic<T>(this EntityMap<T> map,
        SemanticTerm concept) => SemanticEntityMap.Bind(map, concept);
}

/// <summary>Returns a bounded typed page together with the exact snapshot and neutral evidence metadata.</summary>
/// <param name="Items">Materialized root entities in result order.</param>
/// <param name="SnapshotVersion">Exact snapshot used for this page.</param>
/// <param name="NextContinuation">Opaque next-page token, or null when no continuation is supplied.</param>
/// <param name="Evidence">Evidence records associated with the projected page.</param>
/// <param name="Diagnostics">Projection diagnostics accompanying materialization.</param>
/// <param name="MaterializationDuration">Measured time spent materializing the typed page.</param>
public sealed record SemanticEntityPage<T>(
    IReadOnlyList<T> Items,
    DatabaseVersion SnapshotVersion,
    string? NextContinuation,
    IReadOnlyList<EvidenceRecord> Evidence,
    IReadOnlyList<EntityProjectionProjectionDiagnostic> Diagnostics,
    TimeSpan MaterializationDuration);

/// <summary>Base event emitted by a framework-neutral typed Semantic entity watch.</summary>
/// <param name="Version">Snapshot or committed version associated with the watch event.</param>
public abstract record SemanticEntityWatchEvent<T>(DatabaseVersion Version);

/// <summary>Reports that a stable query result may have changed and can be refreshed by the application.</summary>
/// <param name="Version">Committed version associated with the invalidation.</param>
/// <param name="TransactionId">Identity of the transaction that may affect the query.</param>
/// <param name="Cursor">Change-feed position associated with the invalidation.</param>
/// <param name="RelevantEffects">Effect categories that caused the invalidation.</param>
public sealed record SemanticEntityQueryInvalidated<T>(DatabaseVersion Version, Guid TransactionId,
    DatabaseChangeCursor Cursor, IReadOnlyList<SemanticCommittedEffectKind> RelevantEffects)
    : SemanticEntityWatchEvent<T>(Version);

/// <summary>Provides the latest materialized value for a watched single-entity query.</summary>
/// <param name="Value">Latest materialized single-entity result.</param>
/// <param name="Version">Snapshot version of the materialized value.</param>
public sealed record SemanticEntityChanged<T>(T Value, DatabaseVersion Version)
    : SemanticEntityWatchEvent<T>(Version);

/// <summary>Reports that a watched single-entity query no longer returns a value.</summary>
/// <param name="Identity">Identity of the entity no longer returned by the watched query.</param>
/// <param name="Version">Snapshot version at which the entity is absent from the result.</param>
public sealed record SemanticEntityRemoved<T>(string Identity, DatabaseVersion Version)
    : SemanticEntityWatchEvent<T>(Version);

/// <summary>Builds and executes a typed semantic entity query over the neutral query contracts.</summary>
public sealed class SemanticEntityQuery<T>
{
    private readonly ISemanticQueryService _database;
    private readonly SemanticEntityMap<T> _map;
    private readonly List<EntityQueryFilter> _filters = [];
    private readonly List<EntityQueryOrdering> _orderings = [];
    private readonly List<EntityQueryScope> _scopes = [];
    private EntityQueryPage? _page;
    private EntityIdentitySelection? _identities;
    private DatabaseVersion? _snapshot;
    private SemanticAssertionPolicy? _assertionPolicy;
    private EntityProjectionProjectionOptions _options = new();

    private readonly IEntityMappingStoreContext? _mapping;

    private ValueTask<TResult> Run<TResult>(Func<ValueTask<TResult>> operation, CancellationToken token) =>
        _mapping is null ? operation() : _mapping.ExecuteAsync(operation, token);

    internal SemanticEntityQuery(ISemanticQueryService database, SemanticEntityMap<T> map, IEntityMappingStoreContext? mapping = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _mapping = mapping;
    }

    /// <summary>Adds a neutral filter to the query.</summary>
    public SemanticEntityQuery<T> Where(EntityQueryFilter filter)
    {
        _filters.Add(filter ?? throw new ArgumentNullException(nameof(filter)));
        return this;
    }

    /// <summary>Adds a property-existence predicate.</summary>
    public SemanticEntityQuery<T> WhereExists(SemanticTerm property) =>
        Where(new PropertyExistsEntityQueryFilter(property));

    /// <summary>Adds an existence predicate using a directly mapped CLR property.</summary>
    public SemanticEntityQuery<T> WhereExists<TValue>(Expression<Func<T, TValue>> property) =>
        WhereExists(_map.Property(property));

    /// <summary>Adds a disjunction of two or more predicates.</summary>
    public SemanticEntityQuery<T> WhereAny(params EntityQueryFilter[] filters) =>
        Where(new OrEntityQueryFilter(filters));

    /// <summary>Adds the negation of one predicate.</summary>
    public SemanticEntityQuery<T> WhereNot(EntityQueryFilter filter) =>
        Where(new NotEntityQueryFilter(filter));

    /// <summary>Adds an evidence-aware relationship-existence predicate.</summary>
    public SemanticEntityQuery<T> WhereRelationship(
        SemanticTerm relationship,
        SemanticTerm targetConcept,
        EntityQueryFilter? targetFilter = null,
        EntityRelationshipDirection direction = EntityRelationshipDirection.Outgoing) =>
        Where(new RelationshipExistsEntityQueryFilter(relationship, direction, targetConcept, targetFilter));

    /// <summary>Adds a typed property comparison.</summary>
    public SemanticEntityQuery<T> Where(
        SemanticTerm property, EntityQueryComparisonOperator comparison, QueryValue value) =>
        Where(new PropertyComparisonEntityQueryFilter(property, comparison, value));

    /// <summary>Adds a string property comparison.</summary>
    public SemanticEntityQuery<T> Where(
        SemanticTerm property, EntityQueryComparisonOperator comparison, string value) =>
        Where(property, comparison, new LiteralValue(value));

    /// <summary>Adds a Boolean property comparison.</summary>
    public SemanticEntityQuery<T> Where(
        SemanticTerm property, EntityQueryComparisonOperator comparison, bool value) =>
        Where(property, comparison, new LiteralValue(value ? "true" : "false", QueryScalarKind.Boolean));

    /// <summary>Adds an integer property comparison.</summary>
    public SemanticEntityQuery<T> Where(
        SemanticTerm property, EntityQueryComparisonOperator comparison, long value) =>
        Where(property, comparison, new LiteralValue(
            value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer));

    /// <summary>Adds a decimal property comparison.</summary>
    public SemanticEntityQuery<T> Where(
        SemanticTerm property, EntityQueryComparisonOperator comparison, decimal value) =>
        Where(property, comparison, new LiteralValue(
            value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Decimal));

    /// <summary>Adds a typed comparison using a directly mapped CLR property.</summary>
    public SemanticEntityQuery<T> Where<TValue>(Expression<Func<T, TValue>> property,
        EntityQueryComparisonOperator comparison, TValue value) =>
        Where(_map.Property(property), comparison, QueryValueFor(value));

    /// <summary>Adds an ordering to the query.</summary>
    public SemanticEntityQuery<T> OrderBy(
        SemanticTerm property,
        EntityQueryOrderDirection direction = EntityQueryOrderDirection.Ascending,
        EntityQueryOrderValueSelection valueSelection = EntityQueryOrderValueSelection.Minimum,
        EntityQueryMissingValuePlacement missingValues = EntityQueryMissingValuePlacement.Last)
    {
        _orderings.Add(new EntityQueryOrdering(property, direction, valueSelection, missingValues));
        return this;
    }

    /// <summary>Adds an ordering using a directly mapped CLR property.</summary>
    public SemanticEntityQuery<T> OrderBy<TValue>(Expression<Func<T, TValue>> property,
        EntityQueryOrderDirection direction = EntityQueryOrderDirection.Ascending,
        EntityQueryOrderValueSelection valueSelection = EntityQueryOrderValueSelection.Minimum,
        EntityQueryMissingValuePlacement missingValues = EntityQueryMissingValuePlacement.Last) =>
        OrderBy(_map.Property(property), direction, valueSelection, missingValues);

    private static QueryValue QueryValueFor<TValue>(TValue value) => value is null
        ? throw new ArgumentNullException(nameof(value))
        : SemanticMappedValue.QueryValue(value);

    /// <summary>Requests one bounded page, optionally continuing a previous query.</summary>
    public SemanticEntityQuery<T> Take(int count, string? after = null)
    {
        _page = new EntityQueryPage(count, after);
        return this;
    }

    /// <summary>Restricts the query to explicit entity identifiers.</summary>
    public SemanticEntityQuery<T> WithIdentities(IEnumerable<string> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        _identities = new EntityIdentitySelection(identities.Select(identity => new IdentifierValue(identity)));
        return this;
    }

    /// <summary>Restricts the query using CLR identity values understood by the mapped identity scheme.</summary>
    public SemanticEntityQuery<T> WithMappedIdentities(IEnumerable<object> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        SemanticIdentityMemberBinding binding = _map.IdentityBinding ?? throw new InvalidOperationException(
            "The Semantic map must bind an identity before CLR identities can be selected.");
        string prefix = $"{_map.Projection.Concept.Identifier.AbsoluteUri}|{binding.Scheme.Identifier.AbsoluteUri}|";
        return WithIdentities(identities.Select(identity => prefix + SemanticMappedValue.Lexical(
            SemanticMappedValue.Convert(identity, binding.Scheme.ValueKind))));
    }

    /// <summary>Pins execution and explanation to an available database snapshot.</summary>
    public SemanticEntityQuery<T> AtSnapshot(DatabaseVersion snapshot)
    {
        _snapshot = snapshot;
        return this;
    }

    /// <summary>Pins execution and explanation to the snapshot protected by a lease.</summary>
    public SemanticEntityQuery<T> AtSnapshot(SnapshotLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return AtSnapshot(lease.SnapshotVersion);
    }

    /// <summary>Restricts evidence to source observations within a half-open time range.</summary>
    public SemanticEntityQuery<T> WithinObservationTime(
        DateTimeOffset? fromInclusive = null, DateTimeOffset? toExclusive = null)
    {
        SetTemporalScope(new(EntityQueryTemporalDimension.ObservationTime, fromInclusive, toExclusive));
        return this;
    }

    /// <summary>Restricts evidence to source validity intervals overlapping a half-open time range.</summary>
    public SemanticEntityQuery<T> WithinValidTime(
        DateTimeOffset? fromInclusive = null, DateTimeOffset? toExclusive = null)
    {
        SetTemporalScope(new(EntityQueryTemporalDimension.ValidTime, fromInclusive, toExclusive));
        return this;
    }

    /// <summary>Restricts which provenance-bearing assertions may support the result.</summary>
    public SemanticEntityQuery<T> WithAssertionPolicy(SemanticAssertionPolicy policy)
    {
        _assertionPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    private void SetTemporalScope(TemporalEntityQueryScope scope)
    {
        _scopes.RemoveAll(item => item is TemporalEntityQueryScope temporal &&
            temporal.Dimension == scope.Dimension);
        _scopes.Add(scope);
    }

    /// <summary>Includes evidence records and references in the typed page.</summary>
    public SemanticEntityQuery<T> IncludeEvidence(bool include = true)
    {
        _options = _options with
        {
            IncludeEvidence = include
        };
        return this;
    }

    /// <summary>Sets the maximum projected relationship depth.</summary>
    public SemanticEntityQuery<T> WithMaximumRelationshipDepth(int depth)
    {
        _options = _options with
        {
            MaximumRelationshipDepth = depth
        };
        return this;
    }

    /// <summary>Builds the representation-neutral query definition used for execution.</summary>
    public EntityQueryDefinition ToQueryDefinition() => BuildQueryDefinition(_identities);

    private EntityQueryDefinition BuildQueryDefinition(EntityIdentitySelection? identities)
    {
        EntityQueryFilter filter = _filters.Count switch
        {
            0 => MatchAllEntityQueryFilter.Instance,
            1 => _filters[0],
            _ => new AndEntityQueryFilter(_filters)
        };
        return new EntityQueryDefinition(_map.Projection.Concept, _map.Projection, filter,
            _orderings, _page, _scopes, identities, _assertionPolicy);
    }

    /// <summary>Explains the exact neutral query, optionally executing it for measured actuals.</summary>
    public ValueTask<SemanticEntityQueryExplanation> ExplainAsync(
        bool analyze = false, CancellationToken cancellationToken = default) =>
        Run(() => _database.ExplainEntitiesAsync(ToQueryDefinition(), _options, analyze, _snapshot, cancellationToken), cancellationToken);

    /// <summary>Executes against one exact snapshot and materialises the bounded result page.</summary>
    public async ValueTask<SemanticEntityPage<T>> ToPageAsync(CancellationToken cancellationToken = default)
        => await ExecutePageAsync(ToQueryDefinition(), cancellationToken).ConfigureAwait(false);

    private ValueTask<SemanticEntityPage<T>> ExecutePageAsync(EntityQueryDefinition query, CancellationToken cancellationToken) =>
        _mapping is null ? ExecutePageCoreAsync(query, cancellationToken) :
            _mapping.ExecuteAsync(() => ExecutePageCoreAsync(query, cancellationToken), cancellationToken);

    private async ValueTask<SemanticEntityPage<T>> ExecutePageCoreAsync(
        EntityQueryDefinition query, CancellationToken cancellationToken)
    {
        SemanticEntityQueryResult result = await _database.QueryEntitiesAsync(
            query, _options, _snapshot, cancellationToken).ConfigureAwait(false);
        long materializationStarted = Stopwatch.GetTimestamp();
        T[] items = result.Document.Roots.Select(_map.Materialize).ToArray();
        TimeSpan materializationDuration = Stopwatch.GetElapsedTime(materializationStarted);
        return new(items, result.SnapshotVersion, result.NextContinuation,
            result.Document.Evidence, result.Diagnostics, materializationDuration);
    }

    /// <summary>Executes and returns only the typed items when page metadata is not required by the caller.</summary>
    public async ValueTask<IReadOnlyList<T>> ToListAsync(CancellationToken cancellationToken = default) =>
        (await ToPageAsync(cancellationToken).ConfigureAwait(false)).Items;

    /// <summary>Aggregates the selected Semantic entities while preserving snapshot and evidence lineage.</summary>
    public ValueTask<SemanticAggregationResult> AggregateAsync(
        IReadOnlyList<SemanticAggregationDimension> dimensions,
        IReadOnlyList<SemanticAggregationMeasure> measures,
        int maximumInputEntities = SemanticAggregationLimits.MaximumInputEntities,
        CancellationToken cancellationToken = default) =>
        Run(() => _database.AggregateEntitiesAsync(new(ToQueryDefinition(), dimensions, measures, maximumInputEntities),
            _snapshot, cancellationToken), cancellationToken);

    /// <summary>Explains aggregation of the selected Semantic entities.</summary>
    public ValueTask<SemanticAggregationExplanation> ExplainAggregationAsync(
        IReadOnlyList<SemanticAggregationDimension> dimensions,
        IReadOnlyList<SemanticAggregationMeasure> measures,
        bool analyze = false,
        int maximumInputEntities = SemanticAggregationLimits.MaximumInputEntities,
        CancellationToken cancellationToken = default) =>
        Run(() => _database.ExplainAggregationAsync(new(ToQueryDefinition(), dimensions, measures, maximumInputEntities),
            analyze, _snapshot, cancellationToken), cancellationToken);

    /// <summary>Watches until a retained committed change may invalidate an already displayed query snapshot.</summary>
    internal async IAsyncEnumerable<SemanticEntityQueryInvalidated<T>> WatchInvalidationAsync(
        ISemanticChangeFeed changeFeed,
        DatabaseVersion observedSnapshotVersion, DatabaseChangeCursor? cursor = null,
        DatabaseChangeSubscriptionOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EntityQueryDefinition query = ToQueryDefinition();
        await foreach (SemanticEntityQuerySubscriptionEvent change in changeFeed
            .SubscribeToEntityQueryInvalidationAsync(query, observedSnapshotVersion,
                cursor, options, cancellationToken).ConfigureAwait(false))
        {
            if (change.Kind is SemanticEntityQuerySubscriptionEventKind.Invalidated)
            {
                yield return new(change.ChangeVersion, change.TransactionId, change.Cursor, change.RelevantEffects);
            }
        }
    }

    /// <summary>Continuously materializes a single-result query after relevant committed changes.</summary>
    internal async IAsyncEnumerable<SemanticEntityWatchEvent<T>> WatchSingleAsync(
        ISemanticChangeFeed changeFeed, object identity,
        DatabaseChangeSubscriptionOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        EntityIdentitySelection identitySelection = IdentitySelection(identity);
        string projectedIdentity = ((IdentifierValue)identitySelection.Identifiers.Single()).Identifier;
        EntityQueryDefinition query = BuildQueryDefinition(identitySelection);
        DatabaseChangeCursor? cursor = null;
        while (true)
        {
            SemanticEntityPage<T> page = await ExecutePageAsync(query, cancellationToken).ConfigureAwait(false);
            if (page.Items.Count > 1)
            {
                throw new InvalidOperationException("A single-entity watch query returned more than one entity.");
            }

            yield return page.Items.Count == 1
                ? new SemanticEntityChanged<T>(page.Items[0], page.SnapshotVersion)
                : new SemanticEntityRemoved<T>(projectedIdentity, page.SnapshotVersion);
            bool wasInvalidated = false;
            await foreach (SemanticEntityQuerySubscriptionEvent change in changeFeed
                .SubscribeToEntityQueryInvalidationAsync(query, page.SnapshotVersion,
                    cursor, options, cancellationToken).ConfigureAwait(false))
            {
                if (change.Kind is SemanticEntityQuerySubscriptionEventKind.Invalidated)
                {
                    cursor = change.Cursor;
                    wasInvalidated = true;
                    break;
                }
            }

            if (!wasInvalidated)
            {
                yield break;
            }
        }
    }

    private EntityIdentitySelection IdentitySelection(object identity)
    {
        if (_map.IdentityBinding is null)
        {
            return new([new IdentifierValue(identity as string ?? throw new ArgumentException(
                "A query-only map requires the exact projected string identity.", nameof(identity)))]);
        }

        SemanticIdentityMemberBinding binding = _map.IdentityBinding;
        string prefix = $"{_map.Projection.Concept.Identifier.AbsoluteUri}|{binding.Scheme.Identifier.AbsoluteUri}|";
        string projected = prefix + SemanticMappedValue.Lexical(
            SemanticMappedValue.Convert(identity, binding.Scheme.ValueKind));
        return new([new IdentifierValue(projected)]);
    }
}

/// <summary>Creates fluent typed semantic queries without introducing another query engine.</summary>
public static class SemanticEntityQueryExtensions
{
    /// <summary>Starts a fluent query using an explicit concept-to-C# mapping.</summary>
    public static SemanticEntityQuery<T> Entities<T>(
        this ISemanticQueryService database, SemanticEntityMap<T> map) => new(database, map);
}

/// <summary>Exposes change-feed-backed watch operations only from the complete Semantic database capability.</summary>
public static class SemanticEntityWatchExtensions
{
    /// <summary>Watches until a retained committed change may invalidate an already displayed query snapshot.</summary>
    public static IAsyncEnumerable<SemanticEntityQueryInvalidated<T>> WatchInvalidationAsync<T>(
        this SemanticEntityQuery<T> query, ISemanticDatabase database,
        DatabaseVersion observedSnapshotVersion, DatabaseChangeCursor? cursor = null,
        DatabaseChangeSubscriptionOptions? options = null, CancellationToken cancellationToken = default) =>
        query.WatchInvalidationAsync(database, observedSnapshotVersion, cursor, options, cancellationToken);

    /// <summary>Continuously materialises exactly one mapped entity after relevant committed changes.</summary>
    public static IAsyncEnumerable<SemanticEntityWatchEvent<T>> WatchSingleAsync<T>(
        this SemanticEntityQuery<T> query, ISemanticDatabase database, object identity,
        DatabaseChangeSubscriptionOptions? options = null, CancellationToken cancellationToken = default) =>
        query.WatchSingleAsync(database, identity, options, cancellationToken);
}
