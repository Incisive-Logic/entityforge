using System.Linq.Expressions;
using EntityForge.Db.Mapping;
using EntityForge.Query.Projection;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>Compiles scalar POCO conventions into an inspectable Semantic map and target binding.</summary>
public static class SemanticEntityConventions
{
    /// <summary>Gets the compiled native map for this CLR type.</summary>
    public static SemanticEntityMap<T> Map<T>() => Cache<T>.Map;

    /// <summary>Gets the exact durable mapping contract, suitable for explicit registration or upgrade.</summary>
    public static EntityMappingManifest Manifest<T>() => Cache<T>.Manifest;

    /// <summary>Gets the source-neutral target binding shared with transforms.</summary>
    public static SemanticTargetBinding TargetBinding<T>() => Cache<T>.Binding;

    private static class Cache<T>
    {
        private static readonly EntityConvention<T> Shape = EntityConvention<T>.Instance;
        public static readonly SemanticEntityMap<T> Map = Compile(Shape);
        public static readonly EntityMappingManifest Manifest = Map.CreateManifest(Shape.Model, Shape.EntityType,
            AggregateNode.Convention, Shape.MemberIdentities);

        public static readonly SemanticTargetBinding Binding = Map.CreateTargetBinding(new Uri(Shape.TypeBinding + ":binding"),
            EntityConvention<T>.Version, new(new Uri("urn:entityforge:model:" + Uri.EscapeDataString(Shape.Model.Value)),
                EntityConvention<T>.Version, SemanticModelKind.Vocabulary), Shape.MemberIdentities);

    }

    internal static SemanticEntityMap<T> Compile<T>(EntityConvention<T> shape)
    {
        EntityMemberMap identity = shape.Map.Identity!;
        Type identityType = identity.ValueType;
        var scheme = new SemanticIdentityScheme(new Uri(shape.Binding(identity.Name)), "Identity",
            identityType == typeof(Guid) ? SemanticValueKind.Guid : identityType == typeof(Uri) ? SemanticValueKind.AbsoluteUri :
            identityType == typeof(string) ? SemanticValueKind.String : SemanticValueKind.Integer);
        EntityMemberMap[] fields = shape.Map.Members.Where(member => member.Role == EntityMemberRole.Property).ToArray();
        SemanticTerm Term(EntityMemberMap member) => new(new Uri(shape.Binding(member.Name)), SemanticTermRole.Property);
        return new(shape.Map, new(new Uri(shape.TypeBinding), SemanticTermRole.Concept),
            fields.Select(member => new EntityFieldProjection(Term(member), member.Name)),
            shape.Materializer<SemanticEntityMaterializationContext>((context, member, nullable) =>
            {
                if (member.Role == EntityMemberRole.Identity)
                {
                    return Expression.Call(typeof(SemanticEntityConventions), nameof(ReadIdentity), [member.ValueType],
                        Expression.Property(context, nameof(SemanticEntityMaterializationContext.Identity)));
                }

                Type valueType = Nullable.GetUnderlyingType(member.ValueType) ?? member.ValueType;
                Expression value = Expression.Call(context, nullable ? nameof(SemanticEntityMaterializationContext.Optional) :
                    nameof(SemanticEntityMaterializationContext.Required), [valueType], Expression.Constant(member.Name));
                if (nullable)
                {
                    value = Expression.Condition(Expression.Property(value, "HasValue"),
                        Expression.Convert(Expression.Property(value, "Value"), member.ValueType), Expression.Default(member.ValueType));
                }

                return value;
            }), null, new(identity.Name, scheme),
            fields.Select(member => new SemanticMemberBinding(member.Name, Term(member), member.Role, member.Name)));
    }

    private static TValue ReadIdentity<TValue>(string identity) =>
        new EntityMaterializationContext(identity, new Dictionary<string, object?>()).IdentityAs<TValue>();
}
