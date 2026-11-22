using System.Globalization;
using System.Text;
using EntityForge.Query.Values;
using EntityForge.Query.Querying;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static string DistinctScalarKey(QueryValue value)
    {
        if (value is NodeValue node)
        {
            return "node:" + node.Identity;
        }
        if (value is EdgeValue edge)
        {
            return "edge:" + edge.Identity;
        }
        if (value is UnboundValue or NullValue)
        {
            return "null";
        }
        if (NumericKey(value) is { } numericKey)
        {
            return numericKey;
        }
        if (value is LiteralValue literal)
        {
            if (literal.ScalarKind == QueryScalarKind.Duration && DurationValue(literal) is { Months: 0 } duration)
            {
                return "Duration:" + literal.DatatypeIdentifier + ":" + DayTimeNanoseconds(duration).ToString(CultureInfo.InvariantCulture);
            }
            return literal.ScalarKind.ToString() + ":" + (literal.ScalarKind == QueryScalarKind.Duration ? literal.DatatypeIdentifier + ":" : "") + literal.LexicalValue;
        }
        throw new ArgumentException("Scalar DISTINCT requires scalar values.");
    }

    internal static int CompareForOrder(QueryValue left, QueryValue right) =>
        left is ListValue first && right is ListValue second
            ? CompareOrderedLists(first, second) : CompareScalarForOrder(left, right);

    private static int CompareScalarForOrder(QueryValue left, QueryValue right)
    {
        bool leftNull = left is UnboundValue or NullValue;
        bool rightNull = right is UnboundValue or NullValue;
        if (leftNull || rightNull)
        {
            return leftNull.CompareTo(rightNull);
        }
        if (CompareNumbers(left, right, out int numericComparison))
        {
            return numericComparison;
        }
        if (left is LiteralValue first && right is LiteralValue second && first.ScalarKind == second.ScalarKind)
        {
            if (first.ScalarKind == QueryScalarKind.Point)
            {
                return PointValue(first).CompareTo(PointValue(second));
            }
            if (first.ScalarKind == QueryScalarKind.LocalTime)
            {
                return TimeValue(first).CompareTo(TimeValue(second));
            }
            if (first.ScalarKind == QueryScalarKind.ZonedDateTime)
            {
                return ZonedDateTimeValue(first).CompareTo(ZonedDateTimeValue(second));
            }
            if (first.ScalarKind == QueryScalarKind.ZonedTime)
            {
                return ZonedTimeValue(first).CompareTo(ZonedTimeValue(second));
            }
            if (first.ScalarKind == QueryScalarKind.LocalDateTime)
            {
                return LocalDateTimeValue(first).CompareTo(LocalDateTimeValue(second));
            }
            if (first.ScalarKind == QueryScalarKind.Duration)
            {
                return CompareDurationValues(first, second);
            }
            return first.ScalarKind == QueryScalarKind.Date ? DateValue(first).CompareTo(DateValue(second)) : (first.ScalarKind == QueryScalarKind.String ? EntityForge.Db.Query.Planning.UnicodeText.CompareCodepoints(first.LexicalValue, second.LexicalValue) : StringComparer.Ordinal.Compare(first.LexicalValue, second.LexicalValue));
        }
        static int Rank(QueryValue value) => value is LiteralValue literal ? literal.ScalarKind switch
        {
            QueryScalarKind.Point => 0,
            QueryScalarKind.ZonedDateTime => 1,
            QueryScalarKind.LocalDateTime => 2,
            QueryScalarKind.Date => 4,
            QueryScalarKind.ZonedTime => 5,
            QueryScalarKind.LocalTime => 6,
            QueryScalarKind.Duration => 8,
            QueryScalarKind.String => 10,
            QueryScalarKind.Boolean => 12,
            QueryScalarKind.Guid => 13,
            QueryScalarKind.Integer or QueryScalarKind.Decimal or QueryScalarKind.FloatingPoint => 14,
            _ => -1
        } : -1;
        int leftRank = Rank(left), rightRank = Rank(right);
        if (leftRank >= 0 && rightRank >= 0)
        {
            return leftRank.CompareTo(rightRank);
        }
        throw new ArgumentException("Computed graph ordering requires compatible scalar types.");
    }

    internal static QueryValue Evaluate(GraphScalarExpression operand, Func<string, string, QueryValue> property,
        IterationBudget? budget = null)
    {
        try
        {
            return EvaluateCore(operand, property, budget ?? new IterationBudget());
        }
        catch (ArithmeticException exception)
        {
            throw new ArgumentException("GQL numeric expression is outside its supported range or divides by zero.", exception);
        }
    }

    private static QueryValue EvaluateCore(GraphScalarExpression operand, Func<string, string, QueryValue> property, IterationBudget budget)
    {
        if (operand.Function is null)
        {
            return operand.Literal ?? property(operand.Binding!, operand.Property!);
        }
        string function = operand.Function;
        if (function == "FORMAT")
        {
            return FormatTemporal(operand.Arguments!.Select(argument => EvaluateCore(argument, property, budget)).ToArray());
        }
        if (function is "UUID" or "UUID.MOSTSIGNIFICANTBITS" or "UUID.LEASTSIGNIFICANTBITS")
        {
            return UuidFunction(function, operand.Arguments!.Select(argument => EvaluateCore(argument, property, budget)).ToArray());
        }
        if (function == "STRING.REGEXREPLACE")
        {
            return ReplaceRegularExpression(operand.Arguments!.Select(argument => EvaluateCore(argument, property, budget)).ToArray());
        }
        if (function == "__GQL_PATH_FROM_EDGES")
        {
            if (operand.Arguments is not { Count: 2 })
            {
                throw new ArgumentException("Path assembly requires two operands.");
            }
            return AssemblePatternPath(EvaluateCore(operand.Arguments[0], property, budget),
                EvaluateCore(operand.Arguments[1], property, budget), budget);
        }
        if (function is "STARTNODE" or "ENDNODE")
        {
            QueryValue value = EvaluateCore(operand.Arguments![0], property, budget);
            if (value is NullValue or UnboundValue)
            {
                return UnboundValue.Instance;
            }
            if (value is not EdgeValue edge)
            {
                throw new ArgumentException("Relationship endpoint functions require a relationship.");
            }
            return budget.ResolveNode?.Invoke(function == "STARTNODE" ? edge.Source : edge.Target)
                ?? throw new ArgumentException("The relationship endpoint is unavailable in the captured graph.");
        }
        if (function is "DATE.REALTIME" or "DATETIME.REALTIME" or "ZONED_DATETIME.REALTIME" or "TIME.REALTIME" or "ZONED_TIME.REALTIME" or "LOCALTIME.REALTIME" or "LOCAL_TIME.REALTIME" or "LOCALDATETIME.REALTIME" or "LOCAL_DATETIME.REALTIME")
        {
            if (operand.Arguments!.Count > 1)
            {
                throw new ArgumentException($"{function} takes an optional timezone string.");
            }
            return ReadRealtimeClock(function, operand.Arguments.Count == 0 ? null : EvaluateCore(operand.Arguments[0], property, budget));
        }
        if (function is "RAND" or "RANDOMUUID")
        {
            if (operand.Arguments!.Count != 0)
            {
                throw new ArgumentException($"{function} takes no arguments.");
            }
            return function == "RANDOMUUID" ? new LiteralValue(Guid.NewGuid().ToString("D"))
                : FloatingValue(Random.Shared.NextDouble());
        }
        if (function == "MAP_PROJECT")
        {
            return EvaluateMapProjection(operand, property, budget);
        }
        if (function is "REDUCE" or "ALLREDUCE")
        {
            return EvaluateReduction(operand, property, budget);
        }
        if (function == "LOCAL")
        {
            return property("$iteration", ((LiteralValue)operand.Arguments![0].Literal!).LexicalValue);
        }
        if (function is "COMPREHENSION" or "ANY" or "ALL" or "NONE" or "SINGLE")
        {
            return EvaluateIteration(operand, property, budget);
        }
        if (function is "PI" or "E")
        {
            if (operand.Arguments!.Count != 0)
            {
                throw new ArgumentException($"{function} takes no arguments.");
            }
            return FloatingValue(function == "PI" ? Math.PI : Math.E);
        }
        if (function == "CASE")
        {
            var branches = operand.Arguments!;
            for (int index = 0; index + 1 < branches.Count; index += 2)
            {
                if (Boolean(EvaluateCore(branches[index], property, budget)) == true)
                {
                    return EvaluateCore(branches[index + 1], property, budget);
                }
            }
            return EvaluateCore(branches[^1], property, budget);
        }
        if (function == "COALESCE")
        {
            foreach (GraphScalarExpression argument in operand.Arguments!)
            {
                QueryValue item = EvaluateCore(argument, property, budget);
                if (item is not (UnboundValue or NullValue))
                {
                    return item;
                }
            }
            return UnboundValue.Instance;
        }
        QueryValue[] values = operand.Arguments!.Select(argument => EvaluateCore(argument, property, budget)).ToArray();
        if (function is "__GQL_PATH_MULTIPLICITY" or "__GQL_PATH_DECOMPOSITIONS")
        {
            return EvaluatePathMultiplicity(values, budget, function == "__GQL_PATH_DECOMPOSITIONS");
        }
        if (function == "PROPERTY")
        {
            if (values.Length != 2)
            {
                throw new ArgumentException("A property reference requires a value and a property name.");
            }
            if (values[0] is NullValue or UnboundValue)
            {
                return UnboundValue.Instance;
            }
            if (values[0] is not (MapValue or NodeValue or EdgeValue))
            {
                throw GqlDataError("Property references currently require a record, node or edge.", "22G03");
            }
            _ = TryEvaluateMap("INDEX", values, out var selected);
            return selected;
        }
        if (function == "VALUETYPE")
        {
            return new LiteralValue(DescribeValueType(values[0], budget).Render());
        }
        if (function is "LOCALDATETIME" or "LOCAL_DATETIME")
        {
            return ConstructLocalDateTime(values[0]);
        }
        if (function == "LOCALDATETIME.TRUNCATE")
        {
            return TruncateLocalDateTime(values);
        }
        if (function == "INDEX" && values[0] is LiteralValue { ScalarKind: QueryScalarKind.LocalDateTime } localDateTime)
        {
            return LocalDateTimeProperty(localDateTime, values[1]);
        }
        if (function is "DATETIME" or "ZONED_DATETIME")
        {
            return ConstructZonedDateTime(values[0]);
        }
        if (function is "DATETIME.FROMEPOCH" or "DATETIME.FROMEPOCHMILLIS")
        {
            return ZonedDateTimeLiteral(EpochDateTime(TemporalInteger(values[0]), function == "DATETIME.FROMEPOCHMILLIS", values.Length == 2 ? checked((int)TemporalInteger(values[1])) : null));
        }
        if (function == "DATETIME.TRUNCATE")
        {
            return TruncateZonedDateTime(values);
        }
        if (function == "INDEX" && values[0] is LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime } zonedDateTime)
        {
            return ZonedDateTimeProperty(zonedDateTime, values[1]);
        }
        if (function is "TIME" or "ZONED_TIME")
        {
            return ConstructZonedTime(values[0]);
        }
        if (function == "TIME.TRUNCATE")
        {
            return TruncateZonedTime(values);
        }
        if (function == "INDEX" && values[0] is LiteralValue { ScalarKind: QueryScalarKind.ZonedTime } zonedTime)
        {
            return ZonedTimeProperty(zonedTime, values[1]);
        }
        if (function is "LOCALTIME" or "LOCAL_TIME")
        {
            return ConstructLocalTime(values[0]);
        }
        if (function == "LOCALTIME.TRUNCATE")
        {
            return TruncateLocalTime(values);
        }
        if (function == "INDEX" && values[0] is LiteralValue { ScalarKind: QueryScalarKind.LocalTime } localTime)
        {
            return LocalTimeProperty(localTime, values[1]);
        }
        if (function == "POINT")
        {
            return ConstructPoint(values[0]);
        }
        if (function is "POINT.DISTANCE" or "POINT.WITHINBBOX")
        {
            return SpatialFunction(function, values);
        }
        if (function == "INDEX" && values[0] is LiteralValue { ScalarKind: QueryScalarKind.Point } point)
        {
            return PointProperty(point, values[1]);
        }
        if (function == "DATE.TRUNCATE")
        {
            return TruncateDate(values);
        }
        if (function == "DATE")
        {
            return ConstructDate(values[0]);
        }
        if (function == "DURATION")
        {
            return ConstructDuration(values[0]);
        }
        if (function is "DURATION.BETWEEN" or "DURATION_BETWEEN" or "DURATION.INDAYS" or "DURATION.INMONTHS" or "DURATION.INSECONDS")
        {
            return DurationBetween(function, values);
        }
        if (function == "INDEX" && values[0] is LiteralValue { ScalarKind: QueryScalarKind.Duration } duration)
        {
            return DurationProperty(duration, values[1]);
        }
        if (function == "INDEX" && values[0] is LiteralValue { ScalarKind: QueryScalarKind.Date } date)
        {
            return DateProperty(date, values[1]);
        }
        if (function == "SPLIT")
        {
            return Split(values);
        }
        if (function is "STRING.INDEXOF" or "STRING.JOIN")
        {
            return EvaluateStringFunction(function, values);
        }
        if (TryEvaluateEntityInspection(function, values, out QueryValue inspected))
        {
            return inspected;
        }
        if (TryEvaluateConversion(function, values, budget, out QueryValue converted))
        {
            return converted;
        }
        if (TryEvaluateMap(function, values, out QueryValue map))
        {
            return map;
        }
        if (TryEvaluateCollection(function, values, budget, out QueryValue collection))
        {
            return collection;
        }
        if (function is "ISTYPED" or "ISNOTTYPED")
        {
            if (values.Length != 3 || values[1] is not LiteralValue { ScalarKind: QueryScalarKind.String } target ||
                values[2] is not LiteralValue { ScalarKind: QueryScalarKind.Boolean } nullability ||
                !bool.TryParse(nullability.LexicalValue, out bool required))
            {
                throw new ArgumentException("A type predicate requires a value, a type name and a Boolean nullability flag.");
            }
            string type = target.LexicalValue;
            ValidatePredicateType(type);
            bool matches = MatchesPredicateType(values[0], type, budget, required);
            return new LiteralValue((function == "ISTYPED" ? matches : !matches) ? "true" : "false", QueryScalarKind.Boolean);
        }
        if (function is "ISNULL" or "ISNOTNULL")
        {
            bool result = (values[0] is UnboundValue or NullValue) == (function == "ISNULL");
            return new LiteralValue(result ? "true" : "false", QueryScalarKind.Boolean);
        }
        if (function is "ISTRUTH" or "ISNOTTRUTH")
        {
            bool matches = Boolean(values[0]) == Boolean(values[1]);
            return new LiteralValue((function == "ISTRUTH" ? matches : !matches) ? "true" : "false", QueryScalarKind.Boolean);
        }
        if (function is "AND" or "OR" or "XOR" or "NOT")
        {
            bool? left = Boolean(values[0]);
            bool? right = values.Length > 1 ? Boolean(values[1]) : null;
            bool? result = function switch
            {
                "AND" => left & right,
                "OR" => left | right,
                "XOR" => left ^ right,
                _ => !left
            };
            return result.HasValue ? new LiteralValue(result.Value ? "true" : "false", QueryScalarKind.Boolean) : UnboundValue.Instance;
        }
        if (function == "NULLIF")
        {
            bool equal = CollectionEquality(values[0], values[1], budget) == true;
            return equal ? UnboundValue.Instance : values[0];
        }
        if (values.Any(value => value is UnboundValue or NullValue))
        {
            return UnboundValue.Instance;
        }
        if (function == "ISNAN")
        {
            if (!FloatingNumber(values[0], out double number))
            {
                throw new ArgumentException("isNaN requires a number.");
            }
            return new LiteralValue(Double.IsNaN(number) ? "true" : "false", QueryScalarKind.Boolean);
        }
        if (function is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=")
        {
            if (function is not ("=" or "<>" or "!=") && values.Any(value => value is LiteralValue { ScalarKind: QueryScalarKind.Guid }))
            {
                return function is "<=" or ">=" && values[0] is LiteralValue { ScalarKind: QueryScalarKind.Guid } leftUuid &&
                    values[1] is LiteralValue { ScalarKind: QueryScalarKind.Guid } rightUuid && Guid.Parse(leftUuid.LexicalValue) == Guid.Parse(rightUuid.LexicalValue)
                        ? new LiteralValue("true", QueryScalarKind.Boolean) : UnboundValue.Instance;
            }
            int comparison;
            if (CompareNumbers(values[0], values[1], out comparison))
            {
                if (IsNaN(values[0]) || IsNaN(values[1]))
                {
                    return new LiteralValue(function is "<>" or "!=" ? "true" : "false", QueryScalarKind.Boolean);
                }
            }
            else if (values[0] is LiteralValue first && values[1] is LiteralValue second && first.ScalarKind == second.ScalarKind)
            {
                if (first.ScalarKind == QueryScalarKind.Point && first.LexicalValue != second.LexicalValue && function is not ("=" or "<>" or "!="))
                {
                    return UnboundValue.Instance;
                }
                comparison = first.ScalarKind == QueryScalarKind.Date ? DateValue(first).CompareTo(DateValue(second)) :
                    first.ScalarKind == QueryScalarKind.Duration ? CompareDurationValues(first, second) :
                    first.ScalarKind == QueryScalarKind.ZonedDateTime ? ZonedDateTimeValue(first).CompareTo(ZonedDateTimeValue(second)) :
                    first.ScalarKind == QueryScalarKind.ZonedTime ? ZonedTimeValue(first).CompareTo(ZonedTimeValue(second)) :
                    first.ScalarKind == QueryScalarKind.LocalTime ? TimeValue(first).CompareTo(TimeValue(second)) :
                    first.ScalarKind == QueryScalarKind.LocalDateTime ? LocalDateTimeValue(first).CompareTo(LocalDateTimeValue(second)) : (first.ScalarKind == QueryScalarKind.String ? EntityForge.Db.Query.Planning.UnicodeText.CompareCodepoints(first.LexicalValue, second.LexicalValue) : StringComparer.Ordinal.Compare(first.LexicalValue, second.LexicalValue));
            }
            else
            {
                if (function is "=" or "<>" or "!=")
                {
                    return new LiteralValue(function == "=" ? "false" : "true", QueryScalarKind.Boolean);
                }
                throw new ArgumentException("Ordering requires compatible scalar types.");
            }
            bool result = function switch
            {
                "=" => comparison == 0,
                "<>" or "!=" => comparison != 0,
                "<" => comparison < 0,
                "<=" => comparison <= 0,
                ">" => comparison > 0,
                _ => comparison >= 0
            };
            return new LiteralValue(result ? "true" : "false", QueryScalarKind.Boolean);
        }
        if (function == "=~")
        {
            return MatchRegularExpression(values[0], values[1]);
        }
        if (function is "STARTSWITH" or "ENDSWITH" or "CONTAINS")
        {
            if (values[0] is not LiteralValue { ScalarKind: QueryScalarKind.String } input ||
                values[1] is not LiteralValue { ScalarKind: QueryScalarKind.String } needle)
            {
                return UnboundValue.Instance;
            }
            bool matches = function switch
            {
                "STARTSWITH" => input.LexicalValue.StartsWith(needle.LexicalValue, StringComparison.Ordinal),
                "ENDSWITH" => input.LexicalValue.EndsWith(needle.LexicalValue, StringComparison.Ordinal),
                _ => input.LexicalValue.Contains(needle.LexicalValue, StringComparison.Ordinal)
            };
            return new LiteralValue(matches ? "true" : "false", QueryScalarKind.Boolean);
        }
        if (function == "+" && values.Any(value => value is LiteralValue { ScalarKind: QueryScalarKind.String }))
        {
            throw new ArgumentException("String concatenation requires ||.");
        }
        if (function == "SIZE")
        {
            throw new ArgumentException("SIZE requires a list.");
        }
        if (function is "||" or "LOWER" or "UPPER" or "TRIM" or "TRIMLEADING" or "TRIMTRAILING" or "TRIMBOTH" or "LTRIM" or "RTRIM" or "BTRIM" or "NORMALIZE" or "REVERSE" or "REPLACE" or "ISNORMALIZED" or "ISNOTNORMALIZED" or "LEFT" or "RIGHT" or "SUBSTRING" or "CHAR_LENGTH" or "CHARACTER_LENGTH")
        {
            if (values[0] is not LiteralValue { ScalarKind: QueryScalarKind.String } text)
            {
                throw new ArgumentException($"{function} requires a string.");
            }
            string source = text.LexicalValue;
            if (function is "ISNORMALIZED" or "ISNOTNORMALIZED")
            {
                bool normalized = (source == NormalizeText(source, Form(values))) == (function == "ISNORMALIZED");
                return new LiteralValue(normalized ? "true" : "false", QueryScalarKind.Boolean);
            }
            if (function is "CHAR_LENGTH" or "CHARACTER_LENGTH")
            {
                return new LiteralValue(EntityForge.Db.Query.Planning.UnicodeText.CountScalars(source).ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
            }
            int runeCount = function is "LEFT" or "RIGHT" ? EntityForge.Db.Query.Planning.UnicodeText.CountScalars(source) : 0;
            int Count(int index)
            {
                if (function is "LEFT" or "RIGHT" &&
                    values[index] is LiteralValue { ScalarKind: QueryScalarKind.Integer } &&
                    Number(values[index], out decimal stringLength) && stringLength == Decimal.Truncate(stringLength))
                {
                    if (stringLength < 0)
                    {
                        throw GqlDataError("A substring length cannot be negative.", "22011");
                    }
                    // A requested prefix/suffix can exceed the string length.
                    // Clamp before narrowing to the native indexing type.
                    return (int)Math.Min(stringLength, runeCount);
                }
                if (values[index] is not LiteralValue { ScalarKind: QueryScalarKind.Integer } ||
                    !Number(values[index], out decimal number) || number < 0 || number > Int32.MaxValue || number != Decimal.Truncate(number))
                {
                    throw new ArgumentException($"{function} requires a nonnegative integer position or length.");
                }
                return (int)number;
            }
            string result = function switch
            {
                "LOWER" or "UPPER" => EntityForge.Db.Query.Planning.UnicodeText.ChangeCase(source, function == "UPPER",
                    static () => new ArgumentException("Case conversion exceeds 1,048,576 UTF-16 code units.")),
                "TRIM" or "TRIMLEADING" or "TRIMTRAILING" or "TRIMBOTH" or "LTRIM" or "RTRIM" or "BTRIM" => Trim(source, values, function),
                "NORMALIZE" => NormalizeText(source, Form(values)),
                "REPLACE" => Replace(source, values),
                "REVERSE" => String.Concat(source.EnumerateRunes().Select(rune => rune.ToString()).Reverse()),
                "LEFT" => EntityForge.Db.Query.Planning.UnicodeText.SliceScalars(source, 0, Count(1)),
                "RIGHT" => EntityForge.Db.Query.Planning.UnicodeText.SliceScalars(source, runeCount - Count(1), Int32.MaxValue),
                "SUBSTRING" => EntityForge.Db.Query.Planning.UnicodeText.SliceScalars(source, Count(1), values.Length == 3 ? Count(2) : Int32.MaxValue),
                "||" when values[1] is LiteralValue { ScalarKind: QueryScalarKind.String } second => source + second.LexicalValue,
                _ => throw new ArgumentException("String concatenation requires string operands.")
            };
            if (result.Length > 1_048_576)
            {
                throw new ArgumentException("GQL string expression exceeds 1,048,576 characters.");
            }
            return new LiteralValue(result, QueryScalarKind.String);
        }
        if (function == "ROUND")
        {
            return Round(values);
        }
        if (values.Any(value => value is LiteralValue { ScalarKind: QueryScalarKind.Duration }))
        {
            return DurationArithmetic(function, values);
        }
        if (values.Any(value => value is LiteralValue { ScalarKind: QueryScalarKind.FloatingPoint }) ||
            function is not ("+" or "-" or "*" or "/" or "%" or "MOD" or "unary+" or "unary-" or "ABS" or "SIGN" or "CEIL" or "CEILING" or "FLOOR"))
        {
            return FloatingArithmetic(function, values);
        }
        if (values.Any(value => !Number(value, out _)))
        {
            throw new ArgumentException($"{function} requires numeric operands.");
        }
        Number(values[0], out decimal a);
        if (function == "SIGN")
        {
            return new LiteralValue(Math.Sign(a).ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
        }
        decimal b = 0;
        if (values.Length > 1)
        {
            Number(values[1], out b);
        }
        bool integerResult = values.All(value => value is LiteralValue { ScalarKind: QueryScalarKind.Integer }) &&
            function is "+" or "-" or "*" or "/" or "%" or "MOD" or "unary+" or "unary-" or "ABS" or "CEIL" or "CEILING" or "FLOOR";
        try
        {
            decimal numberResult = function switch
            {
                "+" => a + b,
                "-" => a - b,
                "*" => a * b,
                "/" => b == 0 ? throw GqlDataError("Exact numeric division requires a nonzero divisor.", "22012") : a / b,
                "%" or "MOD" => b == 0 ? throw GqlDataError("Exact numeric remainder requires a nonzero divisor.", "22012") : a % b,
                "unary+" => a,
                "unary-" => -a,
                "ABS" => Math.Abs(a),
                "CEIL" or "CEILING" => Math.Ceiling(a),
                "FLOOR" => Math.Floor(a),
                _ => throw new ArgumentException($"Unsupported numeric function {function}.")
            };
            if (integerResult)
            {
                return new LiteralValue(checked((long)numberResult).ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
            }
            return new LiteralValue(numberResult.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Decimal);
        }
        catch (OverflowException)
        {
            throw GqlDataError("An exact numeric result is outside its supported range.", "22003");
        }
    }

    private static string Trim(string source, QueryValue[] values, string function)
    {
        string characters = values.Length == 1 ? " " : values[1] is LiteralValue { ScalarKind: QueryScalarKind.String } text
            ? text.LexicalValue : throw new ArgumentException("TRIM characters must be a string.");
        HashSet<Rune> removed = characters.EnumerateRunes().ToHashSet();
        if (function.StartsWith("TRIM", StringComparison.Ordinal) && characters.EnumerateRunes().Count() != 1)
        {
            throw GqlDataError("TRIM requires exactly one Unicode character; use BTRIM, LTRIM or RTRIM for a character set.", "22027");
        }
        bool leading = function is not ("RTRIM" or "TRIMTRAILING");
        bool trailing = function is not ("LTRIM" or "TRIMLEADING");
        int start = 0, end = 0, index = 0;
        foreach (var rune in source.EnumerateRunes())
        {
            bool remove = removed.Contains(rune);
            if (leading && start == index && remove)
            {
                start++;
            }
            if (!trailing || !remove)
            {
                end = index + 1;
            }
            index++;
        }
        return EntityForge.Db.Query.Planning.UnicodeText.SliceScalars(source, start, Math.Max(0, end - start));
    }

    private static string NormalizeText(string source, NormalizationForm form) =>
        EntityForge.Db.Query.Planning.UnicodeText.Normalize(source, form,
            static () => new ArgumentException("Normalization exceeds its input, output or decomposition work limit."));

    private static NormalizationForm Form(QueryValue[] values) => values.Length == 1 ? NormalizationForm.FormC :
        values[1] is LiteralValue { ScalarKind: QueryScalarKind.String } form ? form.LexicalValue switch
        {
            "NFC" => NormalizationForm.FormC,
            "NFD" => NormalizationForm.FormD,
            "NFKC" => NormalizationForm.FormKC,
            "NFKD" => NormalizationForm.FormKD,
            _ => throw new ArgumentException("Unknown Unicode normalization form.")
        } : throw new ArgumentException("A Unicode normalization form is required.");

    private static string Replace(string source, QueryValue[] values)
    {
        if (values[1] is not LiteralValue { ScalarKind: QueryScalarKind.String } search ||
            values[2] is not LiteralValue { ScalarKind: QueryScalarKind.String } replacement)
        {
            throw new ArgumentException("REPLACE requires string operands.");
        }
        long limit = Int64.MaxValue;
        if (values.Length == 4 && (values[3] is not LiteralValue { ScalarKind: QueryScalarKind.Integer } bound ||
            !Int64.TryParse(bound.LexicalValue, CultureInfo.InvariantCulture, out limit) || limit < 0))
        {
            throw new ArgumentException("REPLACE requires a non-negative integer limit.");
        }
        if (limit == 0)
        {
            return source;
        }
        if (search.LexicalValue.Length == 0)
        {
            if ((long)source.Length + Math.Min(limit, (long)source.EnumerateRunes().Count() + 1) * replacement.LexicalValue.Length > 1_048_576)
            {
                throw new ArgumentException("GQL string expression exceeds 1,048,576 characters.");
            }
            var inserted = new StringBuilder(replacement.LexicalValue);
            long used = 1;
            foreach (Rune rune in source.EnumerateRunes())
            {
                inserted.Append(rune.ToString());
                if (used++ < limit)
                {
                    inserted.Append(replacement.LexicalValue);
                }
            }
            return inserted.ToString();
        }
        var result = new StringBuilder();
        int start = 0;
        int index;
        long replaced = 0;
        while (replaced++ < limit && (index = source.IndexOf(search.LexicalValue, start, StringComparison.Ordinal)) >= 0)
        {
            if ((long)result.Length + index - start + replacement.LexicalValue.Length > 1_048_576)
            {
                throw new ArgumentException("GQL string expression exceeds 1,048,576 characters.");
            }
            result.Append(source, start, index - start).Append(replacement.LexicalValue);
            start = index + search.LexicalValue.Length;
        }
        if ((long)result.Length + source.Length - start > 1_048_576)
        {
            throw new ArgumentException("GQL string expression exceeds 1,048,576 characters.");
        }
        return result.Append(source, start, source.Length - start).ToString();
    }

    private static ArgumentException GqlDataError(string message, string status)
    {
        var failure = new ArgumentException(message);
        failure.Data["EntityForge.GqlStatus"] = status;
        return failure;
    }

    private static double RealFunction(string name, double a, double b, QueryValue[] values)
    {
        static int Compare(QueryValue value, int boundary)
        {
            CompareNumbers(value, new LiteralValue(boundary.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer), out int result);
            return result;
        }
        static double Logarithm(QueryValue value, double approximate)
        {
            // Retain the distance from one before converting exact input to
            // binary64; rounding the input first can turn a valid base into 1.
            if (value is LiteralValue { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal } &&
                Number(value, out decimal exact) && exact is > 0.5m and < 1.5m)
            {
                double delta = (double)(exact - 1), sum = 1 + delta;
                // Correct the rounding introduced by forming 1 + delta. This
                // also retains a nonzero logarithm when sum rounds to one.
                return Math.Log(sum) - ((sum - 1) - delta) / sum;
            }
            return Math.Log(approximate);
        }
        if (name is "ASIN" or "ACOS" && !Double.IsNaN(a) &&
            (Compare(values[0], -1) < 0 || Compare(values[0], 1) > 0))
        {
            throw GqlDataError("An inverse trigonometric argument must be between negative one and one.", "2202M");
        }
        if (name is "LN" or "LOG10" && a <= 0)
        {
            throw name == "LN" ? GqlDataError("LN requires a positive argument.", "2201E") :
                new ArgumentException("A logarithm requires a positive argument.");
        }
        if (name == "LOG" && (!Double.IsNaN(a) && (Compare(values[0], 0) <= 0 || Compare(values[0], 1) == 0) ||
            !Double.IsNaN(b) && Compare(values[1], 0) <= 0))
        {
            throw GqlDataError("LOG requires a positive base other than one and a positive argument.", "2201Z");
        }
        double result = name switch
        {
            "SQRT" => Math.Sqrt(a),
            "POWER" => Math.Pow(a, b),
            "EXP" => Math.Exp(a),
            "LN" => Logarithm(values[0], a),
            "LOG" => Logarithm(values[1], b) / Logarithm(values[0], a),
            "LOG10" => values[0] is LiteralValue { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal } &&
                Number(values[0], out decimal exact) && exact is > 0.5m and < 1.5m
                    ? Logarithm(values[0], a) / Math.Log(10) : Math.Log10(a),
            "SIN" => Math.Sin(a),
            "COS" => Math.Cos(a),
            "TAN" => Math.Tan(a),
            "ASIN" => Math.Asin(a),
            "ACOS" => Math.Acos(a),
            "ATAN" => Math.Atan(a),
            "ATAN2" => Math.Atan2(a, b),
            "COT" => 1 / Math.Tan(a),
            "HAVERSIN" => (1 - Math.Cos(a)) / 2,
            "SINH" => Math.Sinh(a),
            "COSH" => Math.Cosh(a),
            "TANH" => Math.Tanh(a),
            "COTH" => a == 0 ? Double.NaN : 1 / Math.Tanh(a),
            // Form the conversion factor first so an intermediate product does
            // not overflow when the final converted angle is representable.
            "DEGREES" => a * (180 / Math.PI),
            "RADIANS" => a * (Math.PI / 180),
            _ => throw new ArgumentException($"Unsupported numeric function {name}.")
        };
        return result;
    }

    internal static bool? Boolean(QueryValue value) => value switch
    {
        UnboundValue or NullValue => null,
        LiteralValue { ScalarKind: QueryScalarKind.Boolean } literal => bool.Parse(literal.LexicalValue),
        _ => throw new ArgumentException("A boolean expression requires a boolean value.")
    };

    private static bool Number(QueryValue value, out decimal number)
    {
        number = 0;
        return value is LiteralValue { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal or QueryScalarKind.FloatingPoint } literal && Decimal.TryParse(literal.LexicalValue,
            NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }
}
