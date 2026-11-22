using EntityForge.Db.Mapping;
using EntityForge.Semantics;
using static EntityForge.Db.Mapping.DeliveryBinary;

namespace EntityForge.Db.Semantic;

// Complete transported policy, including diagnostic names. Canonical operation
// identities deliberately use the narrower semantic fingerprints instead.
internal static class SemanticDeliveryMetadata
{
    internal static void WritePlan(BinaryWriter w, SemanticEntityDefinition definition,
        SemanticApplicationIdentity application, SemanticEntityStoreLimits limits, SemanticEntityRequest.BindingMetadata? binding)
    {
        WriteDefinition(w, definition);
        WriteApplication(w, application);
        WriteLimits(w, limits);
        WriteBinding(w, binding);
    }

    internal static void WriteDefinition(BinaryWriter w, SemanticEntityDefinition definition)
    {
        w.Write(definition.Types.Count);
        foreach (var type in definition.Types)
        {
            Maps(w, [type.Scalars]);
            WriteScheme(w, type.Scheme);
            w.Write(type.Links.Count);
            foreach (var link in type.Links)
            {
                w.Write(link.Identity);
                w.Write((int)link.Kind);
                w.Write((int)link.CollectionKind);
                w.Write(link.IsNullable);
                w.Write(link.Relationship);
                w.Write(link.TargetIndex.HasValue);
                if (link.TargetIndex is { } index)
                {
                    w.Write(index);
                }

                Optional(w, link.ElementCodec);
            }
        }
    }

    internal static SemanticEntityDefinition ReadDefinition(BinaryReader r)
    {
        var types = new SemanticEntityTypeDefinition[Count(r, SemanticEntityDefinition.MaximumTypes)];
        for (int i = 0; i < types.Length; i++)
        {
            var map = Map(r);
            var scheme = ReadScheme(r);
            var links = new SemanticEntityLinkDefinition[Count(r)];
            for (int j = 0; j < links.Length; j++)
            {
                links[j] = new(Text(r), EnumValue<EntityMemberKind>(r), EnumValue<EntityCollectionKind>(r),
                Flag(r), Text(r), Flag(r) ? r.ReadInt32() : null, Optional(r));
            }

            types[i] = new(map, scheme, links);
        }
        return new(types);
    }

    private static void WriteModel(BinaryWriter w, SemanticModelReference model)
    {
        w.Write(model.Identifier.AbsoluteUri);
        w.Write(model.Version);
        w.Write((int)model.Kind);
    }

    private static SemanticModelReference ReadModel(BinaryReader r) => new(new Uri(Text(r), UriKind.Absolute), Text(r), EnumValue<SemanticModelKind>(r));

    private static void WriteTerm(BinaryWriter w, SemanticTerm term)
    {
        w.Write(term.Identifier.AbsoluteUri);
        w.Write((int)term.Role);
    }

    private static SemanticTerm ReadTerm(BinaryReader r) => new(new Uri(Text(r), UriKind.Absolute), EnumValue<SemanticTermRole>(r));

    private static void WriteScheme(BinaryWriter w, SemanticIdentityScheme scheme)
    {
        w.Write(scheme.Identifier.AbsoluteUri);
        w.Write(scheme.Name);
        w.Write((int)scheme.ValueKind);
        Optional(w, scheme.Description);
    }

    private static SemanticIdentityScheme ReadScheme(BinaryReader r) => new(new Uri(Text(r), UriKind.Absolute), Text(r), EnumValue<SemanticValueKind>(r), Optional(r));

    internal static void WriteApplication(BinaryWriter w, SemanticApplicationIdentity value)
    {
        w.Write(value.Identifier.AbsoluteUri);
        w.Write(value.Version);
        WriteModel(w, value.TargetVocabulary);
        Guid(w, value.GenerationId);
        Guid(w, value.ProducerId);
    }

    internal static SemanticApplicationIdentity ReadApplication(BinaryReader r) => new(new Uri(Text(r), UriKind.Absolute), Text(r), ReadModel(r), Guid(r), Guid(r));

    internal static void WriteLimits(BinaryWriter w, SemanticEntityStoreLimits limits)
    {
        w.Write(limits.Entities);
        w.Write(limits.Depth);
        w.Write(limits.Bytes);
    }

    internal static SemanticEntityStoreLimits ReadLimits(BinaryReader r) => new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());

    internal static void WriteOperation(BinaryWriter w, SemanticEntityWriteOperation value)
    {
        Guid(w, value.OperationId);
        w.Write(value.ObservedAt.Ticks);
        w.Write(value.ObservedAt.Offset.Ticks);
    }

    internal static SemanticEntityWriteOperation ReadOperation(BinaryReader r) => new(Guid(r), new DateTimeOffset(r.ReadInt64(), new TimeSpan(r.ReadInt64())));

    internal static void WritePosition(BinaryWriter w, SemanticSourcePosition? value)
    {
        w.Write(value is not null);
        if (value is null)
        {
            return;
        }

        Guid(w, value.GenerationId);
        w.Write(value.SourceVersion);
        w.Write(value.SourcePosition);
    }

    internal static SemanticSourcePosition? ReadPosition(BinaryReader r) => Flag(r) ? new(Guid(r), Text(r), Text(r)) : null;

    private static void WriteBinding(BinaryWriter w, SemanticEntityRequest.BindingMetadata? binding)
    {
        w.Write(binding is not null);
        if (binding is null)
        {
            return;
        }

        w.Write(binding.Identifier.AbsoluteUri);
        w.Write(binding.Version);
        WriteModel(w, binding.TargetVocabulary);
        WriteTerm(w, binding.Concept);
        WriteScheme(w, binding.IdentityScheme);
        w.Write(binding.Members.Count);
        foreach (var member in binding.Members)
        {
            w.Write(member.Slot);
            WriteTerm(w, member.Term);
            w.Write(member.ValueKind.HasValue);
            if (member.ValueKind is { } kind)
            {
                w.Write((int)kind);
            }

            w.Write(member.TargetConcept is not null);
            if (member.TargetConcept is { } concept)
            {
                WriteTerm(w, concept);
            }

            w.Write(member.TargetIdentityScheme is not null);
            if (member.TargetIdentityScheme is { } scheme)
            {
                WriteScheme(w, scheme);
            }
        }
    }

    internal static SemanticEntityRequest.BindingMetadata? ReadBinding(BinaryReader r)
    {
        if (!Flag(r))
        {
            return null;
        }

        var id = new Uri(Text(r), UriKind.Absolute);
        string version = Text(r);
        var model = ReadModel(r);
        var concept = ReadTerm(r);
        var scheme = ReadScheme(r);
        var members = new SemanticTargetMemberBinding[Count(r)];
        for (int i = 0; i < members.Length; i++)
        {
            members[i] = new(Text(r), ReadTerm(r), Flag(r) ? EnumValue<SemanticValueKind>(r) : null,
            Flag(r) ? ReadTerm(r) : null, Flag(r) ? ReadScheme(r) : null);
        }

        return new(id, version, model, concept, scheme, Array.AsReadOnly(members));
    }
}
