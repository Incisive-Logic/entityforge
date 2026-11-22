using System.Globalization;
using System.Text.RegularExpressions;

namespace EntityForge.Values;

/// <summary>An immutable date in the proleptic Gregorian calendar, including year zero and signed extended years.</summary>
public sealed record CalendarDate : IComparable<CalendarDate>
{
    /// <summary>The smallest supported calendar year.</summary>
    public const int MinimumYear = -999_999_999;

    /// <summary>The largest supported calendar year.</summary>
    public const int MaximumYear = 999_999_999;
    private const long UnixEpochDay = 719_528;

    /// <summary>Constructs and validates a calendar date.</summary>
    /// <param name="year">Signed year, including zero, between the supported year bounds.</param>
    /// <param name="month">Month from one to twelve.</param>
    /// <param name="day">Day from one to the number of days in the selected month.</param>
    /// <exception cref="ArgumentOutOfRangeException">A component is outside the calendar range.</exception>
    public CalendarDate(int year, int month, int day)
    {
        if (year is < MinimumYear or > MaximumYear)
        {
            throw new ArgumentOutOfRangeException(nameof(year));
        }

        if (month is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(month));
        }

        if (day < 1 || day > DaysInMonth(year, month))
        {
            throw new ArgumentOutOfRangeException(nameof(day));
        }

        Year = year;
        Month = month;
        Day = day;
    }

    /// <summary>The signed Gregorian year.</summary>
    public int Year
    {
        get;
    }

    /// <summary>The month from one to twelve.</summary>
    public int Month
    {
        get;
    }

    /// <summary>The day of the month.</summary>
    public int Day
    {
        get;
    }

    /// <summary>The day of the year, beginning with one.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int DayOfYear => Enumerable.Range(1, Month - 1).Sum(month => DaysInMonth(Year, month)) + Day;

    /// <summary>The number of whole calendar days since 1970-01-01.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long EpochDay => BeforeYear(Year) + DayOfYear - 1 - UnixEpochDay;

    /// <summary>The ISO weekday, Monday one through Sunday seven.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int DayOfWeek => (int)(((EpochDay + 3) % 7 + 7) % 7) + 1;

    /// <summary>Returns the number of days in a Gregorian month.</summary>
    /// <param name="year">The signed calendar year.</param>
    /// <param name="month">The month from one to twelve.</param>
    /// <returns>Twenty-eight through thirty-one days.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The year or month is outside the supported range.</exception>
    public static int DaysInMonth(int year, int month)
    {
        if (year is < MinimumYear or > MaximumYear)
        {
            throw new ArgumentOutOfRangeException(nameof(year));
        }

        return month switch
        {
            2 => year % 4 == 0 && (year % 100 != 0 || year % 400 == 0) ? 29 : 28,
            4 or 6 or 9 or 11 => 30,
            1 or 3 or 5 or 7 or 8 or 10 or 12 => 31,
            _ => throw new ArgumentOutOfRangeException(nameof(month))
        };
    }

    /// <summary>Constructs a date from its signed Unix epoch day.</summary>
    /// <param name="epochDay">Whole calendar days since 1970-01-01.</param>
    /// <returns>The corresponding calendar date.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The day is outside the supported calendar range.</exception>
    public static CalendarDate FromEpochDay(long epochDay)
    {
        if (epochDay < BeforeYear(MinimumYear) - UnixEpochDay || epochDay >= BeforeYear((long)MaximumYear + 1) - UnixEpochDay)
        {
            throw new ArgumentOutOfRangeException(nameof(epochDay));
        }

        long absolute = epochDay + UnixEpochDay;
        int lower = MinimumYear, upper = MaximumYear;
        while (lower < upper)
        {
            int middle = lower + (int)(((long)upper - lower + 1) / 2);
            if (BeforeYear(middle) <= absolute)
            {
                lower = middle;
            }
            else
            {
                upper = middle - 1;
            }
        }
        int remaining = (int)(absolute - BeforeYear(lower)), month = 1;
        while (remaining >= DaysInMonth(lower, month))
        {
            remaining -= DaysInMonth(lower, month++);
        }

        return new(lower, month, remaining + 1);
    }

    /// <summary>Parses a calendar date in canonical ISO year-month-day form.</summary>
    /// <param name="text">Four-digit year or signed extended year, followed by two-digit month and day.</param>
    /// <returns>The validated calendar date.</returns>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="FormatException">The text is not a supported calendar date.</exception>
    public static CalendarDate Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = Regex.Match(text, @"\A(?<year>[+-][0-9]{4,9}|[0-9]{4})-(?<month>[0-9]{2})-(?<day>[0-9]{2})\z", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new FormatException("Expected an ISO calendar date.");
        }

        try
        {
            return new(Int32.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture),
                Int32.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture),
                Int32.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture));
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new FormatException("Invalid Gregorian date.", error);
        }
    }

    /// <summary>Compares dates chronologically; a non-null date sorts after null.</summary>
    /// <param name="other">The date to compare.</param>
    /// <returns>A negative, zero or positive integer for earlier, equal or later dates.</returns>
    public int CompareTo(CalendarDate? other) => other is null ? 1 : EpochDay.CompareTo(other.EpochDay);

    /// <summary>Formats the date using invariant ISO calendar notation.</summary>
    /// <returns>A year-month-day string, with a sign for negative or extended positive years.</returns>
    public override string ToString() => (Year < 0 ? "-" + (-Year).ToString("D4", CultureInfo.InvariantCulture) :
        Year > 9999 ? "+" + Year.ToString(CultureInfo.InvariantCulture) : Year.ToString("D4", CultureInfo.InvariantCulture)) +
        "-" + Month.ToString("D2", CultureInfo.InvariantCulture) + "-" + Day.ToString("D2", CultureInfo.InvariantCulture);

    private static long BeforeYear(long year) => 365 * year + Floor(year + 3, 4) - Floor(year + 99, 100) + Floor(year + 399, 400);

    private static long Floor(long value, long divisor) => value >= 0 ? value / divisor : (value - divisor + 1) / divisor;
}
