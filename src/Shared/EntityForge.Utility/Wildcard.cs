using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Provides case-insensitive wildcard matching for file and resource selectors.</summary>
    public static class Wildcard
    {
        /// <summary>The asterisk wildcard token.</summary>
        public const string WildcardChar = "*";
        private const string WildcardRegex = "[.]*";

        private static Regex ToRegex(string wildcardPattern)
        {
            ArgumentException.ThrowIfNullOrEmpty(wildcardPattern, nameof(wildcardPattern));

            return new Regex(Regex.Escape(wildcardPattern).Replace("\\*", ".*").Replace("\\?", "."), RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        /// <summary>Checks whether the supplied pattern contains an asterisk.</summary>
        public static bool HasWildcard(string wildcardPattern)
        {
            return !string.IsNullOrEmpty(WildcardRegex) && wildcardPattern.Contains(WildcardChar);
        }

        /// <summary>Tests a case-insensitive wildcard match; an empty pattern or a single asterisk matches every input.</summary>
        public static bool Match(string wildcardPattern, string match)
        {
            if (string.IsNullOrEmpty(wildcardPattern))
            {
                return true;
            }

            if (string.Equals(WildcardChar, wildcardPattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return WildcardMatch.EqualsWildcard(match, wildcardPattern, true);
        }
    }
}
