using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using EntityForge.Db.Mapping;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>Executable domain target closure, independent of producer assemblies and generated collection nodes.</summary>
public sealed class SemanticEntityDefinition
{
    /// <summary>Maximum domain types in an ordinary target closure.</summary>
    public const int MaximumTypes = 64;

    /// <summary>Creates an immutable target closure; node zero is the root.</summary>
    public SemanticEntityDefinition(IReadOnlyList<SemanticEntityTypeDefinition> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        if (types.Count is < 1 or > MaximumTypes)
        {
            throw new ArgumentOutOfRangeException(nameof(types));
        }

        Types = Array.AsReadOnly(types.Select(type => type ?? throw new ArgumentException("Null target type.", nameof(types))).ToArray());
        if (Types.Select(type => (type.Scalars.Model.Value, type.Scalars.EntityType.Value)).Distinct().Count() != Types.Count)
        {
            throw new ArgumentException("A target closure cannot redefine a persistent type.", nameof(types));
        }

        foreach (var type in Types)
        {
            foreach (var link in type.Links)
            {
                if (link.TargetIndex is int index && (index < 0 || index >= Types.Count))
                {
                    throw new ArgumentException("A link refers outside the target closure.", nameof(types));
                }
            }
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
        writer.Write("entityforge.semantic.definition/v2");
        writer.Write(Types.Count);
        foreach (var type in Types)
        {
            writer.Write(type.Scalars.Fingerprint);
            writer.Write(type.Scheme.Identifier.AbsoluteUri);
            writer.Write((int)type.Scheme.ValueKind);
            writer.Write(type.Links.Count);
            foreach (var link in type.Links)
            {
                writer.Write(link.Identity);
                writer.Write((int)link.Kind);
                writer.Write((int)link.CollectionKind);
                writer.Write(link.IsNullable);
                writer.Write(link.Relationship);
                writer.Write(link.TargetIndex is not null);
                if (link.TargetIndex is { } target)
                {
                    writer.Write(target);
                }

                writer.Write(link.ElementCodec is not null);
                if (link.ElementCodec is { } codec)
                {
                    writer.Write(codec);
                }
            }
        }
        writer.Flush();
        Fingerprint = Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)))).ToLowerInvariant();
    }

    /// <summary>Gets the frozen domain closure in rooted traversal order.</summary>
    public IReadOnlyList<SemanticEntityTypeDefinition> Types
    {
        get;
    }

    /// <summary>Gets the execution-policy digest, excluding producer diagnostic names.</summary>
    public string Fingerprint
    {
        get;
    }

    internal static SemanticEntityDefinition Capture(AggregateNode root,
        Func<AggregateNode, AggregateLink, AggregateNode?>? resolvedTarget = null)
    {
        var indices = new Dictionary<AggregateNode, int>(ReferenceEqualityComparer.Instance);
        var types = new List<SemanticEntityTypeDefinition?>();
        int Visit(AggregateNode node)
        {
            if (indices.TryGetValue(node, out int prior))
            {
                return prior;
            }

            if (types.Count == MaximumTypes)
            {
                throw new NotSupportedException("The target closure exceeds its type bound.");
            }

            int index = types.Count;
            indices.Add(node, index);
            types.Add(null);
            var links = node.Links.OrderBy(link => link.Identity, StringComparer.Ordinal).Select(link =>
            {
                bool scalarCollection = link.Member.IsCollection && link.Member.Kind == EntityMemberKind.Scalar;
                var target = link.Member.IsCollection ? link.CollectionElementTarget : link.Target;
                string? codec = scalarCollection ? SemanticEntityMap<Entity>.ScalarCodec(link.CollectionElementType!,
                    SemanticEntityMap<Entity>.SemanticKind(link.CollectionElementType!)) : null;
                AggregateNode? targetNode = resolvedTarget is null ? target?.Invoke() : resolvedTarget(node, link);
                return new SemanticEntityLinkDefinition(link.Identity, link.Member.Kind, link.Member.CollectionKind,
                    link.Nullable, link.Term.Identifier.AbsoluteUri, targetNode is null ? null : Visit(targetNode), codec);
            }).ToArray();
            types[index] = new(node.ScalarManifest, node.Scheme, links);
            return index;
        }
        Visit(root);
        return new(types.Select(type => type!).ToArray());
    }

    internal SemanticEntityStructure CreateStructure()
    {
        var result = new SemanticEntityStructure[Types.Count];
        for (int index = 0; index < Types.Count; index++)
        {
            var type = Types[index];
            var scalar = type.Scalars;
            var builder = SemanticEntityStructure.For(scalar.Model, scalar.EntityType,
                new(new Uri(scalar.ProfileTypeBinding, UriKind.Absolute), SemanticTermRole.Concept));
            foreach (var member in scalar.Members)
            {
                if (member.ValueCodecVersion != 1 || member.CollectionKind != EntityCollectionKind.None)
                {
                    throw new NotSupportedException("Unsupported scalar target policy.");
                }

                Type valueType = CodecType(member.ValueCodec);
                var binding = scalar.ProfileBindings.Single(binding => binding.MemberIdentity == member.Identity);
                if (member.Kind == EntityMemberKind.Identity)
                {
                    Invoke(builder, nameof(builder.Identity), valueType, member.Identity, type.Scheme);
                }
                else if (member.Kind == EntityMemberKind.Scalar)
                {
                    Invoke(builder, nameof(builder.Property), valueType, member.Identity,
                        new SemanticTerm(new Uri(binding.StoredBinding, UriKind.Absolute), SemanticTermRole.Property), member.IsNullable);
                }
                else
                {
                    throw new NotSupportedException("The scalar definition contains a relationship.");
                }
            }
            foreach (var link in type.Links)
            {
                var term = new SemanticTerm(new Uri(link.Relationship, UriKind.Absolute), SemanticTermRole.Relationship);
                Func<SemanticEntityStructure> target = () => result[link.TargetIndex!.Value];
                if (link.CollectionKind == EntityCollectionKind.None)
                {
                    if (link.Kind == EntityMemberKind.Owned)
                    {
                        builder.Owned(link.Identity, term, target, link.IsNullable);
                    }
                    else
                    {
                        builder.Reference(link.Identity, term, target, link.IsNullable);
                    }
                }
                else if (link.Kind == EntityMemberKind.Scalar)
                {
                    Invoke(builder, nameof(builder.ScalarCollection), CodecType(link.ElementCodec!), link.Identity,
                        term, link.CollectionKind, link.IsNullable);
                }
                else if (link.Kind == EntityMemberKind.Owned)
                {
                    builder.OwnedCollection(link.Identity, term, link.CollectionKind, target, link.IsNullable);
                }
                else
                {
                    builder.ReferenceCollection(link.Identity, term, link.CollectionKind, target, link.IsNullable);
                }
            }
            result[index] = builder.Build();
            EntityExecutionShape.RequireExact(scalar, result[index].Node.ScalarManifest);
        }
        // Reject unreachable or reordered extra policy rather than executing a partial definition.
        if (Capture(result[0].Node).Fingerprint != Fingerprint)
        {
            throw new NotSupportedException("The target closure does not describe the exact executable rooted policy.");
        }

        return result[0];
    }

    private static void Invoke(SemanticEntityStructureBuilder builder, string name, Type type, params object[] arguments)
    {
        try
        {
            typeof(SemanticEntityStructureBuilder).GetMethods().Single(method => method.Name == name && method.IsGenericMethodDefinition)
            .MakeGenericMethod(type).Invoke(builder, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        }
    }

    private static Type CodecType(string codec) => ScalarTypes.SingleOrDefault(type =>
        SemanticEntityMap<Entity>.ScalarCodec(type, SemanticEntityMap<Entity>.SemanticKind(type)) == codec)
        ?? throw new NotSupportedException($"Unsupported Semantic target codec '{codec}'.");

    private static readonly Type[] ScalarTypes = [typeof(string),
        typeof(bool),
        typeof(byte),
        typeof(sbyte),
        typeof(short),
        typeof(ushort),
        typeof(int),
        typeof(uint),
        typeof(long),
        typeof(float),
        typeof(double),
        typeof(decimal),
        typeof(DateOnly),
        typeof(TimeOnly),
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(Guid),
        typeof(Uri),
        typeof(TimeSpan),
        typeof(byte[])];
}

/// <summary>Scalar policy and domain links for one stable Semantic type.</summary>
public sealed class SemanticEntityTypeDefinition
{
    /// <summary>Freezes a type definition.</summary>
    public SemanticEntityTypeDefinition(EntityMappingManifest scalars, SemanticIdentityScheme scheme,
        IReadOnlyList<SemanticEntityLinkDefinition> links)
    {
        Scalars = scalars ?? throw new ArgumentNullException(nameof(scalars));
        Scheme = scheme ?? throw new ArgumentNullException(nameof(scheme));
        if (scalars.Profile != DatabaseProfileId.Semantic)
        {
            throw new ArgumentException("A Semantic definition is required.", nameof(scalars));
        }

        ArgumentNullException.ThrowIfNull(links);
        Links = Array.AsReadOnly(links.Select(link => (link ?? throw new ArgumentException("Null link.", nameof(links))).Validate())
            .OrderBy(link => link.Identity, StringComparer.Ordinal).ToArray());
        if (Links.Select(link => link.Identity).Concat(scalars.Members.Select(member => member.Identity)).Distinct(StringComparer.Ordinal).Count()
            != Links.Count + scalars.Members.Count)
        {
            throw new ArgumentException("Duplicate target slots.", nameof(links));
        }
    }

    /// <summary>Gets scalar execution policy.</summary>
    public EntityMappingManifest Scalars
    {
        get;
    }

    /// <summary>Gets native identity semantics.</summary>
    public SemanticIdentityScheme Scheme
    {
        get;
    }

    /// <summary>Gets domain links, before collection lowering.</summary>
    public IReadOnlyList<SemanticEntityLinkDefinition> Links
    {
        get;
    }
}

/// <summary>Source-independent ownership, reference or collection policy.</summary>
/// <param name="Identity">Stable logical member identity within the entity definition.</param>
/// <param name="Kind">Scalar, owned or reference member classification.</param>
/// <param name="CollectionKind">Scalar, set or ordered-sequence collection policy.</param>
/// <param name="IsNullable">Whether the declared member allows a null value.</param>
/// <param name="Relationship">Absolute semantic relationship URI.</param>
/// <param name="TargetIndex">Optional target definition index for a structured link.</param>
/// <param name="ElementCodec">Optional scalar element codec identifier.</param>
public sealed record SemanticEntityLinkDefinition(string Identity, EntityMemberKind Kind,
    EntityCollectionKind CollectionKind, bool IsNullable, string Relationship, int? TargetIndex, string? ElementCodec)
{
    internal SemanticEntityLinkDefinition Validate()
    {
        EntityMemberName.Validate(Identity);
        if (!Uri.TryCreate(Relationship, UriKind.Absolute, out _))
        {
            throw new ArgumentException("An absolute relationship is required.");
        }

        if (Kind is not (EntityMemberKind.Scalar or EntityMemberKind.Owned or EntityMemberKind.Reference) ||
            CollectionKind is not (EntityCollectionKind.None or EntityCollectionKind.Set or EntityCollectionKind.OrderedSequence))
        {
            throw new ArgumentException("Unsupported link policy.");
        }

        bool scalar = Kind == EntityMemberKind.Scalar;
        if (scalar && (CollectionKind == EntityCollectionKind.None || ElementCodec is null || TargetIndex is not null) ||
            !scalar && (TargetIndex is null || ElementCodec is not null))
        {
            throw new ArgumentException("Incomplete link policy.");
        }

        return this;
    }
}
