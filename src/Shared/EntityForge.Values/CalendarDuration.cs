using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace EntityForge.Values;

/// <summary>A calendar duration retaining independent month, day and nanosecond-precision time components.</summary>
/// <remarks>Months are not converted to days, and days are not converted to seconds. Their elapsed length depends on the calendar and time zone where the duration is applied.</remarks>
public sealed record CalendarDuration : IComparable<CalendarDuration>
{
    /// <summary>Creates a duration with a normalized fractional second.</summary>
    /// <param name="months">Signed calendar months.</param>
    /// <param name="days">Signed calendar days.</param>
    /// <param name="seconds">Signed whole seconds, rounded toward negative infinity.</param>
    /// <param name="nanoseconds">The non-negative fractional second, from zero through 999,999,999.</param>
    /// <exception cref="ArgumentOutOfRangeException">The fractional second is outside its normalized range.</exception>
    public CalendarDuration(long months, long days, long seconds, int nanoseconds)
    {
        if (nanoseconds is < 0 or >= 1_000_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(nanoseconds));
        }
        Months = months;
        Days = days;
        Seconds = seconds;
        Nanoseconds = nanoseconds;
    }

    /// <summary>Signed calendar months, independent of the day and time components.</summary>
    public long Months
    {
        get;
    }

    /// <summary>Signed calendar days, independent of the month and time components.</summary>
    public long Days
    {
        get;
    }

    /// <summary>Whole seconds rounded toward negative infinity; add Nanoseconds to obtain the precise time component.</summary>
    public long Seconds
    {
        get;
    }

    /// <summary>The non-negative fractional second in nanoseconds.</summary>
    public int Nanoseconds
    {
        get;
    }

    /// <summary>Orders durations using the Gregorian average month and 24-hour day, then their independent components.</summary>
    /// <param name="other">The duration to order against; null sorts before this value.</param>
    /// <returns>A negative, zero or positive comparison result.</returns>
    /// <remarks>This is a deterministic sorting convention, not an assertion of elapsed time in a particular calendar or timezone.</remarks>
    public int CompareTo(CalendarDuration? other)
    {
        if (other is null)
        {
            return 1;
        }
        BigInteger Estimate(CalendarDuration value) => ((BigInteger)value.Months * 2_629_746 +
            (BigInteger)value.Days * 86_400 + value.Seconds) * 1_000_000_000 + value.Nanoseconds;
        int result = Estimate(this).CompareTo(Estimate(other));
        if (result == 0)
        {
            result = Months.CompareTo(other.Months);
        }

        if (result == 0)
        {
            result = Days.CompareTo(other.Days);
        }

        if (result == 0)
        {
            result = Seconds.CompareTo(other.Seconds);
        }

        return result == 0 ? Nanoseconds.CompareTo(other.Nanoseconds) : result;
    }

    /// <summary>Normalizes fractional month, day and second components without collapsing whole calendar units.</summary>
    /// <param name="months">Calendar months; fractions use the Gregorian average month of 2,629,746 seconds.</param>
    /// <param name="days">Calendar days; fractional days use 86,400 seconds.</param>
    /// <param name="seconds">Time seconds; precision finer than one nanosecond is truncated.</param>
    /// <returns>The normalized duration.</returns>
    /// <exception cref="OverflowException">A normalized component exceeds its signed 64-bit range.</exception>
    public static CalendarDuration FromComponents(decimal months, decimal days, decimal seconds)
    {
        decimal wholeMonths = Decimal.Truncate(months);
        days += (months - wholeMonths) * 2_629_746 / 86_400;
        decimal wholeDays = Decimal.Truncate(days);
        seconds += (days - wholeDays) * 86_400;
        decimal wholeSeconds = Decimal.Truncate(seconds);
        int nanos = (int)Decimal.Truncate((seconds - wholeSeconds) * 1_000_000_000);
        if (nanos < 0)
        {
            wholeSeconds--;
            nanos += 1_000_000_000;
        }
        return new(checked((long)wholeMonths), checked((long)wholeDays), checked((long)wholeSeconds), nanos);
    }

    /// <summary>Parses an ISO duration with unit-labelled components or an alternative numeric date/time representation.</summary>
    /// <param name="text">An ISO duration. Unit-labelled components may be signed and the last supplied component may be fractional. Alternative forms use four-digit years, two-digit components and up to nine fractional-second digits.</param>
    /// <returns>The normalized duration.</returns>
    /// <exception cref="ArgumentNullException">The input is null.</exception>
    /// <exception cref="FormatException">The input is invalid or its components exceed the supported range.</exception>
    public static CalendarDuration Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        const string number = @"[+-]?[0-9]+(?:[.,][0-9]+)?";
        var match = Regex.Match(text, @"\A(?<sign>[+-]?)P(?:(?<years>" + number + @")Y)?(?:(?<months>" + number +
            @")M)?(?:(?<weeks>" + number + @")W)?(?:(?<days>" + number + @")D)?(?:T(?:(?<hours>" + number +
            @")H)?(?:(?<minutes>" + number + @")M)?(?:(?<seconds>" + number + @")S)?)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        string[] fields = ["years", "months", "weeks", "days", "hours", "minutes", "seconds"];
        var present = fields.Where(field => match.Groups[field].Success).ToArray();
        if (!match.Success && ParseAlternative(text) is { } alternative)
        {
            return alternative;
        }
        if (!match.Success || present.Length == 0 || text.EndsWith('T') || text.EndsWith('t') ||
            present.SkipLast(1).Any(field => match.Groups[field].Value.Contains('.') || match.Groups[field].Value.Contains(',')))
        {
            throw new FormatException("Invalid ISO duration.");
        }
        try
        {
            decimal sign = match.Groups["sign"].Value == "-" ? -1 : 1;
            decimal Value(string name) => match.Groups[name].Success ? sign * Decimal.Parse(
                match.Groups[name].Value.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) : 0;
            return FromComponents(Value("years") * 12 + Value("months"), Value("weeks") * 7 + Value("days"),
                Value("hours") * 3600 + Value("minutes") * 60 + Value("seconds"));
        }
        catch (OverflowException exception)
        {
            throw new FormatException("The duration exceeds its component range.", exception);
        }
    }

    private static CalendarDuration? ParseAlternative(string text)
    {
        var match = Regex.Match(text, @"\A(?<sign>[+-]?)P(?:(?<years>[0-9]{4})(?<dateSep>-?)(?<months>[0-9]{2})\k<dateSep>(?<days>[0-9]{2}))?(?:T(?<hours>[0-9]{2})(?:(?<timeSep>:?)(?<minutes>[0-9]{2})(?:\k<timeSep>(?<seconds>[0-9]{2})(?:[.,](?<fraction>[0-9]{1,9}))?)?)?)?\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !match.Groups["years"].Success && !match.Groups["hours"].Success)
        {
            return null;
        }
        int Value(string name) => match.Groups[name].Success ? Int32.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture) : 0;
        int months = Value("months"), days = Value("days"), hours = Value("hours"), minutes = Value("minutes"), seconds = Value("seconds");
        if (months > 12 || days > 31 || hours > 24 || minutes > 60 || seconds > 60)
        {
            throw new FormatException("An alternative ISO duration component is out of range.");
        }
        int sign = match.Groups["sign"].Value == "-" ? -1 : 1;
        decimal fraction = match.Groups["fraction"].Success ? Decimal.Parse("0." + match.Groups["fraction"].Value, CultureInfo.InvariantCulture) : 0;
        return FromComponents(sign * (Value("years") * 12 + months), sign * days,
            sign * (hours * 3600 + minutes * 60 + seconds + fraction));
    }

    /// <summary>Formats the independent components as an ISO duration, retaining their signs and nanosecond precision.</summary>
    /// <returns>A duration string; zero is represented as PT0S.</returns>
    public override string ToString()
    {
        var result = new StringBuilder("P");
        void Part(BigInteger value, char suffix)
        {
            if (!value.IsZero)
            {
                result.Append(value.ToString(CultureInfo.InvariantCulture)).Append(suffix);
            }
        }
        Part(Months / 12, 'Y');
        Part(Months % 12, 'M');
        Part(Days, 'D');
        BigInteger nanos = (BigInteger)Seconds * 1_000_000_000 + Nanoseconds;
        if (!nanos.IsZero)
        {
            result.Append('T');
            BigInteger whole = nanos / 1_000_000_000;
            Part(whole / 3600, 'H');
            Part(whole / 60 % 60, 'M');
            BigInteger remainder = nanos % 60_000_000_000;
            if (!remainder.IsZero)
            {
                if (remainder.Sign < 0)
                {
                    result.Append('-');
                }
                remainder = BigInteger.Abs(remainder);
                result.Append((remainder / 1_000_000_000).ToString(CultureInfo.InvariantCulture));
                int fraction = (int)(remainder % 1_000_000_000);
                if (fraction != 0)
                {
                    result.Append('.').Append(fraction.ToString("D9", CultureInfo.InvariantCulture).TrimEnd('0'));
                }
                result.Append('S');
            }
        }
        return result.Length == 1 ? "PT0S" : result.ToString();
    }
}
