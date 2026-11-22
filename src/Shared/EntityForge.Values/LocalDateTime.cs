namespace EntityForge.Values;

/// <summary>A proleptic Gregorian date and nanosecond-precision time without an offset or time zone.</summary>
public sealed record LocalDateTime : IComparable<LocalDateTime>
{
    /// <summary>Constructs a local date and time from validated components.</summary>
    /// <param name="date">The Gregorian date, including signed extended years.</param>
    /// <param name="time">The time of day with nanosecond precision.</param>
    /// <exception cref="ArgumentNullException">Either component is null.</exception>
    public LocalDateTime(CalendarDate date, LocalTime time)
    {
        ArgumentNullException.ThrowIfNull(date);
        ArgumentNullException.ThrowIfNull(time);
        Date = date;
        Time = time;
    }

    /// <summary>Gets the immutable Gregorian date.</summary>
    public CalendarDate Date
    {
        get;
    }

    /// <summary>Gets the immutable local time.</summary>
    public LocalTime Time
    {
        get;
    }

    /// <summary>Compares the date first, followed by the time; null sorts first.</summary>
    /// <param name="other">The value to compare.</param>
    /// <returns>A negative, zero or positive comparison result.</returns>
    public int CompareTo(LocalDateTime? other)
    {
        if (other is null)
        {
            return 1;
        }
        int date = Date.CompareTo(other.Date);
        return date == 0 ? Time.CompareTo(other.Time) : date;
    }

    /// <summary>Formats the date and time as canonical ISO text retaining nanosecond precision.</summary>
    /// <returns>The date, a T separator and the local time.</returns>
    public override string ToString() => Date + "T" + Time;

    /// <summary>Parses a canonical Gregorian date with an optional T-separated local time.</summary>
    /// <param name="text">ISO date and optional time, without an offset or zone.</param>
    /// <returns>The local date and time; an omitted time defaults to midnight.</returns>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="FormatException">The syntax is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The date is outside its supported range.</exception>
    public static LocalDateTime Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int separator = text.IndexOf('T');
        return separator < 0 ? new(CalendarDate.Parse(text), new(0)) :
            new(CalendarDate.Parse(text[..separator]), LocalTime.Parse(text[(separator + 1)..]));
    }
}
