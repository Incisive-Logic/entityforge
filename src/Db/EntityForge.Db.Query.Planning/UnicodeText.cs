using System.Globalization;
using System.Text;

namespace EntityForge.Db.Query.Planning;

internal static partial class UnicodeText
{
    internal static readonly string[] CategoryNames =
        "Lu Ll Lt Lm Lo Mn Mc Me Nd Nl No Zs Zl Zp Cc Cf Cs Co Pc Pd Ps Pe Pi Pf Po Sm Sc Sk So Cn".Split(' ');

    internal sealed record CaseData(Dictionary<int, string> Upper, Dictionary<int, string> Lower,
        HashSet<int> Cased, HashSet<int> Ignorable, byte[] Categories);

    internal static readonly Lazy<CaseData> Data = new(ReadCaseData);

    internal static string ChangeCase(string text, bool upper, Func<Exception> workLimit)
    {
        const int maximum = 1_048_576;
        if (text.Length > maximum)
        {
            throw workLimit();
        }
        var data = Data.Value;
        var runes = text.EnumerateRunes().ToArray();
        var followedByCased = new bool[runes.Length];
        bool following = false;
        if (!upper)
        {
            // Compute context once, avoiding quadratic scans across combining marks.
            for (int i = runes.Length - 1; i >= 0; i--)
            {
                followedByCased[i] = following;
                if (!data.Ignorable.Contains(runes[i].Value))
                {
                    following = data.Cased.Contains(runes[i].Value);
                }
            }
        }
        var mappings = upper ? data.Upper : data.Lower;
        var result = new StringBuilder(text.Length);
        bool preceding = false;
        for (int i = 0; i < runes.Length; i++)
        {
            int codepoint = runes[i].Value;
            // Final_Sigma is the only language-independent conditional mapping
            // in Unicode 16. Locale tailoring is deliberately separate from default case mapping.
            string mapped = !upper && codepoint == 0x03A3 && preceding && !followedByCased[i]
                ? "\u03C2" : mappings.GetValueOrDefault(codepoint) ?? runes[i].ToString();
            if (result.Length + mapped.Length > maximum)
            {
                throw workLimit();
            }
            result.Append(mapped);
            if (!data.Ignorable.Contains(codepoint))
            {
                preceding = data.Cased.Contains(codepoint);
            }
        }
        return result.ToString();
    }

    private static CaseData ReadCaseData()
    {
        static IEnumerable<string[]> Rows(string name)
        {
            using var stream = typeof(UnicodeText).Assembly.GetManifestResourceStream(
                "EntityForge.Db.Query.Planning.Unicode." + name + "-16.0.0.txt")
                ?? throw new InvalidOperationException("Missing Unicode case data: " + name);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                string content = line.Split('#', 2)[0].Trim();
                if (content.Length != 0)
                {
                    yield return content.Split(';').Select(field => field.Trim()).ToArray();
                }
            }
        }
        static int Hex(string value) => Int32.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        static string Mapping(string value) => String.Concat(value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(codepoint => new Rune(Hex(codepoint)).ToString()));
        var upper = new Dictionary<int, string>();
        var lower = new Dictionary<int, string>();
        var categories = new byte[0x110000];
        Array.Fill(categories, (byte)UnicodeCategory.OtherNotAssigned);
        int rangeStart = -1;
        foreach (var row in Rows("UnicodeData"))
        {
            int codepoint = Hex(row[0]);
            byte category = checked((byte)Array.IndexOf(CategoryNames, row[2]));
            if (row[1].EndsWith(", First>", StringComparison.Ordinal))
            {
                rangeStart = codepoint;
            }
            if (row[1].EndsWith(", Last>", StringComparison.Ordinal))
            {
                if (rangeStart < 0)
                {
                    throw new InvalidDataException("Unicode category range has no start.");
                }
                Array.Fill(categories, category, rangeStart, codepoint - rangeStart + 1);
                rangeStart = -1;
            }
            else
            {
                categories[codepoint] = category;
            }
            if (row[12].Length != 0)
            {
                upper.Add(codepoint, Mapping(row[12]));
            }
            if (row[13].Length != 0)
            {
                lower.Add(codepoint, Mapping(row[13]));
            }
        }
        foreach (var row in Rows("SpecialCasing"))
        {
            if (row[4].Length != 0)
            {
                continue;
            }
            int codepoint = Hex(row[0]);
            lower[codepoint] = Mapping(row[1]);
            upper[codepoint] = Mapping(row[3]);
        }
        var cased = new HashSet<int>();
        var ignorable = new HashSet<int>();
        foreach (var row in Rows("DerivedCoreProperties"))
        {
            if (row[1] is not ("Cased" or "Case_Ignorable"))
            {
                continue;
            }
            var target = row[1] == "Cased" ? cased : ignorable;
            string[] range = row[0].Split("..", StringSplitOptions.None);
            int first = Hex(range[0]), last = Hex(range[^1]);
            for (int codepoint = first; codepoint <= last; codepoint++)
            {
                target.Add(codepoint);
            }
        }
        return new(upper, lower, cased, ignorable, categories);
    }
}
