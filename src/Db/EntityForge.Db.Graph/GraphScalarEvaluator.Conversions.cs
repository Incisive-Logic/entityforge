using System.Globalization;
using System.Numerics;
using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static bool TryEvaluateConversion(string function, QueryValue[] arguments, IterationBudget budget, out QueryValue result)
    {
        result = UnboundValue.Instance;
        if (function == "CAST")
        {
            if (arguments.Length != 2 || arguments[1] is not LiteralValue { ScalarKind: QueryScalarKind.String } target)
            {
                throw new ArgumentException("CAST requires a value and target type.");
            }
            QueryValue source = arguments[0];
            string type = target.LexicalValue;
            ValidatePredicateType(type);
            result = CastValue(source, type, budget);
            return true;
        }
        return TryEvaluateLegacyConversion(function, arguments, out result);
    }

    private static QueryValue CastValue(QueryValue source, string type, IterationBudget budget)
    {
        if (MatchesPredicateType(source, type, budget))
        {
            return source;
        }
        ValidateListCastLength(source, type, budget);
        string baseType = type.TrimEnd('!');
        if (baseType.StartsWith("RECORD{", StringComparison.Ordinal) && source is MapValue record)
        {
            var fields = RecordTypeFields(baseType);
            if (record.Values.Count != fields.Length || fields.Any(field => !record.Values.ContainsKey(field.Name)))
            {
                throw new ArgumentException("A record CAST requires the same field names in source and target.");
            }
            var converted = new MapValue(fields.Select(field =>
                new KeyValuePair<string, QueryValue>(field.Name, CastValue(record.Values[field.Name], field.Type, budget))));
            _ = BoundedList([converted]);
            return converted;
        }
        if (baseType == "DECIMAL" && source is LiteralValue { ScalarKind: QueryScalarKind.Integer } integerValue)
        {
            return new LiteralValue(integerValue.LexicalValue, QueryScalarKind.Decimal);
        }
        if (baseType == "FLOAT" && source is LiteralValue { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal } numeric &&
            Double.TryParse(numeric.LexicalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && Double.IsFinite(number))
        {
            return FloatingValue(number);
        }
        if (baseType is "INTEGER" or "DECIMAL" &&
            source is LiteralValue { ScalarKind: QueryScalarKind.Decimal or QueryScalarKind.FloatingPoint })
        {
            return CastExactNumeric(source, baseType);
        }
        if (baseType.StartsWith("LIST<", StringComparison.Ordinal) && source is ListValue list)
        {
            string elementType = baseType[5..baseType.LastIndexOf('>')];
            return BoundedList(list.Items.Select(item => CastValue(item, elementType, budget)));
        }
        throw new ArgumentException("This CAST conversion is not yet supported or its value violates the target type.");
    }

    private static QueryValue CastExactNumeric(QueryValue source, string target)
    {
        ArgumentException OutOfRange() => GqlDataError(
            "The numeric value cannot be represented exactly in the CAST target type.", "22003");
        if (!NumericRatio(source, out BigInteger numerator, out BigInteger denominator, out double special) ||
            !Double.IsFinite(special))
        {
            throw OutOfRange();
        }
        if (target == "INTEGER")
        {
            if (denominator != BigInteger.One || numerator < Int64.MinValue || numerator > Int64.MaxValue)
            {
                throw OutOfRange();
            }
            return new LiteralValue(numerator.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
        }

        // A finite binary64 value has a reduced denominator that is a power of
        // two. Convert that exact ratio to the native decimal representation;
        // do not round through a decimal-to-string or double-to-decimal cast.
        bool negative = numerator.Sign < 0;
        BigInteger coefficient = BigInteger.Abs(numerator);
        byte scale = 0;
        while (denominator > BigInteger.One && scale < 28 && denominator.IsEven)
        {
            denominator /= 2;
            coefficient *= 5;
            scale++;
        }
        if (denominator != BigInteger.One || coefficient > (BigInteger.One << 96) - 1)
        {
            throw OutOfRange();
        }
        int low = unchecked((int)(uint)(coefficient & UInt32.MaxValue));
        int middle = unchecked((int)(uint)((coefficient >> 32) & UInt32.MaxValue));
        int high = unchecked((int)(uint)((coefficient >> 64) & UInt32.MaxValue));
        var value = new decimal(low, middle, high, negative, scale);
        return new LiteralValue(value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Decimal);
    }

    private static bool TryEvaluateLegacyConversion(string function, QueryValue[] arguments, out QueryValue result)
    {
        result = UnboundValue.Instance;
        if (function is not ("TOFLOAT" or "TOFLOATLIST" or "TOFLOATORNULL" or "TOINTEGER" or "TOINTEGERORNULL" or "TOINTEGERLIST" or
            "TOBOOLEAN" or "TOBOOLEANORNULL" or "TOBOOLEANLIST" or
            "TOSTRING" or "TOSTRINGORNULL" or "TOSTRINGLIST"))
        {
            return false;
        }
        if (arguments.Length != 1)
        {
            throw new ArgumentException($"{function} requires one argument.");
        }
        bool integer = function.StartsWith("TOINTEGER", StringComparison.Ordinal);
        bool floating = function.StartsWith("TOFLOAT", StringComparison.Ordinal);
        bool text = function.StartsWith("TOSTRING", StringComparison.Ordinal);
        bool tolerant = function.EndsWith("ORNULL", StringComparison.Ordinal) || function.EndsWith("LIST", StringComparison.Ordinal);
        QueryValue ConvertValue(QueryValue value)
        {
            if (value is NullValue or UnboundValue)
            {
                return UnboundValue.Instance;
            }
            if (floating)
            {
                if (value is not LiteralValue floatingLiteral || floatingLiteral.ScalarKind is not
                    (QueryScalarKind.Integer or QueryScalarKind.Decimal or QueryScalarKind.FloatingPoint or QueryScalarKind.String))
                {
                    return tolerant ? UnboundValue.Instance : throw new ArgumentException($"{function} does not accept this value type.");
                }
                return TryFloatingText(floatingLiteral.LexicalValue, out double parsed)
                    ? FloatingValue(parsed) : UnboundValue.Instance;
            }
            if (text)
            {
                if (value is MapValue or ListValue)
                {
                    // Reuse the collection work bounds before recursively formatting.
                    _ = BoundedList([value]);
                    string Child(QueryValue child) => child is NullValue or UnboundValue ? "null" :
                        ConvertValue(child) is LiteralValue converted ? converted.LexicalValue : "null";
                    string formatted = value is MapValue map
                        ? "{" + String.Join(", ", map.Values.Select(pair => pair.Key + ": " + Child(pair.Value))) + "}"
                        : "[" + String.Join(", ", ((ListValue)value).Items.Select(Child)) + "]";
                    if (formatted.Length > 1_048_576)
                    {
                        throw new ArgumentException("String conversion exceeds 1,048,576 characters.");
                    }
                    return new LiteralValue(formatted);
                }
                if (value is not LiteralValue item || item.ScalarKind is not
                    (QueryScalarKind.String or QueryScalarKind.Boolean or QueryScalarKind.Integer or QueryScalarKind.Decimal or QueryScalarKind.FloatingPoint or QueryScalarKind.Date or QueryScalarKind.Duration or QueryScalarKind.Point or QueryScalarKind.LocalTime or QueryScalarKind.LocalDateTime or QueryScalarKind.ZonedTime or QueryScalarKind.ZonedDateTime or QueryScalarKind.Guid))
                {
                    return tolerant ? UnboundValue.Instance : throw new ArgumentException($"{function} does not accept this value type.");
                }
                string lexical = item.LexicalValue;
                if (item.ScalarKind == QueryScalarKind.Point)
                {
                    return new LiteralValue(PointText(PointValue(item)));
                }
                if (item.ScalarKind == QueryScalarKind.FloatingPoint)
                {
                    return new LiteralValue(FloatingText(Double.Parse(lexical, CultureInfo.InvariantCulture)));
                }
                if (item.ScalarKind is QueryScalarKind.Integer or QueryScalarKind.Decimal && Number(item, out decimal numeric))
                {
                    lexical = numeric.ToString("G29", CultureInfo.InvariantCulture);
                    if (item.ScalarKind == QueryScalarKind.Decimal && !lexical.Contains('.') && !lexical.Contains('E'))
                    {
                        lexical += ".0";
                    }
                }
                return new LiteralValue(lexical);
            }
            if (value is not LiteralValue literal || literal.ScalarKind is not
                (QueryScalarKind.Integer or QueryScalarKind.Decimal or QueryScalarKind.FloatingPoint or QueryScalarKind.String or QueryScalarKind.Boolean) ||
                !integer && literal.ScalarKind is QueryScalarKind.Decimal or QueryScalarKind.FloatingPoint)
            {
                return tolerant ? UnboundValue.Instance : throw new ArgumentException($"{function} does not accept this value type.");
            }
            if (!integer)
            {
                if (literal.ScalarKind == QueryScalarKind.Integer && Number(literal, out decimal numeric))
                {
                    return new LiteralValue(numeric == 0 ? "false" : "true", QueryScalarKind.Boolean);
                }
                return bool.TryParse(literal.LexicalValue, out bool boolean)
                    ? new LiteralValue(boolean ? "true" : "false", QueryScalarKind.Boolean) : UnboundValue.Instance;
            }
            if (literal.ScalarKind == QueryScalarKind.Boolean)
            {
                return new LiteralValue(literal.LexicalValue == "true" ? "1" : "0", QueryScalarKind.Integer);
            }
            if (literal.ScalarKind == QueryScalarKind.FloatingPoint)
            {
                double parsed = Double.Parse(literal.LexicalValue, CultureInfo.InvariantCulture);
                long converted = Double.IsNaN(parsed) ? 0 : parsed >= 9223372036854775808d ? Int64.MaxValue :
                    parsed <= Int64.MinValue ? Int64.MinValue : (long)parsed;
                return new LiteralValue(converted.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
            }
            if (!Decimal.TryParse(literal.LexicalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
            {
                if (double.TryParse(literal.LexicalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double wide) &&
                    literal.LexicalValue.Any(Char.IsAsciiDigit) && (wide < Int64.MinValue || wide >= 9223372036854775808d))
                {
                    return tolerant ? UnboundValue.Instance : throw new ArgumentException("The integer conversion exceeds the signed 64-bit range.");
                }
                return UnboundValue.Instance;
            }
            number = Decimal.Truncate(number);
            if (number < Int64.MinValue || number > Int64.MaxValue)
            {
                if (literal.ScalarKind == QueryScalarKind.Decimal)
                {
                    number = Math.Clamp(number, Int64.MinValue, Int64.MaxValue);
                }
                else
                {
                    return tolerant ? UnboundValue.Instance : throw new ArgumentException("The integer conversion exceeds the signed 64-bit range.");
                }
            }
            return new LiteralValue(((long)number).ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
        }
        if (function.EndsWith("LIST", StringComparison.Ordinal))
        {
            result = arguments[0] switch
            {
                ListValue list => BoundedList(list.Items.Select(item => text && item is NullValue or UnboundValue
                    ? new LiteralValue("null") : ConvertValue(item))),
                NullValue or UnboundValue => UnboundValue.Instance,
                _ => throw new ArgumentException($"{function} requires a list.")
            };
        }
        else
        {
            result = ConvertValue(arguments[0]);
        }
        return true;
    }

    private static string FloatingText(double value)
    {
        if (!Double.IsFinite(value))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
        string roundTrip = value.ToString("R", CultureInfo.InvariantCulture);
        double magnitude = Math.Abs(value);
        if (magnitude != 0 && (magnitude < 0.001 || magnitude >= 10000000))
        {
            int separator = roundTrip.IndexOf('E');
            if (separator < 0)
            {
                // Reposition the decimal point without rounding the shortest representation.
                bool negative = roundTrip.StartsWith('-');
                string unsigned = negative ? roundTrip[1..] : roundTrip;
                int point = unsigned.IndexOf('.');
                if (point < 0)
                {
                    point = unsigned.Length;
                }
                string digits = unsigned.Replace(".", "", StringComparison.Ordinal);
                int first = 0;
                while (digits[first] == '0')
                {
                    first++;
                }
                int exponent = point - first - 1;
                digits = digits[first..].TrimEnd('0');
                return (negative ? "-" : "") + digits[0] + "." + (digits.Length == 1 ? "0" : digits[1..]) + "E" + exponent.ToString(CultureInfo.InvariantCulture);
            }
            string mantissa = roundTrip[..separator];
            if (!mantissa.Contains('.'))
            {
                mantissa += ".0";
            }
            return mantissa + "E" + Int32.Parse(roundTrip[(separator + 1)..], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }
        return roundTrip.Contains('.') ? roundTrip : roundTrip + ".0";
    }
}
