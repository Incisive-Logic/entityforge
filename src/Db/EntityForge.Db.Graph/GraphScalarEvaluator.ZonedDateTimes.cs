using System.Globalization;
using EntityForge.Query.Values;
using EntityForge.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static ZonedDateTime ZonedDateTimeValue(LiteralValue value) => ZonedDateTime.Parse(value.LexicalValue);

    private static LiteralValue ZonedDateTimeLiteral(ZonedDateTime value) => new(value.ToString(), QueryScalarKind.ZonedDateTime);

    private static (int Offset, string? Region) DateTimeZone(QueryValue value)
    {
        if (value is not LiteralValue { ScalarKind: QueryScalarKind.String } text || text.LexicalValue.Length == 0)
        {
            throw new ArgumentException("The timezone must be a non-empty string.");
        }
        return text.LexicalValue[0] is '+' or '-' || text.LexicalValue == "Z" ? (ReadFixedOffset(value), null) : (0, text.LexicalValue);
    }

    private static long TemporalInteger(QueryValue value) => value is LiteralValue { ScalarKind: QueryScalarKind.Integer } integer &&
        Int64.TryParse(integer.LexicalValue, CultureInfo.InvariantCulture, out long number) ? number : throw new ArgumentException("An integer temporal component is required.");

    private static ZonedDateTime EpochDateTime(long value, bool milliseconds, int? nanosecond = null)
    {
        long seconds = milliseconds ? Math.DivRem(value, 1000, out _) : value;
        int nanos = 0;
        if (milliseconds)
        {
            long remainder = value % 1000;
            if (remainder < 0)
            {
                seconds--;
                remainder += 1000;
            }
            nanos = (int)remainder * 1_000_000;
        }
        return ZonedDateTime.FromEpochSeconds(seconds, nanosecond ?? nanos);
    }

    private static QueryValue ConstructZonedDateTime(QueryValue input)
    {
        if (input is NullValue or UnboundValue)
        {
            return UnboundValue.Instance;
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime })
        {
            return input;
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.LocalDateTime } local)
        {
            return ZonedDateTimeLiteral(new(LocalDateTimeValue(local), 0));
        }
        if (input is LiteralValue { ScalarKind: QueryScalarKind.String } text)
        {
            try
            {
                string sourceText = text.LexicalValue;
                string? region = null;
                int bracket = sourceText.IndexOf('[');
                if (bracket >= 0)
                {
                    if (!sourceText.EndsWith(']') || bracket == sourceText.Length - 2)
                    {
                        throw new FormatException("Invalid bracketed timezone.");
                    }
                    region = sourceText[(bracket + 1)..^1];
                    sourceText = sourceText[..bracket];
                }
                int separator = sourceText.IndexOf('T');
                string timeText = separator < 0 ? "00" : sourceText[(separator + 1)..];
                var time = ZonedTime.Parse(timeText);
                var localValue = new LocalDateTime(ParseDate(separator < 0 ? sourceText : sourceText[..separator]), time.Time);
                if (region is null)
                {
                    return ZonedDateTimeLiteral(new(localValue, time.OffsetSeconds));
                }
                bool hasOffset = timeText.IndexOfAny(['Z', '+', '-']) >= 0;
                var resolved = GraphTimeZones.Resolve(localValue, region, hasOffset ? time.OffsetSeconds : null);
                if (hasOffset && (resolved.OffsetSeconds != time.OffsetSeconds || resolved.DateTime != localValue))
                {
                    throw new ArgumentException("Timezone and explicit offset do not match.");
                }
                return ZonedDateTimeLiteral(resolved);
            }
            catch (FormatException error)
            {
                throw new ArgumentException("Invalid zoned datetime string.", error);
            }
        }
        if (input is not MapValue map || map.Values.Count == 0)
        {
            throw new ArgumentException("DATETIME requires an ISO string, temporal value or component map.");
        }
        var fields = new Dictionary<string, QueryValue>(map.Values, StringComparer.Ordinal);
        bool explicitOffset = fields.Remove("timezone", out var zone);
        var (offset, regionId) = explicitOffset ? DateTimeZone(zone!) : (0, (string?)null);
        if (fields.ContainsKey("epochSeconds") || fields.ContainsKey("epochMillis"))
        {
            bool millis = fields.Remove("epochMillis", out var epoch);
            if (!millis)
            {
                fields.Remove("epochSeconds", out epoch);
            }
            int? nanos = fields.Remove("nanosecond", out var fraction) ? checked((int)TemporalInteger(fraction)) : null;
            if (fields.Count != 0)
            {
                throw new ArgumentException("Epoch fields cannot be combined with calendar or clock fields.");
            }
            var instant = EpochDateTime(TemporalInteger(epoch!), millis, nanos);
            return ZonedDateTimeLiteral(regionId is null ? instant.WithOffset(offset) : GraphTimeZones.AtInstant(instant, regionId));
        }
        long dayCarry = 0;
        bool convertTimeInstant = false;
        if (fields.TryGetValue("datetime", out var source) && source is LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime } selected)
        {
            var basis = ZonedDateTimeValue(selected);
            basis = explicitOffset ? regionId is null ? basis.WithOffset(offset) : GraphTimeZones.AtInstant(basis, regionId) : basis;
            offset = basis.OffsetSeconds;
            regionId = basis.ZoneId;
            if (fields.Count == 1)
            {
                return ZonedDateTimeLiteral(basis);
            }
            fields["datetime"] = LocalDateTimeLiteral(basis.DateTime);
        }
        else if (fields.TryGetValue("time", out var clock) && clock is LiteralValue { ScalarKind: QueryScalarKind.ZonedTime or QueryScalarKind.ZonedDateTime } selectedTime)
        {
            var time = ZonedTimeValue((LiteralValue)ConstructZonedTime(selectedTime));
            if (!explicitOffset && selectedTime.ScalarKind == QueryScalarKind.ZonedDateTime)
            {
                regionId = ZonedDateTimeValue(selectedTime).ZoneId;
            }
            convertTimeInstant = explicitOffset && regionId is not null;
            if (convertTimeInstant)
            {
                offset = time.OffsetSeconds;
            }
            offset = explicitOffset ? offset : time.OffsetSeconds;
            long nanos = time.Time.NanosecondsOfDay + (offset - time.OffsetSeconds) * 1_000_000_000L;
            dayCarry = Math.DivRem(nanos, LocalTime.NanosecondsPerDay, out long remainder);
            if (remainder < 0)
            {
                dayCarry--;
                remainder += LocalTime.NanosecondsPerDay;
            }
            fields["time"] = TimeLiteral(new(remainder));
        }
        var localDateTime = LocalDateTimeValue((LiteralValue)ConstructLocalDateTime(new MapValue(fields)));
        localDateTime = new(CalendarDate.FromEpochDay(localDateTime.Date.EpochDay + dayCarry), localDateTime.Time);
        return ZonedDateTimeLiteral(regionId is null ? new(localDateTime, offset) : convertTimeInstant ? GraphTimeZones.AtInstant(new(localDateTime, offset), regionId) :
            GraphTimeZones.Resolve(localDateTime, regionId, offset));
    }

    private static QueryValue ZonedDateTimeProperty(LiteralValue literal, QueryValue key)
    {
        var value = ZonedDateTimeValue(literal);
        if (key is LiteralValue { ScalarKind: QueryScalarKind.String } name)
        {
            if (name.LexicalValue == "timezone" && value.ZoneId is not null)
            {
                return new LiteralValue(value.ZoneId, QueryScalarKind.String);
            }
            if (name.LexicalValue is "epochSeconds" or "epochMillis")
            {
                long number = name.LexicalValue == "epochSeconds"
                    ? value.EpochSeconds + (value.EpochSeconds < 0 && value.DateTime.Time.Nanosecond != 0 ? 1 : 0)
                    : checked(value.EpochSeconds * 1000 + value.DateTime.Time.Nanosecond / 1_000_000);
                return new LiteralValue(number.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
            }
            if (name.LexicalValue is "offset" or "timezone" or "offsetSeconds" or "offsetMinutes")
            {
                return ZonedTimeProperty(ZonedTimeLiteral(new(value.DateTime.Time, value.OffsetSeconds)), key);
            }
        }
        return LocalDateTimeProperty(LocalDateTimeLiteral(value.DateTime), key);
    }

    private static QueryValue ShiftZonedDateTime(LiteralValue literal, CalendarDuration duration, bool subtract)
    {
        var value = ZonedDateTimeValue(literal);
        if (value.ZoneId is not null)
        {
            var calendar = (LiteralValue)ShiftLocalDateTime(LocalDateTimeLiteral(value.DateTime), new(duration.Months, duration.Days, 0, 0), subtract);
            var resolved = duration.Months == 0 && duration.Days == 0 ? value : GraphTimeZones.Resolve(LocalDateTimeValue(calendar), value.ZoneId, value.OffsetSeconds);
            var elapsed = (LiteralValue)ShiftLocalDateTime(LocalDateTimeLiteral(resolved.DateTime), new(0, 0, duration.Seconds, duration.Nanoseconds), subtract);
            return ZonedDateTimeLiteral(GraphTimeZones.AtInstant(new(LocalDateTimeValue(elapsed), resolved.OffsetSeconds), value.ZoneId));
        }
        var shifted = (LiteralValue)ShiftLocalDateTime(LocalDateTimeLiteral(value.DateTime), duration, subtract);
        return ZonedDateTimeLiteral(new(LocalDateTimeValue(shifted), value.OffsetSeconds));
    }

    private static QueryValue TruncateZonedDateTime(QueryValue[] values)
    {
        if (values[1] is not LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime or QueryScalarKind.LocalDateTime or QueryScalarKind.Date } source)
        {
            throw new ArgumentException("DATETIME.TRUNCATE requires a zoned datetime.");
        }
        var value = source.ScalarKind == QueryScalarKind.Date ? new ZonedDateTime(new(DateValue(source), new(0)), 0) : ZonedDateTimeValue((LiteralValue)ConstructZonedDateTime(source));
        var fields = values.Length == 3 && values[2] is MapValue map ? new Dictionary<string, QueryValue>(map.Values, StringComparer.Ordinal) : new Dictionary<string, QueryValue>(StringComparer.Ordinal);
        if (values.Length == 3 && values[2] is not (MapValue or NullValue or UnboundValue))
        {
            throw new ArgumentException("Truncation overrides require a component map.");
        }
        bool zoneOverride = fields.Remove("timezone", out var zone);
        var (offset, region) = zoneOverride ? DateTimeZone(zone!) : (value.OffsetSeconds, value.ZoneId);
        var truncated = (LiteralValue)TruncateLocalDateTime([values[0], source.ScalarKind == QueryScalarKind.Date ? source : LocalDateTimeLiteral(value.DateTime), new MapValue(fields)]);
        return ZonedDateTimeLiteral(region is null ? new(LocalDateTimeValue(truncated), offset) : GraphTimeZones.Resolve(LocalDateTimeValue(truncated), region, zoneOverride ? null : offset));
    }
}
