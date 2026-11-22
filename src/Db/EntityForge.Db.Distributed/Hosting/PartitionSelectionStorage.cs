using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace EntityForge.Db.Distributed.Hosting;

// Storage encoding only: admission charges use the complete uncompressed
// selection record. No evidence or recovery state is omitted.
internal static class PartitionSelectionStorage
{
    private static ReadOnlySpan<byte> Magic => "EFSC0001"u8;

    private const int LengthOffset = 8;
    private const int DigestOffset = LengthOffset + sizeof(int);
    private const int HeaderLength = DigestOffset + SHA256.HashSizeInBytes;
    private const int CompressionQuality = 4;
    private const int CompressionWindow = 20;
    private const int Limit = GrpcEtcdPartitionTransactionAuthority.MaximumEncodedPlanBytes;

    internal static byte[] Encode(ReadOnlySpan<byte> original)
    {
        if (original.Length == 0 || original.Length > Limit)
        {
            throw new InvalidDataException("Selection exceeds its expanded storage bound.");
        }

        byte[] buffer = new byte[HeaderLength + BrotliEncoder.GetMaxCompressedLength(original.Length)];
        if (!BrotliEncoder.TryCompress(original, buffer.AsSpan(HeaderLength), out int written,
            CompressionQuality, CompressionWindow))
        {
            throw new InvalidDataException("Selection compression failed.");
        }

        if (HeaderLength + written >= original.Length)
        {
            return original.ToArray();
        }

        Magic.CopyTo(buffer);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(LengthOffset), original.Length);
        // Authenticate both the allocation length and compressed bytes before decoding.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(buffer.AsSpan(0, DigestOffset));
        hash.AppendData(buffer.AsSpan(HeaderLength, written));
        hash.GetHashAndReset().CopyTo(buffer, DigestOffset);
        return buffer.AsSpan(0, HeaderLength + written).ToArray();
    }

    internal static byte[] Decode(ReadOnlySpan<byte> stored)
    {
        if (stored.Length == 0 || stored.Length > Limit)
        {
            throw new InvalidDataException("Selection exceeds its encoded storage bound.");
        }
        // Binary records carry their own checksum even when compression is not
        // beneficial. JSON is retained only for legacy storage codec callers;
        // the format-7 authority requires binary selection records after decode.
        if (stored[0] == (byte)'{' || PartitionSelectionCodec.IsBinary(stored))
        {
            return stored.ToArray();
        }

        if (stored.Length <= HeaderLength || !stored.StartsWith(Magic))
        {
            throw new InvalidDataException("Unknown selection storage format.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(stored[LengthOffset..]);
        if (length <= 0 || length > Limit)
        {
            throw new InvalidDataException("Invalid expanded selection length.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(stored[..DigestOffset]);
        hash.AppendData(stored[HeaderLength..]);
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), stored.Slice(DigestOffset, SHA256.HashSizeInBytes)))
        {
            throw new InvalidDataException("Selection storage checksum differs.");
        }

        byte[] result = new byte[length];
        using var decoder = new BrotliDecoder();
        if (decoder.Decompress(stored[HeaderLength..], result, out int consumed, out int written) != OperationStatus.Done ||
            consumed != stored.Length - HeaderLength || written != length)
        {
            throw new InvalidDataException("Selection storage content or length differs.");
        }

        return result;
    }
}
