using System.Globalization;
using System.Text.RegularExpressions;

namespace EntityForge.Db.Gql;

// Compiler-local bounded cache. Do not change the process-wide Regex.CacheSize:
// application code shares it, and parameter-derived patterns must not grow forever.
internal static class GqlRegex
{
    private const int Capacity = 256;
    private const int MaximumPatternCharacters = 128 * 1024;
    private const int MaximumCachedCharacters = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Dictionary<(string Pattern, RegexOptions Options, string Culture), Regex> Cache = [];
    private static readonly Queue<(string Pattern, RegexOptions Options, string Culture)> Order = [];
    private static int _patternCharacters;

    private static Regex Get(string pattern, RegexOptions options)
    {
        var key = (pattern, options, (options & RegexOptions.CultureInvariant) != 0 ? "" : CultureInfo.CurrentCulture.Name);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var existing))
            {
                return existing;
            }
            var regex = new Regex(pattern, options);
            // The fixed Unicode identifier expression alone exceeds 14,000
            // characters. Bound total retained text as well as entry count;
            // a small per-pattern cutoff defeats reuse of the common grammar.
            if (pattern.Length > MaximumPatternCharacters)
            {
                return regex;
            }
            while (Cache.Count >= Capacity || _patternCharacters + pattern.Length > MaximumCachedCharacters)
            {
                var evicted = Order.Dequeue();
                Cache.Remove(evicted);
                _patternCharacters -= evicted.Pattern.Length;
            }
            Cache.Add(key, regex);
            Order.Enqueue(key);
            _patternCharacters += pattern.Length;
            return regex;
        }
    }

    internal static Match Match(string input, string pattern, RegexOptions options = RegexOptions.None) => Get(pattern, options).Match(input);

    internal static MatchCollection Matches(string input, string pattern, RegexOptions options = RegexOptions.None) => Get(pattern, options).Matches(input);

    internal static bool IsMatch(string input, string pattern, RegexOptions options = RegexOptions.None) => Get(pattern, options).IsMatch(input);

    internal static string Replace(string input, string pattern, string replacement, RegexOptions options = RegexOptions.None) => Get(pattern, options).Replace(input, replacement);

    internal static string Replace(string input, string pattern, MatchEvaluator replacement, RegexOptions options = RegexOptions.None) => Get(pattern, options).Replace(input, replacement);
}
