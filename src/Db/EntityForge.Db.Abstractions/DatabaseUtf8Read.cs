using System.Text;

namespace EntityForge.Db;

// Callers validate their framing and length bounds before reading. Direct decoding
// is only for an accessible memory segment; returned strings own their characters.
internal static class DatabaseUtf8Read
{
    internal static string Read(BinaryReader reader, int length, Encoding encoding)
    {
        if (reader.BaseStream is MemoryStream memory && memory.TryGetBuffer(out var buffer))
        {
            // Check the exposed slice, not the capacity of its backing array.
            if (length < 0 || length > memory.Length - memory.Position)
            {
                throw new InvalidDataException("Invalid UTF-8 field length.");
            }

            int offset = checked(buffer.Offset + (int)memory.Position);
            memory.Position += length;
            return encoding.GetString(buffer.Array!.AsSpan(offset, length));
        }
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new InvalidDataException("Truncated UTF-8 field.");
        }

        return encoding.GetString(bytes);
    }
}
