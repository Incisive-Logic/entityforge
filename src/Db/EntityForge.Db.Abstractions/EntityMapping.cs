using System.Linq.Expressions;
using System.Reflection;
using System.Collections;
using System.Collections.ObjectModel;

namespace EntityForge.Db.Mapping;

/// <summary>Classifies the profile-neutral role of a mapped CLR member.</summary>
public enum EntityMemberRole
{
    /// <summary>Identifies the entity independently of its other stored values.</summary>
    Identity,

    /// <summary>Stores a value on the entity.</summary>
    Property,

    /// <summary>Relates the entity to another entity.</summary>
    Relationship
}

/// <summary>Describes the application-level meaning of a persisted CLR member.</summary>
public enum EntityMemberKind
{
    /// <summary>The stable entity identity.</summary>
    Identity,

    /// <summary>A scalar value or collection of scalar values.</summary>
    Scalar,

    /// <summary>A nested object whose lifecycle belongs to the containing aggregate.</summary>
    Owned,

    /// <summary>A reference to an independently persisted entity.</summary>
    Reference
}

/// <summary>Describes collection semantics that a profile binding must preserve exactly.</summary>
public enum EntityCollectionKind
{
    /// <summary>The member is not collection-valued.</summary>
    None,

    /// <summary>The member is a collection without an explicit ordering or uniqueness contract.</summary>
    Unspecified,

    /// <summary>The collection has set semantics, with uniqueness and no significant order.</summary>
    Set,

    /// <summary>The collection preserves item order and may contain repeated values.</summary>
    OrderedSequence
}

/// <summary>Describes one CLR member independently of any database profile representation.</summary>
/// <param name="Name">The literal CLR member name.</param>
/// <param name="ValueType">The declared CLR type, including the collection type when applicable.</param>
/// <param name="Role">The profile-neutral member role.</param>
/// <param name="IsCollection">Whether the member is collection-valued.</param>
/// <param name="Member">CLR reflection metadata, or null for a named structure.</param>
/// <param name="ReadValue">The accessor delegate accepting one entity and returning the member value.</param>
public sealed record EntityMemberMap(
    string Name, Type ValueType, EntityMemberRole Role, bool IsCollection, MemberInfo? Member,
    Delegate ReadValue)
{
    // Named structures declare nullability without fabricating CLR reflection metadata.
    internal bool? DeclaredNullable
    {
        get; init;
    }

    // Reconstructed targets create new accessor instances per request. Compile the invocation
    // shape once per delegate type, passing the actual accessor so closures cannot cross requests.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type,
        Lazy<Func<Delegate, object, object?>>> Readers = new();

    internal object? Read(object entity) => Readers.GetValue(ReadValue.GetType(), type => new(() =>
    {
        ParameterExpression accessor = Expression.Parameter(typeof(Delegate), "accessor");
        ParameterExpression input = Expression.Parameter(typeof(object), "entity");
        Type inputType = type.GetMethod("Invoke")!.GetParameters().Single().ParameterType;
        return Expression.Lambda<Func<Delegate, object, object?>>(Expression.Convert(
            Expression.Invoke(Expression.Convert(accessor, type), Expression.Convert(input, inputType)), typeof(object)),
            accessor, input).Compile();
    })).Value(ReadValue, entity);

    /// <summary>Gets the logical meaning used by convention and profile compilers.</summary>
    public EntityMemberKind Kind
    {
        get; init;
    }
    = Role switch
    {
        EntityMemberRole.Identity => EntityMemberKind.Identity,
        EntityMemberRole.Property => EntityMemberKind.Scalar,
        EntityMemberRole.Relationship => EntityMemberKind.Reference,
        _ => throw new ArgumentOutOfRangeException(nameof(Role))
    };

    /// <summary>Gets the collection contract; unspecified preserves legacy explicit-map behaviour.</summary>
    public EntityCollectionKind CollectionKind
    {
        get; init;
    }
    =
        IsCollection ? EntityCollectionKind.Unspecified : EntityCollectionKind.None;
}

/// <summary>Provides profile-independent values while materialising a mapped CLR entity.</summary>
/// <param name="identity">The nonnull identity exposed to the materializer.</param>
/// <param name="values">Member values keyed by mapped name; retained without copying and must remain stable during materialization.</param>
public sealed class EntityMaterializationContext(object identity,
    IReadOnlyDictionary<string, object?> values)
{
    internal bool HasValue(string name) => values.TryGetValue(name, out object? value) && value is not null;

    /// <summary>Gets the captured identity before conversion to the application's requested type.</summary>
    public object Identity { get; } = identity ?? throw new ArgumentNullException(nameof(identity));

    /// <summary>Converts the captured entity identity to the materializer's required CLR type.</summary>
    /// <typeparam name="TIdentity">The requested identity type.</typeparam>
    /// <returns>The identity represented as the requested type.</returns>
    /// <exception cref="InvalidOperationException">The captured identity cannot be converted.</exception>
    public TIdentity IdentityAs<TIdentity>() => ConvertValue<TIdentity>(Identity, "identity");

    /// <summary>Reads a required mapped member and converts it to the materializer's requested CLR type.</summary>
    /// <param name="memberName">The literal, ordinal mapped-member name.</param>
    /// <typeparam name="TValue">The requested value type.</typeparam>
    /// <returns>The converted, non-null member value.</returns>
    /// <exception cref="InvalidOperationException">The member is missing, null or cannot be converted.</exception>
    public TValue Required<TValue>(string memberName) => values.TryGetValue(memberName, out object? value) && value is not null
        ? ConvertValue<TValue>(value, memberName)
        : throw new InvalidOperationException($"Mapped member '{memberName}' has no value.");

    /// <summary>Reads an optional mapped member, returning the type's default when it is absent or null.</summary>
    /// <param name="memberName">The literal, ordinal mapped-member name.</param>
    /// <typeparam name="TValue">The requested value type; use a nullable type when absence must remain distinguishable.</typeparam>
    /// <returns>The converted value, or the default of TValue.</returns>
    /// <exception cref="InvalidOperationException">A present value cannot be converted.</exception>
    public TValue? Optional<TValue>(string memberName) => values.TryGetValue(memberName, out object? value) && value is not null
        ? ConvertValue<TValue>(value, memberName) : default;

    private static TValue ConvertValue<TValue>(object value, string memberName)
    {
        if (value is TValue typed)
        {
            return typed;
        }

        try
        {
            Type target = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);
            return (TValue)ConvertValue(value, target, memberName);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException($"Mapped member '{memberName}' cannot be converted to {typeof(TValue).Name}.", exception);
        }
    }

    private static object ConvertValue(object value, Type target, string memberName)
    {
        if (target.IsInstanceOfType(value))
        {
            return value;
        }

        Type? elementType = CollectionElementType(target);
        if (elementType is not null && value is IEnumerable source and not string)
        {
            object[] sourceValues = source.Cast<object>().ToArray();
            Array converted = Array.CreateInstance(elementType, sourceValues.Length);
            for (int index = 0; index < sourceValues.Length; index++)
            {
                converted.SetValue(ConvertValue(sourceValues[index], elementType, memberName), index);
            }

            if (target.IsAssignableFrom(converted.GetType()))
            {
                return converted;
            }

            Type listType = typeof(List<>).MakeGenericType(elementType);
            object list = Activator.CreateInstance(listType, converted) ??
                          throw new InvalidOperationException($"Mapped member '{memberName}' collection cannot be created.");
            if (target.IsInstanceOfType(list))
            {
                return list;
            }

            ConstructorInfo? constructor = target.GetConstructor([typeof(IEnumerable<>).MakeGenericType(elementType)]);
            if (constructor is not null)
            {
                return constructor.Invoke([list]);
            }

            throw new InvalidCastException($"Collection type '{target.Name}' is not supported.");
        }
        if (target == typeof(Guid))
        {
            return Guid.Parse(value.ToString()!);
        }

        if (target == typeof(Uri))
        {
            return new Uri(value.ToString()!, UriKind.Absolute);
        }

        return Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Type? CollectionElementType(Type type)
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
}

/// <summary>Defines the reusable CLR shape shared by one or more database-profile bindings.</summary>
/// <typeparam name="T">The application entity type described by this map.</typeparam>
public sealed class EntityMap<T>
{
    private readonly IReadOnlyList<EntityMemberMap> _orderedMembers;
    private readonly IReadOnlyDictionary<string, EntityMemberMap> _members;

    internal EntityMap(IEnumerable<EntityMemberMap> members)
    {
        EntityMemberMap[] materialized = members.ToArray();
        if (materialized.GroupBy(item => item.Name, StringComparer.Ordinal).Any(group => group.Count() != 1))
        {
            throw new ArgumentException("An entity map cannot contain duplicate CLR members.", nameof(members));
        }

        if (materialized.Count(item => item.Role is EntityMemberRole.Identity) > 1)
        {
            throw new ArgumentException("An entity map can define at most one identity member.", nameof(members));
        }

        foreach (EntityMemberMap member in materialized)
        {
            ValidateShape(member);
        }

        _orderedMembers = Array.AsReadOnly(materialized);
        _members = new ReadOnlyDictionary<string, EntityMemberMap>(
            materialized.ToDictionary(item => item.Name, StringComparer.Ordinal));
    }

    /// <summary>Gets every mapped CLR member in definition order.</summary>
    public IReadOnlyList<EntityMemberMap> Members => _orderedMembers;

    /// <summary>Gets the mapped identity member, when one has been defined.</summary>
    public EntityMemberMap? Identity => _orderedMembers.SingleOrDefault(
        item => item.Role is EntityMemberRole.Identity);

    /// <summary>Returns a mapped member or fails when a profile binding refers to an unmapped member.</summary>
    public EntityMemberMap RequireMember<TValue>(Expression<Func<T, TValue>> member)
    {
        string name = EntityMapExpression.Member(member).Name;
        return _members.TryGetValue(name, out EntityMemberMap? mapped) ? mapped : throw new ArgumentException(
            $"CLR member '{name}' must be declared in the neutral entity map before it can be bound.",
            nameof(member));
    }

    private static void ValidateShape(EntityMemberMap member)
    {
        if (!Enum.IsDefined(member.Role) || !Enum.IsDefined(member.Kind) ||
            !Enum.IsDefined(member.CollectionKind))
        {
            throw new ArgumentException($"CLR member '{member.Name}' has an unknown mapping classification.");
        }

        EntityMemberRole expectedRole = member.Kind switch
        {
            EntityMemberKind.Identity => EntityMemberRole.Identity,
            EntityMemberKind.Scalar => EntityMemberRole.Property,
            EntityMemberKind.Owned or EntityMemberKind.Reference => EntityMemberRole.Relationship,
            _ => throw new ArgumentOutOfRangeException(nameof(member.Kind))
        };
        if (member.Role != expectedRole)
        {
            throw new ArgumentException($"CLR member '{member.Name}' has contradictory role and kind values.");
        }

        if (member.IsCollection != (member.CollectionKind is not EntityCollectionKind.None))
        {
            throw new ArgumentException($"CLR member '{member.Name}' has contradictory collection values.");
        }

        if (member.Kind is EntityMemberKind.Identity && member.IsCollection)
        {
            throw new ArgumentException($"CLR identity member '{member.Name}' cannot be collection-valued.");
        }
    }

}

/// <summary>Starts a profile-neutral CLR entity mapping.</summary>
public static class EntityMap
{
    /// <summary>Begins mapping one CLR type.</summary>
    public static EntityMapBuilder<T> For<T>() => new();
}

/// <summary>Builds a reusable CLR mapping without choosing a database profile.</summary>
public sealed class EntityMapBuilder<T>
{
    private readonly List<EntityMemberMap> _members = [];

    /// <summary>Defines the single stable identity member.</summary>
    public EntityMapBuilder<T> Identity<TValue>(Expression<Func<T, TValue>> member) =>
        Add(member, EntityMemberRole.Identity);

    /// <summary>Defines a scalar or value-object property.</summary>
    public EntityMapBuilder<T> Property<TValue>(Expression<Func<T, TValue>> member) =>
        Add(member, EntityMemberRole.Property);

    /// <summary>Defines a related entity or entity collection.</summary>
    public EntityMapBuilder<T> Relationship<TValue>(Expression<Func<T, TValue>> member) =>
        Add(member, EntityMemberRole.Relationship, EntityMemberKind.Reference,
            EntityCollectionKind.Unspecified);

    /// <summary>Defines a nested value whose lifecycle and persistence are owned by the containing entity.</summary>
    public EntityMapBuilder<T> Owned<TValue>(Expression<Func<T, TValue>> member) =>
        Add(member, EntityMemberRole.Relationship, EntityMemberKind.Owned, CollectionKind(typeof(TValue)));

    /// <summary>Defines a reference to an independently identified entity; saving it does not cascade by default.</summary>
    public EntityMapBuilder<T> Reference<TValue>(Expression<Func<T, TValue>> member) =>
        Add(member, EntityMemberRole.Relationship, EntityMemberKind.Reference, CollectionKind(typeof(TValue)));

    /// <summary>Creates the immutable neutral map.</summary>
    public EntityMap<T> Build() => new(_members);

    internal IReadOnlyList<EntityMemberMap> Members => _members;

    internal EntityMapBuilder<T> Add<TValue>(Expression<Func<T, TValue>> expression, EntityMemberRole role)
        => Add(expression, role, role switch
        {
            EntityMemberRole.Identity => EntityMemberKind.Identity,
            EntityMemberRole.Property => EntityMemberKind.Scalar,
            EntityMemberRole.Relationship => EntityMemberKind.Reference,
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        }, typeof(TValue) != typeof(string) && typeof(TValue) != typeof(byte[]) &&
           typeof(IEnumerable).IsAssignableFrom(typeof(TValue))
            ? EntityCollectionKind.Unspecified : EntityCollectionKind.None);

    internal EntityMapBuilder<T> Add<TValue>(Expression<Func<T, TValue>> expression, EntityMemberRole role,
        EntityMemberKind kind, EntityCollectionKind collectionKind)
    {
        MemberInfo member = EntityMapExpression.Member(expression);
        if (_members.Any(item => String.Equals(item.Name, member.Name, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"CLR member '{member.Name}' is already mapped.", nameof(expression));
        }

        Type valueType = typeof(TValue);
        bool collection = valueType != typeof(string) && valueType != typeof(byte[]) &&
            typeof(System.Collections.IEnumerable)
            .IsAssignableFrom(valueType);
        if (!collection && collectionKind is not EntityCollectionKind.None)
        {
            collectionKind = EntityCollectionKind.None;
        }

        _members.Add(new(member.Name, valueType, role, collection, member, expression.Compile())
        {
            Kind = kind,
            CollectionKind = collectionKind
        });
        return this;
    }

    private static EntityCollectionKind CollectionKind(Type type)
    {
        if (type == typeof(string) || type == typeof(byte[]) ||
            !typeof(IEnumerable).IsAssignableFrom(type))
        {
            return EntityCollectionKind.None;
        }

        if (type.IsArray || Implements(type, typeof(IReadOnlyList<>)) || Implements(type, typeof(IList<>)))
        {
            return EntityCollectionKind.OrderedSequence;
        }

        if (Implements(type, typeof(ISet<>)) || Implements(type, typeof(IReadOnlySet<>)))
        {
            return EntityCollectionKind.Set;
        }

        return EntityCollectionKind.Unspecified;
    }

    private static bool Implements(Type type, Type generic) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == generic ||
        type.GetInterfaces().Any(item => item.IsGenericType && item.GetGenericTypeDefinition() == generic);
}

internal static class EntityMapExpression
{
    public static MemberInfo Member<T, TValue>(Expression<Func<T, TValue>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        Expression body = expression.Body is UnaryExpression { NodeType: ExpressionType.Convert } conversion
            ? conversion.Operand : expression.Body;
        return body is MemberExpression { Expression: ParameterExpression } member
            ? member.Member
            : throw new ArgumentException("An entity mapping must select one direct property or field.",
                nameof(expression));
    }
}
