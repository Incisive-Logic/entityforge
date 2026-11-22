using System.Security.Cryptography;
using Google.Protobuf;
using EntityForge.Semantics;
using System.Text;

namespace EntityForge.Semantics.Protobuf;

/// <summary>Computes version-domain-separated SHA-256 identities for semantic session content and operations.</summary>
public static class SemanticChangeSessionContentHasher
{
    private static readonly byte[] VersionDomain = Encoding.UTF8.GetBytes(
        $"entityforge.semantic.pages.v{SemanticChangeSessionTransportCodec.ContentHashSchemaVersion}\0");

    private static readonly byte[] OperationVersionDomain = Encoding.UTF8.GetBytes(
        $"entityforge.semantic.operation.v{SemanticChangeSessionTransportCodec.OperationHashSchemaVersion}\0");

    /// <summary>Hashes length-framed serialized pages in page-number order and returns a lowercase sha256-prefixed content digest.</summary>
    public static string Compute(IEnumerable<SemanticChangeSessionPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(VersionDomain);
        Span<byte> length = stackalloc byte[5];
        foreach (var page in pages.OrderBy(page => page.PageNumber))
        {
            var payload = SemanticChangeProtobufConverter.ToMessage(page).ToByteArray();
            var lengthBytes = WriteVarint((uint)payload.Length, length);
            hash.AppendData(length[..lengthBytes]);
            hash.AppendData(payload);
        }

        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Hashes the session start and ordered length-framed pages under the operation hash domain.</summary>
    public static string ComputeOperation(SemanticChangeSessionStart start,
        IEnumerable<SemanticChangeSessionPage> pages)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(pages);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(OperationVersionDomain);
        Span<byte> length = stackalloc byte[5];
        Append(hash, SemanticChangeProtobufConverter.ToMessage(start).ToByteArray(), length);
        foreach (SemanticChangeSessionPage page in pages.OrderBy(page => page.PageNumber))
        {
            Append(hash, SemanticChangeProtobufConverter.ToMessage(page).ToByteArray(), length);
        }

        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Computes both existing hash domains from one serialization of each page.</summary>
    public static (string ContentHash, string OperationHash) ComputeBoth(SemanticChangeSessionStart start,
        IEnumerable<SemanticChangeSessionPage> pages)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(pages);
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var operation = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        content.AppendData(VersionDomain);
        operation.AppendData(OperationVersionDomain);
        Span<byte> length = stackalloc byte[5];
        Append(operation, SemanticChangeProtobufConverter.ToMessage(start).ToByteArray(), length);
        foreach (var page in pages.OrderBy(page => page.PageNumber))
        {
            byte[] payload = SemanticChangeProtobufConverter.ToMessage(page).ToByteArray();
            Append(content, payload, length);
            Append(operation, payload, length);
        }
        return ("sha256:" + Convert.ToHexString(content.GetHashAndReset()).ToLowerInvariant(),
            "sha256:" + Convert.ToHexString(operation.GetHashAndReset()).ToLowerInvariant());
    }

    private static void Append(IncrementalHash hash, byte[] payload, Span<byte> length)
    {
        int lengthBytes = WriteVarint((uint)payload.Length, length);
        hash.AppendData(length[..lengthBytes]);
        hash.AppendData(payload);
    }

    private static int WriteVarint(uint value, Span<byte> destination)
    {
        var index = 0;
        while (value >= 0x80)
        {
            destination[index++] = (byte)(value | 0x80);
            value >>= 7;
        }
        destination[index++] = (byte)value;
        return index;
    }
}
