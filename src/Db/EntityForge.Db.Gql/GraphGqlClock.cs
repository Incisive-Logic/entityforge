using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EntityForge.Db.Gql;

internal sealed partial class GraphGqlQueryCompiler
{
    // Fixed syntax patterns belong to the compiler, not the small process-wide
    // Regex cache. Keep the Unicode identifier boundaries and options intact.
    private static readonly Regex StatementClockPattern = new(
        @"(?<!" + GraphGqlIdentifiers.Continuation + @")(?<![.$])(?<name>date|time|datetime|zoned_datetime|zoned_time|localtime|local_time|localdatetime|local_datetime)(?:\.(?:statement|transaction))?\s*\(\s*\)|(?<!" + GraphGqlIdentifiers.Continuation + @")(?<![.$])(?<name>timestamp)\s*\(\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ZonedStatementClockPattern = new(
        @"(?<!" + GraphGqlIdentifiers.Continuation + @")(?<![.$])(?<name>date|time|datetime|zoned_datetime|zoned_time|localtime|local_time|localdatetime|local_datetime)(?:\.(?:statement|transaction))?\s*\(\s*\{\s*timezone\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TimeComponentZonePattern = new(
        @"(?<!" + GraphGqlIdentifiers.Continuation + @")(?<![.$])(?:time|zoned_time)\s*\(\s*\{",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ClockZoneArgumentPattern = new(
        @"(?<!" + GraphGqlIdentifiers.Continuation + @")(?<![.$])(?<name>date|time|datetime|zoned_datetime|zoned_time|localtime|local_time|localdatetime|local_datetime)\.(?:statement|transaction)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly DateTimeOffset? _statementTime;

    internal GraphGqlQueryCompiler(DateTimeOffset? statementTime = null) => _statementTime = statementTime;

    internal static string BindStatementClock(string source, DateTimeOffset instant)
    {
        source = StripComments(source);
        source = NormalizeClockZoneArguments(source);
        source = BindZonedStatementClock(source, instant);
        source = BindTimeComponentZones(source, instant);
        var matches = StatementClockPattern.Matches(MaskQuotedStrings(source));
        if (matches.Count == 0)
        {
            return source;
        }
        var utc = instant.UtcDateTime;
        var result = new StringBuilder();
        int copied = 0;
        foreach (Match match in matches)
        {
            result.Append(source.AsSpan(copied, match.Index - copied));
            string name = match.Groups["name"].Value.ToUpperInvariant();
            result.Append(name switch
            {
                "DATETIME" or "ZONED_DATETIME" => "datetime('" + utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z')",
                "TIME" or "ZONED_TIME" => "time('" + utc.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z')",
                "DATE" => "date('" + utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "')",
                "LOCALTIME" or "LOCAL_TIME" => "localtime('" + utc.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "')",
                "LOCALDATETIME" or "LOCAL_DATETIME" => "localdatetime('" + utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "')",
                _ => instant.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
            });
            copied = match.Index + match.Length;
        }
        return result.Append(source.AsSpan(copied)).ToString();
    }

    private static string BindZonedStatementClock(string source, DateTimeOffset instant, int depth = 0)
    {
        if (depth > 64)
        {
            throw new ArgumentException("Clock expressions exceed 64 nesting levels.");
        }
        string masked = MaskQuotedStrings(source);
        var matches = ZonedStatementClockPattern.Matches(masked);
        var result = new StringBuilder();
        int copied = 0;
        foreach (Match match in matches)
        {
            if (match.Index < copied)
            {
                continue;
            }
            int end = match.Index + match.Length, braces = 1, parentheses = 0, brackets = 0;
            bool multipleFields = false;
            for (; end < masked.Length; end++)
            {
                char c = masked[end];
                if (c == '{')
                {
                    braces++;
                }
                else if (c == '}' && --braces == 0)
                {
                    break;
                }
                else if (c == '(')
                {
                    parentheses++;
                }
                else if (c == ')')
                {
                    parentheses--;
                }
                else if (c == '[')
                {
                    brackets++;
                }
                else if (c == ']')
                {
                    brackets--;
                }
                else if (c == ',' && braces == 1 && parentheses == 0 && brackets == 0)
                {
                    multipleFields = true;
                }
            }
            int closing = end + 1;
            while (closing < masked.Length && Char.IsWhiteSpace(masked[closing]))
            {
                closing++;
            }
            if (multipleFields || end >= masked.Length || closing >= masked.Length || masked[closing] != ')')
            {
                continue;
            }
            string zone = BindZonedStatementClock(source[(match.Index + match.Length)..end], instant, depth + 1);
            string name = match.Groups["name"].Value.ToUpperInvariant() switch
            {
                "ZONED_DATETIME" => "DATETIME",
                "ZONED_TIME" => "TIME",
                "LOCAL_TIME" => "LOCALTIME",
                "LOCAL_DATETIME" => "LOCALDATETIME",
                var value => value
            };
            string datetime = "datetime({datetime:datetime('" + instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z'),timezone:" + zone + "})";
            result.Append(source.AsSpan(copied, match.Index - copied));
            result.Append(name == "DATETIME" ? datetime : name + "(" + datetime + ")");
            copied = closing + 1;
        }
        return result.Append(source.AsSpan(copied)).ToString();
    }

    private static string BindTimeComponentZones(string source, DateTimeOffset instant, int depth = 0)
    {
        if (depth > 64)
        {
            throw new ArgumentException("Clock expressions exceed 64 nesting levels.");
        }
        string masked = MaskQuotedStrings(source);
        var result = new StringBuilder();
        int copied = 0;
        foreach (Match match in TimeComponentZonePattern.Matches(masked))
        {
            if (match.Index < copied)
            {
                continue;
            }
            int start = match.Index + match.Length, end = start, braces = 1;
            for (; end < masked.Length; end++)
            {
                if (masked[end] == '{')
                {
                    braces++;
                }
                else if (masked[end] == '}' && --braces == 0)
                {
                    break;
                }
            }
            if (end == masked.Length)
            {
                continue;
            }
            string content = source[start..end];
            var fields = SplitProjectionItems(content).ToArray();
            bool changed = false;
            for (int i = 0; i < fields.Length; i++)
            {
                var field = GqlRegex.Match(fields[i], @"^\s*timezone\s*:(?<zone>.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
                if (!field.Success)
                {
                    continue;
                }
                string zone = field.Groups["zone"].Value.Trim();
                if (GqlRegex.IsMatch(zone, "^(['\"])(?:Z|UTC|GMT|[+-][0-9:]+)\\1$", RegexOptions.CultureInvariant))
                {
                    continue;
                }
                // A time has no calendar date: named regions use the statement's
                // current offset, even when a dated value supplies its fields.
                fields[i] = "timezone:datetime({datetime:datetime('" +
                    instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture) +
                    "Z'),timezone:" + zone + "}).offset";
                changed = true;
            }
            string rewritten = BindTimeComponentZones(changed ? String.Join(",", fields) : content, instant, depth + 1);
            result.Append(source.AsSpan(copied, start - copied)).Append(rewritten);
            copied = end;
        }
        return result.Append(source.AsSpan(copied)).ToString();
    }

    private static string NormalizeClockZoneArguments(string source, int depth = 0)
    {
        if (depth > 64)
        {
            throw new ArgumentException("Clock expressions exceed 64 nesting levels.");
        }
        string masked = MaskQuotedStrings(source);
        var matches = ClockZoneArgumentPattern.Matches(masked);
        var result = new StringBuilder();
        int copied = 0;
        foreach (Match match in matches)
        {
            if (match.Index < copied)
            {
                continue;
            }
            int start = match.Index + match.Length, end = start, parentheses = 1;
            for (; end < masked.Length; end++)
            {
                if (masked[end] == '(')
                {
                    parentheses++;
                }
                else if (masked[end] == ')' && --parentheses == 0)
                {
                    break;
                }
            }
            if (end == masked.Length || String.IsNullOrWhiteSpace(source[start..end]))
            {
                continue;
            }
            result.Append(source.AsSpan(copied, match.Index - copied));
            result.Append(match.Groups["name"].Value).Append("({timezone:")
                .Append(NormalizeClockZoneArguments(source[start..end], depth + 1)).Append("})");
            copied = end + 1;
        }
        return result.Append(source.AsSpan(copied)).ToString();
    }
}
