using EntityForge.Query.Values;
using EntityForge.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static QueryValue ReadRealtimeClock(string function, QueryValue? argument = null)
    {
        DateTime utc = DateTime.UtcNow;
        var date = new CalendarDate(utc.Year, utc.Month, utc.Day);
        var time = new LocalTime(utc.TimeOfDay.Ticks * 100);
        var value = new ZonedDateTime(new(date, time), 0);
        if (argument is not null)
        {
            var (offset, region) = DateTimeZone(argument);
            value = region is null ? value.WithOffset(offset) : GraphTimeZones.AtInstant(value, region);
        }
        return function switch
        {
            "DATETIME.REALTIME" or "ZONED_DATETIME.REALTIME" => ZonedDateTimeLiteral(value),
            "TIME.REALTIME" or "ZONED_TIME.REALTIME" => ZonedTimeLiteral(new(value.DateTime.Time, value.OffsetSeconds)),
            "DATE.REALTIME" => DateLiteral(value.DateTime.Date),
            "LOCALTIME.REALTIME" or "LOCAL_TIME.REALTIME" => TimeLiteral(value.DateTime.Time),
            "LOCALDATETIME.REALTIME" or "LOCAL_DATETIME.REALTIME" => LocalDateTimeLiteral(value.DateTime),
            _ => throw new ArgumentException("Unknown realtime temporal function.")
        };
    }
}
