using System.Globalization;
using EntityForge.Query.Values;
using EntityForge.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static ZonedTime ZonedTimeValue(LiteralValue value) => ZonedTime.Parse(value.LexicalValue);

    private static LiteralValue ZonedTimeLiteral(ZonedTime value) => new(value.ToString(), QueryScalarKind.ZonedTime);

    private static int ReadFixedOffset(QueryValue value)
    {
        if (value is not LiteralValue { ScalarKind: QueryScalarKind.String } name)
        {
            throw new ArgumentException("The timezone must be a fixed UTC offset string.");
        }
        try
        {
            return name.LexicalValue is "UTC" or "GMT" ? 0 : ZonedTime.ParseOffset(name.LexicalValue);
        }
        catch (FormatException error)
        {
            throw new ArgumentException("A fixed UTC offset is required.", error);
        }
    }

    private static QueryValue ConstructZonedTime(QueryValue input)
    {
        if (input is NullValue or UnboundValue)
        {
            return UnboundValue.Instance;
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime } zonedDateTime)
        {
            var value = ZonedDateTimeValue(zonedDateTime);
            return ZonedTimeLiteral(new(value.DateTime.Time, value.OffsetSeconds));
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.ZonedTime })
        {
            return input;
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.String } text)
        {
            try
            {
                return ZonedTimeLiteral(ZonedTime.Parse(text.LexicalValue));
            }
            catch (FormatException error)
            {
                throw new ArgumentException("Invalid zoned time string.", error);
            }
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.LocalTime or QueryScalarKind.LocalDateTime })
        {
            return ZonedTimeLiteral(new(TimeValue((LiteralValue)ConstructLocalTime(input)), 0));
        }
        if (input is not MapValue map)
        {
            throw new ArgumentException("TIME requires a temporal value, ISO string or component map.");
        }
        var fields = new Dictionary<string, QueryValue>(map.Values, StringComparer.Ordinal);
        ZonedTime? basis = fields.TryGetValue("time", out var source) && source is LiteralValue { ScalarKind: QueryScalarKind.ZonedTime or QueryScalarKind.ZonedDateTime }
            ? ZonedTimeValue((LiteralValue)ConstructZonedTime(source)) : null;
        int offset = basis?.OffsetSeconds ?? 0;
        if (fields.Remove("timezone", out var zone))
        {
            offset = ReadFixedOffset(zone);
        }
        if (basis is not null)
        {
            long nanos = (basis.Time.NanosecondsOfDay + (offset - basis.OffsetSeconds) * 1_000_000_000L) % LocalTime.NanosecondsPerDay;
            if (nanos < 0)
            {
                nanos += LocalTime.NanosecondsPerDay;
            }
            fields["time"] = TimeLiteral(new(nanos));
        }
        return ZonedTimeLiteral(new(TimeValue((LiteralValue)ConstructLocalTime(new MapValue(fields))), offset));
    }

    private static QueryValue ZonedTimeProperty(LiteralValue literal, QueryValue key)
    {
        var time = ZonedTimeValue(literal);
        if (key is LiteralValue { ScalarKind: QueryScalarKind.String } name)
        {
            if (name.LexicalValue is "timezone" or "offset")
            {
                return new LiteralValue(time.Offset, QueryScalarKind.String);
            }
            if (name.LexicalValue is "offsetSeconds" or "offsetMinutes")
            {
                return new LiteralValue((name.LexicalValue == "offsetSeconds" ? time.OffsetSeconds : time.OffsetSeconds / 60).ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
            }
        }
        return LocalTimeProperty(TimeLiteral(time.Time), key);
    }

    private static QueryValue ShiftZonedTime(LiteralValue literal, CalendarDuration duration, bool subtract)
    {
        var time = ZonedTimeValue(literal);
        return ZonedTimeLiteral(new(TimeValue((LiteralValue)ShiftLocalTime(TimeLiteral(time.Time), duration, subtract)), time.OffsetSeconds));
    }

    private static QueryValue TruncateZonedTime(QueryValue[] values)
    {
        if (values[1] is not LiteralValue { ScalarKind: QueryScalarKind.ZonedTime or QueryScalarKind.ZonedDateTime or QueryScalarKind.LocalTime or QueryScalarKind.LocalDateTime })
        {
            throw new ArgumentException("TIME.TRUNCATE requires a zoned time.");
        }
        var time = ZonedTimeValue((LiteralValue)ConstructZonedTime(values[1]));
        var fields = values.Length == 3 && values[2] is MapValue map
            ? new Dictionary<string, QueryValue>(map.Values, StringComparer.Ordinal) : new Dictionary<string, QueryValue>(StringComparer.Ordinal);
        if (values.Length == 3 && values[2] is not (MapValue or NullValue or UnboundValue))
        {
            throw new ArgumentException("Truncation overrides require a map.");
        }
        bool hasZone = fields.Remove("timezone", out var zone);
        var truncated = (LiteralValue)TruncateLocalTime([values[0], TimeLiteral(time.Time), new MapValue(fields)]);
        return ZonedTimeLiteral(new(TimeValue(truncated), hasZone ? ReadFixedOffset(zone!) : time.OffsetSeconds));
    }
}
