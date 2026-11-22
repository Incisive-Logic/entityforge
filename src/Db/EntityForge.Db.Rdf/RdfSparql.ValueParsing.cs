using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static ValueExpression ParseExpression(string[] tokens, ref int index, int depth = 0)
    {
        FilterExpression expression = ParseOr(tokens, ref index, depth);
        return expression is EffectiveBooleanExpression boolean ? boolean.Value : new BooleanValueExpression(expression);
    }

    private static ValueExpression ParseValueExpression(string[] tokens, ref int index, int depth = 0)
    {
        ValidateExpressionDepth(depth);
        ValueExpression value = ParseProduct(tokens, ref index, depth);
        while (index < tokens.Length)
        {
            string operation;
            if (tokens[index] is "+" or "-")
            {
                operation = tokens[index++];
            }
            else if (tokens[index].Length > 1 && tokens[index][0] is '+' or '-')
            {
                // A contiguous sign belongs to the numeric token. SPARQL's
                // additive grammar also admits that signed token after a
                // product, retaining multiplication/division precedence.
                operation = "+";
            }
            else
            {
                break;
            }
            value = new ArithmeticExpression(value, operation, ParseProduct(tokens, ref index, depth + 1));
        }
        return value;
    }

    private static ValueExpression ParseProduct(string[] tokens, ref int index, int depth)
    {
        ValidateExpressionDepth(depth);
        ValueExpression value = ParseUnaryValue(tokens, ref index, depth);
        while (index < tokens.Length && tokens[index] is "*" or "/")
        {
            string operation = tokens[index++];
            value = new ArithmeticExpression(value, operation, ParseUnaryValue(tokens, ref index, depth + 1));
        }
        return value;
    }

    private static ValueExpression ParseUnaryValue(string[] tokens, ref int index, int depth)
    {
        ValidateExpressionDepth(depth);
        if (index < tokens.Length && tokens[index] is "!" or "+" or "-")
        {
            string operation = tokens[index++];
            ValueExpression operand = ParseValuePrimary(tokens, ref index, depth + 1);
            return operation == "!"
                ? new BooleanValueExpression(new NotExpression(operand is BooleanValueExpression boolean
                    ? boolean.Filter : new EffectiveBooleanExpression(operand)))
                : new ValueFunctionExpression(operation, operand);
        }
        return ParseValuePrimary(tokens, ref index, depth);
    }

    private static ValueExpression ParseValuePrimary(string[] tokens, ref int index, int depth)
    {
        ValidateExpressionDepth(depth);
        if (index + 1 < tokens.Length && tokens[index].StartsWith('<') && tokens[index].EndsWith('>') && tokens[index + 1] == "(")
        {
            string name = tokens[index++];
            RequireToken(tokens, ref index, "(");
            if (index < tokens.Length && tokens[index].Equals("DISTINCT", StringComparison.OrdinalIgnoreCase))
            {
                index++;
            }
            var arguments = new List<ValueExpression>();
            if (index < tokens.Length && tokens[index] != ")")
            {
                while (true)
                {
                    arguments.Add(ParseExpression(tokens, ref index, depth + 1));
                    if (index >= tokens.Length || tokens[index] != ",")
                    {
                        break;
                    }
                    index++;
                }
            }
            RequireToken(tokens, ref index, ")");
            string iri = name[1..^1];
            if (arguments.Count == 1 && iri.StartsWith(Xsd, StringComparison.Ordinal) &&
                (iri[Xsd.Length..] is "string" or "boolean" or "integer" or "decimal" or "float" or "double" or "dateTime" || IsIntegerSubtype(iri)))
            {
                return new ValueFunctionExpression(iri, arguments[0]);
            }
            return new CallExpression(name, arguments);
        }
        bool notExists = index + 1 < tokens.Length && tokens[index].Equals("NOT", StringComparison.OrdinalIgnoreCase) &&
            tokens[index + 1].Equals("EXISTS", StringComparison.OrdinalIgnoreCase);
        if (notExists || index < tokens.Length && tokens[index].Equals("EXISTS", StringComparison.OrdinalIgnoreCase))
        {
            index += notExists ? 2 : 1;
            return new BooleanValueExpression(new ExistsExpression(ParseBracedGroup(tokens, ref index, null, depth + 1), notExists));
        }
        if (index < tokens.Length && tokens[index] == "(")
        {
            index++;
            ValueExpression nested = ParseExpression(tokens, ref index, depth + 1);
            RequireToken(tokens, ref index, ")");
            return nested;
        }
        if (index < tokens.Length && IsFilterFunction(tokens[index]))
        {
            return new BooleanValueExpression(ParseFunction(tokens, ref index, depth));
        }
        if (index < tokens.Length && tokens[index].Equals("IF", StringComparison.OrdinalIgnoreCase))
        {
            index++;
            RequireToken(tokens, ref index, "(");
            FilterExpression condition = ParseOr(tokens, ref index, depth + 1);
            RequireToken(tokens, ref index, ",");
            ValueExpression yes = ParseExpression(tokens, ref index, depth + 1);
            RequireToken(tokens, ref index, ",");
            ValueExpression no = ParseExpression(tokens, ref index, depth + 1);
            RequireToken(tokens, ref index, ")");
            return new ConditionalExpression(condition, yes, no);
        }
        if (index < tokens.Length && tokens[index].ToUpperInvariant() is "BNODE" or "NOW" or "UUID" or "STRUUID" or "RAND" or "COALESCE" or "CONCAT" or "STRBEFORE" or "STRAFTER" or "SUBSTR" or "STRDT" or "STRLANG" or "REPLACE")
        {
            string name = tokens[index++].ToUpperInvariant();
            RequireToken(tokens, ref index, "(");
            var arguments = new List<ValueExpression>();
            if (index < tokens.Length && tokens[index] != ")")
            {
                while (true)
                {
                    arguments.Add(ParseExpression(tokens, ref index, depth + 1));
                    if (index >= tokens.Length || tokens[index] != ",")
                    {
                        break;
                    }
                    index++;
                }
            }
            RequireToken(tokens, ref index, ")");
            bool valid = name switch
            {
                "NOW" or "UUID" or "STRUUID" or "RAND" => arguments.Count == 0,
                "BNODE" => arguments.Count is 0 or 1,
                "COALESCE" or "CONCAT" => true,
                "SUBSTR" => arguments.Count is 2 or 3,
                "REPLACE" => arguments.Count is 3 or 4,
                _ => arguments.Count == 2
            };
            if (!valid)
            {
                throw new InvalidDataException("Invalid function argument count.");
            }
            return new CallExpression(name, arguments);
        }
        if (index < tokens.Length && tokens[index].ToUpperInvariant() is "COUNT" or "SUM" or "MIN" or "MAX" or "AVG" or "SAMPLE" or "GROUP_CONCAT")
        {
            string function = tokens[index++].ToUpperInvariant();
            RequireToken(tokens, ref index, "(");
            bool distinct = index < tokens.Length && tokens[index].Equals("DISTINCT", StringComparison.OrdinalIgnoreCase);
            if (distinct)
            {
                index++;
            }
            ValueExpression? argument;
            if (index < tokens.Length && tokens[index] == "*")
            {
                index++;
                if (function != "COUNT")
                {
                    throw new InvalidDataException("Only COUNT accepts '*'.");
                }
                argument = null;
            }
            else
            {
                argument = ParseExpression(tokens, ref index, depth + 1);
                if (HasAggregate(argument))
                {
                    throw new InvalidDataException("Aggregates cannot be nested.");
                }
            }
            string separator = " ";
            if (index < tokens.Length && tokens[index] == ";")
            {
                if (function != "GROUP_CONCAT")
                {
                    throw new InvalidDataException("Only GROUP_CONCAT accepts a separator.");
                }
                index++;
                RequireToken(tokens, ref index, "SEPARATOR");
                RequireToken(tokens, ref index, "=");
                TermPattern term = ParsePatternTerm(tokens, ref index);
                if (term.Constant is not RdfLiteral literal || !IsSimpleStringLiteral(literal))
                {
                    throw new InvalidDataException("SEPARATOR requires a string literal.");
                }
                separator = literal.LexicalForm;
            }
            RequireToken(tokens, ref index, ")");
            return new AggregateValueExpression(new(function, argument, String.Empty, distinct, separator));
        }
        return ParseValueAtom(tokens, ref index, depth);
    }

    private static void RequireToken(string[] tokens, ref int index, string token)
    {
        if (index >= tokens.Length || !tokens[index++].Equals(token, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Expected '{token}'.");
        }
    }

    private static bool HasAggregate(ValueExpression value) => value switch
    {
        AggregateValueExpression => true,
        ValueFunctionExpression function => HasAggregate(function.Argument),
        ArithmeticExpression arithmetic => HasAggregate(arithmetic.Left) || HasAggregate(arithmetic.Right),
        CallExpression call => call.Arguments.Any(HasAggregate),
        BooleanValueExpression boolean => FilterHasAggregate(boolean.Filter),
        ConditionalExpression conditional => FilterHasAggregate(conditional.Condition) || HasAggregate(conditional.True) || HasAggregate(conditional.False),
        _ => false
    };

    private static bool FilterHasAggregate(FilterExpression filter)
    {
        bool found = false;
        MapFilterValues(filter, value =>
        {
            found |= HasAggregate(value);
            return value;
        });
        return found;
    }

    private static SelectProjection ParseSelectProjection(string source, IReadOnlyDictionary<string, string> prefixes)
    {
        if (source == "*")
        {
            return new(true, [], [], []);
        }
        string[] tokens = Tokenize(source, prefixes);
        var variables = new List<string>();
        var aggregates = new List<AggregateProjection>();
        var computed = new List<BindElement>();
        int index = 0;
        while (index < tokens.Length)
        {
            string alias;
            if (tokens[index].StartsWith('?'))
            {
                alias = tokens[index++][1..];
            }
            else
            {
                RequireToken(tokens, ref index, "(");
                ValueExpression value = ParseExpression(tokens, ref index);
                RequireToken(tokens, ref index, "AS");
                if (index >= tokens.Length || !tokens[index].StartsWith('?'))
                {
                    throw new InvalidDataException("A projection expression requires a variable alias.");
                }
                alias = tokens[index++][1..];
                RequireToken(tokens, ref index, ")");
                if (value is AggregateValueExpression aggregate)
                {
                    aggregates.Add(aggregate.Aggregate with
                    {
                        Alias = alias
                    });
                }
                else
                {
                    computed.Add(new(value, alias));
                }
            }
            if (variables.Contains(alias, StringComparer.Ordinal))
            {
                throw new InvalidDataException("A projection variable is duplicated.");
            }
            variables.Add(alias);
        }
        if (variables.Count == 0)
        {
            throw new InvalidDataException("A projection cannot be empty.");
        }
        return new(false, variables, aggregates, computed);
    }
}
