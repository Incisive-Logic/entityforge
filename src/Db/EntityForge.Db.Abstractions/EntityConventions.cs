using System.Collections.ObjectModel;
using System.Linq.Expressions;
using System.Reflection;

namespace EntityForge.Db.Mapping;

/// <summary>Declares persistent identities independently of CLR assembly and type names.</summary>
/// <param name="model">The stable application model namespace.</param>
/// <param name="identity">The stable persistent type identity within that model.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class EntityTypeAttribute(string model, string identity) : Attribute
{
    /// <summary>Gets the application model namespace used by convention mapping.</summary>
    public string Model { get; } = model;

    /// <summary>Gets the persistent type identity, independent of the CLR type name.</summary>
    public string Identity { get; } = identity;
}

/// <summary>Overrides the persistent identity of a property, for example when renaming it.</summary>
/// <param name="identity">The stable member identity to retain across CLR property renames.</param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EntityMemberAttribute(string identity) : Attribute
{
    /// <summary>Gets the stable persistent member identity.</summary>
    public string Identity { get; } = identity;
}

/// <summary>Selects the entity identity property instead of the conventional Id property.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EntityIdentityAttribute : Attribute;

/// <summary>Excludes application-only state from convention mapping.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class EntityIgnoreAttribute : Attribute;

/// <summary>Declares that an object or object collection belongs to its containing aggregate.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EntityOwnedAttribute : Attribute;

/// <summary>Declares an independently persisted object or collection of entity references.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EntityReferenceAttribute : Attribute;

/// <summary>Compiles a deterministic scalar-only CLR shape once per closed type.</summary>
internal sealed class EntityConvention<T>
{
    public const string Version = "poco-scalar-v1";
    private static readonly Lazy<EntityConvention<T>> Cached = new(() => new());

    public static EntityConvention<T> Instance => Cached.Value;

    public EntityModelIdentity Model
    {
        get;
    }

    public PersistentEntityTypeIdentity EntityType
    {
        get;
    }

    public EntityMap<T> Map
    {
        get;
    }

    public IReadOnlyDictionary<string, string> MemberIdentities
    {
        get;
    }

    public string TypeBinding
    {
        get;
    }

    public string Binding(string member) => TypeBinding + ":member:" + Uri.EscapeDataString(MemberIdentities[member]);

    private readonly PropertyInfo[] _properties;
    private readonly HashSet<string> _nullable = new(StringComparer.Ordinal);

    internal static EntityConvention<T> ForAggregate() => new(property =>
        property.IsDefined(typeof(EntityOwnedAttribute)) || property.IsDefined(typeof(EntityReferenceAttribute)) ||
        property.PropertyType != typeof(string) && property.PropertyType != typeof(byte[]) &&
        typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType));

    private EntityConvention(Func<PropertyInfo, bool>? exclude = null)
    {
        Type type = typeof(T);
        if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
        {
            throw new NotSupportedException("A convention entity must be a concrete closed CLR type.");
        }

        EntityTypeAttribute declaration = type.GetCustomAttribute<EntityTypeAttribute>() ??
            throw new NotSupportedException("Convention entities require EntityType with stable model and type identities.");
        Model = new(declaration.Model);
        EntityType = new(declaration.Identity);
        TypeBinding = "urn:entityforge:model:" + Uri.EscapeDataString(Model.Value) + ":type:" + Uri.EscapeDataString(EntityType.Value);
        if (type.GetFields(BindingFlags.Public | BindingFlags.Instance).Any(field => !field.IsDefined(typeof(EntityIgnoreAttribute))))
        {
            throw new NotSupportedException("Convention mapping supports properties; public fields must be explicitly ignored.");
        }

        _properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => !property.IsDefined(typeof(EntityIgnoreAttribute)) && exclude?.Invoke(property) != true)
            .OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
        if (_properties.Any(property => property.GetIndexParameters().Length != 0 || property.GetMethod?.IsPublic != true))
        {
            throw new NotSupportedException("Convention properties require public getters and cannot be indexers.");
        }

        PropertyInfo[] identities = _properties.Where(property => property.IsDefined(typeof(EntityIdentityAttribute))).ToArray();
        PropertyInfo identity = identities.Length == 0
            ? _properties.SingleOrDefault(property => property.Name == "Id") ?? throw new NotSupportedException("A convention entity requires Id or one EntityIdentity property.")
            : identities.Length == 1 ? identities[0] : throw new NotSupportedException("An entity has multiple identity properties.");
        Type identityType = identity.PropertyType;
        if (identityType != typeof(string) && identityType != typeof(Guid) && identityType != typeof(int) && identityType != typeof(long) && identityType != typeof(Uri))
        {
            throw new NotSupportedException("Convention identities support string, Guid, int, long and absolute Uri.");
        }

        var nullability = new NullabilityInfoContext();
        var members = new List<EntityMemberMap>();
        var memberIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (PropertyInfo property in _properties)
        {
            if (property.IsDefined(typeof(EntityOwnedAttribute)) || property.IsDefined(typeof(EntityReferenceAttribute)))
            {
                throw new NotSupportedException("Lifecycle declarations require an aggregate store.");
            }

            Type valueType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (!Scalar(valueType))
            {
                throw new NotSupportedException($"Property '{property.Name}' is not a supported scalar. Nested objects and collections require explicit mapping.");
            }

            NullabilityState state = nullability.Create(property).ReadState;
            if (!valueType.IsValueType && state == NullabilityState.Unknown)
            {
                throw new NotSupportedException($"Property '{property.Name}' needs explicit CLR nullability metadata.");
            }

            bool nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null || state == NullabilityState.Nullable;
            if (property == identity && nullable)
            {
                throw new NotSupportedException("An entity identity cannot be nullable.");
            }

            if (nullable)
            {
                _nullable.Add(property.Name);
            }

            string memberId = property.GetCustomAttribute<EntityMemberAttribute>()?.Identity ?? property.Name;
            ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
            memberIds.Add(property.Name, memberId);
            ParameterExpression entity = Expression.Parameter(type, "entity");
            Expression value = Expression.Property(entity, property);
            if (valueType == typeof(DateTimeOffset))
            {
                value = Expression.Call(typeof(EntityConvention<T>), nameof(RequireUtcInstant), [property.PropertyType], value);
            }

            if (!nullable && !property.PropertyType.IsValueType)
            {
                value = Expression.Coalesce(value, Expression.Throw(Expression.New(typeof(InvalidOperationException).GetConstructor([typeof(string)])!,
                    Expression.Constant($"Required member '{property.Name}' cannot be null.")), property.PropertyType));
            }

            members.Add(new(property.Name, property.PropertyType,
                property == identity ? EntityMemberRole.Identity : EntityMemberRole.Property, false, property,
                Expression.Lambda(Expression.GetFuncType(type, property.PropertyType), value, entity).Compile()));
        }
        if (memberIds.Values.Distinct(StringComparer.Ordinal).Count() != memberIds.Count)
        {
            throw new NotSupportedException("Persistent member identities must be unique.");
        }

        MemberIdentities = new ReadOnlyDictionary<string, string>(memberIds);
        Map = new(members);
    }

    public Func<TContext, T> Materializer<TContext>(Func<ParameterExpression, EntityMemberMap, bool, Expression> read)
    {
        ParameterExpression context = Expression.Parameter(typeof(TContext), "context");
        var values = Map.Members.ToDictionary(member => member.Name,
            member => read(context, member, _nullable.Contains(member.Name)), StringComparer.Ordinal);
        ConstructorInfo[] constructors = typeof(T).GetConstructors();
        var candidates = constructors.Where(constructor => constructor.GetParameters().All(parameter =>
            _properties.Count(property => string.Equals(property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase) &&
                property.PropertyType == parameter.ParameterType) == 1) &&
            _properties.All(property => property.SetMethod?.IsPublic == true || constructor.GetParameters().Any(parameter =>
                string.Equals(property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)))).ToArray();
        if (candidates.Length == 0)
        {
            throw new NotSupportedException("Entity materialization requires a public constructor and writable properties, or a constructor matching every read-only property.");
        }

        int count = candidates.Max(constructor => constructor.GetParameters().Length);
        ConstructorInfo[] best = candidates.Where(constructor => constructor.GetParameters().Length == count).ToArray();
        if (best.Length != 1)
        {
            throw new NotSupportedException("Entity materialization has ambiguous constructors.");
        }

        ConstructorInfo selected = best[0];
        string[] names = selected.GetParameters().Select(parameter => _properties.Single(property =>
            string.Equals(property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)).Name).ToArray();
        NewExpression create = Expression.New(selected, names.Select(name => values[name]));
        MemberInitExpression initialize = Expression.MemberInit(create, _properties.Where(property => !names.Contains(property.Name))
            .Select(property => Expression.Bind(property, values[property.Name])));
        return Expression.Lambda<Func<TContext, T>>(initialize, context).Compile();
    }

    public Func<EntityMaterializationContext, T> Materializer() => Materializer<EntityMaterializationContext>((context, member, nullable) =>
        member.Role == EntityMemberRole.Identity
            ? Expression.Call(context, nameof(EntityMaterializationContext.IdentityAs), [member.ValueType])
            : Expression.Call(context, nullable ? nameof(EntityMaterializationContext.Optional) : nameof(EntityMaterializationContext.Required),
                [member.ValueType], Expression.Constant(member.Name)));

    private static TValue RequireUtcInstant<TValue>(TValue value)
    {
        if (value is DateTimeOffset instant && instant.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Convention DateTimeOffset values require UTC; native timestamp storage does not retain an original offset.");
        }

        return value;
    }

    internal static bool Scalar(Type type) => type == typeof(string) || type == typeof(Guid) || type == typeof(Uri) ||
        type == typeof(bool) || type == typeof(int) || type == typeof(long) || type == typeof(float) || type == typeof(double) ||
        type == typeof(decimal) || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(DateTimeOffset) ||
        type == typeof(TimeSpan) || type == typeof(byte[]);
}
