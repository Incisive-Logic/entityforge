namespace EntityForge.Db.Mapping;

// Shared named-value access and materialization. Profile maps still select codecs and plan all native writes.
internal sealed class EntityStructureShape
{
    private readonly List<(EntityMemberMap Member, Func<EntityMaterializationContext, object?> Read)> _members = [];

    internal void Add<T>(string name, EntityMemberRole role, bool nullable)
    {
        EntityMemberName.Validate(name);
        if (_members.Any(item => item.Member.Name == name))
        {
            throw new ArgumentException("Duplicate structure member.", nameof(name));
        }

        if (Nullable.GetUnderlyingType(typeof(T)) is not null)
        {
            throw new ArgumentException("Use the underlying codec type and the nullable argument.");
        }

        if (role == EntityMemberRole.Identity && _members.Any(item => item.Member.Role == role))
        {
            throw new InvalidOperationException("A structure has exactly one identity.");
        }

        var member = new EntityMemberMap(name, typeof(T), role, false, null,
            (Func<Entity, object?>)(_ => throw new InvalidOperationException("Unbound structure accessor.")))
        {
            DeclaredNullable = nullable
        };
        _members.Add((member, context =>
        {
            if (role == EntityMemberRole.Identity)
            {
                return context.IdentityAs<T>();
            }

            if (!context.HasValue(name))
            {
                if (!nullable)
                {
                    throw new InvalidOperationException($"Required structure member '{name}' has no value.");
                }

                return null;
            }
            object value = context.Required<T>(name)!;
            return role == EntityMemberRole.Relationship ? new EntityReference(value) : value;
        }
        ));
    }

    internal (EntityMap<Entity> Map, Func<EntityMaterializationContext, Entity> Materialize) Build()
    {
        var members = _members.ToArray();
        if (members.Count(item => item.Member.Role == EntityMemberRole.Identity) != 1)
        {
            throw new InvalidOperationException("A structure requires one identity.");
        }

        var names = members.Select(item => item.Member.Name).ToHashSet(StringComparer.Ordinal);
        var map = new EntityMap<Entity>(members.Select(item => item.Member with
        {
            ReadValue = (Func<Entity, object?>)(entity =>
            {
                if (entity.Members.Keys.Any(name => !names.Contains(name)))
                {
                    throw new ArgumentException("Entity contains an unknown structure member.", nameof(entity));
                }

                entity.Members.TryGetValue(item.Member.Name, out object? value);
                if (item.Member.Kind == EntityMemberKind.Reference && value is EntityReference reference)
                {
                    value = reference.Identity;
                }

                EntityMappedValueValidation.ValidatePresence(item.Member, value);
                if (value is not null)
                {
                    value = EntityMappedValueValidation.RequireType(value, item.Member.ValueType);
                }

                return value;
            })
        }));
        return (map, context => new Entity(members.ToDictionary(item => item.Member.Name,
            item => item.Read(context), StringComparer.Ordinal)));
    }
}
