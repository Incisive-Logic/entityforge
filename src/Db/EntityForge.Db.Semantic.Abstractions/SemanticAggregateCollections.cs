using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using EntityForge.Db.Mapping;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

public sealed partial class SemanticAggregateMapBuilder<T> where T : class
{
    /// <summary>Maps scalar values with preserved order and duplicates. Null elements are rejected.</summary>
    public SemanticAggregateMapBuilder<T> ScalarSequence<TElement>(Expression<Func<T, IEnumerable<TElement>?>> member,
        SemanticTerm relationship, Action<T, IReadOnlyList<TElement>?> assign, string? persistentMemberIdentity = null) =>
        Collection(member, relationship, assign, EntityMemberKind.Scalar, EntityCollectionKind.OrderedSequence, null, persistentMemberIdentity);

    /// <summary>Maps unique scalar values using native scalar equality and deterministic ordering.</summary>
    public SemanticAggregateMapBuilder<T> ScalarSet<TElement>(Expression<Func<T, IEnumerable<TElement>?>> member,
        SemanticTerm relationship, Action<T, IReadOnlyList<TElement>?> assign, string? persistentMemberIdentity = null) =>
        Collection(member, relationship, assign, EntityMemberKind.Scalar, EntityCollectionKind.Set, null, persistentMemberIdentity);

    /// <summary>Maps independently identified references with preserved order and duplicates.</summary>
    public SemanticAggregateMapBuilder<T> ReferenceSequence<TElement>(Expression<Func<T, IEnumerable<TElement>?>> member,
        SemanticTerm relationship, Func<SemanticAggregateMap<TElement>> target, Action<T, IReadOnlyList<TElement>?> assign,
        string? persistentMemberIdentity = null) where TElement : class =>
        Collection(member, relationship, assign, EntityMemberKind.Reference, EntityCollectionKind.OrderedSequence, () => target().Node, persistentMemberIdentity);

    /// <summary>Maps a set of independently identified references without cascading writes.</summary>
    public SemanticAggregateMapBuilder<T> ReferenceSet<TElement>(Expression<Func<T, IEnumerable<TElement>?>> member,
        SemanticTerm relationship, Func<SemanticAggregateMap<TElement>> target, Action<T, IReadOnlyList<TElement>?> assign,
        string? persistentMemberIdentity = null) where TElement : class =>
        Collection(member, relationship, assign, EntityMemberKind.Reference, EntityCollectionKind.Set, () => target().Node, persistentMemberIdentity);

    /// <summary>Maps an owned sequence. Distinct instances may have equal values; shared owned instances are rejected.</summary>
    public SemanticAggregateMapBuilder<T> OwnedSequence<TElement>(Expression<Func<T, IEnumerable<TElement>?>> member,
        SemanticTerm relationship, Func<SemanticAggregateMap<TElement>> target, Action<T, IReadOnlyList<TElement>?> assign,
        string? persistentMemberIdentity = null) where TElement : class =>
        Collection(member, relationship, assign, EntityMemberKind.Owned, EntityCollectionKind.OrderedSequence, () => target().Node, persistentMemberIdentity);

    /// <summary>Maps owned objects as a set of unique original keys, rejecting duplicate keys.</summary>
    public SemanticAggregateMapBuilder<T> OwnedSet<TElement>(Expression<Func<T, IEnumerable<TElement>?>> member,
        SemanticTerm relationship, Func<SemanticAggregateMap<TElement>> target, Action<T, IReadOnlyList<TElement>?> assign,
        string? persistentMemberIdentity = null) where TElement : class =>
        Collection(member, relationship, assign, EntityMemberKind.Owned, EntityCollectionKind.Set, () => target().Node, persistentMemberIdentity);

    internal SemanticAggregateMapBuilder<T> Collection<TElement>(Expression<Func<T, IEnumerable<TElement>?>> expression,
        SemanticTerm relationship, Action<T, IReadOnlyList<TElement>?> assign, EntityMemberKind kind,
        EntityCollectionKind collection, Func<AggregateNode>? target, string? persistentIdentity)
    {
        ArgumentNullException.ThrowIfNull(assign);
        if (EntityMapExpression.Member(expression) is not PropertyInfo property || property.GetIndexParameters().Length != 0)
        {
            throw new NotSupportedException("Collections must be direct properties.");
        }

        if (_scalar.EntityMap.Members.Any(item => item.Name == property.Name) || _links.Any(item => item.Member.Name == property.Name))
        {
            throw new ArgumentException("A member may have only one mapping.");
        }

        _links.Add(AggregateCollection.Create(property, expression.Compile(), assign, relationship, kind, collection,
            target, persistentIdentity, _manifest));
        return this;
    }
}

internal static class AggregateCollection
{
    internal const string EncodingVersion = "semantic-collection-chain-v1";

    private sealed class Head<TElement>
    {
        public string Id { get; set; } = "head";

        public Cell<TElement>? First
        {
            get; set;
        }
    }

    private sealed class Cell<TElement>
    {
        public string Id { get; set; } = "";

        public TElement Value { get; set; } = default!;

        public Cell<TElement>? Next
        {
            get; set;
        }
    }

    internal static AggregateLink Create<TOwner, TElement>(PropertyInfo property,
        Func<TOwner, IEnumerable<TElement>?> read, Action<TOwner, IReadOnlyList<TElement>?> assign,
        SemanticTerm relationship, EntityMemberKind kind, EntityCollectionKind collection,
        Func<AggregateNode>? target, string? persistentIdentity, EntityMappingManifest owner) where TOwner : class
    {
        ArgumentNullException.ThrowIfNull(relationship);
        if (relationship.Role != SemanticTermRole.Relationship)
        {
            throw new ArgumentException("Collection storage requires a relationship term.");
        }

        NullabilityState nullable = new NullabilityInfoContext().Create(property).ReadState;
        if (nullable == NullabilityState.Unknown)
        {
            throw new NotSupportedException("Collections require explicit CLR nullability.");
        }

        string id = persistentIdentity ?? property.GetCustomAttribute<EntityMemberAttribute>()?.Identity ?? property.Name;
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var mapped = new EntityMemberMap(property.Name, property.PropertyType,
            kind == EntityMemberKind.Scalar ? EntityMemberRole.Property : EntityMemberRole.Relationship, true, property, read)
        {
            Kind = kind,
            CollectionKind = collection
        };
        return Create<TElement>(mapped, id, nullable == NullabilityState.Nullable, entity => read((TOwner)entity),
            (entity, values) => assign((TOwner)entity, values), relationship, kind, collection, target, owner);
    }

    internal static AggregateLink Create<TElement>(AggregateMember mapped, string id, bool nullable,
        Func<object, IEnumerable?> read, Action<object, IReadOnlyList<TElement>?> assign,
        SemanticTerm relationship, EntityMemberKind kind, EntityCollectionKind collection,
        Func<AggregateNode>? target, EntityMappingManifest owner)
    {
        if (relationship.Role != SemanticTermRole.Relationship)
        {
            throw new ArgumentException("Collection storage requires a relationship term.");
        }

        if (collection is not (EntityCollectionKind.Set or EntityCollectionKind.OrderedSequence))
        {
            throw new ArgumentOutOfRangeException(nameof(collection));
        }

        var (headNode, codec) = BuildCollectionSchema<TElement>(id, kind, target, owner);
        return new(mapped, id, nullable, relationship, () => headNode,
            (entity, value) => assign(entity, MaterializeCollection<TElement>(value, collection, kind, target)), codec)
        {
            CollectionElementTarget = target,
            CollectionElementType = typeof(TElement),
            Capture = (entity, limit, normalize) => CaptureCollection<TElement>(
                entity, limit, normalize, id, read, kind, collection, target)
        };

    }

    private static (AggregateNode Node, string Codec) BuildCollectionSchema<TElement>(string id,
        EntityMemberKind kind, Func<AggregateNode>? target, EntityMappingManifest owner)
    {
        string prefix = "urn:entityforge:collection:v1:" + Uri.EscapeDataString(owner.Model.Value) + ":" +
            Uri.EscapeDataString(owner.EntityType.Value) + ":" + Uri.EscapeDataString(id);
        SemanticTerm Term(string name, SemanticTermRole role) => new(new Uri(prefix + ":" + name), role);
        var scheme = new SemanticIdentityScheme(new Uri(prefix + ":id"), "Collection key", SemanticValueKind.String);
        var model = new EntityModelIdentity("entityforge/collection/v1/" + owner.Model.Value);
        string typePrefix = JsonSerializer.Serialize(new[] { owner.EntityType.Value, id });
        var cellScalarBuilder = SemanticEntityMap.For<Cell<TElement>>(Term("entry", SemanticTermRole.Concept))
            .Identity(cell => cell.Id, scheme);
        string codec = EncodingVersion;
        if (kind == EntityMemberKind.Scalar)
        {
            if (Nullable.GetUnderlyingType(typeof(TElement)) is not null || !EntityConvention<Entity>.Scalar(typeof(TElement)))
            {
                throw new NotSupportedException("Collection elements require a supported non-nullable scalar codec.");
            }

            cellScalarBuilder.Property(cell => cell.Value, Term("value", SemanticTermRole.Property));
        }
        var cellScalar = cellScalarBuilder.Materialize(context => new Cell<TElement>
        {
            Id = context.Identity,
            Value = kind == EntityMemberKind.Scalar ? context.Required<TElement>("Value") : default!
        });
        // Compile the element's exact native codec, including CLR range restrictions, into the durable contract.
        if (kind == EntityMemberKind.Scalar)
        {
            EntityMemberMap value = cellScalar.EntityMap.Members.Single(member => member.Name == "Value");
            codec += "/" + SemanticEntityMap<Cell<TElement>>.ScalarCodec(value, SemanticEntityMap<Cell<TElement>>.SemanticKind(value));
        }
        SemanticAggregateMap<Cell<TElement>>? cellMap = null;
        var cellBuilder = SemanticAggregateMap.For(cellScalar, model, new PersistentEntityTypeIdentity(typePrefix + "/entry"))
            .Owned(cell => cell.Next, Term("next", SemanticTermRole.Relationship), () => cellMap!, (cell, next) => cell.Next = next);
        AggregateNode cellNode = cellBuilder.Build().Node;
        if (kind != EntityMemberKind.Scalar)
        {
            PropertyInfo valueProperty = typeof(Cell<TElement>).GetProperty(nameof(Cell<TElement>.Value))!;
            var member = new EntityMemberMap("Value", typeof(TElement), EntityMemberRole.Relationship, false, valueProperty,
                (Func<Cell<TElement>, TElement>)(cell => cell.Value))
            {
                Kind = kind
            };
            cellNode = cellNode with
            {
                Links = cellNode.Links.Append(new AggregateLink(member, "Value", false,
                Term("value", SemanticTermRole.Relationship), target!, (cell, value) => ((Cell<TElement>)cell).Value = (TElement)value!)).ToArray()
            };
        }
        cellMap = new(cellNode with
        {
            DepthCost = 0
        });
        var headScalar = SemanticEntityMap.For<Head<TElement>>(Term("container", SemanticTermRole.Concept))
            .Identity(head => head.Id, scheme).Materialize(context => new Head<TElement> { Id = context.Identity });
        var headMap = SemanticAggregateMap.For(headScalar, model, new PersistentEntityTypeIdentity(typePrefix + "/container"))
            .Owned(head => head.First, Term("first", SemanticTermRole.Relationship), () => cellMap, (head, first) => head.First = first).Build();
        AggregateNode headNode = headMap.Node with
        {
            DepthCost = 0
        };
        return (headNode, codec);
    }

    private static IReadOnlyList<TElement>? MaterializeCollection<TElement>(object? value,
        EntityCollectionKind collection, EntityMemberKind kind, Func<AggregateNode>? target)
    {
        if (value is null)
        {
            return null;
        }

        var elements = new List<TElement>();
        for (Cell<TElement>? cell = ((Head<TElement>)value).First; cell is not null; cell = cell.Next)
        {
            elements.Add(cell.Value);
        }

        if (collection == EntityCollectionKind.Set)
        {
            string[] keys = elements.Select(element => ElementKey(element!, kind, target)).ToArray();
            if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length ||
                !keys.SequenceEqual(keys.Order(StringComparer.Ordinal)))
            {
                throw new InvalidOperationException("A stored set is duplicated or not canonically ordered.");
            }
        }
        return elements;
    }

    private static Entity? CaptureCollection<TElement>(object entity, int limit, bool normalize,
        string id, Func<object, IEnumerable?> read, EntityMemberKind kind,
        EntityCollectionKind collection, Func<AggregateNode>? target)
    {
        IEnumerable? source;
        bool inputSource = entity is Entity;
        if (entity is Entity input)
        {
            input.Members.TryGetValue(id, out object? supplied);
            if (supplied is null)
            {
                return null;
            }

            if (supplied is not EntityCollection values || values.Kind != collection)
            {
                throw new ArgumentException("Input collection kind must match its mapped set or sequence.");
            }

            source = values.Items;
        }
        else
        {
            source = read(entity);
        }

        if (source is null)
        {
            return null;
        }

        if (collection == EntityCollectionKind.Set &&
            (source is HashSet<TElement> set && !ReferenceEquals(set.Comparer, EqualityComparer<TElement>.Default) ||
             source is not HashSet<TElement> && (source is ISet<TElement> || source is IReadOnlySet<TElement>)))
        {
            throw new NotSupportedException("Set persistence requires native element equality; custom CLR set comparers are not persisted.");
        }

        var elements = new List<(string Key, object Value)>();
        foreach (object? element in source)
        {
            if (elements.Count >= limit)
            {
                throw new InvalidOperationException("The collection exceeds the aggregate entity limit.");
            }

            if (element is null)
            {
                throw new InvalidOperationException("Collection elements cannot be null.");
            }

            if (inputSource)
            {
                if (kind == EntityMemberKind.Owned && element is not Entity)
                {
                    throw new ArgumentException("Owned collection entries require Entity values.");
                }

                if (kind == EntityMemberKind.Reference && element is not EntityReference)
                {
                    throw new ArgumentException("Reference collection entries require independent keys.");
                }

                if (kind == EntityMemberKind.Scalar)
                {
                    EntityRecordShape<Entity>.RequireType(element, typeof(TElement));
                }
            }
            object normalized = normalize && collection == EntityCollectionKind.Set && kind == EntityMemberKind.Scalar ? Normalize(element) : element;
            elements.Add((normalize ? ElementKey(normalized, kind, target) : "", normalized));
        }
        if (normalize && collection == EntityCollectionKind.Set)
        {
            var groups = elements.GroupBy(item => item.Key, StringComparer.Ordinal).ToArray();
            if (kind == EntityMemberKind.Owned && groups.Any(group => group.Count() > 1))
            {
                throw new InvalidOperationException("An owned set contains duplicate entity keys.");
            }

            elements = groups.Select(group => group.First()).OrderBy(item => item.Key, StringComparer.Ordinal).ToList();
        }
        // Both producer paths use the same private chain representation and occurrence identities.
        Entity? next = null;
        for (int index = elements.Count - 1; index >= 0; index--)
        {
            next = new Entity(new Dictionary<string, object?>
            {
                ["Id"] = index.ToString(CultureInfo.InvariantCulture),
                ["Value"] = elements[index].Value,
                ["Next"] = next
            });
        }

        return new Entity(new Dictionary<string, object?> { ["Id"] = "head", ["First"] = next });
    }

    private static object Normalize(object element) => element switch
    {
        decimal number => decimal.Parse(number.ToString("G29", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
        double number when number == 0 => 0d,
        float number when number == 0 => 0f,
        _ => element
    };

    private static string ElementKey(object element, EntityMemberKind kind, Func<AggregateNode>? target)
    {
        if (kind != EntityMemberKind.Scalar)
        {
            SemanticEntityIdentity identity = target!().ReadIdentity(element!);
            return JsonSerializer.Serialize(new[]
            {
                identity.Concept.Identifier.AbsoluteUri, identity.Scheme.Identifier.AbsoluteUri,
                identity.Value.Kind.ToString(), SemanticValueConverter.ToLexical(identity.Value)
            });
        }
        if (element is DateTimeOffset instant && instant.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Collection DateTimeOffset values require UTC.");
        }

        SemanticValue value = SemanticMappedValue.Convert(element!);
        string lexical = value switch
        {
            DecimalSemanticValue number => number.Value.ToString("G29", CultureInfo.InvariantCulture),
            FloatingPointSemanticValue number when number.Value == 0 => "0",
            _ => SemanticValueConverter.ToLexical(value)
        };
        return JsonSerializer.Serialize(new[] { value.Kind.ToString(), lexical });
    }
}
