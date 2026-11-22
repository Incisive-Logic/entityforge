using System.Globalization;
using System.Text.RegularExpressions;
using EntityForge.Query.Values;
using EntityForge.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static CalendarDate DateValue(LiteralValue value) => CalendarDate.Parse(value.LexicalValue);

    private static LiteralValue DateLiteral(CalendarDate date) => new(date.ToString(), QueryScalarKind.Date);

    private static QueryValue ConstructDate(QueryValue input)
    {
        if (input is NullValue or UnboundValue)
        {
            return UnboundValue.Instance;
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.Date } date)
        {
            return date;
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime } zonedDateTime)
        {
            return DateLiteral(ZonedDateTimeValue(zonedDateTime).DateTime.Date);
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.LocalDateTime } datetime)
        {
            return DateLiteral(LocalDateTimeValue(datetime).Date);
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.String } text)
        {
            return DateLiteral(ParseDate(text.LexicalValue));
        }
        if (input is not MapValue map)
        {
            throw new ArgumentException("DATE requires a date, ISO string or component map.");
        }
        string[] fields = ["year", "month", "day", "week", "dayOfWeek", "quarter", "dayOfQuarter", "ordinalDay", "date"];
        if (map.Values.Keys.Any(key => !fields.Contains(key, StringComparer.Ordinal)))
        {
            throw GqlDataError("Unknown DATE component.", "22G05");
        }
        CalendarDate? basis = map.Values.TryGetValue("date", out var source)
            ? source is LiteralValue { ScalarKind: QueryScalarKind.Date } existing ? DateValue(existing)
                : source is LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime } zonedSource ? ZonedDateTimeValue(zonedSource).DateTime.Date
                : source is LiteralValue { ScalarKind: QueryScalarKind.LocalDateTime } selected ? LocalDateTimeValue(selected).Date
                : throw new ArgumentException("The date component must be a DATE value.") : null;
        int Number(string name, int? fallback = null)
        {
            if (!map.Values.TryGetValue(name, out var value))
            {
                return fallback ?? throw new ArgumentException("DATE requires a year.");
            }
            return value is LiteralValue { ScalarKind: QueryScalarKind.Integer } integer &&
                Int32.TryParse(integer.LexicalValue, CultureInfo.InvariantCulture, out int number) ? number
                : throw new ArgumentException("DATE components require integers.");
        }
        bool calendar = map.Values.ContainsKey("month") || map.Values.ContainsKey("day");
        bool week = map.Values.ContainsKey("week") || map.Values.ContainsKey("dayOfWeek");
        bool quarter = map.Values.ContainsKey("quarter") || map.Values.ContainsKey("dayOfQuarter");
        bool ordinal = map.Values.ContainsKey("ordinalDay");
        int year = Number("year", basis is null ? null : week ? IsoWeek(basis).Year : basis.Year);
        if (basis is null && (map.Values.ContainsKey("day") && !map.Values.ContainsKey("month") ||
            map.Values.ContainsKey("dayOfWeek") && !map.Values.ContainsKey("week") ||
            map.Values.ContainsKey("dayOfQuarter") && !map.Values.ContainsKey("quarter")))
        {
            throw GqlDataError("DATE day components require their corresponding month, week or quarter.", "22G05");
        }
        if ((calendar ? 1 : 0) + (week ? 1 : 0) + (quarter ? 1 : 0) + (ordinal ? 1 : 0) > 1)
        {
            throw GqlDataError("DATE cannot mix calendar, week, quarter and ordinal components.", "22G05");
        }
        if (week)
        {
            return DateLiteral(WeekDate(year, Number("week", basis is null ? 1 : IsoWeek(basis).Week), Number("dayOfWeek", basis?.DayOfWeek ?? 1)));
        }
        if (quarter)
        {
            return DateLiteral(QuarterDate(year, Number("quarter", basis is null ? 1 : (basis.Month - 1) / 3 + 1), Number("dayOfQuarter", basis is null ? 1 : (int)(basis.EpochDay - new CalendarDate(basis.Year, (basis.Month - 1) / 3 * 3 + 1, 1).EpochDay + 1))));
        }
        if (ordinal)
        {
            return DateLiteral(OrdinalDate(year, Number("ordinalDay")));
        }
        return DateLiteral(new(year, Number("month", basis?.Month ?? 1), Number("day", basis?.Day ?? 1)));
    }

    private static QueryValue TruncateDate(QueryValue[] values)
    {
        if (values[1] is LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime })
        {
            values[1] = ConstructDate(values[1]);
        }
        if (values[1] is LiteralValue { ScalarKind: QueryScalarKind.LocalDateTime } datetime)
        {
            values[1] = DateLiteral(LocalDateTimeValue(datetime).Date);
        }
        if (values[0] is not LiteralValue { ScalarKind: QueryScalarKind.String } unit ||
            values[1] is not LiteralValue { ScalarKind: QueryScalarKind.Date } value)
        {
            throw new ArgumentException("DATE.TRUNCATE requires a unit string and a date.");
        }
        CalendarDate date = DateValue(value);
        CalendarDate truncated = unit.LexicalValue switch
        {
            "millennium" => new(date.Year / 1000 * 1000, 1, 1),
            "century" => new(date.Year / 100 * 100, 1, 1),
            "decade" => new(date.Year / 10 * 10, 1, 1),
            "year" => new(date.Year, 1, 1),
            "weekYear" => CalendarDate.FromEpochDay(FirstWeek(IsoWeek(date).Year)),
            "quarter" => new(date.Year, (date.Month - 1) / 3 * 3 + 1, 1),
            "month" => new(date.Year, date.Month, 1),
            "week" => CalendarDate.FromEpochDay(date.EpochDay - date.DayOfWeek + 1),
            "day" => date,
            _ => throw new ArgumentException("Unsupported DATE.TRUNCATE unit.")
        };
        if (values.Length == 2 || values[2] is NullValue or UnboundValue)
        {
            return DateLiteral(truncated);
        }
        if (values[2] is not MapValue fields || fields.Values.ContainsKey("date"))
        {
            throw new ArgumentException("DATE.TRUNCATE overrides require a component map.");
        }
        var components = fields.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        components.Add("date", DateLiteral(truncated));
        return ConstructDate(new MapValue(components));
    }

    private static CalendarDate ParseDate(string text)
    {
        // Signed extended years require a separator; ordinary years have four digits.
        var match = Regex.Match(text, @"\A(?<year>[+-][0-9]{4,9}(?=-|$)|[0-9]{4})(?<rest>.*)\z", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new ArgumentException("Invalid ISO date.");
        }
        int year = Int32.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
        string rest = match.Groups["rest"].Value;
        if (rest.Length == 0)
        {
            return new(year, 1, 1);
        }
        bool separated = rest.StartsWith('-');
        if (separated)
        {
            rest = rest[1..];
        }
        var week = Regex.Match(rest, separated ? @"\AW(?<week>[0-9]{1,2})(?:-(?<day>[1-7]))?\z" :
            @"\AW(?<week>[0-9]{1,2})(?:-(?<day>[1-7]))?\z|\AW(?<week>[0-9]{2})(?<day>[1-7])\z");
        if (week.Success)
        {
            return WeekDate(year, Int32.Parse(week.Groups["week"].Value, CultureInfo.InvariantCulture),
                week.Groups["day"].Success ? Int32.Parse(week.Groups["day"].Value, CultureInfo.InvariantCulture) : 1);
        }
        var quarter = Regex.Match(rest, separated ? @"\AQ(?<quarter>[1-4])(?:-(?<day>[0-9]{1,2}))?\z" :
            @"\AQ(?<quarter>[1-4])(?:-?(?<day>[0-9]{1,2}))?\z");
        if (quarter.Success)
        {
            return QuarterDate(year, rest[1] - '0', quarter.Groups["day"].Success ?
                Int32.Parse(quarter.Groups["day"].Value, CultureInfo.InvariantCulture) : 1);
        }
        if (Regex.IsMatch(rest, @"\A[0-9]{3}\z"))
        {
            return OrdinalDate(year, Int32.Parse(rest, CultureInfo.InvariantCulture));
        }
        var calendar = Regex.Match(rest, separated ? @"\A(?<month>[0-9]{1,2})-(?<day>[0-9]{1,2})\z|\A(?<month>[0-9]{2})\z" :
            @"\A(?<month>[0-9]{2})(?<day>[0-9]{2})?\z");
        if (!calendar.Success)
        {
            throw new ArgumentException("Invalid ISO date components.");
        }
        return new(year, Int32.Parse(calendar.Groups["month"].Value, CultureInfo.InvariantCulture), calendar.Groups["day"].Success ?
            Int32.Parse(calendar.Groups["day"].Value, CultureInfo.InvariantCulture) : 1);
    }

    private static CalendarDate OrdinalDate(int year, int day)
    {
        if (day < 1 || day > 337 + CalendarDate.DaysInMonth(year, 2))
        {
            throw new ArgumentException("Invalid ordinal day.");
        }
        return CalendarDate.FromEpochDay(new CalendarDate(year, 1, 1).EpochDay + day - 1);
    }

    private static CalendarDate QuarterDate(int year, int quarter, int day)
    {
        if (quarter is < 1 or > 4)
        {
            throw new ArgumentException("Invalid quarter.");
        }
        int month = (quarter - 1) * 3 + 1;
        int days = Enumerable.Range(month, 3).Sum(item => CalendarDate.DaysInMonth(year, item));
        if (day < 1 || day > days)
        {
            throw new ArgumentException("Invalid quarter day.");
        }
        return CalendarDate.FromEpochDay(new CalendarDate(year, month, 1).EpochDay + day - 1);
    }

    private static long FirstWeek(int year)
    {
        var fourth = new CalendarDate(year, 1, 4);
        return fourth.EpochDay - fourth.DayOfWeek + 1;
    }

    private static CalendarDate WeekDate(int year, int week, int day)
    {
        int weeks = new CalendarDate(year, 1, 1).DayOfWeek == 4 ||
            new CalendarDate(year, 1, 1).DayOfWeek == 3 && CalendarDate.DaysInMonth(year, 2) == 29 ? 53 : 52;
        if (week < 1 || week > weeks || day is < 1 or > 7)
        {
            throw new ArgumentException("Invalid ISO week date.");
        }
        return CalendarDate.FromEpochDay(FirstWeek(year) + (week - 1) * 7 + day - 1);
    }

    private static (int Year, int Week) IsoWeek(CalendarDate date)
    {
        long first = FirstWeek(date.Year);
        if (date.EpochDay < first)
        {
            return (date.Year - 1, (int)((date.EpochDay - FirstWeek(date.Year - 1)) / 7 + 1));
        }
        if (date.Year < CalendarDate.MaximumYear && date.EpochDay >= FirstWeek(date.Year + 1))
        {
            return (date.Year + 1, 1);
        }
        return (date.Year, (int)((date.EpochDay - first) / 7 + 1));
    }

    private static QueryValue DateProperty(LiteralValue literal, QueryValue key)
    {
        if (key is NullValue or UnboundValue)
        {
            return UnboundValue.Instance;
        }
        if (key is not LiteralValue { ScalarKind: QueryScalarKind.String } name)
        {
            throw new ArgumentException("A DATE property name must be a string.");
        }
        CalendarDate date = DateValue(literal);
        int value = name.LexicalValue switch
        {
            "year" => date.Year,
            "month" => date.Month,
            "day" => date.Day,
            "quarter" => (date.Month - 1) / 3 + 1,
            "ordinalDay" => date.DayOfYear,
            "week" => IsoWeek(date).Week,
            "weekYear" => IsoWeek(date).Year,
            "dayOfWeek" => date.DayOfWeek,
            "dayOfQuarter" => (int)(date.EpochDay - new CalendarDate(date.Year, (date.Month - 1) / 3 * 3 + 1, 1).EpochDay + 1),
            _ => throw new ArgumentException("Unknown DATE property.")
        };
        return new LiteralValue(value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
    }
}
