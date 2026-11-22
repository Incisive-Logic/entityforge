using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using EntityForge.Db.Mapping;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>Compiles explicit POCO lifecycle declarations and lossless collection shapes into native aggregate maps.</summary>
public static class SemanticAggregateConventions
{
    /// <summary>Gets the cached aggregate map. Nested entities require EntityOwned or EntityReference.</summary>
    public static SemanticAggregateMap<T> Map<T>() where T : class => Cache<T>.Value.Value;

    private static class Cache<T> where T : class
    {
        internal static readonly Lazy<SemanticAggregateMap<T>> Value = new(Compile<T>);
    }

    private static SemanticAggregateMap<T> Compile<T>() where T : class
    {
        var shape = EntityConvention<T>.ForAggregate();
        var builder = SemanticAggregateMap.For(SemanticEntityConventions.Compile(shape), shape.Model, shape.EntityType);
        foreach (PropertyInfo property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            bool owned = property.IsDefined(typeof(EntityOwnedAttribute));
            bool reference = property.IsDefined(typeof(EntityReferenceAttribute));
            bool ignored = property.IsDefined(typeof(EntityIgnoreAttribute));
            if (owned && reference || ignored && (owned || reference))
            {
                throw new NotSupportedException($"Property '{property.Name}' has contradictory mapping declarations.");
            }

            if (ignored)
            {
                continue;
            }

            Type type = property.PropertyType;
            bool collection = type != typeof(string) && type != typeof(byte[]) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
            if (!owned && !reference && !collection)
            {
                continue;
            }

            if (property.IsDefined(typeof(EntityIdentityAttribute)) || property.Name == shape.Map.Identity!.Name)
            {
                throw new NotSupportedException("An aggregate identity cannot be a relationship or collection.");
            }

            if (property.GetMethod?.IsPublic != true || property.GetIndexParameters().Length != 0 ||
                property.SetMethod?.IsPublic != true || property.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)))
            {
                throw new NotSupportedException($"Aggregate property '{property.Name}' requires a public getter and non-init setter.");
            }

            string id = property.GetCustomAttribute<EntityMemberAttribute>()?.Identity ?? property.Name;
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            var term = new SemanticTerm(new Uri(shape.TypeBinding + ":member:" + Uri.EscapeDataString(id)), SemanticTermRole.Relationship);
            EntityMemberKind kind = owned ? EntityMemberKind.Owned : reference ? EntityMemberKind.Reference : EntityMemberKind.Scalar;
            if (!collection)
            {
                if (!type.IsClass || EntityConvention<T>.Scalar(type))
                {
                    throw new NotSupportedException($"Property '{property.Name}' does not refer to an entity type.");
                }

                Invoke(nameof(AddSingle), [typeof(T), type], [builder, property, term, kind, id]);
                continue;
            }
            (Type element, EntityCollectionKind semantics) = CollectionShape(type);
            bool scalar = EntityConvention<T>.Scalar(element);
            if (scalar && (owned || reference) || !scalar && !owned && !reference || !scalar && !element.IsClass)
            {
                throw new NotSupportedException($"Collection '{property.Name}' requires an unambiguous scalar or entity element lifecycle.");
            }

            NullabilityInfo info = new NullabilityInfoContext().Create(property);
            NullabilityInfo? itemInfo = type.IsArray ? info.ElementType : info.GenericTypeArguments.SingleOrDefault();
            if (Nullable.GetUnderlyingType(element) is not null || itemInfo?.ReadState is NullabilityState.Nullable or NullabilityState.Unknown)
            {
                throw new NotSupportedException("Collection elements require non-nullable types.");
            }

            Invoke(nameof(AddCollection), [typeof(T), element], [builder, property, term, kind, semantics, id]);
        }
        return builder.Build();
    }

    private static (Type, EntityCollectionKind) CollectionShape(Type type)
    {
        if (type.IsArray && type.GetArrayRank() == 1)
        {
            return (type.GetElementType()!, EntityCollectionKind.OrderedSequence);
        }

        if (type.IsGenericType)
        {
            Type definition = type.GetGenericTypeDefinition();
            if (definition == typeof(List<>) || definition == typeof(IList<>) || definition == typeof(IReadOnlyList<>))
            {
                return (type.GetGenericArguments()[0], EntityCollectionKind.OrderedSequence);
            }

            if (definition == typeof(HashSet<>) || definition == typeof(ISet<>) || definition == typeof(IReadOnlySet<>))
            {
                return (type.GetGenericArguments()[0], EntityCollectionKind.Set);
            }
        }
        throw new NotSupportedException("Collections require a one-dimensional array, List/IList/IReadOnlyList, or HashSet/ISet/IReadOnlySet. Other shapes require explicit mapping.");
    }

    private static void AddSingle<T, TChild>(SemanticAggregateMapBuilder<T> builder, PropertyInfo property,
        SemanticTerm term, EntityMemberKind kind, string id) where T : class where TChild : class
    {
        ParameterExpression owner = Expression.Parameter(typeof(T));
        ParameterExpression value = Expression.Parameter(typeof(TChild));
        var read = Expression.Lambda<Func<T, TChild?>>(Expression.Property(owner, property), owner);
        var assign = Expression.Lambda<Action<T, TChild?>>(Expression.Assign(Expression.Property(owner, property), value), owner, value).Compile();
        if (kind == EntityMemberKind.Owned)
        {
            builder.Owned(read, term, Map<TChild>, assign, id);
        }
        else
        {
            builder.Reference(read, term, Map<TChild>, assign, id);
        }
    }

    private static void AddCollection<T, TElement>(SemanticAggregateMapBuilder<T> builder, PropertyInfo property,
        SemanticTerm term, EntityMemberKind kind, EntityCollectionKind semantics, string id) where T : class
    {
        ParameterExpression owner = Expression.Parameter(typeof(T));
        ParameterExpression values = Expression.Parameter(typeof(IReadOnlyList<TElement>));
        var read = Expression.Lambda<Func<T, IEnumerable<TElement>?>>(
            Expression.Convert(Expression.Property(owner, property), typeof(IEnumerable<TElement>)), owner);
        Expression converted = Expression.Convert(Expression.Call(typeof(SemanticAggregateConventions), nameof(ConvertCollection), [typeof(TElement)],
            values, Expression.Constant(property.PropertyType)), property.PropertyType);
        var assign = Expression.Lambda<Action<T, IReadOnlyList<TElement>?>>(
            Expression.Assign(Expression.Property(owner, property), converted), owner, values).Compile();
        Func<AggregateNode>? target = kind == EntityMemberKind.Scalar ? null : () =>
            (AggregateNode)Invoke(nameof(Node), [typeof(TElement)], [])!;
        builder.Collection(read, term, assign, kind, semantics, target, id);
    }

    private static AggregateNode Node<T>() where T : class => Map<T>().Node;

    private static object? ConvertCollection<TElement>(IReadOnlyList<TElement>? values, Type type)
    {
        if (values is null)
        {
            return null;
        }

        if (type.IsArray)
        {
            return values.ToArray();
        }

        Type definition = type.GetGenericTypeDefinition();
        if (definition == typeof(HashSet<>) || definition == typeof(ISet<>) || definition == typeof(IReadOnlySet<>))
        {
            var set = new HashSet<TElement>(values);
            if (set.Count != values.Count)
            {
                throw new InvalidOperationException("CLR equality collapses distinct native set elements.");
            }

            return set;
        }
        return values.ToList();
    }

    private static object? Invoke(string method, Type[] types, object?[] arguments)
    {
        try
        {
            return typeof(SemanticAggregateConventions).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(types).Invoke(null, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
