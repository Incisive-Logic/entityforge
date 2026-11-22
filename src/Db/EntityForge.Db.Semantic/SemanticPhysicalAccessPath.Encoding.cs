using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Diagnostics;
using EntityForge.Db.Storage.Pages;
using EntityForge.Db.Storage;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.AccessPaths;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Kernel;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

internal sealed partial class SemanticPhysicalAccessPath
{
    private SemanticAccessKey IdentityKey(SemanticEntityIdentity identity) =>
        new(SemanticAccessFamily.Identity, string.Empty, AtomKey(identity));

    private SemanticAccessKey ConceptKey(SemanticTerm concept) =>
        new(SemanticAccessFamily.Concept, AtomKey(concept), string.Empty);

    private SemanticAccessKey FieldKey(SemanticTerm field, SemanticValue value) =>
        new(SemanticAccessFamily.FieldEquality, AtomKey(field), AtomKey(value));

    private string OrderedTermKey(SemanticTerm concept, SemanticTerm field)
    {
        EncodingCache? cache = _encodingCache.Value;
        if (cache is null)
        {
            return AtomKey(concept) + AtomKey(field);
        }

        var key = (concept, field);
        if (cache.OrderedTerms.TryGetValue(key, out string? encoded))
        {
            return encoded;
        }

        encoded = AtomKey(concept) + AtomKey(field);
        cache.OrderedTerms.Add(key, encoded);
        return encoded;
    }

    private string AtomKey(SemanticEntityIdentity identity) => CachedAtomKey(identity);

    private string AtomKey(SemanticTerm term) => CachedAtomKey(term);

    private string AtomKey(SemanticValue value) => CachedAtomKey(value);

    private string CachedAtomKey(object value)
    {
        EncodingCache? cache = _encodingCache.Value;
        if (cache is null)
        {
            if (_atomKeyCache.TryGetValue(value, out string? shared))
            {
                return shared;
            }

            shared = GuidKey(Atom(value).Id.Value);
            if (_atomKeyCache.Count + 1 > MaximumCachedAtomKeys)
            {
                _atomKeyCache.Clear();
            }

            _atomKeyCache[value] = shared;
            return shared;
        }
        if (cache.AtomKeys.TryGetValue(value, out string? encoded))
        {
            return encoded;
        }

        encoded = GuidKey(Atom(value).Id.Value);
        cache.AtomKeys.Add(value, encoded);
        return encoded;
    }

    private SemanticAtomRecord Atom(object value)
    {
        Dictionary<object, SemanticAtomRecord>? cache = _encodingCache.Value?.Atoms;
        if (cache is null)
        {
            return EncodeAtom(value);
        }

        if (cache.TryGetValue(value, out SemanticAtomRecord? atom))
        {
            return atom;
        }

        atom = EncodeAtom(value);
        cache.Add(value, atom);
        return atom;
    }

    private SemanticAtomRecord EncodeAtom(object value) => value switch
    {
        SemanticEntityIdentity identity => _atoms.Encode(identity),
        SemanticTerm term => _atoms.Encode(term),
        SemanticValue semanticValue => _atoms.Encode(semanticValue),
        SemanticProvenance provenance => _atoms.Encode(provenance),
        _ => throw new ArgumentException($"Unsupported semantic atom type '{value.GetType().Name}'.", nameof(value))
    };

    private byte[] EncodeIdentity(SemanticEntityIdentity identity) => Atom(identity).CanonicalBytes.ToArray();

    private SemanticEntityIdentity DecodeIdentity(byte[] value) =>
        _atoms.DecodeIdentity(_atomStorage.Create(SemanticAtomKind.Identity, value));

    private static byte[] EncodeAssertion(Guid assertionId) => assertionId.ToByteArray();

    private static Guid DecodeAssertion(byte[] value)
    {
        if (value.Length != GuidByteLength)
        {
            throw new InvalidDataException("Semantic assertion access value is invalid.");
        }

        return new Guid(value);
    }

    private static AppendMergeMutation<SemanticAccessKey, byte[]> ToAppendMergeMutation(
        OrderedPageTreeMutation<SemanticAccessKey, byte[]> mutation) => new(
            mutation.Kind is OrderedPageTreeMutationKind.Add
                ? AppendMergeMutationKind.Add : AppendMergeMutationKind.Remove,
            mutation.Key, mutation.Value);

    private static Task<AppendMergeMutationPlan<TKey, TValue>>? StartPlanning<TKey, TValue>(
        AppendMergeAccessPath<TKey, TValue> access,
        IReadOnlyList<AppendMergeMutation<TKey, TValue>> mutations,
        long databaseVersion, CancellationToken cancellationToken) where TKey : notnull =>
        mutations.Count == 0 ? null : Task.Run(
            () => access.PlanMutations(mutations, databaseVersion, cancellationToken), cancellationToken);

    private static SemanticAccessKey AssertionKey(SemanticAccessFamily family, Guid assertionId) =>
        new(family, assertionId.ToString("N"), string.Empty);

    private static SemanticAccessKey AtomDirectoryKey(SemanticAtomId atomId) =>
        new(SemanticAccessFamily.AtomDirectory, GuidKey(atomId.Value), string.Empty);

    private static SemanticAccessKey AtomPayloadKey(long ordinal, int sequence) =>
        new(SemanticAccessFamily.AtomPayload, ordinal.ToString("D20"), sequence.ToString("D8"));

    private static SemanticAccessKey AtomSequenceKey() =>
        new(SemanticAccessFamily.AtomSequence, string.Empty, string.Empty);

    private static int ComparePosition(string value, string identity, SemanticOrderedEntityPosition after)
    {
        int compared = StringComparer.Ordinal.Compare(value, after.Value);
        return compared != 0 ? compared : StringComparer.Ordinal.Compare(identity, after.Identity);
    }

    private static string EncodeOrderedValue(string value, string identity) =>
        value.Replace("\0", "\0\u0001", StringComparison.Ordinal) + "\0\0" + identity;

    private static (string Value, string Identity) DecodeOrderedValue(string encoded)
    {
        var value = new StringBuilder();
        for (int index = 0; index < encoded.Length - 1; index++)
        {
            if (encoded[index] != '\0')
            {
                value.Append(encoded[index]);
                continue;
            }
            if (encoded[index + 1] == '\0')
            {
                return (value.ToString(), encoded[(index + 2)..]);
            }

            if (encoded[index + 1] == '\u0001')
            {
                value.Append('\0');
                index++;
                continue;
            }
            throw new InvalidDataException("An ordered semantic value is invalid.");
        }
        throw new InvalidDataException("An ordered semantic value is incomplete.");
    }

    private string OrderIdentity(SemanticEntityIdentity identity) => AtomKey(identity);

    private static string GuidKey(Guid value)
    {
        Span<byte> bytes = stackalloc byte[GuidByteLength];
        value.TryWriteBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    private sealed class EncodingCache
    {
        public Dictionary<object, SemanticAtomRecord> Atoms { get; } = new();

        public Dictionary<object, string> AtomKeys { get; } = new();

        public Dictionary<(SemanticTerm Concept, SemanticTerm Field), string> OrderedTerms { get; } = new();
    }

    private static bool Visible(SemanticFieldEvidence item, DatabaseVersion snapshot) =>
        item.BeginVersion.Value <= snapshot.Value &&
        (item.EndVersion is null || snapshot.Value < item.EndVersion.Value.Value);

    private static byte[] EncodeAtomDirectory(long ordinal, int chunkCount)
    {
        byte[] value = new byte[AtomDirectoryValueLength];
        BinaryPrimitives.WriteInt64BigEndian(value, ordinal);
        BinaryPrimitives.WriteInt32BigEndian(value.AsSpan(sizeof(long)), chunkCount);
        return value;
    }

    private static AtomDirectoryEntry DecodeAtomDirectory(byte[] value)
    {
        if (value.Length != AtomDirectoryValueLength)
        {
            throw new InvalidDataException("A semantic atom directory entry is invalid.");
        }

        long ordinal = BinaryPrimitives.ReadInt64BigEndian(value);
        int chunkCount = BinaryPrimitives.ReadInt32BigEndian(value.AsSpan(sizeof(long)));
        if (ordinal < 0 || chunkCount <= 0)
        {
            throw new InvalidDataException("A semantic atom directory entry has invalid bounds.");
        }

        return new(ordinal, chunkCount);
    }

    private static byte[] EncodeAtomSequence(long value)
    {
        byte[] result = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(result, value);
        return result;
    }

    private static long DecodeAtomSequence(byte[] value)
    {
        if (value.Length != sizeof(long))
        {
            throw new InvalidDataException("The semantic atom sequence is invalid.");
        }

        long result = BinaryPrimitives.ReadInt64BigEndian(value);
        return result >= 0 ? result : throw new InvalidDataException("The semantic atom sequence is negative.");
    }

    private sealed class KeyCodec : IOrderedIndexCodec<SemanticAccessKey>
    {
        private const int HeaderLength = 3 + sizeof(int) * 2;
        private const byte Utf8EncodingKind = 0;
        private const byte AtomSequenceEncodingKind = 1;
        private const int AtomTextLength = 32;
        private const int AtomBytesLength = 16;

        public int GetEncodedLength(SemanticAccessKey value) => checked(HeaderLength +
            EncodedComponentLength(value.Term) + EncodedComponentLength(value.Value));

        public byte[] Encode(SemanticAccessKey value)
        {
            byte termKind = EncodingKind(value.Term);
            byte valueKind = EncodingKind(value.Value);
            int termLength = EncodedComponentLength(value.Term, termKind);
            int valueLength = EncodedComponentLength(value.Value, valueKind);
            byte[] result = new byte[HeaderLength + termLength + valueLength];
            result[0] = (byte)value.Family;
            result[1] = termKind;
            result[2] = valueKind;
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(3), termLength);
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(3 + sizeof(int)), valueLength);
            EncodeComponent(value.Term, termKind, result.AsSpan(HeaderLength, termLength));
            EncodeComponent(value.Value, valueKind,
                result.AsSpan(HeaderLength + termLength, valueLength));
            return result;
        }

        public SemanticAccessKey Decode(ReadOnlySpan<byte> value)
        {
            if (value.Length < HeaderLength)
            {
                throw new InvalidDataException("Semantic access key is truncated.");
            }

            int termLength = BinaryPrimitives.ReadInt32BigEndian(value[3..]);
            int valueLength = BinaryPrimitives.ReadInt32BigEndian(value[(3 + sizeof(int))..]);
            int header = HeaderLength;
            if (termLength < 0 || valueLength < 0 || header + termLength + valueLength != value.Length)
            {
                throw new InvalidDataException("Semantic access key bounds are invalid.");
            }

            return new((SemanticAccessFamily)value[0],
                DecodeComponent(value[1], value.Slice(header, termLength)),
                DecodeComponent(value[2], value[(header + termLength)..]));
        }

        private static byte EncodingKind(string value) => IsAtomSequence(value)
            ? AtomSequenceEncodingKind : Utf8EncodingKind;

        private static int EncodedComponentLength(string value) =>
            EncodedComponentLength(value, EncodingKind(value));

        private static int EncodedComponentLength(string value, byte kind) => kind switch
        {
            AtomSequenceEncodingKind => checked(value.Length / AtomTextLength * AtomBytesLength),
            Utf8EncodingKind => Encoding.UTF8.GetByteCount(value),
            _ => throw new InvalidDataException("Semantic access key encoding is invalid.")
        };

        private static void EncodeComponent(string value, byte kind, Span<byte> destination)
        {
            if (kind == Utf8EncodingKind)
            {
                Encoding.UTF8.GetBytes(value.AsSpan(), destination);
                return;
            }
            for (int index = 0, offset = 0; index < value.Length;
                 index += AtomTextLength, offset += AtomBytesLength)
            {
                for (int byteIndex = 0; byteIndex < AtomBytesLength; byteIndex++)
                {
                    destination[offset + byteIndex] = checked((byte)((Hex(value[index + byteIndex * 2]) << 4) |
                        Hex(value[index + byteIndex * 2 + 1])));
                }
            }
        }

        private static string DecodeComponent(byte kind, ReadOnlySpan<byte> value)
        {
            if (kind == Utf8EncodingKind)
            {
                return Encoding.UTF8.GetString(value);
            }

            if (kind != AtomSequenceEncodingKind || value.Length == 0 ||
                value.Length % AtomBytesLength != 0)
            {
                throw new InvalidDataException("Semantic access key encoding is invalid.");
            }

            return Convert.ToHexString(value).ToLowerInvariant();
        }

        private static bool IsAtomSequence(string value)
        {
            if (value.Length == 0 || value.Length % AtomTextLength != 0)
            {
                return false;
            }

            foreach (char character in value)
            {
                if (!char.IsAsciiHexDigit(character))
                {
                    return false;
                }
            }

            return true;
        }

        private static int Hex(char value) => value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => throw new InvalidDataException("Semantic atom key encoding is invalid.")
        };
    }
}
