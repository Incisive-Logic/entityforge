using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using EntityForge.Query.Values;
using EntityForge.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static CalendarDuration DurationValue(LiteralValue value) => CalendarDuration.Parse(value.LexicalValue);

    private static LiteralValue DurationLiteral(CalendarDuration value, Uri? datatype = null)
    {
        if (value.Months != 0 && (value.Days != 0 || value.Seconds != 0 || value.Nanoseconds != 0))
        {
            throw new ArgumentException("Mixed year-month and day-time duration values are not supported.");
        }
        datatype ??= value.Months != 0 ? DatabaseValue.Duration.YearToMonthDatatype : DatabaseValue.Duration.DayToSecondDatatype;
        _ = new DatabaseValue.Duration(value, DatabaseValue.Duration.QualifierFor(datatype));
        return new(value.ToString(), QueryScalarKind.Duration, datatype);
    }

    private static CalendarDuration DurationComponents(decimal months, decimal days, decimal seconds)
    {
        if (months != Decimal.Truncate(months))
        {
            throw new ArgumentException("Fractional months are not supported; months cannot be approximated as elapsed days.");
        }
        if (months != 0 && (days != 0 || seconds != 0))
        {
            throw new ArgumentException("Mixed year-month and day-time duration values are not supported.");
        }
        return CalendarDuration.FromComponents(months, days, seconds);
    }

    private static CalendarDuration ParseDurationString(string text)
    {
        // GQL's unit-labelled duration productions use integer components;
        // only seconds have a fractional part. The native calendar parser
        // also accepts broader ISO forms for non-GQL callers.
        if (Regex.IsMatch(text, @"[.,][0-9]+[YMWDH]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Duration strings support fractional seconds only.");
        }
        string datePart = text.Split(['T', 't'])[0];
        var calendarFields = Regex.Matches(datePart, @"(?<number>[+-]?[0-9]+(?:[.,][0-9]+)?)(?<unit>[YM])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (calendarFields.Count != 0)
        {
            decimal months = 0;
            foreach (Match field in calendarFields)
            {
                months += Decimal.Parse(field.Groups["number"].Value.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture) * (field.Groups["unit"].Value.Equals("Y", StringComparison.OrdinalIgnoreCase) ? 12 : 1);
            }
            _ = DurationComponents(months, 0, 0);
            if (datePart.IndexOfAny(['D', 'd', 'W', 'w']) >= 0 || text.IndexOfAny(['T', 't']) >= 0)
            {
                throw new ArgumentException("Mixed year-month and day-time duration strings are not supported.");
            }
        }
        return CalendarDuration.Parse(text);
    }

    private static BigInteger DayTimeNanoseconds(CalendarDuration value) =>
        ((BigInteger)value.Days * 86_400 + value.Seconds) * 1_000_000_000 + value.Nanoseconds;

    private static int CompareDurationValues(LiteralValue left, LiteralValue right)
    {
        if (left.DatatypeIdentifier != right.DatatypeIdentifier)
        {
            throw new ArgumentException("Different duration families, including unqualified native durations, cannot be compared.");
        }
        var first = DurationValue(left);
        var second = DurationValue(right);
        if (first.Months == 0 && second.Months == 0)
        {
            return DayTimeNanoseconds(first).CompareTo(DayTimeNanoseconds(second));
        }
        if (first.Days == 0 && first.Seconds == 0 && first.Nanoseconds == 0 &&
            second.Days == 0 && second.Seconds == 0 && second.Nanoseconds == 0)
        {
            return first.Months.CompareTo(second.Months);
        }
        throw new ArgumentException("Comparison between mixed or different duration families is not supported.");
    }

    private static QueryValue ConstructDuration(QueryValue input)
    {
        if (input is NullValue or UnboundValue)
        {
            return UnboundValue.Instance;
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.Duration } existing)
        {
            return existing;
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.String } text)
        {
            try
            {
                var value = ParseDurationString(text.LexicalValue);
                bool months = text.LexicalValue.Split(['T', 't'])[0].IndexOfAny(['Y', 'y', 'M', 'm']) >= 0;
                return DurationLiteral(value, months ? DatabaseValue.Duration.YearToMonthDatatype : DatabaseValue.Duration.DayToSecondDatatype);
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                throw new ArgumentException("Invalid duration string.", exception);
            }
        }
        if (input is not MapValue map || map.Values.Count == 0)
        {
            throw new ArgumentException("DURATION requires an ISO string or non-empty component map.");
        }
        string[] fields = ["years", "months", "weeks", "days", "hours", "minutes", "seconds", "milliseconds", "microseconds", "nanoseconds"];
        if (map.Values.Keys.Any(key => !fields.Contains(key, StringComparer.Ordinal)))
        {
            throw GqlDataError("Unknown duration component.", "22G07");
        }
        decimal Value(string name)
        {
            if (!map.Values.TryGetValue(name, out var value))
            {
                return 0;
            }
            if (value is not LiteralValue { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal or QueryScalarKind.FloatingPoint } literal ||
                !Decimal.TryParse(literal.LexicalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
            {
                throw new ArgumentException("Duration components require finite numbers.");
            }
            return number;
        }
        bool yearMonth = map.Values.ContainsKey("years") || map.Values.ContainsKey("months");
        if (yearMonth && map.Values.Keys.Any(key => key is not ("years" or "months")))
        {
            throw new ArgumentException("Mixed duration field families are not supported.");
        }
        return DurationLiteral(DurationComponents(Value("years") * 12 + Value("months"), Value("weeks") * 7 + Value("days"),
            Value("hours") * 3600 + Value("minutes") * 60 + Value("seconds") + Value("milliseconds") / 1000 +
            Value("microseconds") / 1_000_000 + Value("nanoseconds") / 1_000_000_000),
            yearMonth ? DatabaseValue.Duration.YearToMonthDatatype : DatabaseValue.Duration.DayToSecondDatatype);
    }

    private static QueryValue DurationProperty(LiteralValue literal, QueryValue key)
    {
        if (key is NullValue or UnboundValue)
        {
            return UnboundValue.Instance;
        }
        if (key is not LiteralValue { ScalarKind: QueryScalarKind.String } name)
        {
            throw new ArgumentException("A duration property name must be a string.");
        }
        CalendarDuration duration = DurationValue(literal);
        BigInteger nanos = (BigInteger)duration.Seconds * 1_000_000_000 + duration.Nanoseconds;
        BigInteger Floor(BigInteger number, int divisor) => number / divisor - (number.Sign < 0 && number % divisor != 0 ? 1 : 0);
        BigInteger value = name.LexicalValue switch
        {
            "years" => duration.Months / 12,
            "quarters" => duration.Months / 3,
            "months" => duration.Months,
            "monthsOfYear" => duration.Months % 12,
            "monthsOfQuarter" => duration.Months % 3,
            "quartersOfYear" => duration.Months / 3 % 4,
            "weeks" => duration.Days / 7,
            "days" => duration.Days,
            "daysOfWeek" => duration.Days % 7,
            "hours" => duration.Seconds / 3600,
            "minutes" => duration.Seconds / 60,
            "seconds" => duration.Seconds,
            "minutesOfHour" => duration.Seconds / 60 % 60,
            "secondsOfMinute" => duration.Seconds % 60,
            "milliseconds" => Floor(nanos, 1_000_000),
            "microseconds" => Floor(nanos, 1000),
            "nanoseconds" => nanos,
            "millisecondsOfSecond" => duration.Nanoseconds / 1_000_000,
            "microsecondsOfSecond" => duration.Nanoseconds / 1000,
            "nanosecondsOfSecond" => duration.Nanoseconds,
            _ => throw new ArgumentException("Unknown duration property.")
        };
        if (value < Int64.MinValue || value > Int64.MaxValue)
        {
            throw new ArgumentException("Duration component exceeds the integer range.");
        }
        return new LiteralValue(value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
    }

    private static CalendarDate ShiftDateMonths(CalendarDate date, BigInteger months)
    {
        BigInteger month = (BigInteger)date.Year * 12 + date.Month - 1 + months;
        BigInteger year = month / 12 - (month.Sign < 0 && month % 12 != 0 ? 1 : 0);
        if (year < CalendarDate.MinimumYear || year > CalendarDate.MaximumYear)
        {
            throw new ArgumentException("Calendar arithmetic exceeds the date range.");
        }
        int targetMonth = (int)(month - year * 12) + 1;
        return new((int)year, targetMonth, Math.Min(date.Day, CalendarDate.DaysInMonth((int)year, targetMonth)));
    }

    private static QueryValue DurationBetween(string function, QueryValue[] values)
    {
        if (values.Any(value => value is NullValue or UnboundValue))
        {
            return UnboundValue.Instance;
        }
        if (values.Any(value => value is LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime }) ||
            values.Any(value => value is LiteralValue { ScalarKind: QueryScalarKind.ZonedTime }) &&
            values.Any(value => value is LiteralValue { ScalarKind: QueryScalarKind.Date or QueryScalarKind.LocalDateTime }))
        {
            if (!values.All(value => value is LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime or QueryScalarKind.LocalDateTime or QueryScalarKind.Date or QueryScalarKind.LocalTime or QueryScalarKind.ZonedTime }))
            {
                throw new ArgumentException("Duration differences require temporal arguments.");
            }
            var zonedBasis = values.OfType<LiteralValue>().FirstOrDefault(value => value.ScalarKind == QueryScalarKind.ZonedDateTime);
            var basis = zonedBasis is not null ? ZonedDateTimeValue(zonedBasis) :
                new ZonedDateTime(new(DateValue((LiteralValue)ConstructDate(values.First(value => value is LiteralValue { ScalarKind: QueryScalarKind.Date or QueryScalarKind.LocalDateTime }))), new(0)),
                    ZonedTimeValue(values.OfType<LiteralValue>().First(value => value.ScalarKind == QueryScalarKind.ZonedTime)).OffsetSeconds);
            ZonedDateTime Normalize(QueryValue value) => value is LiteralValue literal ? literal.ScalarKind switch
            {
                QueryScalarKind.ZonedDateTime => ZonedDateTimeValue(literal),
                QueryScalarKind.LocalDateTime => new(LocalDateTimeValue(literal), basis.OffsetSeconds),
                QueryScalarKind.Date => new(new(DateValue(literal), new(0)), basis.OffsetSeconds),
                QueryScalarKind.ZonedTime => new(new(basis.DateTime.Date, ZonedTimeValue(literal).Time), ZonedTimeValue(literal).OffsetSeconds),
                QueryScalarKind.LocalTime => new(new(basis.DateTime.Date, TimeValue(literal)), basis.OffsetSeconds),
                _ => throw new ArgumentException("Unsupported temporal value.")
            } : throw new ArgumentException("Unsupported temporal value.");
            var startZoned = Normalize(values[0]);
            var endZoned = Normalize(values[1]);
            if (startZoned.ZoneId is not null && startZoned.ZoneId == endZoned.ZoneId && function is not ("DURATION.INSECONDS" or "DURATION_BETWEEN"))
            {
                var calendar = LocalDateTimeDifference(function, startZoned.DateTime, endZoned.DateTime);
                if (function != "DURATION.BETWEEN")
                {
                    return calendar;
                }
                var duration = DurationValue((LiteralValue)calendar);
                var shifted = ZonedDateTimeValue((LiteralValue)ShiftZonedDateTime(ZonedDateTimeLiteral(startZoned), new(duration.Months, duration.Days, 0, 0), false));
                decimal seconds = endZoned.EpochSeconds - shifted.EpochSeconds + (endZoned.DateTime.Time.Nanosecond - shifted.DateTime.Time.Nanosecond) / 1_000_000_000m;
                return DurationLiteral(DurationComponents(duration.Months, duration.Days, seconds));
            }
            return LocalDateTimeDifference(function, startZoned.DateTime, endZoned.DateTime, startZoned.OffsetSeconds - endZoned.OffsetSeconds);
        }
        if (values[0] is not LiteralValue { ScalarKind: QueryScalarKind.Date } first ||
            values[1] is not LiteralValue { ScalarKind: QueryScalarKind.Date } second)
        {
            if (values.All(value => value is LiteralValue { ScalarKind: QueryScalarKind.Date or QueryScalarKind.LocalTime or QueryScalarKind.LocalDateTime }) &&
                values.Any(value => value is LiteralValue { ScalarKind: QueryScalarKind.Date or QueryScalarKind.LocalDateTime }))
            {
                CalendarDate? DatePart(QueryValue value) => value is LiteralValue { ScalarKind: QueryScalarKind.Date } date ? DateValue(date) :
                    value is LiteralValue { ScalarKind: QueryScalarKind.LocalDateTime } datetime ? LocalDateTimeValue(datetime).Date : null;
                LocalTime TimePart(QueryValue value) => value is LiteralValue { ScalarKind: QueryScalarKind.LocalTime } time ? TimeValue(time) :
                    value is LiteralValue { ScalarKind: QueryScalarKind.LocalDateTime } datetime ? LocalDateTimeValue(datetime).Time : new(0);
                CalendarDate startDate = DatePart(values[0]) ?? DatePart(values[1])!;
                CalendarDate endDate = DatePart(values[1]) ?? startDate;
                return LocalDateTimeDifference(function, new(startDate, TimePart(values[0])), new(endDate, TimePart(values[1])));
            }
            if (values.All(value => value is LiteralValue { ScalarKind: QueryScalarKind.LocalTime or QueryScalarKind.ZonedTime }) && values.Any(value => value is LiteralValue { ScalarKind: QueryScalarKind.ZonedTime }))
            {
                if (function is "DURATION.INDAYS" or "DURATION.INMONTHS")
                {
                    throw new ArgumentException("Calendar differences require dates.");
                }
                int offset = values.OfType<LiteralValue>().Where(value => value.ScalarKind == QueryScalarKind.ZonedTime).Select(value => ZonedTimeValue(value).OffsetSeconds).First();
                long Nanos(QueryValue value) => value is LiteralValue { ScalarKind: QueryScalarKind.ZonedTime } zoned ? ZonedTimeValue(zoned).Time.NanosecondsOfDay - ZonedTimeValue(zoned).OffsetSeconds * 1_000_000_000L : TimeValue((LiteralValue)value).NanosecondsOfDay - offset * 1_000_000_000L;
                return DurationLiteral(DurationComponents(0, 0, (Nanos(values[1]) - Nanos(values[0])) / 1_000_000_000m));
            }
            if (values[0] is LiteralValue { ScalarKind: QueryScalarKind.LocalTime } startTime &&
                values[1] is LiteralValue { ScalarKind: QueryScalarKind.LocalTime } endTime)
            {
                long nanos = TimeValue(endTime).NanosecondsOfDay - TimeValue(startTime).NanosecondsOfDay;
                if (function is "DURATION.INDAYS" or "DURATION.INMONTHS")
                {
                    throw new ArgumentException("Calendar differences require dates; local times have no days or months.");
                }
                return DurationLiteral(DurationComponents(0, 0, nanos / 1_000_000_000m));
            }
            throw new ArgumentException("Duration differences require supported temporal values.");
        }
        CalendarDate start = DateValue(first), end = DateValue(second);
        long days = end.EpochDay - start.EpochDay;
        if (function is "DURATION.INDAYS" or "DURATION_BETWEEN")
        {
            return DurationLiteral(new(0, days, 0, 0));
        }
        if (function == "DURATION.INSECONDS")
        {
            return DurationLiteral(new(0, 0, checked(days * 86_400), 0));
        }
        long months = ((long)end.Year - start.Year) * 12 + end.Month - start.Month;
        if (months > 0 && end.Day < start.Day)
        {
            months--;
        }
        else if (months < 0 && end.Day > start.Day)
        {
            months++;
        }
        long remainder = function == "DURATION.INMONTHS" ? 0 : end.EpochDay - ShiftDateMonths(start, months).EpochDay;
        return DurationLiteral(new(months, remainder, 0, 0), function == "DURATION.INMONTHS" ? DatabaseValue.Duration.YearToMonthDatatype : null);
    }

    private static QueryValue DurationArithmetic(string function, QueryValue[] values)
    {
        if (values.OfType<LiteralValue>().Any(value => value.ScalarKind == QueryScalarKind.Duration &&
            DatabaseValue.Duration.QualifierFor(value.DatatypeIdentifier) == DatabaseDurationQualifier.Unqualified))
        {
            throw new ArgumentException("Duration arithmetic requires an explicit year-to-month or day-to-second qualifier.");
        }
        bool IsDuration(QueryValue value) => value is LiteralValue { ScalarKind: QueryScalarKind.Duration };
        CalendarDuration Read(QueryValue value) => DurationValue((LiteralValue)value);
        decimal Seconds(CalendarDuration value) => value.Seconds + value.Nanoseconds / 1_000_000_000m;
        if (values.Length == 1 && IsDuration(values[0]) && function == "ABS")
        {
            CalendarDuration duration = Read(values[0]);
            decimal seconds = Seconds(duration);
            if (duration.Months != 0 && (duration.Days != 0 || seconds != 0))
            {
                throw new ArgumentException("ABS currently requires a year-month or day-time duration, without mixed qualifiers.");
            }
            if (duration.Months != 0)
            {
                return DurationLiteral(DurationComponents(Math.Abs((decimal)duration.Months), 0, 0), ((LiteralValue)values[0]).DatatypeIdentifier);
            }
            // Normalize the day-time value before taking its absolute value:
            // individual components can have opposing signs after arithmetic.
            BigInteger totalNanoseconds = ((BigInteger)duration.Days * 86_400 + duration.Seconds) * 1_000_000_000
                + duration.Nanoseconds;
            BigInteger days = BigInteger.DivRem(BigInteger.Abs(totalNanoseconds), 86_400_000_000_000, out var remainder);
            return DurationLiteral(DurationComponents(0, (decimal)days, (decimal)remainder / 1_000_000_000m), ((LiteralValue)values[0]).DatatypeIdentifier);
        }
        if (values.Length == 1 && IsDuration(values[0]) && function is "unary+" or "unary-")
        {
            if (function == "unary+")
            {
                return values[0];
            }
            CalendarDuration duration = Read(values[0]);
            return DurationLiteral(DurationComponents(-(decimal)duration.Months,
                -(decimal)duration.Days, -Seconds(duration)), ((LiteralValue)values[0]).DatatypeIdentifier);
        }
        if (values.Length == 2 && function is "+" or "-")
        {
            if (values[0] is LiteralValue { ScalarKind: QueryScalarKind.LocalDateTime } datetime && IsDuration(values[1]))
            {
                return ShiftLocalDateTime(datetime, Read(values[1]), function == "-");
            }
            if (function == "+" && IsDuration(values[0]) && values[1] is LiteralValue { ScalarKind: QueryScalarKind.LocalDateTime } rightDateTime)
            {
                return ShiftLocalDateTime(rightDateTime, Read(values[0]), false);
            }
            if (values[0] is LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime } zonedDateTime && IsDuration(values[1]))
            {
                return ShiftZonedDateTime(zonedDateTime, Read(values[1]), function == "-");
            }
            if (function == "+" && IsDuration(values[0]) && values[1] is LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime } rightZonedDateTime)
            {
                return ShiftZonedDateTime(rightZonedDateTime, Read(values[0]), false);
            }
            if (values[0] is LiteralValue { ScalarKind: QueryScalarKind.ZonedTime } zonedTime && IsDuration(values[1]))
            {
                return ShiftZonedTime(zonedTime, Read(values[1]), function == "-");
            }
            if (function == "+" && IsDuration(values[0]) && values[1] is LiteralValue { ScalarKind: QueryScalarKind.ZonedTime } rightZonedTime)
            {
                return ShiftZonedTime(rightZonedTime, Read(values[0]), false);
            }
            if (values[0] is LiteralValue { ScalarKind: QueryScalarKind.LocalTime } time && IsDuration(values[1]))
            {
                return ShiftLocalTime(time, Read(values[1]), function == "-");
            }
            if (function == "+" && IsDuration(values[0]) && values[1] is LiteralValue { ScalarKind: QueryScalarKind.LocalTime } rightTime)
            {
                return ShiftLocalTime(rightTime, Read(values[0]), false);
            }
            int sign = function == "+" ? 1 : -1;
            if (IsDuration(values[0]) && IsDuration(values[1]))
            {
                _ = CompareDurationValues((LiteralValue)values[0], (LiteralValue)values[1]);
                CalendarDuration a = Read(values[0]), b = Read(values[1]);
                return DurationLiteral(DurationComponents((decimal)a.Months + sign * (decimal)b.Months,
                    (decimal)a.Days + sign * (decimal)b.Days, Seconds(a) + sign * Seconds(b)), ((LiteralValue)values[0]).DatatypeIdentifier);
            }
            int dateIndex = values[0] is LiteralValue { ScalarKind: QueryScalarKind.Date } ? 0 : 1;
            if (values[dateIndex] is LiteralValue { ScalarKind: QueryScalarKind.Date } date &&
                IsDuration(values[1 - dateIndex]) && (dateIndex == 0 || sign == 1))
            {
                var duration = Read(values[1 - dateIndex]);
                CalendarDate shifted = ShiftDateMonths(DateValue(date), (BigInteger)sign * duration.Months);
                BigInteger epoch = shifted.EpochDay + sign * ((BigInteger)duration.Days + duration.Seconds / 86_400);
                if (epoch < Int64.MinValue || epoch > Int64.MaxValue)
                {
                    throw new ArgumentException("Calendar arithmetic exceeds the date range.");
                }
                return DateLiteral(CalendarDate.FromEpochDay((long)epoch));
            }
        }
        if (values.Length == 2 && function is "*" or "/")
        {
            int durationIndex = IsDuration(values[0]) ? 0 : 1;
            if (IsDuration(values[durationIndex]) && (function == "*" || durationIndex == 0) &&
                Number(values[1 - durationIndex], out decimal factor))
            {
                CalendarDuration duration = Read(values[durationIndex]);
                if (function == "/")
                {
                    if (factor == 0)
                    {
                        throw GqlDataError("Duration division requires a nonzero divisor.", "22012");
                    }
                    return DurationLiteral(DurationComponents(duration.Months / factor, duration.Days / factor, Seconds(duration) / factor), ((LiteralValue)values[durationIndex]).DatatypeIdentifier);
                }
                return DurationLiteral(DurationComponents(duration.Months * factor, duration.Days * factor, Seconds(duration) * factor), ((LiteralValue)values[durationIndex]).DatatypeIdentifier);
            }
        }
        throw new ArgumentException("Unsupported duration arithmetic operands.");
    }
}
