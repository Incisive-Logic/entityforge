using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static RdfTerm? DateTimeComponent(string function, RdfTerm? value)
    {
        if (value is not RdfLiteral literal || literal.Datatype != Xsd + "dateTime")
        {
            return null;
        }
        Match match = DateTimeLexical().Match(literal.LexicalForm.Trim(' ', '\t', '\r', '\n'));
        if (!match.Success)
        {
            return null;
        }
        string yearText = match.Groups["year"].Value;
        string digits = yearText.TrimStart('-');
        if (digits.Length > 4096 || match.Groups["fraction"].Length > 4097)
        {
            throw new RdfSparqlWorkLimitException("Date/time numeric precision exceeds 4096 digits.");
        }
        if (digits.Length > 4 && digits[0] == '0')
        {
            return null;
        }
        BigInteger year = BigInteger.Parse(yearText, CultureInfo.InvariantCulture);
        int Number(string name) => Int32.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture);
        int month = Number("month"), day = Number("day"), hour = Number("hour"), minute = Number("minute"), second = Number("second");
        string fraction = match.Groups["fraction"].Value;
        int Days()
        {
            BigInteger calendarYear = year.Sign < 0 ? year + 1 : year;
            return month == 2 ? (calendarYear % 4 == 0 && (calendarYear % 100 != 0 || calendarYear % 400 == 0) ? 29 : 28) :
                month is 4 or 6 or 9 or 11 ? 30 : 31;
        }
        if (year.IsZero || month is < 1 or > 12 || day < 1 || day > Days() || hour > 24 || minute > 59 || second > 59 ||
            hour == 24 && (minute != 0 || second != 0 || fraction.Any(c => c is not ('.' or '0'))))
        {
            return null;
        }
        string zone = match.Groups["zone"].Value;
        int zoneHours = zone.Length > 1 ? Int32.Parse(zone.AsSpan(1, 2), CultureInfo.InvariantCulture) : 0;
        int zoneMinutes = zone.Length > 1 ? Int32.Parse(zone.AsSpan(4, 2), CultureInfo.InvariantCulture) : 0;
        if (zoneHours > 14 || zoneMinutes > 59 || zoneHours == 14 && zoneMinutes != 0)
        {
            return null;
        }
        // 24:00:00 denotes midnight at the start of the next calendar day.
        if (hour == 24)
        {
            hour = 0;
            if (++day > Days())
            {
                day = 1;
                if (++month > 12)
                {
                    month = 1;
                    year++;
                    if (year.IsZero)
                    {
                        year++;
                    }
                }
            }
        }
        if (function == "CANONICAL")
        {
            string normalizedYear = BigInteger.Abs(year).ToString(CultureInfo.InvariantCulture).PadLeft(4, '0');
            string normalizedZone = zone.Length > 0 && zoneHours == 0 && zoneMinutes == 0 ? "Z" : zone;
            string normalized = String.Create(CultureInfo.InvariantCulture,
                $"{(year.Sign < 0 ? "-" : String.Empty)}{normalizedYear}-{month:00}-{day:00}T{hour:00}:{minute:00}:{second:00}{fraction.TrimEnd('0').TrimEnd('.')}{normalizedZone}");
            return new RdfLiteral(normalized, Xsd + "dateTime");
        }
        if (function == "TZ")
        {
            return new RdfLiteral(zone);
        }
        if (function == "TIMEZONE")
        {
            if (zone.Length == 0)
            {
                return null;
            }
            string duration = zoneHours == 0 && zoneMinutes == 0 ? "PT0S" :
                (zone[0] == '-' ? "-PT" : "PT") +
                (zoneHours == 0 ? String.Empty : zoneHours.ToString(CultureInfo.InvariantCulture) + "H") +
                (zoneMinutes == 0 ? String.Empty : zoneMinutes.ToString(CultureInfo.InvariantCulture) + "M");
            return new RdfLiteral(duration, Xsd + "dayTimeDuration");
        }
        if (function == "SECONDS")
        {
            string tail = fraction.TrimEnd('0').TrimEnd('.');
            return new RdfLiteral(second.ToString(CultureInfo.InvariantCulture) + tail, Xsd + "decimal");
        }
        BigInteger component = function switch
        {
            "YEAR" => year,
            "MONTH" => month,
            "DAY" => day,
            // SPARQL HOURS uses the lexical field, unlike instant comparison
            // and date components which use the normalized dateTime value.
            "HOURS" => Number("hour"),
            "MINUTES" => minute,
            _ => throw new InvalidOperationException("Unknown date/time component.")
        };
        return new RdfLiteral(component.ToString(CultureInfo.InvariantCulture), Xsd + "integer");
    }

    [GeneratedRegex(@"\A(?<year>-?[0-9]{4,})-(?<month>[0-9]{2})-(?<day>[0-9]{2})T(?<hour>[0-9]{2}):(?<minute>[0-9]{2}):(?<second>[0-9]{2})(?<fraction>\.[0-9]+)?(?<zone>Z|[+-][0-9]{2}:[0-9]{2})?\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DateTimeLexical();
}
