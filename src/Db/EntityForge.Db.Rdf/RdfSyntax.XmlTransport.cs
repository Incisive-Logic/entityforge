using System.Text;

namespace EntityForge.Db.Rdf;

public static partial class RdfSyntax
{
    internal static string? DecodeLoadedXmlTransport(Stream stream, string? charset)
    {
        // Callers supply bounded in-memory documents. RFC 7303 gives the BOM
        // precedence over the HTTP charset, then leaves XML to its own rules.
        long start = stream.Position;
        Span<byte> prefix = stackalloc byte[4];
        int length = stream.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
        prefix = prefix[..length];
        Encoding? encoding = null;
        int bom = 0;
        if (prefix.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
        {
            encoding = new UTF32Encoding(true, false, true);
            bom = 4;
        }
        else if (prefix.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
        {
            encoding = new UTF32Encoding(false, false, true);
            bom = 4;
        }
        else if (prefix.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            encoding = new UTF8Encoding(false, true);
            bom = 3;
        }
        else if (prefix.StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            encoding = new UnicodeEncoding(true, false, true);
            bom = 2;
        }
        else if (prefix.StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            encoding = new UnicodeEncoding(false, false, true);
            bom = 2;
        }
        else if (charset is not null)
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
            catch (Exception failure) when (failure is ArgumentException or NotSupportedException)
            {
                throw new InvalidDataException("The XML response charset is unsupported.", failure);
            }
        }
        stream.Position = start + bom;
        if (encoding is null)
        {
            // XmlReader consumes the original bytes and honors the declaration.
            return null;
        }
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return reader.ReadToEnd();
    }
}
