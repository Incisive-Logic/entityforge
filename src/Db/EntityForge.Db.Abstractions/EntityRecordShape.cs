using System.Collections.ObjectModel;

namespace EntityForge.Db.Mapping;

// Rebind only runtime accessors. Native profile planners still own every mutation and identity rule.
internal sealed class EntityRecordShape<T>
{
    private readonly EntityMappingManifest _binding;
    private readonly Func<EntityMemberMap, object, object> _normalize;
    private readonly IReadOnlyList<EntityMemberMap> _members;

    internal EntityMap<IReadOnlyDictionary<string, object?>> Map
    {
        get;
    }

    internal EntityRecordShape(EntityMap<T> map, EntityMappingManifest binding,
        Func<EntityMemberMap, object, object> normalize)
    {
        if (map.Members.Any(member => member.Kind == EntityMemberKind.Owned || member.CollectionKind == EntityCollectionKind.OrderedSequence &&
            !(binding.Profile == DatabaseProfileId.PropertyGraph && member.Kind == EntityMemberKind.Scalar)))
        {
            throw new NotSupportedException("Native record compilation does not support owned values or ordered sequences.");
        }

        _binding = binding;
        _normalize = normalize;
        _members = map.Members;
        Map = new(map.Members.Select(member => member with
        {
            ReadValue = (Func<IReadOnlyDictionary<string, object?>, object?>)(record => record[member.Name])
        }));
    }

    internal IReadOnlyDictionary<string, object?> Capture(IReadOnlyDictionary<string, object?> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Keys.Any(slot => _binding.Members.All(member => member.Identity != slot)))
        {
            throw new ArgumentException("Input contains an unknown target slot.", nameof(slots));
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (EntityMemberMap member in _members)
        {
            EntityMemberContract contract = _binding.Members.Single(item => item.ClrName == member.Name);
            slots.TryGetValue(contract.Identity, out object? value);
            if (member.Kind == EntityMemberKind.Reference && value is EntityReference reference)
            {
                value = reference.Identity;
            }

            EntityMappedValueValidation.ValidatePresence(member, value, contract.IsNullable);
            if (value is not null && member.IsCollection)
            {
                var expectedKind = member.CollectionKind == EntityCollectionKind.OrderedSequence ? EntityCollectionKind.OrderedSequence : EntityCollectionKind.Set;
                if (value is not EntityCollection collection || collection.Kind != expectedKind)
                {
                    throw new ArgumentException("Native collection slots require their declared collection semantics.", nameof(slots));
                }

                Type elementType = EntityMappingManifestFactory.ValueType(member);
                Array items = Array.CreateInstance(elementType, collection.Items.Count);
                for (int index = 0; index < collection.Items.Count; index++)
                {
                    object item = collection.Items[index] ?? throw new InvalidOperationException("Mapped collection elements cannot be null.");
                    if (member.Kind == EntityMemberKind.Reference && item is EntityReference referenceItem)
                    {
                        item = referenceItem.Identity;
                    }

                    items.SetValue(_normalize(member, item), index);
                }
                value = items;
            }
            else if (value is not null)
            {
                value = _normalize(member, value);
            }

            values.Add(member.Name, value is byte[] bytes ? bytes.ToArray() : value);
        }
        return new ReadOnlyDictionary<string, object?>(values);
    }

    internal object CaptureIdentity(object identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var member = _members.Single(member => member.Kind == EntityMemberKind.Identity);
        EntityMappedValueValidation.ValidatePresence(member, identity);
        object value = _normalize(member, identity);
        return value;
    }

    internal static object RequireType(object value, Type type) => EntityMappedValueValidation.RequireType(value, type);
}

// Shared target rules for runtime POCO members and evaluated record slots.
internal static class EntityMappedValueValidation
{
    internal static object? Validate(EntityMemberMap member, object? value)
    {
        ValidatePresence(member, value);
        if (value is not null && member.Role != EntityMemberRole.Relationship)
        {
            RequireType(value, member.ValueType);
        }

        return value;
    }

    internal static void ValidatePresence(EntityMemberMap member, object? value, bool? nullable = null)
    {
        if (value is null && (member.Kind == EntityMemberKind.Identity || !(nullable ?? EntityMappingManifestFactory.IsNullable(member))))
        {
            throw new InvalidOperationException($"Required mapped member '{member.Name}' has no value.");
        }

        if (member.Kind == EntityMemberKind.Identity && value is string text && string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("A mapped identity cannot be empty.");
        }
    }

    internal static object RequireType(object value, Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (!type.IsInstanceOfType(value))
        {
            throw new InvalidOperationException($"Target codec requires {type.Name}; received {value.GetType().Name}. Convert explicitly in the source expression.");
        }

        if (value is Uri uri && !uri.IsAbsoluteUri)
        {
            throw new InvalidOperationException("Target URI values must be absolute.");
        }

        if (value is DateTimeOffset instant && instant.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Target timestamps must be UTC.");
        }

        return value;
    }
}

internal static class EntityRecordInterpretation
{
    internal static string Fingerprint(EntityMappingManifest binding, string? producer, bool delete, int batchOperations = 0)
    {
        if (producer is not null && (producer.Length != 64 || producer.Any(character => !Uri.IsHexDigit(character))))
        {
            throw new ArgumentException("A producer fingerprint must be a SHA-256 hexadecimal digest.", nameof(producer));
        }

        if (batchOperations != 0)
        {
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Format = 1,
                    Binding = binding.Fingerprint,
                    Producer = producer,
                    Delete = delete,
                    BatchOperations = batchOperations
                })));
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                Format = 1,
                Binding = binding.Fingerprint,
                Producer = producer,
                Delete = delete
            })));
    }
}
