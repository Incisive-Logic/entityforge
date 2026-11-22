using System.Numerics;
using EntityForge.Query.Values;
using EntityForge.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static LocalDateTime LocalDateTimeValue(LiteralValue value) => LocalDateTime.Parse(value.LexicalValue);

    private static LiteralValue LocalDateTimeLiteral(LocalDateTime value) => new(value.ToString(), QueryScalarKind.LocalDateTime);

    private static readonly string[] LocalTimeFields = ["hour", "minute", "second", "millisecond", "microsecond", "nanosecond"];

    private static QueryValue ConstructLocalDateTime(QueryValue input)
    {
        if (input is NullValue or UnboundValue)
        {
            return UnboundValue.Instance;
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime } zonedDateTime)
        {
            return LocalDateTimeLiteral(ZonedDateTimeValue(zonedDateTime).DateTime);
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.LocalDateTime } existing)
        {
            return existing;
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.String } text)
        {
            int separator = text.LexicalValue.IndexOf('T');
            try
            {
                return LocalDateTimeLiteral(separator < 0 ? new(ParseDate(text.LexicalValue), new(0)) :
                    new(ParseDate(text.LexicalValue[..separator]), LocalTime.Parse(text.LexicalValue[(separator + 1)..])));
            }
            catch (FormatException exception)
            {
                throw new ArgumentException("Invalid local datetime string.", exception);
            }
        }
        if (input is not MapValue map || map.Values.Count == 0)
        {
            throw new ArgumentException("LOCALDATETIME requires an ISO string, local datetime or non-empty component map.");
        }
        LocalDateTime? basis = map.Values.TryGetValue("datetime", out var source)
            ? source is LiteralValue { ScalarKind: QueryScalarKind.LocalDateTime } datetime ? LocalDateTimeValue(datetime)
                : source is LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime } zonedSource ? ZonedDateTimeValue(zonedSource).DateTime
                : throw new ArgumentException("The datetime component requires a datetime.") : null;
        if (basis is not null && (map.Values.ContainsKey("date") || map.Values.ContainsKey("time")))
        {
            throw GqlDataError("A datetime selector cannot be combined with a date or time selector.", "22G05");
        }
        var dates = map.Values.Where(pair => !LocalTimeFields.Contains(pair.Key, StringComparer.Ordinal) && pair.Key is not ("time" or "datetime"))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var times = map.Values.Where(pair => LocalTimeFields.Contains(pair.Key, StringComparer.Ordinal) || pair.Key == "time")
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (basis is not null)
        {
            dates.Add("date", DateLiteral(basis.Date));
            times.Add("time", TimeLiteral(basis.Time));
        }
        if (times.Count > 0 && !dates.ContainsKey("date") &&
            !(dates.ContainsKey("month") && dates.ContainsKey("day") || dates.ContainsKey("week") && dates.ContainsKey("dayOfWeek") ||
              dates.ContainsKey("quarter") && dates.ContainsKey("dayOfQuarter") || dates.ContainsKey("ordinalDay")))
        {
            throw GqlDataError("Time components require a complete calendar, week, quarter or ordinal date.", "22G05");
        }
        var date = (LiteralValue)ConstructDate(new MapValue(dates));
        LocalTime time = times.Count == 0 ? new(0) : TimeValue((LiteralValue)ConstructLocalTime(new MapValue(times)));
        return LocalDateTimeLiteral(new(DateValue(date), time));
    }

    private static QueryValue LocalDateTimeProperty(LiteralValue value, QueryValue key)
    {
        LocalDateTime datetime = LocalDateTimeValue(value);
        return key is LiteralValue { ScalarKind: QueryScalarKind.String } name && LocalTimeFields.Contains(name.LexicalValue, StringComparer.Ordinal)
            ? LocalTimeProperty(TimeLiteral(datetime.Time), key) : DateProperty(DateLiteral(datetime.Date), key);
    }

    private static QueryValue TruncateLocalDateTime(QueryValue[] values)
    {
        if (values[1] is LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime })
        {
            values[1] = ConstructLocalDateTime(values[1]);
        }
        if (values[0] is not LiteralValue { ScalarKind: QueryScalarKind.String } unit || values[1] is not LiteralValue source ||
            source.ScalarKind is not (QueryScalarKind.Date or QueryScalarKind.LocalDateTime))
        {
            throw new ArgumentException("LOCALDATETIME.TRUNCATE requires a unit and a date or local datetime.");
        }
        var datetime = source.ScalarKind == QueryScalarKind.Date ? new LocalDateTime(DateValue(source), new(0)) : LocalDateTimeValue(source);
        var overrides = values.Length < 3 || values[2] is NullValue or UnboundValue ? new MapValue(new Dictionary<string, QueryValue>()) :
            values[2] as MapValue ?? throw new ArgumentException("Truncation overrides require a component map.");
        if (overrides.Values.Keys.Any(key => key is "date" or "time" or "datetime"))
        {
            throw new ArgumentException("Truncation overrides cannot contain temporal selectors.");
        }
        var dates = overrides.Values.Where(pair => !LocalTimeFields.Contains(pair.Key, StringComparer.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var times = overrides.Values.Where(pair => LocalTimeFields.Contains(pair.Key, StringComparer.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        bool timeUnit = unit.LexicalValue is "hour" or "minute" or "second" or "millisecond" or "microsecond";
        if (timeUnit && source.ScalarKind == QueryScalarKind.Date)
        {
            throw new ArgumentException("A date has no time component to truncate.");
        }
        LiteralValue date;
        if (timeUnit)
        {
            dates.Add("date", DateLiteral(datetime.Date));
            date = (LiteralValue)ConstructDate(new MapValue(dates));
        }
        else
        {
            date = (LiteralValue)TruncateDate([unit, DateLiteral(datetime.Date), new MapValue(dates)]);
        }
        var time = (LiteralValue)TruncateLocalTime([timeUnit ? unit : new LiteralValue("day"), TimeLiteral(datetime.Time), new MapValue(times)]);
        return LocalDateTimeLiteral(new(DateValue(date), TimeValue(time)));
    }

    private static QueryValue ShiftLocalDateTime(LiteralValue value, CalendarDuration duration, bool subtract)
    {
        LocalDateTime datetime = LocalDateTimeValue(value);
        int sign = subtract ? -1 : 1;
        CalendarDate date = ShiftDateMonths(datetime.Date, (BigInteger)duration.Months * sign);
        BigInteger nanos = datetime.Time.NanosecondsOfDay + sign * ((BigInteger)duration.Seconds * 1_000_000_000 + duration.Nanoseconds);
        BigInteger days = BigInteger.DivRem(nanos, LocalTime.NanosecondsPerDay, out BigInteger remainder);
        if (remainder.Sign < 0)
        {
            days--;
            remainder += LocalTime.NanosecondsPerDay;
        }
        BigInteger epochDay = date.EpochDay + (BigInteger)duration.Days * sign + days;
        if (epochDay < Int64.MinValue || epochDay > Int64.MaxValue)
        {
            throw new ArgumentException("Local datetime arithmetic exceeds the calendar range.");
        }
        return LocalDateTimeLiteral(new(CalendarDate.FromEpochDay((long)epochDay), new((long)remainder)));
    }

    private static QueryValue LocalDateTimeDifference(string function, LocalDateTime start, LocalDateTime end, int offsetSeconds = 0)
    {
        long dayDifference = end.Date.EpochDay - start.Date.EpochDay;
        long nanos = end.Time.NanosecondsOfDay - start.Time.NanosecondsOfDay + offsetSeconds * 1_000_000_000L;
        if (function == "DURATION_BETWEEN")
        {
            decimal totalSeconds = dayDifference * 86_400m + nanos / 1_000_000_000m;
            long wholeDays = (long)Decimal.Truncate(totalSeconds / 86_400m);
            return DurationLiteral(CalendarDuration.FromComponents(0, wholeDays, totalSeconds - wholeDays * 86_400m));
        }
        if (function == "DURATION.INSECONDS")
        {
            return DurationLiteral(CalendarDuration.FromComponents(0, 0, dayDifference * 86_400m + nanos / 1_000_000_000m));
        }
        if (function == "DURATION.INDAYS")
        {
            dayDifference = (long)Decimal.Truncate((dayDifference * 86_400m + nanos / 1_000_000_000m) / 86_400);
            return DurationLiteral(new(0, dayDifference, 0, 0));
        }
        long months = ((long)end.Date.Year - start.Date.Year) * 12 + end.Date.Month - start.Date.Month;
        if (months > 0 && (end.Date.Day < start.Date.Day || end.Date.Day == start.Date.Day && nanos < 0))
        {
            months--;
        }
        else if (months < 0 && (end.Date.Day > start.Date.Day || end.Date.Day == start.Date.Day && nanos > 0))
        {
            months++;
        }
        if (function == "DURATION.INMONTHS")
        {
            return DurationLiteral(new(months, 0, 0, 0), DatabaseValue.Duration.YearToMonthDatatype);
        }
        long days = end.Date.EpochDay - ShiftDateMonths(start.Date, months).EpochDay;
        decimal seconds = days * 86_400m + nanos / 1_000_000_000m;
        days = (long)Decimal.Truncate(seconds / 86_400);
        return DurationLiteral(CalendarDuration.FromComponents(months, days, seconds - days * 86_400));
    }
}
