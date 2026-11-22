using System.Globalization;
using System.Numerics;
using System.Text;
using EntityForge.Query.Values;
using EntityForge.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private sealed record TemporalFormatPart(char Symbol, int Width, string? Text = null,
        IReadOnlyList<TemporalFormatPart>? Optional = null, int Padding = 0);

    private sealed class MissingTemporalFormatFieldException : Exception;

    private static QueryValue FormatTemporal(QueryValue[] values)
    {
        if (values.Any(value => value is NullValue or UnboundValue))
        {
            return UnboundValue.Instance;
        }
        if (values[0] is not LiteralValue temporal || temporal.ScalarKind is not
            (QueryScalarKind.Date or QueryScalarKind.LocalTime or QueryScalarKind.ZonedTime or
             QueryScalarKind.LocalDateTime or QueryScalarKind.ZonedDateTime or QueryScalarKind.Duration))
        {
            throw new ArgumentException("FORMAT requires a temporal value.");
        }
        if (values.Length == 1)
        {
            return new LiteralValue(TemporalIsoText(temporal));
        }
        if (values[1] is not LiteralValue { ScalarKind: QueryScalarKind.String } pattern)
        {
            throw new ArgumentException("FORMAT requires a string pattern.");
        }
        if (pattern.LexicalValue.Length > 4096)
        {
            throw new ArgumentException("A temporal format pattern cannot exceed 4096 characters.");
        }
        int cursor = 0;
        var parts = ParseTemporalFormat(pattern.LexicalValue, ref cursor, 0);
        CalendarDate? date = null;
        LocalTime? time = null;
        int? offset = null;
        string? zone = null;
        switch (temporal.ScalarKind)
        {
            case QueryScalarKind.Date:
                date = DateValue(temporal);
                break;
            case QueryScalarKind.LocalTime:
                time = TimeValue(temporal);
                break;
            case QueryScalarKind.LocalDateTime:
                var local = LocalDateTimeValue(temporal);
                date = local.Date;
                time = local.Time;
                break;
            case QueryScalarKind.ZonedTime:
                var zonedTime = ZonedTimeValue(temporal);
                time = zonedTime.Time;
                offset = zonedTime.OffsetSeconds;
                break;
            case QueryScalarKind.ZonedDateTime:
                var zoned = ZonedDateTimeValue(temporal);
                date = zoned.DateTime.Date;
                time = zoned.DateTime.Time;
                offset = zoned.OffsetSeconds;
                zone = zoned.ZoneId ?? new ZonedTime(time, offset.Value).Offset;
                break;
        }
        CalendarDuration? duration = temporal.ScalarKind == QueryScalarKind.Duration ? DurationValue(temporal) : null;
        IEnumerable<TemporalFormatPart> Flatten(IEnumerable<TemporalFormatPart> items) => items.SelectMany(part =>
            part.Optional is null ? [part] : Flatten(part.Optional));
        var symbols = Flatten(parts).Where(part => part.Text is null).Select(part => part.Symbol).ToHashSet();
        foreach (var part in Flatten(parts).Where(part => part.Text is null))
        {
            ValidateTemporalFormat(part, duration is not null);
        }
        string Render(IReadOnlyList<TemporalFormatPart> items)
        {
            var result = new StringBuilder();
            foreach (var part in items)
            {
                string text;
                if (part.Optional is { } optional)
                {
                    try
                    {
                        text = Render(optional);
                    }
                    catch (MissingTemporalFormatFieldException)
                    {
                        text = String.Empty;
                    }
                }
                else
                {
                    text = part.Text ?? (duration is null
                        ? FormatInstantField(part.Symbol, part.Width, date, time, offset, zone)
                        : FormatDurationField(part.Symbol, part.Width, duration, symbols));
                }
                if (part.Padding > 0)
                {
                    if (text.Length > part.Padding)
                    {
                        throw new ArgumentException("A temporal field exceeds its requested padding width.");
                    }
                    text = text.PadLeft(part.Padding);
                }
                result.Append(text);
                if (result.Length > 1_048_576)
                {
                    throw new ArgumentException("Temporal formatting exceeds its output bound.");
                }
            }
            return result.ToString();
        }
        try
        {
            return new LiteralValue(Render(parts));
        }
        catch (MissingTemporalFormatFieldException)
        {
            throw new ArgumentException("The temporal value does not contain a field required by the format pattern.");
        }
    }

    private static List<TemporalFormatPart> ParseTemporalFormat(string pattern, ref int cursor, int depth)
    {
        if (depth > 16)
        {
            throw new ArgumentException("Temporal format sections exceed 16 nesting levels.");
        }
        var parts = new List<TemporalFormatPart>();
        int padding = 0;
        while (cursor < pattern.Length)
        {
            char symbol = pattern[cursor++];
            TemporalFormatPart part;
            if (symbol == ']')
            {
                if (depth == 0 || padding != 0)
                {
                    throw new ArgumentException("Unexpected temporal format section end.");
                }
                return parts;
            }
            if (symbol == '[')
            {
                part = new(symbol, 1, Optional: ParseTemporalFormat(pattern, ref cursor, depth + 1));
            }
            else if (symbol == '\'')
            {
                var text = new StringBuilder();
                bool closed = false;
                if (cursor < pattern.Length && pattern[cursor] == '\'')
                {
                    text.Append('\'');
                    cursor++;
                    closed = true;
                }
                while (!closed && cursor < pattern.Length)
                {
                    char next = pattern[cursor++];
                    if (next != '\'')
                    {
                        text.Append(next);
                    }
                    else if (cursor < pattern.Length && pattern[cursor] == '\'')
                    {
                        text.Append('\'');
                        cursor++;
                    }
                    else
                    {
                        closed = true;
                    }
                }
                if (!closed)
                {
                    throw new ArgumentException("Unclosed temporal format literal.");
                }
                part = new('\0', 0, text.ToString());
            }
            else if (Char.IsAsciiLetter(symbol))
            {
                int width = 1;
                while (cursor < pattern.Length && pattern[cursor] == symbol)
                {
                    cursor++;
                    width++;
                }
                if (symbol == 'p')
                {
                    if (padding != 0 || cursor == pattern.Length || !Char.IsAsciiLetter(pattern[cursor]))
                    {
                        throw new ArgumentException("Temporal padding must precede a field.");
                    }
                    padding = width;
                    continue;
                }
                part = new(symbol, width);
            }
            else if (symbol is '{' or '}' or '#')
            {
                throw new ArgumentException("Reserved temporal format character.");
            }
            else
            {
                part = new('\0', 0, symbol.ToString());
            }
            parts.Add(part with
            {
                Padding = padding
            });
            padding = 0;
        }
        // Java closes any still-open optional sections at the end of a pattern.
        return parts;
    }

    private static void ValidateTemporalFormat(TemporalFormatPart part, bool duration)
    {
        char c = part.Symbol;
        int n = part.Width;
        bool valid = duration ? "yYuqQMLwWdDhHkKmsnSAN".Contains(c) && n <= (c is 'S' or 'n' ? 9 : 19) : c switch
        {
            'u' or 'y' or 'Y' or 'g' or 'A' or 'n' or 'N' => n <= 19,
            'd' or 'H' or 'h' or 'K' or 'k' or 'm' or 's' or 'w' => n <= 2,
            'D' => n <= 3,
            'M' or 'L' or 'Q' or 'q' or 'G' or 'E' or 'e' => n <= 5,
            'c' => n is 1 or 3 or 4 or 5,
            'W' or 'F' or 'a' => n == 1,
            'B' => n is 1 or 4 or 5,
            'S' => n <= 9,
            'V' => n == 2,
            'X' or 'x' or 'Z' => n <= 5,
            'O' => n is 1 or 4,
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException($"Unsupported temporal format field or width: {new string(c, n)}.");
        }
    }

    private static string TemporalNumber(BigInteger number, int width) =>
        (number.Sign < 0 ? "-" : "") + BigInteger.Abs(number).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

    private static string TemporalIsoText(LiteralValue value)
    {
        string Time(LocalTime time) => time.Hour.ToString("D2", CultureInfo.InvariantCulture) + ":" +
            time.Minute.ToString("D2", CultureInfo.InvariantCulture) + ":" + time.Second.ToString("D2", CultureInfo.InvariantCulture) +
            (time.Nanosecond == 0 ? "" : "." + time.Nanosecond.ToString("D9", CultureInfo.InvariantCulture).TrimEnd('0'));
        if (value.ScalarKind == QueryScalarKind.LocalTime)
        {
            return Time(TimeValue(value));
        }
        if (value.ScalarKind == QueryScalarKind.LocalDateTime)
        {
            var local = LocalDateTimeValue(value);
            return local.Date + "T" + Time(local.Time);
        }
        if (value.ScalarKind == QueryScalarKind.ZonedTime)
        {
            var zoned = ZonedTimeValue(value);
            return Time(zoned.Time) + zoned.Offset;
        }
        if (value.ScalarKind == QueryScalarKind.ZonedDateTime)
        {
            var zoned = ZonedDateTimeValue(value);
            return zoned.DateTime.Date + "T" + Time(zoned.DateTime.Time) + new ZonedTime(zoned.DateTime.Time, zoned.OffsetSeconds).Offset +
                (zoned.ZoneId is null ? "" : "[" + zoned.ZoneId + "]");
        }
        return value.LexicalValue;
    }

    private static string FormatDurationField(char symbol, int width, CalendarDuration duration, IReadOnlySet<char> fields)
    {
        bool Has(string symbols) => symbols.Any(fields.Contains);
        BigInteger months = duration.Months, days = duration.Days;
        BigInteger seconds = duration.Seconds;
        BigInteger Remainder(BigInteger amount, params (string Symbols, long Unit)[] larger)
        {
            foreach (var item in larger)
            {
                if (Has(item.Symbols))
                {
                    amount %= item.Unit;
                }
            }
            return amount;
        }
        BigInteger number = symbol switch
        {
            'y' or 'Y' or 'u' => months / 12,
            'q' or 'Q' => Remainder(months, ("yYu", 12)) / 3,
            'M' or 'L' => Remainder(months, ("yYu", 12), ("qQ", 3)),
            'w' or 'W' => days / 7,
            'd' or 'D' => Remainder(days, ("wW", 7)),
            'h' or 'H' or 'k' or 'K' => seconds / 3600,
            'm' => Remainder(seconds, ("hHkK", 3600)) / 60,
            's' => Remainder(seconds, ("hHkK", 3600), ("m", 60)),
            'A' or 'N' => Remainder((Has("s") ? 0 : Remainder(seconds, ("hHkK", 3600), ("m", 60))) * 1_000_000_000 + duration.Nanoseconds,
                (symbol == 'N' ? "A" : "", 1_000_000)) / (symbol == 'A' ? 1_000_000 : 1),
            _ => duration.Nanoseconds
        };
        return symbol is 'S' or 'n' ? TemporalNumber(number, 9)[..Math.Min(width, 9)] : TemporalNumber(number, width);
    }

    private static string FormatInstantField(char symbol, int width, CalendarDate? date, LocalTime? time, int? offset, string? zone)
    {
        CalendarDate Date() => date ?? throw new MissingTemporalFormatFieldException();
        LocalTime Time() => time ?? throw new MissingTemporalFormatFieldException();
        int Offset() => offset ?? throw new MissingTemporalFormatFieldException();
        string Number(long value) => TemporalNumber(value, width);
        string Year(int value) => width == 2 ? TemporalNumber(Math.Abs(value) % 100, 2) :
            (value >= 0 && width >= 4 && value.ToString(CultureInfo.InvariantCulture).Length > width ? "+" : "") + Number(value);
        string Month()
        {
            int month = Date().Month;
            return width <= 2 ? Number(month) : width == 3 ? CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month) :
                width == 4 ? CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month) : CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month)[..1];
        }
        string DayName()
        {
            int day = Date().DayOfWeek % 7;
            if (symbol is 'e' or 'c' && width <= 2)
            {
                return Number(day + 1);
            }
            string name = CultureInfo.InvariantCulture.DateTimeFormat.GetDayName((DayOfWeek)day);
            return width <= 3 ? name[..3] : width == 4 ? name : name[..1];
        }
        long WeekStart(CalendarDate value) => value.EpochDay - value.DayOfWeek % 7;
        (int Year, long Week) Week()
        {
            var d = Date();
            int year = d.Year;
            long start = WeekStart(new(year, 1, 1));
            if (year < CalendarDate.MaximumYear && d.EpochDay >= WeekStart(new(year + 1, 1, 1)))
            {
                year++;
                start = WeekStart(new(year, 1, 1));
            }
            return (year, (d.EpochDay - start) / 7 + 1);
        }
        return symbol switch
        {
            'u' => Year(Date().Year),
            'y' => Year(Date().Year > 0 ? Date().Year : 1 - Date().Year),
            'Y' => Year(Week().Year),
            'G' => Date().Year > 0 ? width == 4 ? "Anno Domini" : width == 5 ? "A" : "AD" : width == 4 ? "Before Christ" : width == 5 ? "B" : "BC",
            'M' or 'L' => Month(),
            'd' => Number(Date().Day),
            'D' => Number(Date().DayOfYear),
            'g' => Number(Date().EpochDay + 40587),
            'Q' or 'q' => width <= 2 ? Number((Date().Month - 1) / 3 + 1) : width == 3 ? "Q" + ((Date().Month - 1) / 3 + 1) :
                width == 4 ? new[] { "1st quarter", "2nd quarter", "3rd quarter", "4th quarter" }[(Date().Month - 1) / 3] : ((Date().Month - 1) / 3 + 1).ToString(CultureInfo.InvariantCulture),
            'E' or 'e' or 'c' => DayName(),
            'w' => Number(Week().Week),
            'W' => Number((Date().EpochDay - WeekStart(new(Date().Year, Date().Month, 1))) / 7 + 1),
            'F' => Number((Date().Day - 1) / 7 + 1),
            'H' => Number(Time().Hour),
            'k' => Number(Time().Hour == 0 ? 24 : Time().Hour),
            'K' => Number(Time().Hour % 12),
            'h' => Number(Time().Hour % 12 == 0 ? 12 : Time().Hour % 12),
            'm' => Number(Time().Minute),
            's' => Number(Time().Second),
            'S' => TemporalNumber(Time().Nanosecond, 9)[..width],
            'n' => Number(Time().Nanosecond),
            'A' => Number(Time().NanosecondsOfDay / 1_000_000),
            'N' => Number(Time().NanosecondsOfDay),
            'a' => Time().Hour < 12 ? "AM" : "PM",
            'B' => Time().Hour == 0 && Time().Minute == 0 ? width == 5 ? "mi" : "midnight" :
                Time().Hour == 12 && Time().Minute == 0 ? width == 5 ? "n" : "noon" :
                Time().Hour < 6 || Time().Hour >= 21 ? "at night" : Time().Hour < 12 ? "in the morning" : Time().Hour < 18 ? "in the afternoon" : "in the evening",
            'V' => zone ?? throw new MissingTemporalFormatFieldException(),
            'O' or 'X' or 'x' or 'Z' => FormatTemporalOffset(symbol, width, Offset()),
            _ => throw new ArgumentException("Unsupported temporal format field.")
        };
    }

    private static string FormatTemporalOffset(char symbol, int width, int offset)
    {
        if (symbol == 'Z')
        {
            return width <= 3 ? FormatTemporalOffset('x', 2, offset) : FormatTemporalOffset(width == 4 ? 'O' : 'X', width == 4 ? 4 : 5, offset);
        }
        if (offset == 0 && symbol is 'X' or 'O')
        {
            return symbol == 'X' ? "Z" : "GMT";
        }
        int absolute = Math.Abs(offset), hour = absolute / 3600, minute = absolute / 60 % 60, second = absolute % 60;
        string sign = offset < 0 ? "-" : "+";
        string Digits(int value) => value.ToString("D2", CultureInfo.InvariantCulture);
        if (symbol == 'O')
        {
            return "GMT" + sign + (width == 1 ? hour.ToString(CultureInfo.InvariantCulture) : Digits(hour)) +
                (width == 4 || minute != 0 || second != 0 ? ":" + Digits(minute) : "") + (second != 0 ? ":" + Digits(second) : "");
        }
        return sign + Digits(hour) + (width == 1 ? minute == 0 ? "" : Digits(minute) :
            (width is 3 or 5 ? ":" : "") + Digits(minute)) + (width >= 4 && second != 0 ? (width == 5 ? ":" : "") + Digits(second) : "");
    }
}
