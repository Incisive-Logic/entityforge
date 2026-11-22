using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;

namespace EntityForge.Db.Mapping;

/// <summary>Stable application namespace for durable object-mapping contracts.</summary>
public sealed record EntityModelIdentity
{
    /// <summary>Creates an application model identity without normalizing its spelling or case.</summary>
    /// <param name="value">A stable nonblank application namespace.</param>
    /// <exception cref="ArgumentException">The value is null, empty or whitespace.</exception>
    public EntityModelIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    /// <summary>Gets the exact application model namespace.</summary>
    public string Value
    {
        get;
    }

    /// <summary>Returns the underlying identifier without adding formatting or changing its case.</summary>
    /// <returns>The identifier value.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one CLR-shaped persistent type within an application model.</summary>
public sealed record PersistentEntityTypeIdentity
{
    /// <summary>Creates a persistent type identity independent of its CLR name.</summary>
    /// <param name="value">A stable nonblank type identity within the application model.</param>
    /// <exception cref="ArgumentException">The value is null, empty or whitespace.</exception>
    public PersistentEntityTypeIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    /// <summary>Gets the exact persistent type identity.</summary>
    public string Value
    {
        get;
    }

    /// <summary>Returns the underlying identifier without adding formatting or changing its case.</summary>
    /// <returns>The identifier value.</returns>
    public override string ToString() => Value;
}

/// <summary>Durable logical contract for one persistent member; it contains no CLR reflection objects.</summary>
/// <param name="Identity">The stable member identity, retained across CLR renames.</param>
/// <param name="ClrName">The current CLR member name.</param>
/// <param name="Kind">The identity, scalar, ownership or reference interpretation.</param>
/// <param name="CollectionKind">The collection semantics, or None for a scalar member.</param>
/// <param name="IsNullable">Whether persisted values may omit this member or contain null.</param>
/// <param name="ValueCodec">The stable codec identifier used to interpret stored values.</param>
/// <param name="ValueCodecVersion">The positive codec format version.</param>
/// <remarks>Validation occurs when the contract is incorporated into an <see cref="EntityMappingManifest"/>.</remarks>
public sealed record EntityMemberContract(
    string Identity,
    string ClrName,
    EntityMemberKind Kind,
    EntityCollectionKind CollectionKind,
    bool IsNullable,
    string ValueCodec,
    int ValueCodecVersion)
{
    internal EntityMemberContract Validate()
    {
        EntityMemberName.Validate(Identity);
        EntityMemberName.Validate(ClrName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ValueCodec);
        if (ValueCodecVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ValueCodecVersion));
        }

        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        if (!Enum.IsDefined(CollectionKind))
        {
            throw new ArgumentOutOfRangeException(nameof(CollectionKind));
        }

        if (Kind is EntityMemberKind.Identity && CollectionKind is not EntityCollectionKind.None)
        {
            throw new ArgumentException("An entity identity cannot be collection-valued.");
        }

        if (Kind is EntityMemberKind.Identity && IsNullable)
        {
            throw new ArgumentException("An entity identity cannot be nullable.");
        }

        return this;
    }
}

/// <summary>Profile-native binding of one stable logical member identity.</summary>
/// <param name="MemberIdentity">The logical member identity being bound.</param>
/// <param name="StoredBinding">The profile-native storage name or identifier.</param>
/// <param name="StorageNamespace">The storage namespace in which the binding must be unique.</param>
/// <param name="TargetTypeBinding">The target type binding for a reference, when applicable.</param>
/// <param name="TargetIdentityBinding">The target identity binding for a reference, when applicable.</param>
/// <param name="Direction">The profile-specific relationship direction, when applicable.</param>
public sealed record EntityProfileMemberContract(
    string MemberIdentity,
    string StoredBinding,
    string StorageNamespace = "value",
    string? TargetTypeBinding = null,
    string? TargetIdentityBinding = null,
    string? Direction = null)
{
    internal EntityProfileMemberContract Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(MemberIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(StoredBinding);
        ArgumentException.ThrowIfNullOrWhiteSpace(StorageNamespace);
        if (TargetTypeBinding is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(TargetTypeBinding);
        }

        if (TargetIdentityBinding is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(TargetIdentityBinding);
        }

        if (Direction is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Direction);
        }

        return this;
    }
}

/// <summary>
/// Durable interpretation of one object type. This is the persistence contract, not a runtime reflection cache.
/// </summary>
public sealed class EntityMappingManifest
{
    /// <summary>The supported durable mapping manifest format version.</summary>
    public const int CurrentFormatVersion = 4;

    /// <summary>Validates and captures one complete logical mapping and its profile storage bindings.</summary>
    /// <param name="model">The stable application model namespace.</param>
    /// <param name="convention">The explicit mapping or convention version identifier.</param>
    /// <param name="entityType">The stable persistent type identity.</param>
    /// <param name="profile">The database profile interpreting the bindings.</param>
    /// <param name="profileTypeBinding">The profile-native type binding.</param>
    /// <param name="members">Logical members, with unique identities and exactly one nonnullable scalar identity; copied.</param>
    /// <param name="profileBindings">Exactly one binding per logical member, with unique storage slots; copied.</param>
    /// <param name="formatVersion">The supported manifest format version.</param>
    /// <exception cref="ArgumentException">An identity, member declaration or binding is invalid or inconsistent.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A profile, format version, member kind or codec version is unsupported.</exception>
    /// <remarks>Collections are captured in ordinal member-identity order. Construction does not register or migrate the mapping.</remarks>
    public EntityMappingManifest(EntityModelIdentity model, string convention,
        PersistentEntityTypeIdentity entityType, DatabaseProfileId profile, string profileTypeBinding,
        IReadOnlyList<EntityMemberContract> members, IReadOnlyList<EntityProfileMemberContract> profileBindings,
        int formatVersion = CurrentFormatVersion)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        ArgumentException.ThrowIfNullOrWhiteSpace(convention);
        EntityType = entityType ?? throw new ArgumentNullException(nameof(entityType));
        ArgumentException.ThrowIfNullOrWhiteSpace(profileTypeBinding);
        if (!Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }

        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion));
        }

        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(profileBindings);
        var memberCopy = new EntityMemberContract[members.Count];
        var memberNames = new HashSet<string>(StringComparer.Ordinal);
        int identities = 0;
        for (int index = 0; index < memberCopy.Length; index++)
        {
            EntityMemberContract member = members[index]?.Validate() ??
                throw new ArgumentException("A mapping member cannot be null.", nameof(members));
            if (!memberNames.Add(member.Identity))
            {
                throw new ArgumentException("A mapping requires unique members and exactly one identity.", nameof(members));
            }

            if (member.Kind is EntityMemberKind.Identity)
            {
                identities++;
            }

            memberCopy[index] = member;
        }
        if (identities != 1)
        {
            throw new ArgumentException("A mapping requires unique members and exactly one identity.", nameof(members));
        }

        if (profileBindings.Count != memberCopy.Length)
        {
            throw new ArgumentException("Profile bindings must cover every logical member exactly once.", nameof(profileBindings));
        }

        var bindingCopy = new EntityProfileMemberContract[profileBindings.Count];
        var storageSlots = new HashSet<(string Namespace, string Binding)>();
        for (int index = 0; index < bindingCopy.Length; index++)
        {
            EntityProfileMemberContract binding = profileBindings[index]?.Validate() ??
                throw new ArgumentException("A profile binding cannot be null.", nameof(profileBindings));
            // Removing from the complete member set detects both missing and repeated bindings.
            if (!memberNames.Remove(binding.MemberIdentity))
            {
                throw new ArgumentException("Profile bindings must cover every logical member exactly once.", nameof(profileBindings));
            }

            if (!storageSlots.Add((binding.StorageNamespace, binding.StoredBinding)))
            {
                throw new ArgumentException("Independently writable members cannot share a profile storage slot.", nameof(profileBindings));
            }

            bindingCopy[index] = binding;
        }
        Array.Sort(memberCopy, static (left, right) => StringComparer.Ordinal.Compare(left.Identity, right.Identity));
        Array.Sort(bindingCopy, static (left, right) => StringComparer.Ordinal.Compare(left.MemberIdentity, right.MemberIdentity));
        FormatVersion = formatVersion;
        Convention = convention;
        Profile = profile;
        ProfileTypeBinding = profileTypeBinding;
        Members = Array.AsReadOnly(memberCopy);
        ProfileBindings = Array.AsReadOnly(bindingCopy);
        Fingerprint = ComputeFingerprint();
    }

    /// <summary>Gets the durable manifest encoding version.</summary>
    public int FormatVersion
    {
        get;
    }

    /// <summary>Gets the stable application model namespace.</summary>
    public EntityModelIdentity Model
    {
        get;
    }

    /// <summary>Gets the convention or explicit-mapping version identifier.</summary>
    public string Convention
    {
        get;
    }

    /// <summary>Gets the stable persistent type identity within the model.</summary>
    public PersistentEntityTypeIdentity EntityType
    {
        get;
    }

    /// <summary>Gets the profile interpreting the storage bindings.</summary>
    public DatabaseProfileId Profile
    {
        get;
    }

    /// <summary>Gets the profile-native type binding.</summary>
    public string ProfileTypeBinding
    {
        get;
    }

    /// <summary>Gets the captured logical members in ordinal identity order.</summary>
    public IReadOnlyList<EntityMemberContract> Members
    {
        get;
    }

    /// <summary>Gets the captured storage bindings in ordinal member-identity order.</summary>
    public IReadOnlyList<EntityProfileMemberContract> ProfileBindings
    {
        get;
    }

    /// <summary>Gets the deterministic fingerprint of the encoded durable mapping contract.</summary>
    public string Fingerprint
    {
        get;
    }

    private string ComputeFingerprint() => EntityMappingBinaryCodec.Fingerprint(this);

}

/// <summary>Classifies whether a proposed mapping preserves the established persisted interpretation.</summary>
public enum EntityMappingCompatibility
{
    /// <summary>The durable mapping fingerprints match exactly.</summary>
    Identical,

    /// <summary>The fingerprints differ, but existing persistent interpretations remain compatible and no members were added.</summary>
    Compatible,

    /// <summary>Only compatible optional persistent members were added.</summary>
    CompatibleAddition,

    /// <summary>A changed interpretation, removed member or required addition needs an explicit migration.</summary>
    MigrationRequired
}

/// <summary>Reports a mapping comparison without applying any changes to storage.</summary>
/// <param name="Compatibility">The compatibility category.</param>
/// <param name="Reasons">Explanations of required migration or compatible additions; an empty list for identical or compatible mappings.</param>
public sealed record EntityMappingCompatibilityResult(
    EntityMappingCompatibility Compatibility, IReadOnlyList<string> Reasons);

/// <summary>Compares an established durable mapping with a proposed replacement.</summary>
public static class EntityMappingCompatibilityValidator
{
    /// <summary>Compares durable mapping fingerprints and declarations to classify a proposed mapping change.</summary>
    /// <param name="established">The mapping already established in the database.</param>
    /// <param name="proposed">The replacement mapping to assess.</param>
    /// <returns>The compatibility category and the reasons for any differences.</returns>
    /// <exception cref="ArgumentNullException">Either mapping is null.</exception>
    /// <remarks>This is a comparison only; it does not provision, migrate or replace a mapping.</remarks>
    public static EntityMappingCompatibilityResult Compare(EntityMappingManifest established,
        EntityMappingManifest proposed)
    {
        ArgumentNullException.ThrowIfNull(established);
        ArgumentNullException.ThrowIfNull(proposed);
        if (established.Fingerprint == proposed.Fingerprint)
        {
            return new(EntityMappingCompatibility.Identical, Array.Empty<string>());
        }

        var reasons = new List<string>();
        if (established.Model != proposed.Model)
        {
            reasons.Add("The model identity changed.");
        }

        if (established.EntityType != proposed.EntityType)
        {
            reasons.Add("The persistent type identity changed.");
        }

        if (established.Profile != proposed.Profile)
        {
            reasons.Add("The database profile changed.");
        }

        if (!String.Equals(established.Convention, proposed.Convention, StringComparison.Ordinal))
        {
            reasons.Add("The mapping convention version changed.");
        }

        if (!String.Equals(established.ProfileTypeBinding, proposed.ProfileTypeBinding,
                StringComparison.Ordinal))
        {
            reasons.Add("The profile type binding changed.");
        }

        var proposedMembers = proposed.Members.ToDictionary(item => item.Identity, StringComparer.Ordinal);
        var proposedBindings = proposed.ProfileBindings.ToDictionary(item => item.MemberIdentity,
            StringComparer.Ordinal);
        var establishedBindings = established.ProfileBindings.ToDictionary(item => item.MemberIdentity,
            StringComparer.Ordinal);
        foreach (EntityMemberContract member in established.Members)
        {
            if (!proposedMembers.TryGetValue(member.Identity, out EntityMemberContract? replacement))
            {
                reasons.Add($"Persistent member '{member.Identity}' was removed.");
                continue;
            }
            if (member.Kind != replacement.Kind || member.CollectionKind != replacement.CollectionKind ||
                member.IsNullable != replacement.IsNullable ||
                !String.Equals(member.ValueCodec, replacement.ValueCodec, StringComparison.Ordinal) ||
                member.ValueCodecVersion != replacement.ValueCodecVersion)
            {
                reasons.Add($"Persistent member '{member.Identity}' changed interpretation.");
            }

            if (establishedBindings[member.Identity] != proposedBindings[member.Identity])
            {
                reasons.Add($"Persistent member '{member.Identity}' changed its stored binding.");
            }
        }
        EntityMemberContract[] additions = proposed.Members
            .Where(item => established.Members.All(old => old.Identity != item.Identity)).ToArray();
        if (additions.Any(item => !item.IsNullable))
        {
            reasons.Add("A newly added persistent member is required.");
        }

        return reasons.Count == 0
            ? additions.Length == 0
                ? new(EntityMappingCompatibility.Compatible, Array.Empty<string>())
                : new(EntityMappingCompatibility.CompatibleAddition,
                    additions.Select(item => $"Optional member '{item.Identity}' was added.").ToArray())
            : new(EntityMappingCompatibility.MigrationRequired, reasons.AsReadOnly());
    }
}

/// <summary>Projects a runtime CLR map and profile bindings into a durable, reflection-free contract.</summary>
internal static class EntityMappingManifestFactory
{

    public static EntityMappingManifest Create<T>(EntityMap<T> map, EntityModelIdentity model,
        PersistentEntityTypeIdentity entityType, DatabaseProfileId profile, string profileTypeBinding,
        IReadOnlyDictionary<string, EntityProfileMemberContract> memberBindings,
        Func<EntityMemberMap, string> codec, string convention = "explicit-v1",
        IReadOnlyDictionary<string, string>? persistentMemberIdentities = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(memberBindings);
        if (persistentMemberIdentities is not null && persistentMemberIdentities.Keys.Any(name =>
                map.Members.All(member => member.Name != name)))
        {
            throw new ArgumentException("Persistent member identities contain a member absent from the logical map.",
                nameof(persistentMemberIdentities));
        }

        string PersistentIdentity(EntityMemberMap member) => persistentMemberIdentities is not null &&
            persistentMemberIdentities.TryGetValue(member.Name, out string? identity) ? identity : member.Name;
        EntityMemberContract[] members = map.Members.Select(member => new EntityMemberContract(
            PersistentIdentity(member), member.Name, member.Kind, member.CollectionKind, IsNullable(member),
            codec(member), 1)).ToArray();
        EntityProfileMemberContract[] bindings = map.Members.Select(member =>
            memberBindings.TryGetValue(member.Name, out EntityProfileMemberContract? binding)
                ? binding with
                {
                    MemberIdentity = PersistentIdentity(member)
                }
                : throw new ArgumentException(
                    $"Profile binding for persistent member '{member.Name}' is missing.", nameof(memberBindings)))
            .ToArray();
        if (memberBindings.Count != bindings.Length)
        {
            throw new ArgumentException("Profile bindings contain a member absent from the logical map.",
                nameof(memberBindings));
        }

        return new(model, convention, entityType, profile, profileTypeBinding, members, bindings);
    }

    internal static bool IsNullable(EntityMemberMap member)
    {
        if (member.DeclaredNullable is bool declared)
        {
            return declared;
        }

        if (Nullable.GetUnderlyingType(member.ValueType) is not null)
        {
            return true;
        }

        if (member.ValueType.IsValueType)
        {
            return false;
        }

        return member.Member switch
        {
            PropertyInfo property => new NullabilityInfoContext().Create(property).ReadState is NullabilityState.Nullable,
            FieldInfo field => new NullabilityInfoContext().Create(field).ReadState is NullabilityState.Nullable,
            _ => true
        };
    }

    internal static Type ValueType(EntityMemberMap member)
    {
        Type type = Nullable.GetUnderlyingType(member.ValueType) ?? member.ValueType;
        if (!member.IsCollection)
        {
            return type;
        }

        if (type.IsArray)
        {
            return type.GetElementType()!;
        }

        Type? enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type : type.GetInterfaces().FirstOrDefault(candidate => candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0] ?? throw new NotSupportedException(
            $"Collection member '{member.Name}' has no identifiable element type.");
    }
}
