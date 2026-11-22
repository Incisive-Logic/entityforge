using System.Runtime.InteropServices;
using System.Text;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Durability;

/// <summary>Identifies semantic committed-change encodings used by change-feed consumers.</summary>
public static class SemanticCommittedChangeCodecs
{
    /// <summary>The codec identifier for a semantic dataset-reset notification.</summary>
    public const string DatasetReset = "entityforge.semantic.dataset-reset";
    /// <summary>The dataset-reset payload codec version.</summary>
    public const int DatasetResetVersion = 1;
}

// Tables retain sharing across effects without JSON conversion or compression on the commit path.
internal static partial class SemanticCommittedEffectsCodec
{
    public const string CodecId = "entityforge.semantic.effects";
    public const int CodecVersion = 10;
    private const int MaximumPayloadBytes = 512 * 1024 * 1024;
    private const int MaximumItems = 10_000_000;
    private const int GuidBytes = 16;

    private static ReadOnlySpan<byte> Magic => "EFSE0010"u8;

    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static bool SupportsVersion(int version) => version is CodecVersion or LegacySemanticCommittedEffectsCodec.CodecVersion;

    public static ReadOnlyMemory<byte> Encode(SemanticCommittedEffects effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        if (effects.Effects.Count > MaximumItems)
        {
            throw new InvalidDataException("Too many Semantic effects.");
        }

        var tables = CodecTables.Create(effects.Effects);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(Magic);
        WriteTable(writer, tables.Subjects, SemanticValueBinaryCodec.Identity);
        WriteTable(writer, tables.Terms, SemanticValueBinaryCodec.Term);
        WriteTable(writer, tables.Values, SemanticValueBinaryCodec.Write);
        WriteTable(writer, tables.Provenance, (w, p) =>
        {
            long lengthPosition = w.BaseStream.Position;
            w.Write(0);
            SemanticProvenanceBinaryCodec.Write(w, p);
            long end = w.BaseStream.Position;
            int length = checked((int)(end - lengthPosition - sizeof(int)));
            if (length > SemanticValueBinaryCodec.MaximumValueBytes)
            {
                throw new InvalidDataException("Semantic provenance exceeds its bound.");
            }

            w.BaseStream.Position = lengthPosition;
            w.Write(length);
            w.BaseStream.Position = end;
        });
        writer.Write(effects.Effects.Count);
        foreach (var effect in effects.Effects)
        {
            writer.Write((byte)effect.Kind);
            switch (effect)
            {
                case SemanticFieldAssertionAdded e:
                    Field(e.Assertion);
                    break;
                case SemanticFieldAssertionRetracted e:
                    Field(e.Assertion);
                    break;
                case SemanticRelationshipAssertionAdded e:
                    Relationship(e.Assertion);
                    break;
                case SemanticRelationshipAssertionRetracted e:
                    Relationship(e.Assertion);
                    break;
                case SemanticConceptMembershipChanged e:
                    writer.Write(tables.SubjectIndex[e.Subject]);
                    writer.Write(e.WasMember);
                    writer.Write(e.IsMember);
                    break;
                case SemanticFieldEvidenceChanged e:
                    Counts(e.Subject, e.Term, e.BeforeCount, e.AfterCount);
                    break;
                case SemanticRelationshipEvidenceChanged e:
                    Counts(e.Subject, e.Relationship, e.BeforeCount, e.AfterCount);
                    break;
                case SemanticCatalogModelRegistered e:
                    Model(writer, e.Model);
                    break;
                case SemanticCatalogTermRegistered e:
                    writer.Write(tables.TermIndex[e.Term]);
                    break;
                case SemanticCatalogIdentitySchemeRegistered e:
                    Scheme(writer, e.Scheme);
                    break;
                case SemanticGovernanceRulePublished e:
                    Optional(writer, e.Before, Rule);
                    Rule(writer, e.After);
                    break;
                case SemanticConformanceIssueRecorded e:
                    Id(writer, e.SessionId);
                    Issue(writer, e.Issue);
                    break;
                default:
                    throw new InvalidDataException("Unknown Semantic committed effect.");
            }
            CheckSize(stream);
        }
        writer.Flush();
        return stream.GetBuffer().AsMemory(0, checked((int)stream.Length));

        void Field(SemanticFieldEvidence e)
        {
            Id(writer, e.AssertionId);
            writer.Write(tables.SubjectIndex[e.Subject]);
            writer.Write(tables.TermIndex[e.Field]);
            writer.Write(tables.ValueIndex[e.Value]);
            writer.Write(tables.ProvenanceIndex[e.Provenance]);
            Versions(e.BeginVersion, e.EndVersion);
        }
        void Relationship(SemanticRelationshipEvidence e)
        {
            Id(writer, e.AssertionId);
            writer.Write(tables.SubjectIndex[e.Subject]);
            writer.Write(tables.TermIndex[e.Relationship]);
            writer.Write(tables.SubjectIndex[e.Target]);
            writer.Write(tables.ProvenanceIndex[e.Provenance]);
            Versions(e.BeginVersion, e.EndVersion);
        }
        void Versions(DatabaseVersion begin, DatabaseVersion? end)
        {
            writer.Write(begin.Value);
            writer.Write(end.HasValue);
            if (end.HasValue)
            {
                writer.Write(end.Value.Value);
            }
        }
        void Counts(SemanticEntityIdentity subject, SemanticTerm term, int before, int after)
        {
            writer.Write(tables.SubjectIndex[subject]);
            writer.Write(tables.TermIndex[term]);
            writer.Write(before);
            writer.Write(after);
        }
    }

    public static SemanticCommittedEffects Decode(ReadOnlyMemory<byte> payload, int version = CodecVersion)
    {
        if (version == LegacySemanticCommittedEffectsCodec.CodecVersion)
        {
            return LegacySemanticCommittedEffectsCodec.Decode(payload);
        }

        if (version != CodecVersion || payload.Length > MaximumPayloadBytes || !payload.Span.StartsWith(Magic))
        {
            throw new InvalidDataException("Unknown or oversized Semantic committed effects.");
        }

        using var stream = MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, false)
            : new MemoryStream(payload.ToArray(), false);
        using var reader = new BinaryReader(stream, Utf8);
        try
        {
            stream.Position = Magic.Length;
            var subjects = Table(reader, SemanticValueBinaryCodec.Identity);
            var terms = Table(reader, SemanticValueBinaryCodec.Term);
            var values = Table(reader, SemanticValueBinaryCodec.Read);
            var provenance = Table(reader, r => SemanticProvenanceBinaryCodec.Decode(Exact(r, r.ReadInt32())));
            var effects = new SemanticCommittedEffect[Count(reader)];
            for (int i = 0; i < effects.Length; i++)
            {
                effects[i] = (SemanticCommittedEffectKind)reader.ReadByte() switch
                {
                    SemanticCommittedEffectKind.FieldAssertionAdded => new SemanticFieldAssertionAdded(Field()),
                    SemanticCommittedEffectKind.FieldAssertionRetracted => new SemanticFieldAssertionRetracted(Field()),
                    SemanticCommittedEffectKind.RelationshipAssertionAdded => new SemanticRelationshipAssertionAdded(Relationship()),
                    SemanticCommittedEffectKind.RelationshipAssertionRetracted => new SemanticRelationshipAssertionRetracted(Relationship()),
                    SemanticCommittedEffectKind.ConceptMembershipChanged => new SemanticConceptMembershipChanged(At(subjects), Flag(reader), Flag(reader)),
                    SemanticCommittedEffectKind.FieldEvidenceChanged => new SemanticFieldEvidenceChanged(At(subjects), At(terms), reader.ReadInt32(), reader.ReadInt32()),
                    SemanticCommittedEffectKind.RelationshipEvidenceChanged => new SemanticRelationshipEvidenceChanged(At(subjects), At(terms), reader.ReadInt32(), reader.ReadInt32()),
                    SemanticCommittedEffectKind.CatalogModelRegistered => new SemanticCatalogModelRegistered(Model(reader)),
                    SemanticCommittedEffectKind.CatalogTermRegistered => new SemanticCatalogTermRegistered(At(terms)),
                    SemanticCommittedEffectKind.CatalogIdentitySchemeRegistered => new SemanticCatalogIdentitySchemeRegistered(Scheme(reader)),
                    SemanticCommittedEffectKind.GovernanceRulePublished => new SemanticGovernanceRulePublished(Optional(reader, Rule), Rule(reader)),
                    SemanticCommittedEffectKind.ConformanceIssueRecorded => new SemanticConformanceIssueRecorded(Id(reader), Issue(reader)),
                    _ => throw new InvalidDataException("Unknown Semantic committed-effect tag.")
                };
            }

            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("Trailing Semantic committed effects.");
            }

            return new(effects);

            T At<T>(T[] table)
            {
                int index = reader.ReadInt32();
                if ((uint)index >= (uint)table.Length)
                {
                    throw new InvalidDataException("Invalid Semantic effect table reference.");
                }

                return table[index];
            }
            DatabaseVersion? End() => Flag(reader) ? new(reader.ReadInt64()) : null;
            SemanticFieldEvidence Field() => new(Id(reader), At(subjects), At(terms), At(values), At(provenance), new(reader.ReadInt64()), End());
            SemanticRelationshipEvidence Relationship() => new(Id(reader), At(subjects), At(terms), At(subjects), At(provenance), new(reader.ReadInt64()), End());
        }
        catch (Exception e) when (e is EndOfStreamException or ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Invalid Semantic committed effects.", e);
        }
    }

    public static SemanticCommittedEffects Decode(ReadOnlySpan<byte> payload) => Decode((ReadOnlyMemory<byte>)payload.ToArray());

    private static void CheckSize(Stream stream)
    {
        if (stream.Length > MaximumPayloadBytes)
        {
            throw new InvalidDataException("Semantic effects exceed their byte bound.");
        }
    }

    private static void WriteTable<T>(BinaryWriter writer, IReadOnlyList<T> items, Action<BinaryWriter, T> write)
    {
        if (items.Count > MaximumItems)
        {
            throw new InvalidDataException("Too many Semantic effect table entries.");
        }

        writer.Write(items.Count);
        foreach (T item in items)
        {
            write(writer, item);
            CheckSize(writer.BaseStream);
        }
    }

    private static T[] Table<T>(BinaryReader reader, Func<BinaryReader, T> read)
    {
        var items = new T[Count(reader)];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = read(reader);
        }

        return items;
    }

    private static int Count(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > MaximumItems || count > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("Invalid Semantic effect collection length.");
        }

        return count;
    }

    private static byte[] Exact(BinaryReader reader, int length)
    {
        if (length < 0 || length > SemanticValueBinaryCodec.MaximumValueBytes || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("Invalid Semantic effect field length.");
        }

        return reader.ReadBytes(length);
    }

    private static bool Flag(BinaryReader r) => SemanticValueBinaryCodec.Flag(r);

    private static void Text(BinaryWriter w, string value) => SemanticValueBinaryCodec.Text(w, value);

    private static string Text(BinaryReader r) => SemanticValueBinaryCodec.Text(r);

    private static void Id(BinaryWriter w, Guid id)
    {
        Span<byte> bytes = stackalloc byte[GuidBytes];
        id.TryWriteBytes(bytes);
        w.Write(bytes);
    }

    private static Guid Id(BinaryReader r) => new(Exact(r, GuidBytes));

    private static void Optional<T>(BinaryWriter w, T? value, Action<BinaryWriter, T> write) where T : class
    {
        w.Write(value is not null);
        if (value is not null)
        {
            write(w, value);
        }
    }

    private static T? Optional<T>(BinaryReader r, Func<BinaryReader, T> read) where T : class => Flag(r) ? read(r) : null;

    private static T EnumValue<T>(BinaryReader r) where T : struct, Enum
    {
        T value = (T)Enum.ToObject(typeof(T), r.ReadByte());
        return Enum.IsDefined(value) ? value : throw new InvalidDataException("Unknown Semantic enumeration.");
    }

    private static void Model(BinaryWriter w, SemanticModelReference m)
    {
        Text(w, m.Identifier.OriginalString);
        Text(w, m.Version);
        w.Write((byte)m.Kind);
    }

    private static SemanticModelReference Model(BinaryReader r) => new(new Uri(Text(r), UriKind.Absolute), Text(r), EnumValue<SemanticModelKind>(r));

    private static void Scheme(BinaryWriter w, SemanticIdentityScheme s)
    {
        Text(w, s.Identifier.OriginalString);
        Text(w, s.Name);
        w.Write((byte)s.ValueKind);
        Optional(w, s.Description, Text);
    }

    private static SemanticIdentityScheme Scheme(BinaryReader r) => new(new Uri(Text(r), UriKind.Absolute), Text(r), EnumValue<SemanticValueKind>(r), Optional(r, Text));

    private static void Rule(BinaryWriter w, SemanticGovernanceRule rule)
    {
        Text(w, rule.Identifier.OriginalString);
        w.Write((byte)rule.Kind);
        Text(w, rule.DefinitionReference.OriginalString);
        Text(w, rule.Revision);
        w.Write(rule.Enabled);
        w.Write(rule.CatalogVersion.Value);
        Optional(w, rule.Scope, (writer, scope) =>
        {
            writer.Write((byte)scope.Enforcement);
            WriteTable(writer, scope.ApplicableConcepts, SemanticValueBinaryCodec.Term);
        });
    }

    private static SemanticGovernanceRule Rule(BinaryReader r) => new(new Uri(Text(r), UriKind.Absolute), EnumValue<SemanticGovernanceRuleKind>(r),
        new Uri(Text(r), UriKind.Absolute), Text(r), Flag(r), new(r.ReadInt64()), Optional(r, reader =>
        {
            var enforcement = EnumValue<SemanticGovernanceEnforcement>(reader);
            return new SemanticGovernanceScope(Table(reader, SemanticValueBinaryCodec.Term), enforcement);
        }));

    private static void Issue(BinaryWriter w, SemanticValidationIssue issue)
    {
        Text(w, issue.Path);
        Text(w, issue.Message);
        Text(w, issue.Code);
        w.Write((byte)issue.Severity);
        Optional(w, issue.Subject, SemanticValueBinaryCodec.Identity);
        Optional(w, issue.Term, SemanticValueBinaryCodec.Term);
        Optional(w, issue.RejectedValue, SemanticValueBinaryCodec.Write);
        Optional(w, issue.Constraint, (writer, uri) => Text(writer, uri.OriginalString));
    }

    private static SemanticValidationIssue Issue(BinaryReader r) => new(Text(r), Text(r), Text(r), EnumValue<SemanticValidationSeverity>(r),
        Optional(r, SemanticValueBinaryCodec.Identity), Optional(r, SemanticValueBinaryCodec.Term), Optional(r, SemanticValueBinaryCodec.Read),
        Optional(r, reader => new Uri(Text(reader), UriKind.Absolute)));
}
