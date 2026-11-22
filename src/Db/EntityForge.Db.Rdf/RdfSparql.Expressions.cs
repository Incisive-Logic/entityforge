using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Numerics;
using System.Text;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static FilterResult Evaluate(RdfSparqlReadView database, FilterExpression expression,
        IReadOnlyDictionary<string, RdfTerm> row) => expression switch
        {
            ComparisonExpression comparison => EvaluateComparison(comparison, row, database),
            EffectiveBooleanExpression boolean => EffectiveBoolean(EvaluateValue(boolean.Value, row, database)),
            BinaryBooleanExpression boolean => boolean.Operator == "&&"
                ? And(Evaluate(database, boolean.Left, row), Evaluate(database, boolean.Right, row))
                : Or(Evaluate(database, boolean.Left, row), Evaluate(database, boolean.Right, row)),
            NotExpression not => Evaluate(database, not.Operand, row) switch
            {
                FilterResult.True => FilterResult.False,
                FilterResult.False => FilterResult.True,
                _ => FilterResult.Error
            },
            FunctionExpression function => EvaluateFunction(function, row, database),
            InExpression member => EvaluateIn(member, row, database),
            ExistsExpression exists => Result((EvaluateGroup(database, exists.Group,
                [new Dictionary<string, RdfTerm>(row, StringComparer.Ordinal)], row).Count > 0) != exists.Negated),
            _ => throw new ArgumentOutOfRangeException(nameof(expression))
        };

    private static FilterResult EvaluateComparison(ComparisonExpression comparison,
        IReadOnlyDictionary<string, RdfTerm> row, RdfSparqlReadView? database = null)
    {
        RdfTerm? left = EvaluateValue(comparison.Left, row, database);
        RdfTerm? right = EvaluateValue(comparison.Right, row, database);
        if (left is null || right is null)
        {
            return FilterResult.Error;
        }

        int? ordering = CompareTerms(left, right);
        if (NumericComparisonHasNaN(left, right) && comparison.Operator is "<" or "<=" or ">" or ">=")
        {
            return FilterResult.False;
        }
        if (ordering is null && comparison.Operator is "<" or "<=" or ">" or ">=")
        {
            return FilterResult.Error;
        }
        bool? value = comparison.Operator switch
        {
            "=" => ValueEquals(left, right),
            "!=" => !ValueEquals(left, right),
            "<" => ordering < 0,
            "<=" => ordering <= 0,
            ">" => ordering > 0,
            ">=" => ordering >= 0,
            _ => null
        };
        return value is bool result ? Result(result) : FilterResult.Error;
    }

    private static bool? ValueEquals(RdfTerm left, RdfTerm right)
    {
        if (NumericComparisonHasNaN(left, right))
        {
            return false;
        }
        // Identical ill-typed literals retain RDF-term equality (the W3C
        // open-world cases); this does not validate their numeric values.
        if (Equals(left, right))
        {
            return true;
        }
        if (left is RdfLiteral leftLiteral && right is RdfLiteral rightLiteral &&
            IsNumeric(leftLiteral) && IsNumeric(rightLiteral))
        {
            return TryCompareNumeric(leftLiteral, rightLiteral, out int numericComparison) ? numericComparison == 0 : null;
        }
        if (left is RdfLiteral a && right is RdfLiteral b)
        {
            if (a.Language is not null || b.Language is not null)
            {
                return a.LexicalForm == b.LexicalForm && a.Language == b.Language;
            }
            if (a.Datatype == Xsd + "dateTime" && b.Datatype == Xsd + "dateTime")
            {
                int? order = CompareDateTimes(a, b);
                return order is null ? null : order == 0;
            }
            if (a.Datatype == Xsd + "boolean" && b.Datatype == Xsd + "boolean")
            {
                var av = BooleanValue(a.LexicalForm);
                var bv = BooleanValue(b.LexicalForm);
                return av is null || bv is null ? null : av == bv;
            }
            if (IsStringLiteral(a) && IsStringLiteral(b))
            {
                return a.LexicalForm == b.LexicalForm && a.Language == b.Language;
            }
            return Equals(left, right) ? true : null;
        }

        return Equals(left, right);
    }

    private static FilterResult EvaluateFunction(FunctionExpression function,
        IReadOnlyDictionary<string, RdfTerm> row, RdfSparqlReadView? database = null)
    {
        RdfTerm? first = EvaluateValue(function.First, row, database);
        if (function.Name == "BOUND")
        {
            return Result(first is not null);
        }

        if (first is null)
        {
            return FilterResult.Error;
        }

        if (function.Name is "ISIRI" or "ISURI")
        {
            return Result(first is RdfIri);
        }

        if (function.Name == "ISBLANK")
        {
            return Result(first is RdfBlankNode);
        }

        if (function.Name == "ISLITERAL")
        {
            return Result(first is RdfLiteral);
        }

        if (function.Name == "ISNUMERIC")
        {
            return Result(first is RdfLiteral numeric && IsNumeric(numeric) &&
                (NumericRank(numeric) >= 2 ? Floating(numeric, out _) : TryParseExactDecimal(numeric, out _, out _)));
        }

        RdfTerm? second = EvaluateValue(function.Second!, row, database);
        if (second is null)
        {
            return FilterResult.Error;
        }

        if (function.Name == "SAMETERM")
        {
            return Result(Equals(first, second));
        }

        if (function.Name == "LANGMATCHES")
        {
            if (first is not RdfLiteral languageLiteral || !IsSimpleStringLiteral(languageLiteral) ||
                second is not RdfLiteral rangeLiteral || !IsSimpleStringLiteral(rangeLiteral))
            {
                return FilterResult.Error;
            }
            string language = languageLiteral.LexicalForm, range = rangeLiteral.LexicalForm;
            return Result(
                (range == "*" ? language.Length > 0 : language.Equals(range, StringComparison.OrdinalIgnoreCase) ||
                 language.StartsWith(range + "-", StringComparison.OrdinalIgnoreCase)));
        }
        if (function.Name == "REGEX")
        {
            if (first is not RdfLiteral inputLiteral || !IsStringLiteral(inputLiteral) ||
                second is not RdfLiteral patternLiteral || !IsSimpleStringLiteral(patternLiteral))
            {
                return FilterResult.Error;
            }

            string flags = String.Empty;
            if (function.Third is not null)
            {
                if (EvaluateValue(function.Third, row, database) is not RdfLiteral flagsLiteral ||
                    !IsSimpleStringLiteral(flagsLiteral))
                {
                    return FilterResult.Error;
                }

                flags = flagsLiteral.LexicalForm;
            }
            if (flags.Any(flag => flag is not ('i' or 'm' or 's' or 'x')))
            {
                return FilterResult.Error;
            }

            RegexOptions options = RegexOptions.CultureInvariant;
            if (flags.Contains('i'))
            {
                options |= RegexOptions.IgnoreCase;
            }

            if (flags.Contains('m'))
            {
                options |= RegexOptions.Multiline;
            }

            if (flags.Contains('s'))
            {
                options |= RegexOptions.Singleline;
            }

            try
            {
                var regex = new Regex(PrepareRegexPattern(patternLiteral.LexicalForm, flags), options, TimeSpan.FromMilliseconds(100));
                return Result(XPathBackreferenceRegex.Required(patternLiteral.LexicalForm, flags)
                    ? new XPathBackreferenceRegex(patternLiteral.LexicalForm, flags).Matches(inputLiteral.LexicalForm).Any()
                    : regex.IsMatch(inputLiteral.LexicalForm));
            }
            catch (ArgumentException)
            {
                return FilterResult.Error;
            }
        }
        if (first is not RdfLiteral left || second is not RdfLiteral right)
        {
            return FilterResult.Error;
        }

        if (!IsStringLiteral(left) || !IsStringLiteral(right) || !CompatibleStringArguments(left, right))
        {
            return FilterResult.Error;
        }

        bool? value = function.Name switch
        {
            "STRSTARTS" => left.LexicalForm.StartsWith(right.LexicalForm, StringComparison.Ordinal),
            "STRENDS" => left.LexicalForm.EndsWith(right.LexicalForm, StringComparison.Ordinal),
            "CONTAINS" => left.LexicalForm.Contains(right.LexicalForm, StringComparison.Ordinal),
            _ => null
        };
        return value is bool result ? Result(result) : FilterResult.Error;
    }

    private static FilterResult EvaluateIn(InExpression expression, IReadOnlyDictionary<string, RdfTerm> row, RdfSparqlReadView? database = null)
    {
        RdfTerm? operand = EvaluateValue(expression.Operand, row, database);
        if (operand is null)
        {
            return FilterResult.Error;
        }

        bool error = false;
        bool contains = expression.Values.Any(value =>
        {
            RdfTerm? candidate = EvaluateValue(value, row, database);
            if (candidate is null)
            {
                error = true;
                return false;
            }
            bool? equal = ValueEquals(operand, candidate);
            error |= equal is null;
            return equal == true;
        });
        if (!contains && error)
        {
            return FilterResult.Error;
        }

        return Result(contains != expression.Negated);
    }

    private static FilterResult Result(bool value) => value ? FilterResult.True : FilterResult.False;

    private static FilterResult And(FilterResult left, FilterResult right) =>
        left == FilterResult.False || right == FilterResult.False ? FilterResult.False :
        left == FilterResult.Error || right == FilterResult.Error ? FilterResult.Error : FilterResult.True;

    private static FilterResult Or(FilterResult left, FilterResult right) =>
        left == FilterResult.True || right == FilterResult.True ? FilterResult.True :
        left == FilterResult.Error || right == FilterResult.Error ? FilterResult.Error : FilterResult.False;

    private enum FilterResult
    {
        False, True, Error
    }

    private static RdfTerm? EvaluateValue(ValueExpression expression,
        IReadOnlyDictionary<string, RdfTerm> row, RdfSparqlReadView? database = null) => expression switch
        {
            EvaluatedValueExpression evaluated => evaluated.Value,
            TermValueExpression term => Resolve(term.Term, row),
            ValueFunctionExpression function => EvaluateValueFunction(function, row, database),
            ArithmeticExpression arithmetic => Arithmetic(EvaluateValue(arithmetic.Left, row, database), arithmetic.Operator, EvaluateValue(arithmetic.Right, row, database)),
            CallExpression call => EvaluateCall(call, row, database),
            BooleanValueExpression boolean => BooleanTerm(EvaluateScalar(boolean.Filter, row, database)),
            ConditionalExpression conditional => EvaluateConditional(conditional, row, database),
            _ => null
        };

    private static RdfTerm? EvaluateValueFunction(ValueFunctionExpression function,
        IReadOnlyDictionary<string, RdfTerm> row, RdfSparqlReadView? database = null)
    {
        RdfTerm? value = EvaluateValue(function.Argument, row, database);
        if (function.Name.StartsWith(Xsd, StringComparison.Ordinal))
        {
            return CastValue(function.Name[Xsd.Length..], value);
        }
        return function.Name switch
        {
            "+" or "-" => UnaryNumeric(function.Name, value),
            "IRI" or "URI" => ConstructIri(value, function.BaseIri),
            "YEAR" or "MONTH" or "DAY" or "HOURS" or "MINUTES" or "SECONDS" or "TIMEZONE" or "TZ" =>
                DateTimeComponent(function.Name, value),
            "ABS" or "CEIL" or "FLOOR" or "ROUND" => NumericFunction(function.Name, value),
            "ENCODE_FOR_URI" when value is RdfLiteral literal && IsStringLiteral(literal) =>
                EncodeUriLiteral(literal.LexicalForm),
            "MD5" or "SHA1" or "SHA256" or "SHA384" or "SHA512" when value is RdfLiteral literal && IsSimpleStringLiteral(literal) =>
                HashLiteral(function.Name, literal.LexicalForm),
            "STR" when value is RdfIri iri => new RdfLiteral(iri.Value),
            "STR" when value is RdfLiteral literal => new RdfLiteral(literal.LexicalForm),
            "LANG" when value is RdfLiteral literal => new RdfLiteral(literal.Language ?? String.Empty),
            "DATATYPE" when value is RdfLiteral { Datatype: { } datatype } => new RdfIri(datatype),
            "STRLEN" when value is RdfLiteral literal && IsStringLiteral(literal) =>
                new RdfLiteral(EntityForge.Db.Query.Planning.UnicodeText.CountScalars(literal.LexicalForm).ToString(CultureInfo.InvariantCulture),
                    "http://www.w3.org/2001/XMLSchema#integer"),
            "UCASE" when value is RdfLiteral literal && IsStringLiteral(literal) =>
                new RdfLiteral(ChangeCase(literal.LexicalForm, upper: true), literal.Datatype, literal.Language),
            "LCASE" when value is RdfLiteral literal && IsStringLiteral(literal) =>
                new RdfLiteral(ChangeCase(literal.LexicalForm, upper: false), literal.Datatype, literal.Language),
            _ => null
        };
    }

    private static int? CompareTerms(RdfTerm left, RdfTerm right)
    {
        if (left is not RdfLiteral leftLiteral || right is not RdfLiteral rightLiteral)
        {
            return null;
        }

        if (IsNumeric(leftLiteral) && IsNumeric(rightLiteral) &&
            TryCompareNumeric(leftLiteral, rightLiteral, out int numericComparison))
        {
            return numericComparison;
        }

        if (leftLiteral.Datatype == Xsd + "boolean" && rightLiteral.Datatype == Xsd + "boolean")
        {
            var a = BooleanValue(leftLiteral.LexicalForm);
            var b = BooleanValue(rightLiteral.LexicalForm);
            return a is null || b is null ? null : a.Value.CompareTo(b.Value);
        }
        if (leftLiteral.Datatype == Xsd + "dateTime" && rightLiteral.Datatype == Xsd + "dateTime")
        {
            return CompareDateTimes(leftLiteral, rightLiteral);
        }
        if (!IsStringLiteral(leftLiteral) || !IsStringLiteral(rightLiteral))
        {
            return null;
        }

        if (!String.Equals(leftLiteral.Datatype, rightLiteral.Datatype, StringComparison.Ordinal) ||
            !String.Equals(leftLiteral.Language, rightLiteral.Language, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return CompareCodepoints(leftLiteral.LexicalForm, rightLiteral.LexicalForm);
    }

    private static int CompareCodepoints(string left, string right) =>
        EntityForge.Db.Query.Planning.UnicodeText.CompareCodepoints(left, right);

    private static bool IsNumeric(RdfLiteral literal) => NumericRank(literal) != Int32.MaxValue;

    private static bool IsStringLiteral(RdfLiteral literal) =>
        literal.Language is not null || literal.Datatype is null or "http://www.w3.org/2001/XMLSchema#string";

    private static bool IsSimpleStringLiteral(RdfLiteral literal) =>
        literal.Language is null &&
        (literal.Datatype is null or "http://www.w3.org/2001/XMLSchema#string");

    private static bool CompatibleStringArguments(RdfLiteral left, RdfLiteral right) =>
        right.Language is null || String.Equals(left.Language, right.Language, StringComparison.OrdinalIgnoreCase);

    private static bool TryCompareNumeric(RdfLiteral left, RdfLiteral right, out int comparison)
    {
        int rank = Math.Max(NumericRank(left), NumericRank(right));
        if (rank <= 1 && TryParseExactDecimal(left, out BigInteger leftValue, out int leftScale) &&
            TryParseExactDecimal(right, out BigInteger rightValue, out int rightScale))
        {
            int scale = Math.Max(leftScale, rightScale);
            comparison = (leftValue * BigInteger.Pow(10, scale - leftScale)).CompareTo(
                rightValue * BigInteger.Pow(10, scale - rightScale));
            return true;
        }
        if (rank == 2 && Floating(left, out double lf, true) && Floating(right, out double rf, true) &&
            !double.IsNaN(lf) && !double.IsNaN(rf))
        {
            comparison = ((float)lf).CompareTo((float)rf);
            return true;
        }
        if (rank == 3 && Floating(left, out double leftDouble) && Floating(right, out double rightDouble) &&
            !Double.IsNaN(leftDouble) && !Double.IsNaN(rightDouble))
        {
            comparison = leftDouble.CompareTo(rightDouble);
            return true;
        }
        comparison = 0;
        return false;
    }

    private static int NumericRank(RdfLiteral literal) => literal.Datatype switch
    {
        "http://www.w3.org/2001/XMLSchema#integer" => 0,
        "http://www.w3.org/2001/XMLSchema#decimal" => 1,
        "http://www.w3.org/2001/XMLSchema#float" => 2,
        "http://www.w3.org/2001/XMLSchema#double" => 3,
        _ => IsIntegerSubtype(literal.Datatype) ? 0 : Int32.MaxValue
    };

    private static bool TryParseExactDecimal(RdfLiteral literal, out BigInteger value, out int scale)
    {
        string lexical = literal.LexicalForm.Trim(' ', '\t', '\r', '\n');
        bool negative = lexical.Length > 0 && lexical[0] == '-';
        int start = negative || lexical.Length > 0 && lexical[0] == '+' ? 1 : 0;
        int decimalPoint = lexical.IndexOf('.', start);
        scale = decimalPoint < 0 ? 0 : lexical.Length - decimalPoint - 1;
        string digits = decimalPoint < 0 ? lexical[start..] : lexical[start..decimalPoint] + lexical[(decimalPoint + 1)..];
        if (NumericRank(literal) == 0 && decimalPoint >= 0 ||
            digits.Length == 0 || digits.Any(character => character is < '0' or > '9') ||
            !BigInteger.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            value = default;
            scale = default;
            return false;
        }
        if (negative)
        {
            value = -value;
        }

        return !IsIntegerSubtype(literal.Datatype) || FitsIntegerSubtype(literal.Datatype!, value);
    }
}
