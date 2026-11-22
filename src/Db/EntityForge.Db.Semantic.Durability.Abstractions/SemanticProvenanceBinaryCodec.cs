using System.Text;
using System.Runtime.InteropServices;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

internal static class SemanticProvenanceBinaryCodec
{
    internal static void Write(BinaryWriter writer, SemanticProvenance value)
    {
        writer.Write(value.SourceSystem.AbsoluteUri);
        writer.Write(value.SourceRecord);
        WriteDate(writer, value.ObservedAt);
        WriteModel(writer, value.TargetVocabulary);
        writer.Write(value.Mappings.Count);
        foreach (SemanticModelReference mapping in value.Mappings)
        {
            WriteModel(writer, mapping);
        }

        WriteNullableDate(writer, value.ValidFrom);
        WriteNullableDate(writer, value.ValidTo);
        WriteNullableString(writer, value.CorrelationId);
        writer.Write(value.SourceEntity is not null);
        if (value.SourceEntity is not null)
        {
            writer.Write(value.SourceEntity.Identifier.AbsoluteUri);
            WriteNullableString(writer, value.SourceEntity.Description);
        }
        writer.Write(value.TransformActivity is not null);
        if (value.TransformActivity is not null)
        {
            writer.Write(value.TransformActivity.Identifier.AbsoluteUri);
            WriteNullableDate(writer, value.TransformActivity.StartedAt);
            WriteNullableDate(writer, value.TransformActivity.EndedAt);
        }
        writer.Write(value.ResponsibleAgent is not null);
        if (value.ResponsibleAgent is not null)
        {
            writer.Write(value.ResponsibleAgent.Identifier.AbsoluteUri);
            WriteNullableString(writer, value.ResponsibleAgent.Name);
        }
        writer.Write(value.TransformTemplate is not null);
        if (value.TransformTemplate is not null)
        {
            writer.Write(value.TransformTemplate.Identifier.AbsoluteUri);
            writer.Write(value.TransformTemplate.Version);
            WriteNullableString(writer, value.TransformTemplate.ContentHash);
        }
        WriteDate(writer, value.GeneratedAt);
        writer.Write(value.SourceVocabulary is not null);
        if (value.SourceVocabulary is not null)
        {
            WriteModel(writer, value.SourceVocabulary);
        }

        writer.Write(value.TargetBinding is not null);
        if (value.TargetBinding is not null)
        {
            writer.Write(value.TargetBinding.Identifier.AbsoluteUri);
            writer.Write(value.TargetBinding.Version);
            writer.Write(value.TargetBinding.ContentHash!);
        }
    }

    internal static SemanticProvenance Decode(ReadOnlyMemory<byte> payload)
    {
        using var stream = MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, false)
            : new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        var sourceSystem = DatabaseDecodedIdentifiers.AbsoluteUri(reader.ReadString());
        string sourceRecord = reader.ReadString();
        DateTimeOffset observedAt = ReadDate(reader);
        SemanticModelReference targetVocabulary = ReadModel(reader);
        int mappingCount = reader.ReadInt32();
        if (mappingCount < 0 || mappingCount > stream.Length - stream.Position)
        {
            throw new InvalidDataException("Semantic provenance mapping count is invalid.");
        }

        var mappings = new SemanticModelReference[mappingCount];
        for (int index = 0; index < mappings.Length; index++)
        {
            mappings[index] = ReadModel(reader);
        }

        DateTimeOffset? validFrom = ReadNullableDate(reader);
        DateTimeOffset? validTo = ReadNullableDate(reader);
        string? correlationId = ReadNullableString(reader);
        SemanticProvenanceEntity? sourceEntity = SemanticValueBinaryCodec.Flag(reader)
            ? new(DatabaseDecodedIdentifiers.AbsoluteUri(reader.ReadString()), ReadNullableString(reader)) : null;
        SemanticProvenanceActivity? activity = SemanticValueBinaryCodec.Flag(reader)
            ? new(DatabaseDecodedIdentifiers.AbsoluteUri(reader.ReadString()), ReadNullableDate(reader), ReadNullableDate(reader)) : null;
        SemanticProvenanceAgent? agent = SemanticValueBinaryCodec.Flag(reader)
            ? new(DatabaseDecodedIdentifiers.AbsoluteUri(reader.ReadString()), ReadNullableString(reader)) : null;
        SemanticArtifactReference? template = SemanticValueBinaryCodec.Flag(reader)
            ? new(DatabaseDecodedIdentifiers.AbsoluteUri(reader.ReadString()), ReadIdentifier(reader), ReadNullableIdentifier(reader)) : null;
        DateTimeOffset generatedAt = ReadDate(reader);
        SemanticModelReference? sourceVocabulary = SemanticValueBinaryCodec.Flag(reader) ? ReadModel(reader) : null;
        SemanticArtifactReference? targetBinding = SemanticValueBinaryCodec.Flag(reader)
            ? new(DatabaseDecodedIdentifiers.AbsoluteUri(reader.ReadString()), ReadIdentifier(reader), ReadIdentifier(reader)) : null;
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Semantic provenance atom has trailing bytes.");
        }

        return new(sourceSystem, sourceRecord, observedAt, targetVocabulary, mappings,
            validFrom, validTo, correlationId, sourceEntity, activity, agent, template, generatedAt,
            sourceVocabulary, targetBinding);
    }

    private static void WriteModel(BinaryWriter writer, SemanticModelReference value)
    {
        writer.Write(value.Identifier.AbsoluteUri);
        writer.Write(value.Version);
        writer.Write((byte)value.Kind);
    }

    private static SemanticModelReference ReadModel(BinaryReader reader) =>
        new(DatabaseDecodedIdentifiers.AbsoluteUri(reader.ReadString()), ReadIdentifier(reader),
            (SemanticModelKind)reader.ReadByte());

    private static string ReadIdentifier(BinaryReader reader) => DatabaseDecodedIdentifiers.Text(reader.ReadString());

    private static string? ReadNullableIdentifier(BinaryReader reader) =>
        SemanticValueBinaryCodec.Flag(reader) ? ReadIdentifier(reader) : null;

    private static void WriteDate(BinaryWriter writer, DateTimeOffset value)
    {
        writer.Write(value.Ticks);
        writer.Write(checked((short)value.Offset.TotalMinutes));
    }

    private static DateTimeOffset ReadDate(BinaryReader reader) =>
        new(reader.ReadInt64(), TimeSpan.FromMinutes(reader.ReadInt16()));

    private static void WriteNullableDate(BinaryWriter writer, DateTimeOffset? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            WriteDate(writer, value.Value);
        }
    }

    private static DateTimeOffset? ReadNullableDate(BinaryReader reader) =>
        SemanticValueBinaryCodec.Flag(reader) ? ReadDate(reader) : null;

    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value);
        }
    }

    private static string? ReadNullableString(BinaryReader reader) =>
        SemanticValueBinaryCodec.Flag(reader) ? reader.ReadString() : null;

}
