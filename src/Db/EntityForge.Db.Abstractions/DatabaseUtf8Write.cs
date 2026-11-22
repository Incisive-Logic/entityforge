using System.Text;

namespace EntityForge.Db;

// Caller validates byte count and writes its existing framing before entering.
// No complete encoded copy or retained buffer is needed for a synchronous write.
internal static class DatabaseUtf8Write
{
    private const int BufferBytes = 1024;
    private const int MaximumBytesPerChar = 3;

    internal static void Write(BinaryWriter writer, string value, Encoding encoding)
    {
        Span<byte> buffer = stackalloc byte[BufferBytes];
        if (value.Length <= BufferBytes / MaximumBytesPerChar)
        {
            int written = encoding.GetBytes(value.AsSpan(), buffer);
            writer.Write(buffer[..written]);
            return;
        }
        var encoder = encoding.GetEncoder();
        ReadOnlySpan<char> remaining = value.AsSpan();
        do
        {
            encoder.Convert(remaining, buffer, true, out int used, out int written, out bool complete);
            writer.Write(buffer[..written]);
            remaining = remaining[used..];
            if (complete)
            {
                return;
            }
        }
        while (true);
    }
}
