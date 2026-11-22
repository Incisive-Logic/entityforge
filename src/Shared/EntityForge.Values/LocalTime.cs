using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EntityForge.Values;

/// <summary>A time of day with nanosecond precision and no date, offset or time zone.</summary>
public sealed record LocalTime : IComparable<LocalTime>
{
    /// <summary>The number of nanoseconds in a 24-hour day.</summary>
    public const long NanosecondsPerDay = 86_400_000_000_000;

    /// <summary>Constructs a time from its distance after midnight.</summary>
    /// <param name="nanosecondsOfDay">Nanoseconds after midnight, from zero through 86,399,999,999,999.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside one day.</exception>
    public LocalTime(long nanosecondsOfDay)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nanosecondsOfDay);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(nanosecondsOfDay, NanosecondsPerDay);
        NanosecondsOfDay = nanosecondsOfDay;
    }

    /// <summary>Gets the exact distance after midnight in nanoseconds.</summary>
    public long NanosecondsOfDay
    {
        get;
    }

    /// <summary>Gets the hour, from 0 through 23.</summary>
    [JsonIgnore]
    public int Hour => (int)(NanosecondsOfDay / 3_600_000_000_000);

    /// <summary>Gets the minute within the hour, from 0 through 59.</summary>
    [JsonIgnore]
    public int Minute => (int)(NanosecondsOfDay / 60_000_000_000 % 60);

    /// <summary>Gets the second within the minute, from 0 through 59.</summary>
    [JsonIgnore]
    public int Second => (int)(NanosecondsOfDay / 1_000_000_000 % 60);

    /// <summary>Gets the nanosecond within the second, from 0 through 999,999,999.</summary>
    [JsonIgnore]
    public int Nanosecond => (int)(NanosecondsOfDay % 1_000_000_000);

    /// <summary>Compares times by their distance after midnight; null sorts first.</summary>
    /// <param name="other">The time to compare.</param>
    /// <returns>A negative, zero or positive comparison result.</returns>
    public int CompareTo(LocalTime? other) => other is null ? 1 : NanosecondsOfDay.CompareTo(other.NanosecondsOfDay);

    /// <summary>Formats an ISO time with seconds and an optional exact fractional second.</summary>
    /// <returns>Canonical invariant text.</returns>
    public override string ToString() => String.Create(CultureInfo.InvariantCulture, $"{Hour:00}:{Minute:00}:{Second:00}") +
        (Nanosecond == 0 ? "" : "." + Nanosecond.ToString("D9", CultureInfo.InvariantCulture).TrimEnd('0'));

    /// <summary>Parses an ISO basic or extended time, with an optional T prefix and up to nine fractional digits.</summary>
    /// <param name="text">A time containing an hour, optional minute and optional second.</param>
    /// <returns>The exact local time.</returns>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="FormatException">The syntax or component ranges are invalid.</exception>
    public static LocalTime Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = Regex.Match(text, @"\AT?(?<h>[0-9]{2})(?:(?<sep>:?)(?<m>[0-9]{2})(?:\k<sep>(?<s>[0-9]{2})(?:[.,](?<f>[0-9]{1,9}))?)?)?\z", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new FormatException("Invalid ISO local time.");
        }
        int Read(string name) => match.Groups[name].Success ? Int32.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture) : 0;
        int hour = Read("h"), minute = Read("m"), second = Read("s");
        if (hour > 23 || minute > 59 || second > 59)
        {
            throw new FormatException("Local time components are outside their ranges.");
        }
        int nano = match.Groups["f"].Success ? Int32.Parse(match.Groups["f"].Value.PadRight(9, '0'), CultureInfo.InvariantCulture) : 0;
        return new(((hour * 60L + minute) * 60 + second) * 1_000_000_000 + nano);
    }
}
