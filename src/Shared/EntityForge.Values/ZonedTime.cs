using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EntityForge.Values;

/// <summary>A nanosecond time of day with a fixed UTC offset, without a calendar date.</summary>
public sealed record ZonedTime : IComparable<ZonedTime>
{
    /// <summary>Constructs a local time and its signed offset east of UTC.</summary>
    /// <param name="time">The local time of day.</param>
    /// <param name="offsetSeconds">The offset in seconds, between -64,800 and 64,800 inclusive.</param>
    /// <exception cref="ArgumentNullException">The local time is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The offset exceeds eighteen hours.</exception>
    public ZonedTime(LocalTime time, int offsetSeconds)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(offsetSeconds, -64_800);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offsetSeconds, 64_800);
        Time = time;
        OffsetSeconds = offsetSeconds;
    }

    /// <summary>Gets the local time of day.</summary>
    public LocalTime Time
    {
        get;
    }

    /// <summary>Gets the signed offset east of UTC, in seconds.</summary>
    public int OffsetSeconds
    {
        get;
    }

    /// <summary>Gets the canonical offset, using Z for UTC and including seconds only when needed.</summary>
    [JsonIgnore]
    public string Offset
    {
        get
        {
            int offset = Math.Abs(OffsetSeconds);
            return offset == 0 ? "Z" : String.Create(CultureInfo.InvariantCulture,
                $"{(OffsetSeconds < 0 ? '-' : '+')}{offset / 3600:00}:{offset / 60 % 60:00}") +
                (offset % 60 == 0 ? "" : ":" + (offset % 60).ToString("D2", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Compares the UTC-adjusted times, then their local times; null sorts first.</summary>
    /// <param name="other">The time to compare.</param>
    /// <returns>A negative, zero or positive comparison result.</returns>
    public int CompareTo(ZonedTime? other)
    {
        if (other is null)
        {
            return 1;
        }
        int comparison = (Time.NanosecondsOfDay - OffsetSeconds * 1_000_000_000L)
            .CompareTo(other.Time.NanosecondsOfDay - other.OffsetSeconds * 1_000_000_000L);
        return comparison != 0 ? comparison : Time.CompareTo(other.Time);
    }

    /// <summary>Formats the local time followed by its canonical UTC offset.</summary>
    /// <returns>Invariant ISO time text, retaining all fractional digits.</returns>
    public override string ToString() => Time + Offset;

    /// <summary>Parses a basic or extended ISO time and optional fixed offset; an omitted offset means UTC.</summary>
    /// <param name="text">The time and optional Z or signed offset.</param>
    /// <returns>The exact local time and offset.</returns>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="FormatException">The syntax or component ranges are invalid.</exception>
    public static ZonedTime Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = Regex.Match(text, @"\A(?<time>[^Z+-]+)(?<offset>Z|[+-][0-9:]+)?\z", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new FormatException("Invalid ISO zoned time.");
        }
        return new(LocalTime.Parse(match.Groups["time"].Value), match.Groups["offset"].Success ? ParseOffset(match.Groups["offset"].Value) : 0);
    }

    /// <summary>Parses Z or a signed offset in hours, hours/minutes or hours/minutes/seconds.</summary>
    /// <param name="text">The UTC offset, with optional consistent colon separators.</param>
    /// <returns>The signed number of seconds east of UTC.</returns>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="FormatException">The offset syntax is invalid or exceeds eighteen hours.</exception>
    public static int ParseOffset(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text == "Z")
        {
            return 0;
        }
        var match = Regex.Match(text, @"\A(?<sign>[+-])(?<h>[0-9]{2})(?:(?<separator>:?)(?<m>[0-9]{2})(?:\k<separator>(?<s>[0-9]{2}))?)?\z", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new FormatException("Invalid UTC offset.");
        }
        int Read(string name) => match.Groups[name].Success ? Int32.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture) : 0;
        int hour = Read("h"), minute = Read("m"), second = Read("s");
        if (hour > 18 || minute > 59 || second > 59 || hour == 18 && (minute != 0 || second != 0))
        {
            throw new FormatException("UTC offset is outside its supported range.");
        }
        return (match.Groups["sign"].Value == "-" ? -1 : 1) * (hour * 3600 + minute * 60 + second);
    }
}
