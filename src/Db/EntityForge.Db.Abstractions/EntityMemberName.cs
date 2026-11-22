using System.Globalization;
using System.Text;

namespace EntityForge.Db.Mapping;

// Names are literal ordinal keys, never paths or expressions. Reject rather than rewrite text.
internal static class EntityMemberName
{
    private const int MaximumScalars = 256;

    internal static void Validate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        int count = 0;
        for (int index = 0; index < name.Length;)
        {
            if (!Rune.TryGetRuneAt(name, index, out Rune rune))
            {
                throw new ArgumentException("Member names require valid Unicode text.", nameof(name));
            }

            if (++count > MaximumScalars || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or
                UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                throw new ArgumentException("Member names permit at most 256 Unicode characters and no control or formatting characters.", nameof(name));
            }

            index += rune.Utf16SequenceLength;
        }
        if (!name.IsNormalized(NormalizationForm.FormC))
        {
            throw new ArgumentException("Member names must already use Unicode NFC normalization.", nameof(name));
        }
    }
}
