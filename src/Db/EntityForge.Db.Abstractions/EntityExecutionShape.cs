namespace EntityForge.Db.Mapping;

// Runtime accessors for stable target slots, never producer CLR names or reflection members.
internal static class EntityExecutionShape
{
    internal static EntityMappingManifest TargetManifest(EntityMappingManifest manifest) => new(
        manifest.Model, manifest.Convention, manifest.EntityType, manifest.Profile,
        manifest.ProfileTypeBinding, manifest.Members.Select(member => member with
        {
            ClrName = member.Identity
        }).ToArray(), manifest.ProfileBindings, manifest.FormatVersion);

    internal static EntityMap<IReadOnlyDictionary<string, object?>> Create(
        EntityMappingManifest manifest, Func<string, Type> codecType)
    {
        return new(manifest.Members.Select(member =>
        {
            if (member.ValueCodecVersion != 1 || member.Kind == EntityMemberKind.Owned ||
                member.CollectionKind == EntityCollectionKind.OrderedSequence &&
                    !(manifest.Profile == DatabaseProfileId.PropertyGraph && member.Kind == EntityMemberKind.Scalar))
            {
                throw new NotSupportedException("This target requires an unsupported codec, owned value or collection execution policy.");
            }

            Type elementType = codecType(member.ValueCodec);
            bool collection = member.CollectionKind != EntityCollectionKind.None;
            Type type = collection ? elementType.MakeArrayType() : elementType;
            var role = member.Kind switch
            {
                EntityMemberKind.Identity => EntityMemberRole.Identity,
                EntityMemberKind.Scalar => EntityMemberRole.Property,
                EntityMemberKind.Reference => EntityMemberRole.Relationship,
                _ => throw new NotSupportedException("Unsupported target member kind.")
            };
            return new EntityMemberMap(member.Identity, type, role, collection, null,
                (Func<IReadOnlyDictionary<string, object?>, object?>)(values => values[member.Identity]))
            {
                DeclaredNullable = member.IsNullable,
                CollectionKind = member.CollectionKind
            };
        }));
    }

    internal static void RequireExact(EntityMappingManifest requested, EntityMappingManifest executable)
    {
        if (requested.Fingerprint != executable.Fingerprint)
        {
            throw new NotSupportedException("The target definition contains execution policy unsupported by this profile planner.");
        }
    }
}
