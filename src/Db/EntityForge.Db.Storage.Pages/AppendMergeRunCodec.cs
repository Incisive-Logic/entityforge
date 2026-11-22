using System.Buffers.Binary;

namespace EntityForge.Db.Storage.Pages;

internal sealed class AppendMergeRunCodec<TKey, TValue> where TKey : notnull
{
    private const byte EntryRecordKind = 51;
    private const int HeaderLength = sizeof(byte) * 2 + sizeof(long) + sizeof(int) * 2;
    private readonly IOrderedIndexCodec<TKey> _keys;
    private readonly IOrderedIndexCodec<TValue> _values;

    public AppendMergeRunCodec(IOrderedIndexCodec<TKey> keys, IOrderedIndexCodec<TValue> values)
    {
        _keys = keys;
        _values = values;
    }

    public byte[] EncodeKey(TKey key) => _keys.Encode(key);

    public TKey DecodeKey(ReadOnlySpan<byte> key) => _keys.Decode(key);

    public byte[] Encode(AppendMergeRunEntry<TKey, TValue> entry)
    {
        byte[] key = _keys.Encode(entry.Key);
        return Encode(entry, key);
    }

    public byte[] Encode(AppendMergeRunEntry<TKey, TValue> entry, ReadOnlySpan<byte> key)
    {
        byte[] value = _values.Encode(entry.Value);
        byte[] result = new byte[EncodedLength(key.Length, value.Length)];
        Encode(entry, key, value, result);
        return result;
    }

    public byte[] EncodeValue(TValue value) => _values.Encode(value);

    public static int EncodedLength(int keyLength, int valueLength) =>
        checked(HeaderLength + keyLength + valueLength);

    public void Encode(AppendMergeRunEntry<TKey, TValue> entry, ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> value, Span<byte> destination)
    {
        if (destination.Length != EncodedLength(key.Length, value.Length))
        {
            throw new ArgumentException("The append/merge destination has an invalid length.", nameof(destination));
        }

        destination[0] = EntryRecordKind;
        destination[1] = (byte)entry.Kind;
        BinaryPrimitives.WriteInt64LittleEndian(destination[(sizeof(byte) * 2)..], entry.Version);
        BinaryPrimitives.WriteInt32LittleEndian(destination[(sizeof(byte) * 2 + sizeof(long))..], key.Length);
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[(sizeof(byte) * 2 + sizeof(long) + sizeof(int))..], value.Length);
        key.CopyTo(destination[HeaderLength..]);
        value.CopyTo(destination[(HeaderLength + key.Length)..]);
    }

    public AppendMergeRunEntry<TKey, TValue> Decode(ReadOnlySpan<byte> record)
    {
        ReadHeader(record, out AppendMergeMutationKind kind, out long version,
            out int keyLength, out int valueLength);
        return new(kind, version, _keys.Decode(record.Slice(HeaderLength, keyLength)),
            _values.Decode(record.Slice(HeaderLength + keyLength, valueLength)));
    }

    public bool SupportsValueValidation => _values is IValidatingOrderedIndexCodec<TValue>;

    public TKey DecodeBoundaryKey(ReadOnlySpan<byte> record)
    {
        if (!SupportsValueValidation)
        {
            return Decode(record).Key;
        }
        // Boundary reads need only the key, but must still validate the full record.
        _ = DecodeForLookup(record, static _ => false, out TKey key);
        return key;
    }

    public AppendMergeRunEntry<TKey, TValue>? DecodeForLookup(ReadOnlySpan<byte> record,
        Func<TKey, bool> include, out TKey key)
    {
        if (_values is not IValidatingOrderedIndexCodec<TValue> validator)
        {
            throw new InvalidOperationException("Filtered decoding requires explicit value validation.");
        }

        ReadHeader(record, out AppendMergeMutationKind kind, out long version,
            out int keyLength, out int valueLength);
        key = _keys.Decode(record.Slice(HeaderLength, keyLength));
        ReadOnlySpan<byte> value = record.Slice(HeaderLength + keyLength, valueLength);
        if (!include(key))
        {
            validator.Validate(value);
            return null;
        }
        return new(kind, version, key, _values.Decode(value));
    }

    private static void ReadHeader(ReadOnlySpan<byte> record, out AppendMergeMutationKind kind,
        out long version, out int keyLength, out int valueLength)
    {
        if (record.Length < HeaderLength || record[0] != EntryRecordKind ||
            !Enum.IsDefined((AppendMergeMutationKind)record[1]))
        {
            throw Corrupt("Append/merge entry is invalid.");
        }

        version = BinaryPrimitives.ReadInt64LittleEndian(record[(sizeof(byte) * 2)..]);
        keyLength = BinaryPrimitives.ReadInt32LittleEndian(record[(sizeof(byte) * 2 + sizeof(long))..]);
        valueLength = BinaryPrimitives.ReadInt32LittleEndian(
            record[(sizeof(byte) * 2 + sizeof(long) + sizeof(int))..]);
        if (version <= 0 || keyLength < 0 || valueLength < 0 ||
            record.Length != HeaderLength + (long)keyLength + valueLength)
        {
            throw Corrupt("Append/merge entry bounds are invalid.");
        }

        kind = (AppendMergeMutationKind)record[1];
    }

    private static PhysicalPageCorruptionException Corrupt(string message) => new(message);
}

internal readonly record struct AppendMergeRunEntry<TKey, TValue>(AppendMergeMutationKind Kind,
    long Version, TKey Key, TValue Value) where TKey : notnull;
