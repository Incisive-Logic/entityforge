using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EntityForge.Db.Storage.Pages;

internal sealed record AppendMergeKeyFilter(int HashCount, int BitCount, ReadOnlyMemory<byte> Bits)
{
    private const int BitsPerByte = 8;

    public bool MightContain(ReadOnlySpan<byte> key)
    {
        (ulong first, ulong second) = Hashes(key);
        ReadOnlySpan<byte> bits = Bits.Span;
        for (int index = 0; index < HashCount; index++)
        {
            int bit = (int)(unchecked(first + (ulong)index * second) % (ulong)BitCount);
            if ((bits[bit / BitsPerByte] & (1 << (bit % BitsPerByte))) == 0)
            {
                return false;
            }
        }
        return true;
    }

    public static AppendMergeKeyFilter Build(IEnumerable<ReadOnlyMemory<byte>> keys,
        int entryCount, AppendMergeOptions options)
    {
        Builder builder = CreateBuilder(entryCount, options);
        foreach (ReadOnlyMemory<byte> key in keys)
        {
            builder.Add(key.Span);
        }

        return builder.Build();
    }

    public static Builder CreateBuilder(int entryCount, AppendMergeOptions options) =>
        new(entryCount, options);

    internal sealed class Builder
    {
        private readonly int _hashCount;
        private readonly int _bitCount;
        private readonly byte[] _bits;

        public Builder(int entryCount, AppendMergeOptions options)
        {
            _hashCount = options.FilterHashCount;
            _bitCount = Math.Max(BitsPerByte, Math.Min(
                checked(options.MaximumFilterBytes * BitsPerByte),
                checked(entryCount * options.FilterBitsPerKey)));
            _bits = new byte[(_bitCount + BitsPerByte - 1) / BitsPerByte];
        }

        public void Add(ReadOnlySpan<byte> key)
        {
            (ulong first, ulong second) = Hashes(key);
            for (int index = 0; index < _hashCount; index++)
            {
                int bit = (int)(unchecked(first + (ulong)index * second) % (ulong)_bitCount);
                _bits[bit / BitsPerByte] |= (byte)(1 << (bit % BitsPerByte));
            }
        }

        public AppendMergeKeyFilter Build() => new(_hashCount, _bitCount, _bits);
    }

    private static (ulong First, ulong Second) Hashes(ReadOnlySpan<byte> key)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(key, hash);
        ulong first = BinaryPrimitives.ReadUInt64LittleEndian(hash);
        ulong second = BinaryPrimitives.ReadUInt64LittleEndian(hash[sizeof(ulong)..]) | 1UL;
        return (first, second);
    }
}
