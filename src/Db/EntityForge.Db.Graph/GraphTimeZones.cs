using EntityForge.Values;

namespace EntityForge.Db.Graph;

// Timezone rules stay in the execution layer. Stored values retain both the
// regional identity and resolved offset, so replay never reinterprets an instant.
internal static class GraphTimeZones
{
    internal static string DataVersion => NodaTime.DateTimeZoneProviders.Tzdb.VersionId;

    private static NodaTime.DateTimeZone Find(string id) => NodaTime.DateTimeZoneProviders.Tzdb.GetZoneOrNull(id)
        ?? throw new ArgumentException("Unknown IANA timezone: " + id);

    // TZDB's final recurring rules repeat with the Gregorian 400-year cycle.
    // Dates before the provider's calendar range use its initial fixed offset.
    private static int LookupYear(int year) => year > 9999 ? 9200 + year % 400 : year;

    private static NodaTime.LocalDateTime Lookup(LocalDateTime value) => new NodaTime.LocalDateTime(
        LookupYear(value.Date.Year), value.Date.Month, value.Date.Day, value.Time.Hour, value.Time.Minute, value.Time.Second)
        .PlusNanoseconds(value.Time.Nanosecond);

    internal static ZonedDateTime Resolve(LocalDateTime local, string zoneId, int? preferredOffset = null)
    {
        var zone = Find(zoneId);
        if (local.Date.Year < -9998)
        {
            return new(local, zone.GetUtcOffset(NodaTime.Instant.MinValue).Seconds, zoneId);
        }
        var lookup = Lookup(local);
        var mapping = zone.MapLocal(lookup);
        var resolved = mapping.Count switch
        {
            0 => zone.AtLeniently(lookup),
            2 when preferredOffset == mapping.LateInterval.WallOffset.Seconds => mapping.Last(),
            _ => mapping.First()
        };
        int shiftedDays = NodaTime.Period.Between(lookup.Date, resolved.Date, NodaTime.PeriodUnits.Days).Days;
        return new(new(CalendarDate.FromEpochDay(local.Date.EpochDay + shiftedDays), new(resolved.TimeOfDay.NanosecondOfDay)), resolved.Offset.Seconds, zoneId);
    }

    internal static ZonedDateTime AtInstant(ZonedDateTime value, string zoneId)
    {
        var zone = Find(zoneId);
        var utc = ZonedDateTime.FromEpochSeconds(value.EpochSeconds, value.DateTime.Time.Nanosecond).DateTime;
        int offset = utc.Date.Year < -9998 ? zone.GetUtcOffset(NodaTime.Instant.MinValue).Seconds :
            zone.GetUtcOffset(Lookup(utc).InUtc().ToInstant()).Seconds;
        return new(value.WithOffset(offset).DateTime, offset, zoneId);
    }
}
