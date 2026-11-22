using System.Buffers;
using System.Text;

namespace EntityForge.Db.Hosting;

internal static partial class SparqlProtocolEndpoint
{
    private static readonly UTF8Encoding StrictProtocolUtf8 = new(false, true);

    // Validate before the framework decoder can preserve malformed escapes or
    // replace invalid UTF-8. Never reinterpret literal percent signs after decoding.
    private static void ValidateProtocolParameters(string? encoded)
    {
        if (encoded is null || !encoded.Contains('%'))
        {
            return;
        }
        byte[] bytes = ArrayPool<byte>.Shared.Rent(Math.Max(1, encoded.Length / 3));
        try
        {
            int position = 0;
            while ((position = encoded.IndexOf('%', position)) >= 0)
            {
                int count = 0;
                do
                {
                    if (position + 2 >= encoded.Length || !Uri.IsHexDigit(encoded[position + 1]) || !Uri.IsHexDigit(encoded[position + 2]))
                    {
                        throw new ArgumentException("SPARQL parameters contain an invalid percent escape.");
                    }
                    bytes[count++] = (byte)((Uri.FromHex(encoded[position + 1]) << 4) | Uri.FromHex(encoded[position + 2]));
                    position += 3;
                } while (position < encoded.Length && encoded[position] == '%');
                _ = StrictProtocolUtf8.GetCharCount(bytes.AsSpan(0, count));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
        }
    }
}
