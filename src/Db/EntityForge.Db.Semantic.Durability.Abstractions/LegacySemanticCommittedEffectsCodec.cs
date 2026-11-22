using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Compression;
using EntityForge.Db.Semantic;
using EntityForge.Db;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Durability;

internal static class LegacySemanticCommittedEffectsCodec
{
    public const string CodecId = "entityforge.semantic.effects";
    public const int CodecVersion = 9;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static ReadOnlyMemory<byte> Encode(SemanticCommittedEffects effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        using var payload = new MemoryStream();
        using (var compression = new BrotliStream(payload, CompressionLevel.Fastest, true))
        {
            Serialize(compression, effects);
        }

        if (!payload.TryGetBuffer(out ArraySegment<byte> segment) || segment.Array is null)
        {
            throw new InvalidOperationException("The Semantic committed-effect buffer could not be accessed.");
        }

        return new ReadOnlyMemory<byte>(segment.Array, segment.Offset, checked((int)payload.Length));
    }

    private static void Serialize(Stream destination, SemanticCommittedEffects effects)
    {
        using var writer = new Utf8JsonWriter(destination);
        JsonSerializer.Serialize(writer, effects, SerializerOptions);
        writer.Flush();
    }

    public static SemanticCommittedEffects Decode(ReadOnlyMemory<byte> payload)
    {
        using Stream compressed = ReadStream(payload);
        using var decompression = new BrotliStream(compressed, CompressionMode.Decompress);
        SemanticCommittedEffects wire = JsonSerializer.Deserialize<SemanticCommittedEffects>(
            decompression, SerializerOptions) ??
            throw new InvalidDataException("The semantic committed-effect payload is empty.");
        return wire;
    }

    public static SemanticCommittedEffects Decode(ReadOnlySpan<byte> payload) =>
        Decode((ReadOnlyMemory<byte>)payload.ToArray());

    private static Stream ReadStream(ReadOnlyMemory<byte> payload)
    {
        if (MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> segment) && segment.Array is not null)
        {
            return new MemoryStream(segment.Array, segment.Offset, segment.Count, false, true);
        }

        return new MemoryStream(payload.ToArray(), false);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new SemanticCommittedEffectsJsonConverter());
        options.Converters.Add(new SemanticProvenanceJsonConverter());
        return options;
    }

    private sealed class SemanticCommittedEffectsJsonConverter : JsonConverter<SemanticCommittedEffects>
    {
        public override SemanticCommittedEffects Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            Require(ref reader, JsonTokenType.StartObject, "Semantic committed effects must be an object.");
            List<SemanticEntityIdentity> subjects = ReadTable<SemanticEntityIdentity>(ref reader, "s", options);
            List<SemanticTerm> terms = ReadTable<SemanticTerm>(ref reader, "t", options);
            List<SemanticValue> values = ReadTable<SemanticValue>(ref reader, "v", options);
            List<SemanticProvenance> provenance = ReadTable<SemanticProvenance>(ref reader, "p", options);
            ReadProperty(ref reader, "e");
            Require(ref reader, JsonTokenType.StartArray, "The semantic effect table is invalid.");
            var effects = new List<SemanticCommittedEffect>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                effects.Add(ReadEffect(ref reader, subjects, terms, values, provenance, options));
            }

            if (reader.TokenType != JsonTokenType.EndArray || !reader.Read() ||
                reader.TokenType != JsonTokenType.EndObject)
            {
                throw new JsonException("Semantic committed effects are incomplete.");
            }

            return new(effects);
        }

        public override void Write(Utf8JsonWriter writer, SemanticCommittedEffects value,
            JsonSerializerOptions options)
        {
            CodecTables tables = CodecTables.Create(value.Effects);
            writer.WriteStartObject();
            WriteTable(writer, "s", tables.Subjects, options);
            WriteTable(writer, "t", tables.Terms, options);
            WriteTable(writer, "v", tables.Values, options);
            WriteTable(writer, "p", tables.Provenance, options);
            writer.WritePropertyName("e");
            writer.WriteStartArray();
            foreach (SemanticCommittedEffect effect in value.Effects)
            {
                WriteEffect(writer, effect, tables, options);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        private static SemanticCommittedEffect ReadEffect(ref Utf8JsonReader reader,
            IReadOnlyList<SemanticEntityIdentity> subjects, IReadOnlyList<SemanticTerm> terms,
            IReadOnlyList<SemanticValue> values, IReadOnlyList<SemanticProvenance> provenance,
            JsonSerializerOptions options)
        {
            Require(ref reader, JsonTokenType.StartArray, "A semantic committed effect must be an array.");
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetByte(out byte kind))
            {
                throw new JsonException("A semantic committed effect has no valid kind.");
            }

            SemanticCommittedEffect effect = (SemanticCommittedEffectKind)kind switch
            {
                SemanticCommittedEffectKind.FieldAssertionAdded => new SemanticFieldAssertionAdded(
                    ReadField(ref reader, subjects, terms, values, provenance)),
                SemanticCommittedEffectKind.FieldAssertionRetracted => new SemanticFieldAssertionRetracted(
                    ReadField(ref reader, subjects, terms, values, provenance)),
                SemanticCommittedEffectKind.RelationshipAssertionAdded =>
                    new SemanticRelationshipAssertionAdded(
                        ReadRelationship(ref reader, subjects, terms, provenance)),
                SemanticCommittedEffectKind.RelationshipAssertionRetracted =>
                    new SemanticRelationshipAssertionRetracted(
                        ReadRelationship(ref reader, subjects, terms, provenance)),
                SemanticCommittedEffectKind.ConceptMembershipChanged =>
                    new SemanticConceptMembershipChanged(subjects[NextInt(ref reader)],
                        NextBoolean(ref reader), NextBoolean(ref reader)),
                SemanticCommittedEffectKind.FieldEvidenceChanged =>
                    new SemanticFieldEvidenceChanged(subjects[NextInt(ref reader)], terms[NextInt(ref reader)],
                        NextInt(ref reader), NextInt(ref reader)),
                SemanticCommittedEffectKind.RelationshipEvidenceChanged =>
                    new SemanticRelationshipEvidenceChanged(subjects[NextInt(ref reader)], terms[NextInt(ref reader)],
                        NextInt(ref reader), NextInt(ref reader)),
                SemanticCommittedEffectKind.CatalogModelRegistered =>
                    new SemanticCatalogModelRegistered(Next<SemanticModelReference>(ref reader, options)),
                SemanticCommittedEffectKind.CatalogTermRegistered =>
                    new SemanticCatalogTermRegistered(terms[NextInt(ref reader)]),
                SemanticCommittedEffectKind.CatalogIdentitySchemeRegistered =>
                    new SemanticCatalogIdentitySchemeRegistered(Next<SemanticIdentityScheme>(ref reader, options)),
                SemanticCommittedEffectKind.GovernanceRulePublished =>
                    new SemanticGovernanceRulePublished(NextNullable<SemanticGovernanceRule>(ref reader, options),
                        Next<SemanticGovernanceRule>(ref reader, options)),
                SemanticCommittedEffectKind.ConformanceIssueRecorded =>
                    new SemanticConformanceIssueRecorded(NextGuid(ref reader),
                        Next<SemanticValidationIssue>(ref reader, options)),
                _ => throw new JsonException($"Semantic committed-effect kind {kind} is unsupported.")
            };
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
            {
                throw new JsonException("A semantic committed effect has trailing content.");
            }

            return effect;
        }

        private static void WriteEffect(Utf8JsonWriter writer, SemanticCommittedEffect value,
            CodecTables tables, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue((byte)value.Kind);
            switch (value)
            {
                case SemanticFieldAssertionAdded effect:
                    WriteField(writer, effect.Assertion, tables);
                    break;
                case SemanticFieldAssertionRetracted effect:
                    WriteField(writer, effect.Assertion, tables);
                    break;
                case SemanticRelationshipAssertionAdded effect:
                    WriteRelationship(writer, effect.Assertion, tables);
                    break;
                case SemanticRelationshipAssertionRetracted effect:
                    WriteRelationship(writer, effect.Assertion, tables);
                    break;
                case SemanticConceptMembershipChanged effect:
                    writer.WriteNumberValue(tables.SubjectIndex[effect.Subject]);
                    writer.WriteBooleanValue(effect.WasMember);
                    writer.WriteBooleanValue(effect.IsMember);
                    break;
                case SemanticFieldEvidenceChanged effect:
                    WriteEvidenceChanged(writer, effect.Subject, effect.Term,
                        effect.BeforeCount, effect.AfterCount, tables);
                    break;
                case SemanticRelationshipEvidenceChanged effect:
                    WriteEvidenceChanged(writer, effect.Subject, effect.Relationship,
                        effect.BeforeCount, effect.AfterCount, tables);
                    break;
                case SemanticCatalogModelRegistered effect:
                    JsonSerializer.Serialize(writer, effect.Model, options);
                    break;
                case SemanticCatalogTermRegistered effect:
                    writer.WriteNumberValue(tables.TermIndex[effect.Term]);
                    break;
                case SemanticCatalogIdentitySchemeRegistered effect:
                    JsonSerializer.Serialize(writer, effect.Scheme, options);
                    break;
                case SemanticGovernanceRulePublished effect:
                    JsonSerializer.Serialize(writer, effect.Before, options);
                    JsonSerializer.Serialize(writer, effect.After, options);
                    break;
                case SemanticConformanceIssueRecorded effect:
                    writer.WriteStringValue(effect.SessionId);
                    JsonSerializer.Serialize(writer, effect.Issue, options);
                    break;
                default:
                    throw new JsonException($"Semantic committed-effect type {value.GetType().Name} is unsupported.");
            }
            writer.WriteEndArray();
        }

        private static void WriteField(Utf8JsonWriter writer, SemanticFieldEvidence value, CodecTables tables)
        {
            writer.WriteStringValue(value.AssertionId);
            writer.WriteNumberValue(tables.SubjectIndex[value.Subject]);
            writer.WriteNumberValue(tables.TermIndex[value.Field]);
            writer.WriteNumberValue(tables.ValueIndex[value.Value]);
            writer.WriteNumberValue(tables.ProvenanceIndex[value.Provenance]);
            writer.WriteNumberValue(value.BeginVersion.Value);
            WriteEndVersion(writer, value.EndVersion);
        }

        private static void WriteRelationship(Utf8JsonWriter writer, SemanticRelationshipEvidence value,
            CodecTables tables)
        {
            writer.WriteStringValue(value.AssertionId);
            writer.WriteNumberValue(tables.SubjectIndex[value.Subject]);
            writer.WriteNumberValue(tables.TermIndex[value.Relationship]);
            writer.WriteNumberValue(tables.SubjectIndex[value.Target]);
            writer.WriteNumberValue(tables.ProvenanceIndex[value.Provenance]);
            writer.WriteNumberValue(value.BeginVersion.Value);
            WriteEndVersion(writer, value.EndVersion);
        }

        private static SemanticFieldEvidence ReadField(ref Utf8JsonReader reader,
            IReadOnlyList<SemanticEntityIdentity> subjects, IReadOnlyList<SemanticTerm> terms,
            IReadOnlyList<SemanticValue> values, IReadOnlyList<SemanticProvenance> provenance) =>
            new(NextGuid(ref reader), subjects[NextInt(ref reader)], terms[NextInt(ref reader)],
                values[NextInt(ref reader)], provenance[NextInt(ref reader)],
                new(NextLong(ref reader)), NextVersion(ref reader));

        private static SemanticRelationshipEvidence ReadRelationship(ref Utf8JsonReader reader,
            IReadOnlyList<SemanticEntityIdentity> subjects, IReadOnlyList<SemanticTerm> terms,
            IReadOnlyList<SemanticProvenance> provenance) =>
            new(NextGuid(ref reader), subjects[NextInt(ref reader)], terms[NextInt(ref reader)],
                subjects[NextInt(ref reader)], provenance[NextInt(ref reader)],
                new(NextLong(ref reader)), NextVersion(ref reader));

        private static void WriteEvidenceChanged(Utf8JsonWriter writer, SemanticEntityIdentity subject,
            SemanticTerm term, int before, int after, CodecTables tables)
        {
            writer.WriteNumberValue(tables.SubjectIndex[subject]);
            writer.WriteNumberValue(tables.TermIndex[term]);
            writer.WriteNumberValue(before);
            writer.WriteNumberValue(after);
        }

        private static void WriteEndVersion(Utf8JsonWriter writer, DatabaseVersion? value)
        {
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteNumberValue(value.Value.Value);
            }
        }

        private static DatabaseVersion? NextVersion(ref Utf8JsonReader reader)
        {
            if (!reader.Read())
            {
                throw new JsonException("A semantic version is missing.");
            }

            return reader.TokenType == JsonTokenType.Null ? null : new(reader.GetInt64());
        }

        private static int NextInt(ref Utf8JsonReader reader) =>
            reader.Read() && reader.TokenType == JsonTokenType.Number ? reader.GetInt32() :
                throw new JsonException("A semantic integer is missing.");

        private static long NextLong(ref Utf8JsonReader reader) =>
            reader.Read() && reader.TokenType == JsonTokenType.Number ? reader.GetInt64() :
                throw new JsonException("A semantic integer is missing.");

        private static bool NextBoolean(ref Utf8JsonReader reader) =>
            reader.Read() && reader.TokenType is JsonTokenType.True or JsonTokenType.False ? reader.GetBoolean() :
                throw new JsonException("A semantic Boolean is missing.");

        private static Guid NextGuid(ref Utf8JsonReader reader) =>
            reader.Read() && reader.TokenType == JsonTokenType.String && reader.TryGetGuid(out Guid value) ? value :
                throw new JsonException("A semantic assertion identity is missing.");

        private static T Next<T>(ref Utf8JsonReader reader, JsonSerializerOptions options) =>
            reader.Read() ? JsonSerializer.Deserialize<T>(ref reader, options) ??
                throw new JsonException("A semantic payload is empty.") : throw new JsonException("A semantic payload is missing.");

        private static T? NextNullable<T>(ref Utf8JsonReader reader, JsonSerializerOptions options) where T : class
        {
            if (!reader.Read())
            {
                throw new JsonException("A semantic payload is missing.");
            }

            return JsonSerializer.Deserialize<T>(ref reader, options);
        }

        private static void WriteTable<T>(Utf8JsonWriter writer, string name, IReadOnlyList<T> values,
            JsonSerializerOptions options)
        {
            writer.WritePropertyName(name);
            writer.WriteStartArray();
            foreach (T value in values)
            {
                JsonSerializer.Serialize(writer, value, options);
            }

            writer.WriteEndArray();
        }

        private static List<T> ReadTable<T>(ref Utf8JsonReader reader, string name,
            JsonSerializerOptions options)
        {
            ReadProperty(ref reader, name);
            Require(ref reader, JsonTokenType.StartArray, $"Semantic table '{name}' is invalid.");
            var result = new List<T>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                result.Add(JsonSerializer.Deserialize<T>(ref reader, options) ??
                    throw new JsonException($"Semantic table '{name}' contains an empty item."));
            }

            return result;
        }

        private static void ReadProperty(ref Utf8JsonReader reader, string name)
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.PropertyName || reader.GetString() != name ||
                !reader.Read())
            {
                throw new JsonException($"Semantic property '{name}' is missing.");
            }
        }

        private static void Require(ref Utf8JsonReader reader, JsonTokenType expected, string message)
        {
            if (reader.TokenType != expected)
            {
                throw new JsonException(message);
            }
        }

        private sealed class CodecTables
        {
            public List<SemanticEntityIdentity> Subjects { get; } = [];

            public List<SemanticTerm> Terms { get; } = [];

            public List<SemanticValue> Values { get; } = [];

            public List<SemanticProvenance> Provenance { get; } = [];

            public Dictionary<SemanticEntityIdentity, int> SubjectIndex { get; } = [];

            public Dictionary<SemanticTerm, int> TermIndex { get; } = [];

            public Dictionary<SemanticValue, int> ValueIndex { get; } = [];

            public Dictionary<SemanticProvenance, int> ProvenanceIndex { get; } = [];

            public static CodecTables Create(IReadOnlyList<SemanticCommittedEffect> effects)
            {
                var tables = new CodecTables();
                foreach (SemanticCommittedEffect effect in effects)
                {
                    tables.Add(effect);
                }

                return tables;
            }

            private void Add(SemanticCommittedEffect effect)
            {
                switch (effect)
                {
                    case SemanticFieldAssertionAdded item:
                        Add(item.Assertion);
                        break;
                    case SemanticFieldAssertionRetracted item:
                        Add(item.Assertion);
                        break;
                    case SemanticRelationshipAssertionAdded item:
                        Add(item.Assertion);
                        break;
                    case SemanticRelationshipAssertionRetracted item:
                        Add(item.Assertion);
                        break;
                    case SemanticConceptMembershipChanged item:
                        Add(Subjects, SubjectIndex, item.Subject);
                        break;
                    case SemanticFieldEvidenceChanged item:
                        Add(Subjects, SubjectIndex, item.Subject);
                        Add(Terms, TermIndex, item.Term);
                        break;
                    case SemanticRelationshipEvidenceChanged item:
                        Add(Subjects, SubjectIndex, item.Subject);
                        Add(Terms, TermIndex, item.Relationship);
                        break;
                    case SemanticCatalogTermRegistered item:
                        Add(Terms, TermIndex, item.Term);
                        break;
                }
            }

            private void Add(SemanticFieldEvidence item)
            {
                Add(Subjects, SubjectIndex, item.Subject);
                Add(Terms, TermIndex, item.Field);
                Add(Values, ValueIndex, item.Value);
                Add(Provenance, ProvenanceIndex, item.Provenance);
            }

            private void Add(SemanticRelationshipEvidence item)
            {
                Add(Subjects, SubjectIndex, item.Subject);
                Add(Subjects, SubjectIndex, item.Target);
                Add(Terms, TermIndex, item.Relationship);
                Add(Provenance, ProvenanceIndex, item.Provenance);
            }

            private static void Add<T>(List<T> values, Dictionary<T, int> indices, T value) where T : notnull
            {
                if (!indices.ContainsKey(value))
                {
                    indices.Add(value, values.Count);
                    values.Add(value);
                }
            }
        }
    }

    private sealed class SemanticProvenanceJsonConverter : JsonConverter<SemanticProvenance>
    {
        public override SemanticProvenance Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            WireProvenance wire = JsonSerializer.Deserialize<WireProvenance>(ref reader, options) ??
                throw new JsonException("Semantic provenance is empty.");
            return new(wire.SourceSystem, wire.SourceRecord, wire.ObservedAt, wire.TargetVocabulary,
                wire.Mappings, wire.ValidFrom, wire.ValidTo, wire.CorrelationId, wire.SourceEntity,
                wire.TransformActivity, wire.ResponsibleAgent, wire.TransformTemplate, wire.GeneratedAt,
                wire.SourceVocabulary, wire.TargetBinding);
        }

        public override void Write(Utf8JsonWriter writer, SemanticProvenance value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString(nameof(WireProvenance.SourceSystem), value.SourceSystem.AbsoluteUri);
            writer.WriteString(nameof(WireProvenance.SourceRecord), value.SourceRecord);
            writer.WriteString(nameof(WireProvenance.ObservedAt), value.ObservedAt);
            writer.WritePropertyName(nameof(WireProvenance.TargetVocabulary));
            JsonSerializer.Serialize(writer, value.TargetVocabulary, options);
            writer.WritePropertyName(nameof(WireProvenance.Mappings));
            JsonSerializer.Serialize(writer, value.Mappings, options);
            WriteNullable(writer, nameof(WireProvenance.ValidFrom), value.ValidFrom);
            WriteNullable(writer, nameof(WireProvenance.ValidTo), value.ValidTo);
            WriteNullable(writer, nameof(WireProvenance.CorrelationId), value.CorrelationId);
            WriteNullable(writer, nameof(WireProvenance.SourceEntity), value.SourceEntity, options);
            WriteNullable(writer, nameof(WireProvenance.TransformActivity), value.TransformActivity, options);
            WriteNullable(writer, nameof(WireProvenance.ResponsibleAgent), value.ResponsibleAgent, options);
            WriteNullable(writer, nameof(WireProvenance.TransformTemplate), value.TransformTemplate, options);
            writer.WriteString(nameof(WireProvenance.GeneratedAt), value.GeneratedAt);
            WriteNullable(writer, nameof(WireProvenance.SourceVocabulary), value.SourceVocabulary, options);
            WriteNullable(writer, nameof(WireProvenance.TargetBinding), value.TargetBinding, options);
            writer.WriteEndObject();
        }

        private static void WriteNullable(Utf8JsonWriter writer, string name, DateTimeOffset? value)
        {
            if (value is null)
            {
                writer.WriteNull(name);
            }
            else
            {
                writer.WriteString(name, value.Value);
            }
        }

        private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
        {
            if (value is null)
            {
                writer.WriteNull(name);
            }
            else
            {
                writer.WriteString(name, value);
            }
        }

        private static void WriteNullable<T>(Utf8JsonWriter writer, string name, T? value,
            JsonSerializerOptions options) where T : class
        {
            writer.WritePropertyName(name);
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                JsonSerializer.Serialize(writer, value, options);
            }
        }

        private sealed record WireProvenance(
            Uri SourceSystem,
            string SourceRecord,
            DateTimeOffset ObservedAt,
            SemanticModelReference TargetVocabulary,
            IReadOnlyList<SemanticModelReference> Mappings,
            DateTimeOffset? ValidFrom,
            DateTimeOffset? ValidTo,
            string? CorrelationId,
            SemanticProvenanceEntity? SourceEntity,
            SemanticProvenanceActivity? TransformActivity,
            SemanticProvenanceAgent? ResponsibleAgent,
            SemanticArtifactReference? TransformTemplate,
            DateTimeOffset GeneratedAt,
            SemanticModelReference? SourceVocabulary,
            SemanticArtifactReference? TargetBinding);
    }

}
