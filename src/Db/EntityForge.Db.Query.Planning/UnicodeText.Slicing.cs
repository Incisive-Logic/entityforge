using System.Text;

namespace EntityForge.Db.Query.Planning;

internal static partial class UnicodeText
{
    internal static int CountScalars(string text)
    {
        int count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            count++;
        }
        return count;
    }

    // Indices are zero-based Unicode scalar positions. Callers retain their
    // language-specific index, numeric-range, null and error semantics.
    internal static string SliceScalars(string text, int start, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
        {
            return String.Empty;
        }
        int index = 0, offset = 0, begin = -1, end = 0;
        bool malformed = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (index == start)
            {
                begin = offset;
            }
            if (index >= start)
            {
                if (index - start == count)
                {
                    break;
                }
                malformed |= rune.Utf16SequenceLength == 1 && Char.IsSurrogate(text[offset]);
                end = offset + rune.Utf16SequenceLength;
            }
            offset += rune.Utf16SequenceLength;
            index++;
        }
        if (begin < 0)
        {
            return String.Empty;
        }
        if (!malformed)
        {
            return text.Substring(begin, end - begin);
        }
        // Preserve EnumerateRunes' replacement behavior for malformed UTF-16
        // supplied through a programmatic value API.
        var result = new StringBuilder(end - begin);
        Span<char> encoded = stackalloc char[2];
        foreach (var rune in text.AsSpan(begin, end - begin).EnumerateRunes())
        {
            int written = rune.EncodeToUtf16(encoded);
            result.Append(encoded[..written]);
        }
        return result.ToString();
    }
}
