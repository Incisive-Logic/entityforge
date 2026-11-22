namespace EntityForge.Values;

/// <summary>A Gregorian date and nanosecond time with a resolved UTC offset and optional regional zone identity.</summary>
public sealed record ZonedDateTime : IComparable<ZonedDateTime>
{
    /// <summary>Constructs a date and time with an offset east of UTC.</summary>
    /// <param name="dateTime">The local date and time.</param>
    /// <param name="offsetSeconds">The signed offset in seconds, within eighteen hours of UTC.</param>
    /// <param name="zoneId">An optional zone identity retained alongside the resolved offset; null or empty denotes a fixed offset.</param>
    /// <exception cref="ArgumentNullException">The local date and time is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The offset is outside its supported range.</exception>
    /// <exception cref="ArgumentException">The zone identity is malformed.</exception>
    public ZonedDateTime(LocalDateTime dateTime, int offsetSeconds, string? zoneId = null)
    {
        ArgumentNullException.ThrowIfNull(dateTime);
        ArgumentOutOfRangeException.ThrowIfLessThan(offsetSeconds, -64_800);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offsetSeconds, 64_800);
        DateTime = dateTime;
        OffsetSeconds = offsetSeconds;
        if (!String.IsNullOrEmpty(zoneId) && (zoneId.Length > 255 || zoneId.Any(c => !Char.IsAsciiLetterOrDigit(c) && c is not ('/' or '_' or '-' or '+' or ':' or '.'))))
        {
            throw new ArgumentException("Invalid timezone identity.", nameof(zoneId));
        }
        ZoneId = String.IsNullOrEmpty(zoneId) ? null : zoneId;
    }

    /// <summary>Gets the immutable local date and time.</summary>
    public LocalDateTime DateTime
    {
        get;
    }

    /// <summary>Gets the signed offset east of UTC, in seconds.</summary>
    public int OffsetSeconds
    {
        get;
    }

    /// <summary>Gets the optional regional zone identity. The stored offset remains authoritative for the represented instant.</summary>
    public string? ZoneId
    {
        get;
    }

    /// <summary>Gets the whole seconds since 1970-01-01T00:00:00Z, rounded toward negative infinity.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long EpochSeconds => DateTime.Date.EpochDay * 86_400 + DateTime.Time.NanosecondsOfDay / 1_000_000_000 - OffsetSeconds;

    /// <summary>Compares instants first, then local date/time fields; null sorts first.</summary>
    /// <param name="other">The date and time to compare.</param>
    /// <returns>A negative, zero or positive comparison result.</returns>
    public int CompareTo(ZonedDateTime? other)
    {
        if (other is null)
        {
            return 1;
        }
        int comparison = EpochSeconds.CompareTo(other.EpochSeconds);
        if (comparison == 0)
        {
            comparison = DateTime.Time.Nanosecond.CompareTo(other.DateTime.Time.Nanosecond);
        }
        if (comparison == 0)
        {
            comparison = DateTime.CompareTo(other.DateTime);
        }
        return comparison == 0 ? StringComparer.Ordinal.Compare(ZoneId, other.ZoneId) : comparison;
    }

    /// <summary>Formats the local date/time and UTC offset, retaining nanosecond precision.</summary>
    /// <returns>Invariant ISO date/time text.</returns>
    public override string ToString() => DateTime.Date + "T" + new ZonedTime(DateTime.Time, OffsetSeconds) + (ZoneId is null ? "" : "[" + ZoneId + "]");

    /// <summary>Parses an ISO date/time and resolved offset, with an optional bracketed zone identity. No timezone rules are consulted.</summary>
    /// <param name="text">The date/time text.</param>
    /// <returns>The local date/time and offset.</returns>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="FormatException">The date/time syntax is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A component exceeds its supported range.</exception>
    public static ZonedDateTime Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? zone = null;
        int bracket = text.IndexOf('[');
        if (bracket >= 0)
        {
            if (!text.EndsWith(']') || bracket == text.Length - 2)
            {
                throw new FormatException("Invalid bracketed timezone identity.");
            }
            zone = text[(bracket + 1)..^1];
            text = text[..bracket];
        }
        int separator = text.IndexOf('T');
        if (separator < 0)
        {
            if (zone is not null)
            {
                throw new FormatException("A regional datetime requires a resolved time and offset.");
            }
            return new(new(CalendarDate.Parse(text), new(0)), 0);
        }
        if (zone is not null && text[(separator + 1)..].IndexOfAny(['Z', '+', '-']) < 0)
        {
            throw new FormatException("A regional datetime requires a resolved UTC offset.");
        }
        var time = ZonedTime.Parse(text[(separator + 1)..]);
        return new(new(CalendarDate.Parse(text[..separator]), time.Time), time.OffsetSeconds, zone);
    }

    /// <summary>Constructs a UTC value from whole epoch seconds and a fractional nanosecond component.</summary>
    /// <param name="seconds">Whole seconds since the Unix epoch.</param>
    /// <param name="nanosecond">The fractional component from zero through 999,999,999.</param>
    /// <returns>The UTC date/time.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The fraction or resulting date is outside its supported range.</exception>
    public static ZonedDateTime FromEpochSeconds(long seconds, int nanosecond = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nanosecond);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nanosecond, 999_999_999);
        long day = Math.DivRem(seconds, 86_400, out long remainder);
        if (remainder < 0)
        {
            day--;
            remainder += 86_400;
        }
        return new(new(CalendarDate.FromEpochDay(day), new(remainder * 1_000_000_000 + nanosecond)), 0);
    }

    /// <summary>Changes the offset while retaining the represented instant.</summary>
    /// <param name="offsetSeconds">The new signed UTC offset, in seconds.</param>
    /// <returns>The same instant represented using the new local date/time.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The new offset or local date is outside its supported range.</exception>
    public ZonedDateTime WithOffset(int offsetSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(offsetSeconds, -64_800);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offsetSeconds, 64_800);
        return new(FromEpochSeconds(EpochSeconds + offsetSeconds, DateTime.Time.Nanosecond).DateTime, offsetSeconds);
    }
}
