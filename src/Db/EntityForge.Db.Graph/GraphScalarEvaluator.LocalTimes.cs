using System.Globalization;
using System.Numerics;
using EntityForge.Query.Values;
using EntityForge.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static LocalTime TimeValue(LiteralValue value) => LocalTime.Parse(value.LexicalValue);

    private static LiteralValue TimeLiteral(LocalTime value) => new(value.ToString(), QueryScalarKind.LocalTime);

    private static QueryValue ConstructLocalTime(QueryValue input)
    {
        if (input is NullValue or UnboundValue)
        {
            return UnboundValue.Instance;
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime } zonedDateTime)
        {
            return TimeLiteral(ZonedDateTimeValue(zonedDateTime).DateTime.Time);
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.ZonedTime } zoned)
        {
            return TimeLiteral(ZonedTimeValue(zoned).Time);
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.LocalTime } existing)
        {
            return existing;
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.LocalDateTime } datetime)
        {
            return TimeLiteral(LocalDateTimeValue(datetime).Time);
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.String } text)
        {
            try
            {
                return TimeLiteral(LocalTime.Parse(text.LexicalValue));
            }
            catch (FormatException exception)
            {
                throw new ArgumentException("Invalid local time string.", exception);
            }
        }
        if (input is not MapValue map)
        {
            throw new ArgumentException("LOCALTIME requires a local time, ISO string or component map.");
        }
        string[] fields = ["time", "hour", "minute", "second", "millisecond", "microsecond", "nanosecond"];
        if (map.Values.Keys.Any(key => !fields.Contains(key, StringComparer.Ordinal)))
        {
            throw GqlDataError("Unknown local time component.", "22G05");
        }
        LocalTime? basis = map.Values.TryGetValue("time", out var source)
            ? source is LiteralValue { ScalarKind: QueryScalarKind.LocalTime } time ? TimeValue(time)
                : source is LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime } zonedDateTimeSource ? ZonedDateTimeValue(zonedDateTimeSource).DateTime.Time
                : source is LiteralValue { ScalarKind: QueryScalarKind.ZonedTime } zonedSource ? ZonedTimeValue(zonedSource).Time
                : source is LiteralValue { ScalarKind: QueryScalarKind.LocalDateTime } selected ? LocalDateTimeValue(selected).Time
                : throw new ArgumentException("The time component requires a local time.") : null;
        bool Has(string name) => map.Values.ContainsKey(name);
        bool fraction = Has("millisecond") || Has("microsecond") || Has("nanosecond");
        if (basis is null && (!Has("hour") || (Has("second") || fraction) && !Has("minute") || fraction && !Has("second")))
        {
            throw GqlDataError("Local time components require their preceding hour, minute and second components.", "22G05");
        }
        int Read(string name, int fallback, int maximum)
        {
            if (!map.Values.TryGetValue(name, out var value))
            {
                return fallback;
            }
            if (value is not LiteralValue { ScalarKind: QueryScalarKind.Integer } integer ||
                !Int32.TryParse(integer.LexicalValue, CultureInfo.InvariantCulture, out int number) || number < 0 || number > maximum)
            {
                throw new ArgumentException("Invalid local time component: " + name);
            }
            return number;
        }
        int hour = Read("hour", basis?.Hour ?? 0, 23), minute = Read("minute", basis?.Minute ?? 0, 59), second = Read("second", basis?.Second ?? 0, 59);
        int nano = fraction ? Read("millisecond", 0, 999) * 1_000_000 + Read("microsecond", 0, Has("millisecond") ? 999 : 999_999) * 1000 +
            Read("nanosecond", 0, Has("microsecond") ? 999 : Has("millisecond") ? 999_999 : 999_999_999) : basis?.Nanosecond ?? 0;
        return TimeLiteral(new(((hour * 60L + minute) * 60 + second) * 1_000_000_000 + nano));
    }

    private static QueryValue LocalTimeProperty(LiteralValue literal, QueryValue key)
    {
        if (key is NullValue or UnboundValue)
        {
            return UnboundValue.Instance;
        }
        if (key is not LiteralValue { ScalarKind: QueryScalarKind.String } name)
        {
            throw new ArgumentException("A local time property name must be a string.");
        }
        LocalTime time = TimeValue(literal);
        int value = name.LexicalValue switch
        {
            "hour" => time.Hour,
            "minute" => time.Minute,
            "second" => time.Second,
            "millisecond" => time.Nanosecond / 1_000_000,
            "microsecond" => time.Nanosecond / 1000,
            "nanosecond" => time.Nanosecond,
            _ => throw new ArgumentException("Unknown local time property.")
        };
        return new LiteralValue(value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
    }

    private static QueryValue TruncateLocalTime(QueryValue[] values)
    {
        if (values[1] is LiteralValue { ScalarKind: QueryScalarKind.ZonedTime or QueryScalarKind.LocalDateTime or QueryScalarKind.ZonedDateTime })
        {
            values[1] = ConstructLocalTime(values[1]);
        }
        if (values[0] is not LiteralValue { ScalarKind: QueryScalarKind.String } unit ||
            values[1] is not LiteralValue { ScalarKind: QueryScalarKind.LocalTime } value)
        {
            throw new ArgumentException("LOCALTIME.TRUNCATE requires a unit and a local time.");
        }
        long quantum = unit.LexicalValue switch
        {
            "day" => LocalTime.NanosecondsPerDay,
            "hour" => 3_600_000_000_000,
            "minute" => 60_000_000_000,
            "second" => 1_000_000_000,
            "millisecond" => 1_000_000,
            "microsecond" => 1000,
            _ => throw new ArgumentException("Unsupported LOCALTIME.TRUNCATE unit.")
        };
        LocalTime truncated = new(TimeValue(value).NanosecondsOfDay / quantum * quantum);
        if (values.Length == 2 || values[2] is NullValue or UnboundValue)
        {
            return TimeLiteral(truncated);
        }
        if (values[2] is not MapValue overrides || overrides.Values.ContainsKey("time"))
        {
            throw new ArgumentException("LOCALTIME.TRUNCATE overrides require a component map.");
        }
        var fields = overrides.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (quantum < 1_000_000_000 && fields.Keys.Any(key => key is "millisecond" or "microsecond" or "nanosecond"))
        {
            if (!fields.ContainsKey("millisecond"))
            {
                fields.Add("millisecond", new LiteralValue((truncated.Nanosecond / 1_000_000).ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer));
            }
            if (quantum == 1000 && !fields.ContainsKey("microsecond"))
            {
                fields.Add("microsecond", new LiteralValue((truncated.Nanosecond / 1000 % 1000).ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer));
            }
        }
        fields.Add("time", TimeLiteral(truncated));
        return ConstructLocalTime(new MapValue(fields));
    }

    private static QueryValue ShiftLocalTime(LiteralValue time, CalendarDuration duration, bool subtract)
    {
        BigInteger delta = (BigInteger)duration.Seconds * 1_000_000_000 + duration.Nanoseconds;
        BigInteger nanos = (TimeValue(time).NanosecondsOfDay + (subtract ? -delta : delta)) % LocalTime.NanosecondsPerDay;
        if (nanos.Sign < 0)
        {
            nanos += LocalTime.NanosecondsPerDay;
        }
        return TimeLiteral(new((long)nanos));
    }
}
