using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EntityForge.Db;

// Domain-separated, length-delimited internal fingerprints. Never hash formatted
// JSON or concatenate variable-length fields without framing.
internal sealed class DatabaseBinaryFingerprint : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private const int TextBufferBytes = 1024;
    private const int GuidBytes = 16;
    private readonly byte[] _text = new byte[TextBufferBytes];
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal long Bytes
    {
        get; private set;
    }

    internal DatabaseBinaryFingerprint(string domain) => Text(domain);

    internal void Long(long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        Append(bytes);
    }

    internal void Guid(Guid value)
    {
        Span<byte> bytes = stackalloc byte[GuidBytes];
        value.TryWriteBytes(bytes);
        Append(bytes);
    }

    internal void Text(string value)
    {
        Long(Utf8.GetByteCount(value));
        var encoder = Utf8.GetEncoder();
        ReadOnlySpan<char> chars = value.AsSpan();
        do
        {
            encoder.Convert(chars, _text, true, out int used, out int written, out bool complete);
            Append(_text.AsSpan(0, written));
            chars = chars[used..];
            if (complete)
            {
                break;
            }
        }
        while (true);
    }

    private void Append(ReadOnlySpan<byte> value)
    {
        Bytes = checked(Bytes + value.Length);
        _hash.AppendData(value);
    }

    internal void BytesValue(ReadOnlySpan<byte> value)
    {
        Long(value.Length);
        Append(value);
    }

    internal string Finish() => Convert.ToHexString(_hash.GetHashAndReset());

    public void Dispose() => _hash.Dispose();
}
