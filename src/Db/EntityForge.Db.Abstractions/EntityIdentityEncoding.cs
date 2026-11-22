using System.Security.Cryptography;
using System.Text;

namespace EntityForge.Db.Mapping;

// Versioned, domain-separated identity material shared by mapped profile clients.
// Length prefixes distinguish empty fields, embedded separators and field boundaries.
internal static class EntityIdentityEncoding
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(string domain, params string[] fields)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(domain);
        writer.Write(fields.Length);
        foreach (string field in fields)
        {
            writer.Write(field);
        }

        writer.Flush();
        return stream.ToArray();
    }

    // Source-record identities are string-valued domain fields. Preserve their full
    // content without JSON escaping or an expanding hex/base64 wrapper.
    internal static string Text(string domain, params string[] fields)
    {
        var builder = new StringBuilder();
        void Field(string value)
        {
            _ = Utf8.GetByteCount(value); // Reject malformed UTF-16 consistently with binary identities.
            builder.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(value);
        }
        Field(domain);
        foreach (string field in fields)
        {
            Field(field);
        }

        return builder.ToString();
    }

    internal static byte[] Digest(string domain, params string[] fields) => SHA256.HashData(Encode(domain, fields));

    internal static string Hash(string domain, params string[] fields) =>
        Convert.ToHexString(Digest(domain, fields)).ToLowerInvariant();
}
