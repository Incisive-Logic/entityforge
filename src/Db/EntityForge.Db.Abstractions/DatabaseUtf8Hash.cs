using System.Security.Cryptography;
using System.Text;

namespace EntityForge.Db;

// Hashes UTF8 text, including Encoding.UTF8 replacement fallback.
// Scratch space is bounded; no value or digest cache retains database contents.
internal static class DatabaseUtf8Hash
{
    private const int BufferBytes = 1024;
    private const int MaximumBytesPerChar = 3;

    internal static byte[] Hash(string value)
    {
        byte[] digest = new byte[SHA256.HashSizeInBytes];
        Hash(value, digest);
        return digest;
    }

    internal static byte[] Hash(string value, HashAlgorithmName algorithm)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (algorithm == HashAlgorithmName.SHA256)
        {
            return Hash(value);
        }
        using var hash = IncrementalHash.CreateHash(algorithm);
        Span<byte> buffer = stackalloc byte[BufferBytes];
        Append(hash, Encoding.UTF8.GetEncoder(), value.AsSpan(), buffer, true);
        return hash.GetHashAndReset();
    }

    internal static void Hash(string value, Span<byte> digest)
    {
        ArgumentNullException.ThrowIfNull(value);
        Span<byte> buffer = stackalloc byte[BufferBytes];
        if (value.Length <= BufferBytes / MaximumBytesPerChar)
        {
            int written = Encoding.UTF8.GetBytes(value.AsSpan(), buffer);
            SHA256.HashData(buffer[..written], digest);
            return;
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Encoding.UTF8.GetEncoder(), value.AsSpan(), buffer, true);
        hash.GetHashAndReset(digest);
    }

    internal static byte[] HashJoined(IEnumerable<string> values, string separator)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(separator);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var encoder = Encoding.UTF8.GetEncoder();
        Span<byte> buffer = stackalloc byte[BufferBytes];
        bool first = true;
        foreach (var value in values)
        {
            if (!first)
            {
                Append(hash, encoder, separator.AsSpan(), buffer, false);
            }

            Append(hash, encoder, value.AsSpan(), buffer, false);
            first = false;
        }
        Append(hash, encoder, ReadOnlySpan<char>.Empty, buffer, true);
        return hash.GetHashAndReset();
    }

    private static void Append(IncrementalHash hash, Encoder encoder, ReadOnlySpan<char> text,
        Span<byte> buffer, bool flush)
    {
        do
        {
            encoder.Convert(text, buffer, flush, out int used, out int written, out bool complete);
            hash.AppendData(buffer[..written]);
            text = text[used..];
            if (complete)
            {
                return;
            }
        }
        while (true);
    }
}
