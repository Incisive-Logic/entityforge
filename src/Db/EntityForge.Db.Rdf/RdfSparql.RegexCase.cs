using System.Text;
using System.Text.RegularExpressions;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static readonly Lazy<IReadOnlyDictionary<int, int[]>> RegexCaseVariants = new(() =>
    {
        var upperGroups = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var lowerGroups = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var mappings = new Dictionary<int, (string Upper, string Lower)>();
        var data = UnicodeCaseData.Value;
        // XPath compares full upper/lower results for each single character.
        // Those results may contain several characters (for example, the two
        // st ligatures both uppercase to ST). The regex atom remains one scalar.
        var candidates = data.Upper.Keys.Concat(data.Lower.Keys).Concat(
            data.Upper.Values.Concat(data.Lower.Values).SelectMany(value => value.EnumerateRunes().Select(rune => rune.Value)));
        foreach (int scalar in candidates.Distinct())
        {
            if (!Rune.IsValid(scalar))
            {
                continue;
            }
            string identity = new Rune(scalar).ToString();
            string upper = data.Upper.GetValueOrDefault(scalar, identity), lower = data.Lower.GetValueOrDefault(scalar, identity);
            mappings.Add(scalar, (upper, lower));
            if (!upperGroups.TryGetValue(upper, out var upperGroup))
            {
                upperGroups.Add(upper, upperGroup = []);
            }
            upperGroup.Add(scalar);
            if (!lowerGroups.TryGetValue(lower, out var lowerGroup))
            {
                lowerGroups.Add(lower, lowerGroup = []);
            }
            lowerGroup.Add(scalar);
        }
        // XPath compares equal upper OR equal lower mappings, rather than the
        // regex engine's platform-specific case-insensitive character table.
        return mappings.ToDictionary(pair => pair.Key, pair => upperGroups[pair.Value.Upper]
            .Concat(lowerGroups[pair.Value.Lower]).Distinct().Order().ToArray());
    });

    private static string RegexCaseLiteral(int scalar, bool ignoreCase)
    {
        int[] variants = ignoreCase && RegexCaseVariants.Value.TryGetValue(scalar, out var mapped) ? mapped : [scalar];
        return "(?-i:" + String.Join("|", variants.Select(value => Regex.Escape(new Rune(value).ToString()))) + ")";
    }

    private static string RegexCaseRange(int first, int last, bool ignoreCase)
    {
        string range = RegexScalarRange(first, last);
        if (!ignoreCase)
        {
            return range;
        }
        var extra = RegexCaseVariants.Value.Where(pair => pair.Key >= first && pair.Key <= last)
            .SelectMany(pair => pair.Value).Where(value => value < first || value > last).Distinct().Order().ToArray();
        if (extra.Length == 0)
        {
            return "(?-i:" + range + ")";
        }
        var alternatives = new List<string> { range };
        for (int index = 0; index < extra.Length; index++)
        {
            int start = extra[index];
            while (index + 1 < extra.Length && extra[index + 1] == extra[index] + 1)
            {
                index++;
            }
            alternatives.Add(RegexScalarRange(start, extra[index]));
        }
        return "(?-i:" + String.Join("|", alternatives) + ")";
    }

}
