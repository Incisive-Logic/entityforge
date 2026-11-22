using System.Globalization;
using System.Numerics;
using System.Text;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static bool? BooleanValue(string lexical) => lexical.Trim(' ', '\t', '\r', '\n') switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => null
    };

    private static FilterResult EffectiveBoolean(RdfTerm? term)
    {
        if (term is not RdfLiteral literal)
        {
            return FilterResult.Error;
        }
        if (literal.Datatype == Xsd + "boolean")
        {
            return Result(BooleanValue(literal.LexicalForm) == true);
        }
        if (IsStringLiteral(literal))
        {
            return Result(literal.LexicalForm.Length != 0);
        }
        if (IsNumeric(literal))
        {
            if (NumericRank(literal) >= 2)
            {
                return Result(Floating(literal, out double number) && number != 0 && !double.IsNaN(number));
            }
            return Result(TryParseExactDecimal(literal, out var value, out _) && !value.IsZero);
        }
        return FilterResult.Error;
    }

    private static FilterResult EvaluateScalar(FilterExpression filter, IReadOnlyDictionary<string, RdfTerm> row, RdfSparqlReadView? database = null) => filter switch
    {
        ExistsExpression exists when database is not null => Evaluate(database, exists, row),
        EffectiveBooleanExpression boolean => EffectiveBoolean(EvaluateValue(boolean.Value, row, database)),
        ComparisonExpression comparison => EvaluateComparison(comparison, row, database),
        FunctionExpression function => EvaluateFunction(function, row, database),
        InExpression membership => EvaluateIn(membership, row, database),
        BinaryBooleanExpression binary => binary.Operator == "&&" ? And(EvaluateScalar(binary.Left, row, database), EvaluateScalar(binary.Right, row, database)) : Or(EvaluateScalar(binary.Left, row, database), EvaluateScalar(binary.Right, row, database)),
        NotExpression not => EvaluateScalar(not.Operand, row, database) switch { FilterResult.True => FilterResult.False, FilterResult.False => FilterResult.True, _ => FilterResult.Error },
        _ => FilterResult.Error
    };

    private static RdfTerm? EvaluateCall(CallExpression call, IReadOnlyDictionary<string, RdfTerm> row, RdfSparqlReadView? database = null)
    {
        if (call.Name.StartsWith('<'))
        {
            // An unknown extension IRI is valid call syntax but an expression
            // error. It never causes remote retrieval or arbitrary invocation.
            return null;
        }
        if (call.Name == "BNODE")
        {
            return EvaluateBlankNode(call, row, database);
        }
        if (call.Name == "UUID")
        {
            return new RdfIri("urn:uuid:" + Guid.NewGuid().ToString("D"));
        }
        if (call.Name == "STRUUID")
        {
            return new RdfLiteral(Guid.NewGuid().ToString("D"));
        }
        if (call.Name == "RAND")
        {
            return new RdfLiteral(Random.Shared.NextDouble().ToString("R", CultureInfo.InvariantCulture), Xsd + "double");
        }
        if (call.Name == "COALESCE")
        {
            foreach (ValueExpression expression in call.Arguments)
            {
                if (EvaluateValue(expression, row, database) is { } value)
                {
                    return value;
                }
            }
            return null;
        }
        RdfTerm?[] terms = call.Arguments.Select(argument => EvaluateValue(argument, row, database)).ToArray();
        if (terms.Any(term => term is null))
        {
            return null;
        }
        if (call.Name == "CONCAT")
        {
            if (terms.Any(term => term is not RdfLiteral literal || !IsStringLiteral(literal)))
            {
                return null;
            }
            var strings = terms.Cast<RdfLiteral>().ToArray();
            if (strings.Sum(literal => (long)literal.LexicalForm.Length) > 1_048_576)
            {
                throw new RdfSparqlWorkLimitException("CONCAT exceeds 1,048,576 characters.");
            }
            string? language = strings.Length > 0 && strings.All(literal => literal.Language == strings[0].Language) ? strings[0].Language : null;
            return new RdfLiteral(String.Concat(strings.Select(literal => literal.LexicalForm)), language: language);
        }
        if (terms[0] is not RdfLiteral first || !IsStringLiteral(first))
        {
            return null;
        }
        if (call.Name is "STRDT" or "STRLANG")
        {
            if (!IsSimpleStringLiteral(first))
            {
                return null;
            }
            if (call.Name == "STRDT" && terms[1] is RdfIri datatype)
            {
                return datatype.Value == RdfLiteral.LanguageStringDatatype ? null : new RdfLiteral(first.LexicalForm, datatype.Value);
            }
            if (call.Name == "STRLANG" && terms[1] is RdfLiteral language && IsSimpleStringLiteral(language) &&
                RdfTermValidation.IsLanguageTag(language.LexicalForm))
            {
                return new RdfLiteral(first.LexicalForm, language: language.LexicalForm);
            }
            return null;
        }
        if (call.Name == "REPLACE")
        {
            return ReplaceString(first, terms);
        }
        if (call.Name == "SUBSTR")
        {
            if (terms[1] is not RdfLiteral start || !IsNumeric(start) || NumericRank(start) != 0 ||
                !TryParseExactDecimal(start, out BigInteger from, out _))
            {
                return null;
            }
            BigInteger length = 0;
            if (terms.Length == 3 && (terms[2] is not RdfLiteral count || !IsNumeric(count) || NumericRank(count) != 0 ||
                !TryParseExactDecimal(count, out length, out _)))
            {
                return null;
            }
            BigInteger end = from + length;
            // Clamp exact integer bounds before narrowing. UTF-16 length is a
            // safe upper bound on scalar positions, including huge inputs.
            int lower = (int)BigInteger.Clamp(from - 1, 0, first.LexicalForm.Length);
            int upper = terms.Length == 2 ? first.LexicalForm.Length :
                (int)BigInteger.Clamp(end - 1, 0, first.LexicalForm.Length);
            string text = EntityForge.Db.Query.Planning.UnicodeText.SliceScalars(first.LexicalForm, lower, Math.Max(0, upper - lower));
            return new RdfLiteral(text, first.Datatype, first.Language);
        }
        if (terms[1] is not RdfLiteral second || !IsStringLiteral(second) || !CompatibleStringArguments(first, second))
        {
            return null;
        }
        int at = first.LexicalForm.IndexOf(second.LexicalForm, StringComparison.Ordinal);
        if (at < 0)
        {
            return new RdfLiteral(String.Empty);
        }
        return new RdfLiteral(call.Name == "STRBEFORE" ? first.LexicalForm[..at] : first.LexicalForm[(at + second.LexicalForm.Length)..],
            first.Datatype, first.Language);
    }

    private static RdfTerm? EvaluateConditional(ConditionalExpression conditional, IReadOnlyDictionary<string, RdfTerm> row, RdfSparqlReadView? database = null) =>
        EvaluateScalar(conditional.Condition, row, database) switch
        {
            FilterResult.True => EvaluateValue(conditional.True, row, database),
            FilterResult.False => EvaluateValue(conditional.False, row, database),
            _ => null
        };

    private static RdfTerm? BooleanTerm(FilterResult result) => result == FilterResult.Error ? null :
        new RdfLiteral(result == FilterResult.True ? "true" : "false", Xsd + "boolean");
}
