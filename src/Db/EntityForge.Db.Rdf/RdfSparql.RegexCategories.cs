using System.Globalization;
using System.Collections.Concurrent;
using System.Text;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static readonly Lazy<IReadOnlyDictionary<char, string>> RegexScalarCategories = new(BuildRegexScalarCategories);
    private static readonly ConcurrentDictionary<(string Category, bool Negated), Lazy<string>> RegexUnicodeCategories = new();
    // UnicodeCategory enum order, including the categories with no valid scalar
    // members (surrogates). Names are validated before entering the bounded cache.
    private static readonly string[] RegexCategoryNames = EntityForge.Db.Query.Planning.UnicodeText.CategoryNames;

    private static bool TryPrepareRegexCategory(string source, int start, out int end, out string pattern)
    {
        end = start;
        pattern = String.Empty;
        if (start + 3 >= source.Length || source[start] != '\\' || source[start + 1] is not ('p' or 'P') || source[start + 2] != '{')
        {
            return false;
        }
        int closing = source.IndexOf('}', start + 3);
        if (closing < 0)
        {
            return false;
        }
        string name = source[(start + 3)..closing];
        if (name.StartsWith("Is", StringComparison.Ordinal))
        {
            if (!RegexUnicodeBlocks.Value.TryGetValue(name, out string? block))
            {
                throw new ArgumentException("The pattern names an unknown Unicode block.");
            }
            pattern = "(?-i:" + (source[start + 1] == 'P' ? "(?!" + block + ")" + RegexScalar : block) + ")";
            end = closing;
            return true;
        }
        if (name.Length is < 1 or > 2 || !RegexCategoryNames.Any(category => category.StartsWith(name, StringComparison.Ordinal)))
        {
            return false;
        }
        bool negated = source[start + 1] == 'P';
        pattern = "(?-i:" + RegexUnicodeCategories.GetOrAdd((name, negated), key => new Lazy<string>(() =>
            BuildRegexScalarPattern(scalar => RegexCategoryNames[UnicodeCaseData.Value.Categories[scalar]]
                .StartsWith(key.Category, StringComparison.Ordinal) != key.Negated))).Value + ")";
        end = closing;
        return true;
    }

    private static IReadOnlyDictionary<char, string> BuildRegexScalarCategories()
    {
        // XPath character escapes describe Unicode scalars, whereas .NET regex
        // categories inspect UTF-16 code units. Compile supplementary characters
        // into paired-surrogate alternatives without introducing capture groups.
        var result = new Dictionary<char, string>();
        foreach (char escape in "wWdD")
        {
            bool Included(int scalar)
            {
                var category = (UnicodeCategory)UnicodeCaseData.Value.Categories[scalar];
                bool member = Char.ToLowerInvariant(escape) == 'd'
                    ? category == UnicodeCategory.DecimalDigitNumber
                    : category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
                        UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or
                        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or
                        UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber or
                        UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol;
                return Char.IsLower(escape) == member;
            }
            result.Add(escape, BuildRegexScalarPattern(Included));
        }
        return result;
    }

    private static string BuildRegexScalarPattern(Func<int, bool> included)
    {
        string Class(int first, int last, Func<int, bool> predicate)
        {
            var ranges = new StringBuilder();
            for (int value = first; value <= last; value++)
            {
                if (!predicate(value))
                {
                    continue;
                }
                int start = value;
                while (value < last && predicate(value + 1))
                {
                    value++;
                }
                ranges.Append("\\u").Append(start.ToString("X4", CultureInfo.InvariantCulture));
                if (value != start)
                {
                    ranges.Append("-\\u").Append(value.ToString("X4", CultureInfo.InvariantCulture));
                }
            }
            return ranges.Length == 0 ? String.Empty : "[" + ranges + "]";
        }
        var alternatives = new List<string>();
        string bmp = Class(0, 0xFFFF, scalar => scalar is not (>= 0xD800 and <= 0xDFFF) && included(scalar));
        if (bmp.Length != 0)
        {
            alternatives.Add(bmp);
        }
        var pairs = Enumerable.Range(0xD800, 1024).Select(high => new
        {
            High = high,
            Low = Class(0xDC00, 0xDFFF, low => included(0x10000 + ((high - 0xD800) << 10) + low - 0xDC00))
        }).Where(pair => pair.Low.Length != 0).GroupBy(pair => pair.Low, StringComparer.Ordinal);
        foreach (var group in pairs)
        {
            var highs = group.Select(pair => pair.High).ToHashSet();
            alternatives.Add(Class(0xD800, 0xDBFF, highs.Contains) + group.Key);
        }
        return alternatives.Count == 0 ? "(?!)" : "(?:" + String.Join("|", alternatives) + ")";
    }
}
