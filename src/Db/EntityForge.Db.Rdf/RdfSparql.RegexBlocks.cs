using System.Globalization;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> RegexUnicodeBlocks = new(() =>
    {
        using var stream = typeof(RdfSparql).Assembly.GetManifestResourceStream("EntityForge.Db.Rdf.Unicode.Blocks-16.0.0.txt")
            ?? throw new InvalidOperationException("The Unicode block table is missing.");
        using var reader = new StreamReader(stream);
        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            string data = line.Split('#', 2)[0].Trim();
            if (data.Length == 0)
            {
                continue;
            }
            string[] fields = data.Split(';', StringSplitOptions.TrimEntries);
            string[] range = fields[0].Split("..");
            int first = Int32.Parse(range[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int last = Int32.Parse(range[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            // XPath block names remove spaces, retaining case and hyphens.
            // Surrogate blocks contain no Unicode scalar values.
            blocks.Add("Is" + fields[1].Replace(" ", String.Empty, StringComparison.Ordinal),
                first >= 0xD800 && last <= 0xDFFF ? "(?!)" : RegexScalarRange(first, last));
        }
        // Names from the Unicode 3.1 block table used by XML Schema 1.0.
        blocks.Add("IsGreek", blocks["IsGreekandCoptic"]);
        blocks.Add("IsCombiningMarksforSymbols", blocks["IsCombiningDiacriticalMarksforSymbols"]);
        blocks.Add("IsPrivateUse", "(?:" + blocks["IsPrivateUseArea"] + "|" +
            blocks["IsSupplementaryPrivateUseArea-A"] + "|" + blocks["IsSupplementaryPrivateUseArea-B"] + ")");
        return blocks;
    });
}
