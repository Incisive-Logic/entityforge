using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Numerics;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static Group ParseGroup(string[] tokens, ref int index, TermPattern? graph, bool requireClose,
        int depth)
    {
        if (depth > MaximumGroupDepth)
        {
            throw new RdfSparqlWorkLimitException(
                $"A SPARQL query cannot exceed {MaximumGroupDepth} nested graph groups.");
        }

        var elements = new List<GroupElement>();
        var filters = new List<FilterExpression>();
        if (requireClose && index > 0 && tokens[index - 1] == "{" && index < tokens.Length &&
            tokens[index].Equals("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            index--;
            return new([new SubqueryElement(ParseSubquery(tokens, ref index))], []);
        }
        PatternBlankScope? blanks = null;
        while (index < tokens.Length && tokens[index] != "}")
        {
            if (tokens[index].Equals("FILTER", StringComparison.OrdinalIgnoreCase))
            {
                FilterExpression filter = ParseFilter(tokens, ref index, graph, depth + 1);
                MapFilterValues(filter, value => HasAggregate(value)
                    ? throw new InvalidDataException("Aggregates are not permitted in FILTER.") : value);
                filters.Add(filter);
                if (index < tokens.Length && tokens[index] == ".")
                {
                    index++;
                }

                continue;
            }
            if (tokens[index].ToUpperInvariant() is "OPTIONAL" or "MINUS" or "VALUES" or "BIND" or "GRAPH" or "SERVICE" or "{")
            {
                blanks = null;
            }
            if (tokens[index].Equals("OPTIONAL", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                elements.Add(new OptionalElement(ParseBracedGroup(tokens, ref index, graph, depth + 1)));
                if (index < tokens.Length && tokens[index] == ".")
                {
                    index++;
                }

                continue;
            }
            if (tokens[index].Equals("MINUS", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                elements.Add(new MinusElement(ParseBracedGroup(tokens, ref index, graph, depth + 1)));
                if (index < tokens.Length && tokens[index] == ".")
                {
                    index++;
                }

                continue;
            }
            if (tokens[index].Equals("VALUES", StringComparison.OrdinalIgnoreCase))
            {
                elements.Add(ParseValues(tokens, ref index));
                if (index < tokens.Length && tokens[index] == ".")
                {
                    index++;
                }

                continue;
            }
            if (tokens[index].Equals("BIND", StringComparison.OrdinalIgnoreCase))
            {
                BindElement bind = ParseBind(tokens, ref index);
                if (elements.Any(element => IntroducesVariable(element, bind.Variable)))
                {
                    throw new InvalidDataException("BIND cannot replace an in-scope variable.");
                }
                elements.Add(bind);
                if (index < tokens.Length && tokens[index] == ".")
                {
                    index++;
                }

                continue;
            }
            if (tokens[index].Equals("SERVICE", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                bool silent = index < tokens.Length && tokens[index].Equals("SILENT", StringComparison.OrdinalIgnoreCase);
                if (silent)
                {
                    index++;
                }
                var endpoint = ParsePatternTerm(tokens, ref index);
                if (endpoint.Constant is not RdfIri && endpoint.Variable is null)
                {
                    throw new InvalidDataException("SERVICE requires an endpoint IRI or variable.");
                }
                int start = index;
                Group remote = ParseBracedGroup(tokens, ref index, null, depth + 1);
                elements.Add(new ServiceElement(remote, endpoint, String.Join(" ", tokens[start..index]), silent, Guid.NewGuid()));
                if (index < tokens.Length && tokens[index] == ".")
                {
                    index++;
                }
                continue;
            }
            if (tokens[index].Equals("GRAPH", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 2 >= tokens.Length || tokens[index + 2] != "{")
                {
                    throw new InvalidDataException("A SPARQL GRAPH clause requires a graph term and a group.");
                }

                index++;
                TermPattern graphPattern = ParsePatternTerm(tokens, ref index);
                ValidateGraphTerm(graphPattern);
                if (index >= tokens.Length || tokens[index] != "{")
                {
                    throw new InvalidDataException("A SPARQL GRAPH clause requires a graph term and a group.");
                }

                Group named = ParseBracedGroup(tokens, ref index, null, depth + 1);
                elements.Add(new NamedGraphElement(named, graphPattern));
                if (index < tokens.Length && tokens[index] == ".")
                {
                    index++;
                }

                continue;
            }
            if (tokens[index] == "{")
            {
                var branches = new List<Group> { ParseBracedGroup(tokens, ref index, graph, depth + 1) };
                while (index < tokens.Length && tokens[index].Equals("UNION", StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                    branches.Add(ParseBracedGroup(tokens, ref index, graph, depth + 1));
                }
                elements.Add(branches.Count == 1
                    ? new NestedGroupElement(branches[0])
                    : new UnionElement(branches));
                if (index < tokens.Length && tokens[index] == ".")
                {
                    index++;
                }

                continue;
            }
            blanks ??= new(index);
            elements.AddRange(ParseTriples(tokens, ref index, graph, blanks).Select(pattern => new PatternElement(pattern)));
        }
        if (requireClose)
        {
            if (index >= tokens.Length || tokens[index] != "}")
            {
                throw new InvalidDataException("A SPARQL group must be closed.");
            }

            index++;
        }
        return new(elements, filters);
    }

    private static ValuesElement ParseValues(string[] tokens, ref int index)
    {
        index++;
        var variables = new List<string>();
        bool tabular = index < tokens.Length && tokens[index] == "(";
        if (tabular)
        {
            index++;
            while (index < tokens.Length && tokens[index] != ")")
            {
                if (!tokens[index].StartsWith('?'))
                {
                    throw new InvalidDataException("A VALUES column must be a variable.");
                }

                variables.Add(tokens[index++][1..]);
            }
            if (index >= tokens.Length || tokens[index++] != ")")
            {
                throw new InvalidDataException("A VALUES variable list must be closed.");
            }
        }
        else if (index < tokens.Length && tokens[index].StartsWith('?'))
        {
            variables.Add(tokens[index++][1..]);
        }

        if (variables.Count == 0 && !tabular || variables.Distinct(StringComparer.Ordinal).Count() != variables.Count)
        {
            throw new InvalidDataException("A VALUES clause requires unique variables.");
        }

        if (index >= tokens.Length || tokens[index++] != "{")
        {
            throw new InvalidDataException("A VALUES clause requires a braced value list.");
        }

        var rows = new List<IReadOnlyList<RdfTerm?>>();
        while (index < tokens.Length && tokens[index] != "}")
        {
            bool tuple = tabular && tokens[index] == "(";
            if (tabular && !tuple)
            {
                throw new InvalidDataException("A tabular VALUES row must be parenthesized.");
            }

            if (tuple)
            {
                index++;
            }

            var values = new List<RdfTerm?>(variables.Count);
            while (values.Count < variables.Count)
            {
                if (index >= tokens.Length || tokens[index] is "}" or ")")
                {
                    throw new InvalidDataException("A VALUES row has the wrong number of columns.");
                }

                if (tokens[index].Equals("UNDEF", StringComparison.OrdinalIgnoreCase))
                {
                    values.Add(null);
                    index++;
                    continue;
                }
                TermPattern term = ParsePatternTerm(tokens, ref index);
                if (term.Constant is null)
                {
                    throw new InvalidDataException("VALUES entries must be concrete RDF terms or UNDEF.");
                }

                values.Add(term.Constant);
            }
            if (tuple && (index >= tokens.Length || tokens[index++] != ")"))
            {
                throw new InvalidDataException("A VALUES row must be closed.");
            }

            rows.Add(values);
            if (rows.Count > MaximumValues)
            {
                throw new RdfSparqlWorkLimitException(
                    $"SPARQL VALUES exceeded {MaximumValues} entries.");
            }
        }
        if (index >= tokens.Length || tokens[index++] != "}")
        {
            throw new InvalidDataException("A VALUES clause must be closed.");
        }

        return new(variables, rows);
    }

    private static bool IntroducesVariable(GroupElement element, string variable)
    {
        bool Term(TermPattern? term) => term is not null && (term.Variable == variable ||
            term.Quoted is { } quoted && (Term(quoted.Subject) || Term(quoted.Predicate) || Term(quoted.Object)));
        bool Body(Group group) => group.Elements.Any(item => IntroducesVariable(item, variable));
        return element switch
        {
            PatternElement pattern => Term(pattern.Pattern.Subject) || Term(pattern.Pattern.Predicate) ||
                Term(pattern.Pattern.Object) || Term(pattern.Pattern.Graph),
            BindElement bind => bind.Variable == variable,
            ValuesElement values => values.Variables.Contains(variable),
            NamedGraphElement named => Term(named.Graph) || Body(named.Group),
            ServiceElement service => Term(service.Endpoint) || Body(service.Group),
            NestedGroupElement nested => Body(nested.Group),
            OptionalElement optional => Body(optional.Group),
            UnionElement union => union.Groups.Any(Body),
            SubqueryElement subquery => subquery.Query.Projection.SelectAll
                ? Body(subquery.Query.Body) || subquery.Query.Modifiers.TrailingValues?.Variables.Contains(variable) == true
                : subquery.Query.Projection.OutputVariables.Contains(variable),
            _ => false
        };
    }

    private static BindElement ParseBind(string[] tokens, ref int index)
    {
        index++;
        if (index >= tokens.Length || tokens[index++] != "(")
        {
            throw new InvalidDataException("BIND requires parentheses.");
        }

        ValueExpression value = ParseExpression(tokens, ref index);
        if (HasAggregate(value))
        {
            throw new InvalidDataException("Aggregates are not permitted in BIND.");
        }
        if (index >= tokens.Length || !tokens[index++].Equals("AS", StringComparison.OrdinalIgnoreCase) ||
            index >= tokens.Length || !tokens[index].StartsWith('?'))
        {
            throw new InvalidDataException("BIND requires AS followed by a variable.");
        }

        string variable = tokens[index++][1..];
        if (index >= tokens.Length || tokens[index++] != ")")
        {
            throw new InvalidDataException("BIND must be closed.");
        }

        return new(value, variable);
    }

    private static Group ParseBracedGroup(string[] tokens, ref int index, TermPattern? graph, int depth)
    {
        if (index + 1 < tokens.Length && tokens[index] == "{" && tokens[index + 1].Equals("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return new([new SubqueryElement(ParseSubquery(tokens, ref index))], []);
        }
        if (index >= tokens.Length || tokens[index++] != "{")
        {
            throw new InvalidDataException("A SPARQL group must begin with '{'.");
        }

        Group group = ParseGroup(tokens, ref index, graph, true, depth);
        return group;
    }

    private static int CountPatterns(Group group) => group.Elements.Sum(element => element switch
    {
        PatternElement => 1,
        OptionalElement optional => CountPatterns(optional.Group),
        NestedGroupElement nested => CountPatterns(nested.Group),
        UnionElement union => union.Groups.Sum(CountPatterns),
        MinusElement minus => CountPatterns(minus.Group),
        SubqueryElement subquery => CountPatterns(subquery.Query.Body),
        ValuesElement => 0,
        BindElement => 0,
        _ => 0
    }) + group.Filters.Sum(filter => filter is ExistsExpression exists ? CountPatterns(exists.Group) : 0);

    private static IReadOnlyList<Pattern> ParseTriples(string[] tokens, ref int index, TermPattern? graph, PatternBlankScope blanks)
    {
        var patterns = new List<Pattern>();
        bool structured = index < tokens.Length && tokens[index] is "[" or "(";
        TermPattern subject = ParseGraphNode(tokens, ref index, graph, blanks, patterns, 0);
        if (!structured || index < tokens.Length && !TripleBoundary(tokens[index]))
        {
            ParsePredicateObjectList(tokens, ref index, subject, graph, blanks, patterns, 0);
        }
        if (patterns.Count == 0 || index < tokens.Length && !TripleBoundary(tokens[index]))
        {
            throw new InvalidDataException("A triple block requires a predicate and a dot between adjacent triples.");
        }
        if (index < tokens.Length && tokens[index] == ".")
        {
            index++;
        }
        return patterns;
    }

    private static bool TripleBoundary(string token) => token is "." or "}" or "]" or "{" ||
        token.ToUpperInvariant() is "FILTER" or "OPTIONAL" or "MINUS" or "GRAPH" or "SERVICE" or "BIND" or "VALUES";

    private static void ParsePredicateObjectList(string[] tokens, ref int index, TermPattern subject,
        TermPattern? graph, PatternBlankScope blanks, List<Pattern> patterns, int depth)
    {
        while (true)
        {
            TermPattern? predicate = null;
            RdfPathExpression? path = null;
            int predicateStart = index;
            if (index < tokens.Length && tokens[index] is "^" or "(" or "!")
            {
                path = ParsePath(tokens, ref index);
            }
            else
            {
                predicate = ParsePatternTerm(tokens, ref index, allowPredicateShorthand: true);
                if (predicate.Constant is RdfIri && index < tokens.Length && IsPathOperator(tokens[index]))
                {
                    index = predicateStart;
                    predicate = null;
                    path = ParsePath(tokens, ref index);
                }
            }
            if (predicate?.Constant is not null and not RdfIri ||
                predicate?.Quoted is not null)
            {
                throw new InvalidDataException("A SPARQL triple pattern has an invalid predicate.");
            }

            while (true)
            {
                if (index >= tokens.Length || tokens[index] is "," or ";" or "." or "}" or "]")
                {
                    throw new InvalidDataException("A SPARQL object list requires an object after its predicate or comma.");
                }
                TermPattern obj = ParseGraphNode(tokens, ref index, graph, blanks, patterns, depth);
                patterns.Add(new Pattern(subject, predicate, obj, graph, path));
                if (index >= tokens.Length || tokens[index] != ",")
                {
                    break;
                }
                index++;
            }

            if (index >= tokens.Length || tokens[index] != ";")
            {
                break;
            }
            do
            {
                index++;
            }
            while (index < tokens.Length && tokens[index] == ";");
            // SPARQL permits trailing semicolons before the next graph-pattern boundary.
            if (index >= tokens.Length || TripleBoundary(tokens[index]))
            {
                break;
            }
        }
    }

    private static ParsedSelectQuery ParseSubquery(string[] tokens, ref int index)
    {
        int open = index++;
        int depth = 1;
        while (index < tokens.Length && depth > 0)
        {
            if (tokens[index] == "{")
            {
                depth++;
            }
            else if (tokens[index] == "}")
            {
                depth--;
            }

            index++;
        }
        if (depth != 0)
        {
            throw new InvalidDataException("A SPARQL subquery must be closed.");
        }

        string source = String.Join(' ', tokens[(open + 1)..(index - 1)]);
        var parsed = ParseSelectQuery(source);
        if (parsed.Dataset is not null)
        {
            throw new InvalidDataException("A subquery cannot declare a dataset.");
        }
        return parsed;
    }

    private static FilterExpression ParseFilter(string[] tokens, ref int index, TermPattern? graph, int depth)
    {
        index++;
        return ParseConstraint(tokens, ref index, graph, depth);
    }

    private static FilterExpression ParseConstraint(string[] tokens, ref int index, TermPattern? graph, int depth)
    {
        bool negatedExists = index < tokens.Length && tokens[index].Equals("NOT", StringComparison.OrdinalIgnoreCase);
        if (negatedExists)
        {
            index++;
        }

        if (index < tokens.Length && tokens[index].Equals("EXISTS", StringComparison.OrdinalIgnoreCase))
        {
            index++;
            return new ExistsExpression(ParseBracedGroup(tokens, ref index, graph, depth), negatedExists);
        }
        if (negatedExists)
        {
            throw new InvalidDataException("A constraint starting with NOT must be followed by EXISTS.");
        }

        if (index < tokens.Length && IsFilterFunction(tokens[index]))
        {
            return ParseFunction(tokens, ref index);
        }
        if (index + 1 < tokens.Length && tokens[index + 1] == "(" &&
            (tokens[index].StartsWith('<') && tokens[index].EndsWith('>') || Regex.IsMatch(tokens[index], @"^[A-Za-z_][A-Za-z0-9_]*$")))
        {
            return new EffectiveBooleanExpression(ParseValuePrimary(tokens, ref index, depth + 1));
        }

        if (index >= tokens.Length || tokens[index++] != "(")
        {
            throw new InvalidDataException("A constraint must be a parenthesized expression or a function call.");
        }

        FilterExpression expression = ParseOr(tokens, ref index, 0);
        if (index >= tokens.Length || tokens[index++] != ")")
        {
            throw new InvalidDataException("A parenthesized constraint must be closed.");
        }

        return expression;
    }

    private static FilterExpression ParseOr(string[] tokens, ref int index, int depth)
    {
        ValidateExpressionDepth(depth);
        FilterExpression expression = ParseAnd(tokens, ref index, depth);
        while (index < tokens.Length && tokens[index] == "||")
        {
            index++;
            expression = new BinaryBooleanExpression(expression, "||", ParseAnd(tokens, ref index, depth));
        }
        return expression;
    }

    private static FilterExpression ParseAnd(string[] tokens, ref int index, int depth)
    {
        FilterExpression expression = ParseRelational(tokens, ref index, depth);
        while (index < tokens.Length && tokens[index] == "&&")
        {
            index++;
            expression = new BinaryBooleanExpression(expression, "&&", ParseRelational(tokens, ref index, depth));
        }
        return expression;
    }

    private static FilterExpression ParseRelational(string[] tokens, ref int index, int depth)
    {
        ValidateExpressionDepth(depth);
        ValueExpression left = ParseValueExpression(tokens, ref index, depth);
        bool negateIn = index < tokens.Length && tokens[index].Equals("NOT", StringComparison.OrdinalIgnoreCase);
        if (negateIn)
        {
            index++;
        }

        if (index < tokens.Length && tokens[index].Equals("IN", StringComparison.OrdinalIgnoreCase))
        {
            index++;
            if (index >= tokens.Length || tokens[index++] != "(")
            {
                throw new InvalidDataException("FILTER IN requires a parenthesized value list.");
            }

            var values = new List<ValueExpression>();
            while (index < tokens.Length && tokens[index] != ")")
            {
                values.Add(ParseExpression(tokens, ref index, depth + 1));
                if (index < tokens.Length && tokens[index] == ",")
                {
                    index++;
                }
                else
                {
                    break;
                }
            }
            if (index >= tokens.Length || tokens[index++] != ")")
            {
                throw new InvalidDataException("FILTER IN requires a closed value list.");
            }

            return new InExpression(left, values, negateIn);
        }
        if (negateIn)
        {
            throw new InvalidDataException("FILTER NOT must be followed by IN.");
        }

        if (index >= tokens.Length || tokens[index] is not ("=" or "!=" or "<" or "<=" or ">" or ">="))
        {
            return left is BooleanValueExpression boolean ? boolean.Filter : new EffectiveBooleanExpression(left);
        }

        string operation = tokens[index++];
        return new ComparisonExpression(left, operation, ParseValueExpression(tokens, ref index, depth));
    }

    private static void ValidateExpressionDepth(int depth)
    {
        if (depth > MaximumExpressionDepth)
        {
            throw new RdfSparqlWorkLimitException(
                $"A SPARQL FILTER cannot exceed {MaximumExpressionDepth} nested expressions.");
        }
    }

    private static FilterExpression ParseFunction(string[] tokens, ref int index, int depth = 0)
    {
        string function = tokens[index++].ToUpperInvariant();
        if (index >= tokens.Length || tokens[index++] != "(")
        {
            throw new InvalidDataException($"FILTER function {function} requires parentheses.");
        }

        if (function == "BOUND")
        {
            if (index >= tokens.Length || !tokens[index].StartsWith('?'))
            {
                throw new InvalidDataException("BOUND requires a variable argument.");
            }
            ValueExpression variable = new TermValueExpression(ParsePatternTerm(tokens, ref index));
            RequireToken(tokens, ref index, ")");
            return new FunctionExpression(function, variable, null, null);
        }
        ValueExpression first = ParseExpression(tokens, ref index, depth + 1);
        ValueExpression? second = null;
        ValueExpression? third = null;
        if (index < tokens.Length && tokens[index] == ",")
        {
            index++;
            second = ParseExpression(tokens, ref index, depth + 1);
        }
        if (index < tokens.Length && tokens[index] == ",")
        {
            index++;
            third = ParseExpression(tokens, ref index, depth + 1);
        }
        if (index >= tokens.Length || tokens[index++] != ")")
        {
            throw new InvalidDataException($"FILTER function {function} must be closed.");
        }

        int argumentCount = 1 + (second is null ? 0 : 1) + (third is null ? 0 : 1);
        bool valid = function switch
        {
            "BOUND" or "ISIRI" or "ISURI" or "ISNUMERIC" or "ISBLANK" or "ISLITERAL" => argumentCount == 1,
            "STRSTARTS" or "STRENDS" or "CONTAINS" or "LANGMATCHES" or "SAMETERM" => argumentCount == 2,
            "REGEX" => argumentCount is 2 or 3,
            _ => false
        };
        if (!valid)
        {
            throw new InvalidDataException($"FILTER function {function} has the wrong number of arguments.");
        }

        return new FunctionExpression(function, first, second, third);
    }

    private static ValueExpression ParseValueAtom(string[] tokens, ref int index, int depth = 0)
    {
        ValidateExpressionDepth(depth);
        if (index < tokens.Length && tokens[index].ToUpperInvariant() is
            "IRI" or "URI" or "YEAR" or "MONTH" or "DAY" or "HOURS" or "MINUTES" or "SECONDS" or "TIMEZONE" or "TZ" or "STR" or "LANG" or "DATATYPE" or "STRLEN" or "UCASE" or "LCASE" or "ABS" or "CEIL" or "FLOOR" or "ROUND" or "ENCODE_FOR_URI" or "MD5" or "SHA1" or "SHA256" or "SHA384" or "SHA512")
        {
            string function = tokens[index++].ToUpperInvariant();
            if (index >= tokens.Length || tokens[index++] != "(")
            {
                throw new InvalidDataException($"Value function {function} requires parentheses.");
            }

            ValueExpression argument = ParseExpression(tokens, ref index, depth + 1);
            if (index >= tokens.Length || tokens[index++] != ")")
            {
                throw new InvalidDataException($"Value function {function} must be closed.");
            }

            return new ValueFunctionExpression(function, argument);
        }
        return new TermValueExpression(ParsePatternTerm(tokens, ref index));
    }

    private static bool IsFilterFunction(string token) => token.Equals("BOUND", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("isIRI", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("isURI", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("isNumeric", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("STRENDS", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("isBlank", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("isLiteral", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("STRSTARTS", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("CONTAINS", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("LANGMATCHES", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("sameTerm", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("REGEX", StringComparison.OrdinalIgnoreCase);

    private static TermPattern ParsePatternTerm(string[] tokens, ref int index, int depth = 0, PatternBlankScope? blanks = null, bool allowPredicateShorthand = false)
    {
        if (index >= tokens.Length || tokens[index] is "{" or "}" or "[" or "]" or "." or ")" or "=" or "!=" or ">>")
        {
            throw new InvalidDataException("A SPARQL term is missing or incomplete.");
        }

        string token = tokens[index++];
        if (token == "a")
        {
            if (!allowPredicateShorthand)
            {
                throw new InvalidDataException("The shorthand 'a' is only permitted as a predicate.");
            }
            return new(null, new RdfIri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"), null);
        }
        if (token.StartsWith("_:", StringComparison.Ordinal) || token == "[]")
        {
            if (blanks is null)
            {
                throw new InvalidDataException("A blank-node pattern is only valid in subject or object position.");
            }
            return token == "[]" ? new("$anonymous:" + index, null, null) : blanks.Label(token[2..]);
        }
        if (token.StartsWith('?'))
        {
            return new(token[1..], null, null);
        }

        if (token != "<<")
        {
            return new(null, ParseTerm(token), null);
        }

        if (depth >= RdfTripleTerm.MaximumNestingDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(tokens),
                $"A SPARQL triple-term pattern cannot exceed {RdfTripleTerm.MaximumNestingDepth} levels.");
        }

        TermPattern subject = ParsePatternTerm(tokens, ref index, depth + 1, blanks);
        TermPattern predicate = ParsePatternTerm(tokens, ref index, depth + 1, allowPredicateShorthand: true);
        TermPattern obj = ParsePatternTerm(tokens, ref index, depth + 1, blanks);
        if (index >= tokens.Length || tokens[index++] != ">>" || subject.Constant is RdfLiteral ||
            predicate.Constant is not null and not RdfIri || predicate.Quoted is not null)
        {
            throw new InvalidDataException("A SPARQL quoted-triple pattern is invalid.");
        }

        return new(null, null, new(subject, predicate, obj));
    }

    private static Modifiers ParseModifiers(string tail, IReadOnlyDictionary<string, string>? prefixes = null,
        int maximumWindow = MaximumResults)
    {
        FilterExpression? having = null;
        string[] tokens = Tokenize(tail, prefixes ?? new Dictionary<string, string>(StringComparer.Ordinal));
        int index = 0;
        bool At(string keyword) => index < tokens.Length && tokens[index].Equals(keyword, StringComparison.OrdinalIgnoreCase);
        void Require(string keyword)
        {
            if (!At(keyword))
            {
                throw new InvalidDataException($"Expected {keyword} in solution modifiers.");
            }
            index++;
        }
        var groups = new List<string>();
        var groupExpressions = new List<BindElement>();
        if (At("GROUP"))
        {
            index++;
            Require("BY");
            while (index < tokens.Length && !At("HAVING") && !At("ORDER") && !At("LIMIT") && !At("OFFSET") && !At("VALUES"))
            {
                if (tokens[index].StartsWith('?'))
                {
                    groups.Add(tokens[index++][1..]);
                    continue;
                }
                bool parenthesized = At("(");
                if (parenthesized)
                {
                    index++;
                }
                ValueExpression value;
                if (parenthesized)
                {
                    value = ParseExpression(tokens, ref index);
                }
                else
                {
                    // A BuiltInCall (including EXISTS) or FunctionCall is one
                    // group condition. Arithmetic around it needs parentheses.
                    FilterExpression constraint = ParseConstraint(tokens, ref index, null, 0);
                    value = constraint is EffectiveBooleanExpression boolean
                        ? boolean.Value : new BooleanValueExpression(constraint);
                }
                if (HasAggregate(value))
                {
                    throw new InvalidDataException("GROUP BY cannot contain an aggregate.");
                }
                string? alias = null;
                if (parenthesized && At("AS"))
                {
                    index++;
                    if (index >= tokens.Length || !tokens[index].StartsWith('?'))
                    {
                        throw new InvalidDataException("A grouping alias must be a variable.");
                    }
                    alias = tokens[index++][1..];
                }
                if (parenthesized)
                {
                    Require(")");
                }
                string? variable = value is TermValueExpression { Term.Variable: { } name } ? name : null;
                bool explicitAlias = alias is not null;
                alias ??= variable ?? "$group" + groups.Count;
                groups.Add(alias);
                // An explicit AS remains an assignment even when its operand
                // has the same name. Retain it so the shared scope check can
                // reject rebinding a graph-pattern or earlier grouping variable.
                if (explicitAlias || alias != variable)
                {
                    groupExpressions.Add(new(value, alias));
                }
            }
            if (groups.Count == 0)
            {
                throw new InvalidDataException("GROUP BY requires variables.");
            }
        }
        if (At("HAVING"))
        {
            index++;
            do
            {
                FilterExpression condition = ParseConstraint(tokens, ref index, null, 0);
                having = having is null ? condition : new BinaryBooleanExpression(having, "&&", condition);
            }
            while (index < tokens.Length && !At("ORDER") && !At("LIMIT") && !At("OFFSET") && !At("VALUES"));
        }
        var order = new List<OrderCondition>();
        if (At("ORDER"))
        {
            index++;
            Require("BY");
            while (index < tokens.Length && !At("LIMIT") && !At("OFFSET") && !At("VALUES"))
            {
                bool descending = At("DESC");
                bool directed = descending || At("ASC");
                if (directed)
                {
                    index++;
                }
                bool parenthesized = At("(");
                if (directed && !parenthesized)
                {
                    throw new InvalidDataException("ASC/DESC requires parentheses.");
                }
                if (parenthesized)
                {
                    index++;
                }
                ValueExpression value;
                if (parenthesized)
                {
                    value = ParseExpression(tokens, ref index);
                    Require(")");
                }
                else if (index < tokens.Length && tokens[index].StartsWith('?'))
                {
                    // OrderCondition admits a single variable, not an arbitrary
                    // unbracketed expression beginning with one.
                    value = ParseValuePrimary(tokens, ref index, 0);
                }
                else
                {
                    FilterExpression constraint = ParseConstraint(tokens, ref index, null, 0);
                    value = constraint is EffectiveBooleanExpression boolean
                        ? boolean.Value : new BooleanValueExpression(constraint);
                }
                order.Add(new(value, descending));
                if (order.Count > 16)
                {
                    throw new RdfSparqlWorkLimitException("ORDER BY supports at most 16 conditions.");
                }
            }
            if (order.Count == 0)
            {
                throw new InvalidDataException("ORDER BY requires a condition.");
            }
        }
        int? limit = null;
        int? offset = null;
        while (index < tokens.Length && !At("VALUES"))
        {
            bool isLimit = At("LIMIT");
            if (!isLimit && !At("OFFSET"))
            {
                throw new InvalidDataException("Invalid solution modifier.");
            }
            if (isLimit ? limit.HasValue : offset.HasValue)
            {
                throw new InvalidDataException("Duplicate solution modifier.");
            }
            index++;
            if (index >= tokens.Length || !Int32.TryParse(tokens[index++], NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            {
                throw new InvalidDataException("LIMIT/OFFSET requires a non-negative 32-bit integer.");
            }
            if (value > maximumWindow)
            {
                throw new ArgumentOutOfRangeException(nameof(tail));
            }
            if (isLimit)
            {
                limit = value;
            }
            else
            {
                offset = value;
            }
        }
        int skip = offset ?? 0;
        int take = limit ?? maximumWindow - skip;
        if (take > maximumWindow - skip)
        {
            throw new ArgumentOutOfRangeException(nameof(tail));
        }
        ValuesElement? values = At("VALUES") ? ParseValues(tokens, ref index) : null;
        if (index != tokens.Length)
        {
            throw new InvalidDataException("VALUES must be the final query clause.");
        }
        return new(groups, order, take, skip, having)
        {
            GroupExpressions = groupExpressions,
            TrailingValues = values
        };
    }

    private static ParsedSelectQuery ParseSelectQuery(string query, int maximumWindow = MaximumResults)
    {
        if (ParseDataset(query) is { } dataset)
        {
            return ParseSelectQuery(dataset.Query, maximumWindow) with
            {
                Dataset = dataset
            };
        }
        (string queryBody, IReadOnlyDictionary<string, string> prefixes) = ParseProlog(query);
        string[] tokens = Tokenize(queryBody, prefixes);
        int index = 0;
        RequireToken(tokens, ref index, "SELECT");
        bool distinct = index < tokens.Length && tokens[index].ToUpperInvariant() is "DISTINCT" or "REDUCED";
        if (distinct)
        {
            index++;
        }
        int opening = -1, projectionDepth = 0;
        for (int position = index; position < tokens.Length; position++)
        {
            if (tokens[position] == "{" && projectionDepth == 0)
            {
                opening = position;
                break;
            }
            projectionDepth += tokens[position] == "(" ? 1 : tokens[position] == ")" ? -1 : 0;
            if (projectionDepth < 0)
            {
                throw new InvalidDataException("Unbalanced SELECT projection parentheses.");
            }
        }
        if (opening < 0)
        {
            throw new InvalidDataException("SELECT requires a graph group.");
        }
        int projectionEnd = opening > index && tokens[opening - 1].Equals("WHERE", StringComparison.OrdinalIgnoreCase) ? opening - 1 : opening;
        SelectProjection projection = ParseSelectProjection(String.Join(" ", tokens[index..projectionEnd]), prefixes);
        index = opening + 1;
        Group body = ParseGroup(tokens, ref index, null, true, 0);
        ValidatePatternBlankScopes(body);
        if (CountPatterns(body) > MaximumPatterns)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Too many triple patterns.");
        }
        Modifiers modifiers = ParseModifiers(String.Join(" ", tokens[index..]), maximumWindow: maximumWindow);
        body = ApplyGroupingExpressions(body, modifiers);
        (projection, modifiers) = PrepareExpressions(projection, modifiers);
        modifiers = modifiers with
        {
            GroupExpressions = []
        };
        foreach (string alias in projection.Computed.Select(binding => binding.Variable).Concat(projection.Aggregates.Select(aggregate => aggregate.Alias)))
        {
            if (body.Elements.Any(element => IntroducesVariable(element, alias)) || modifiers.TrailingValues?.Variables.Contains(alias) == true)
            {
                throw new InvalidDataException("A projection alias cannot replace an in-scope variable.");
            }
        }
        var parsed = new ParsedSelectQuery(projection, body, modifiers, distinct)
        {
            SourceVariables = tokens.Where(token => token.StartsWith('?')).Select(token => token[1..])
                .ToHashSet(StringComparer.Ordinal)
        };
        return prefixes.TryGetValue("$base", out var baseIri)
            ? new QueryFunctionBindings(false, baseIri).Plan(parsed) : parsed;
    }

    internal static IReadOnlyList<string> ResultVariables(string query)
    {
        ParsedSelectQuery plan = GetSelectPlan(query);
        return plan.Projection.SelectAll
            ? Variable().Matches(NormalizeQueryLexicalForm(query)).Select(match => match.Value[1..]).Distinct(StringComparer.Ordinal)
                .Where(variable => plan.Body.Elements.Any(element => IntroducesVariable(element, variable)) || plan.Modifiers.TrailingValues?.Variables.Contains(variable) == true).ToArray()
            : plan.Projection.OutputVariables;
    }

    private static Group ParseQueryBody(string source, string query, IReadOnlyDictionary<string, string> prefixes)
    {
        string[] tokens = Tokenize(source, prefixes, enclosingGroups: 1);
        int tokenIndex = 0;
        Group body;
        if (tokens.FirstOrDefault()?.Equals("SELECT", StringComparison.OrdinalIgnoreCase) == true)
        {
            string[] enclosed = ["{", .. tokens, "}"];
            body = new([new SubqueryElement(ParseSubquery(enclosed, ref tokenIndex))], []);
            tokenIndex = tokens.Length;
        }
        else
        {
            body = ParseGroup(tokens, ref tokenIndex, null, false, 0);
        }
        ValidatePatternBlankScopes(body);
        if (tokenIndex != tokens.Length || CountPatterns(body) > MaximumPatterns)
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }

        return prefixes.TryGetValue("$base", out var baseIri)
            ? new QueryFunctionBindings(false, baseIri).Body(body) : body;
    }

    private static (string Query, IReadOnlyDictionary<string, string> Prefixes) ParseProlog(string query, int maximumCharacters = 262_144, bool allowPrefixReplacement = false)
    {
        query = NormalizeQueryLexicalForm(query, maximumCharacters);
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        int position = 0;
        int declarations = 0;
        string? baseIri = null;
        while (true)
        {
            Match declaredBase = BaseDeclaration().Match(query, position);
            if (declaredBase.Success && declaredBase.Index == position)
            {
                if (++declarations > MaximumPrefixes)
                {
                    throw new RdfSparqlWorkLimitException("Too many SPARQL prolog declarations.");
                }
                // SPARQL 1.1 section 19.5 requires each declared BASE to be
                // absolute, even when an earlier declaration supplied a base.
                // Relative body IRIs and PREFIX namespaces still resolve below.
                baseIri = new RdfIri(declaredBase.Groups["iri"].Value).Value;
                position += declaredBase.Length;
                continue;
            }
            Match prefix = PrefixDeclaration().Match(query, position);
            if (!prefix.Success || prefix.Index != position)
            {
                break;
            }

            string name = prefix.Groups["name"].Value;
            if (!allowPrefixReplacement && prefixes.ContainsKey(name))
            {
                throw new InvalidDataException("A PREFIX cannot be declared more than once in a SPARQL query.");
            }
            prefixes[name] = RdfIriResolver.Resolve(prefix.Groups["iri"].Value, baseIri);

            if (++declarations > MaximumPrefixes)
            {
                throw new RdfSparqlWorkLimitException($"SPARQL parsing exceeded {MaximumPrefixes} prefix declarations.");
            }

            position += prefix.Length;
        }
        if (baseIri is not null)
        {
            prefixes["$base"] = baseIri;
        }
        return (baseIri is null ? query[position..] : ResolveQueryIris(query[position..], baseIri, maximumCharacters), prefixes);
    }

    private static RdfTerm ParseTerm(string token)
    {
        if (token.StartsWith('<') && token.EndsWith('>'))
        {
            return new RdfIri(token[1..^1]);
        }

        Match literal = Literal().Match(token);
        if (literal.Success)
        {
            if (literal.Groups["lang"].Success && !RdfTermValidation.IsLanguageTag(literal.Groups["lang"].Value))
            {
                throw new InvalidDataException("The SPARQL literal contains an invalid language tag.");
            }
            return new RdfLiteral(Regex.Unescape(
                literal.Groups["longValue"].Success
                    ? literal.Groups["longValue"].Value
                    : literal.Groups["value"].Value),
            literal.Groups["type"].Success ? literal.Groups["type"].Value : null,
            literal.Groups["lang"].Success ? literal.Groups["lang"].Value : null);
        }

        if (token.Length > 4096)
        {
            throw new RdfSparqlWorkLimitException("A numeric token exceeds 4096 characters.");
        }
        if (Boolean.TryParse(token, out bool boolean))
        {
            return new RdfLiteral(boolean ? "true" : "false", Xsd + "boolean");
        }
        if (BigInteger.TryParse(token, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out _))
        {
            return new RdfLiteral(token,
                "http://www.w3.org/2001/XMLSchema#integer");
        }

        if (token.Contains('e') || token.Contains('E'))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                return new RdfLiteral(token, Xsd + "double");
            }
        }
        var decimalLiteral = new RdfLiteral(token, Xsd + "decimal");
        if (TryParseExactDecimal(decimalLiteral, out _, out _))
        {
            return decimalLiteral;
        }

        throw new InvalidDataException($"SPARQL term '{token}' is unsupported.");
    }

    private static string[] Tokenize(string body, IReadOnlyDictionary<string, string> prefixes, int enclosingGroups = 0)
    {
        MatchCollection matches = Token().Matches(body);
        // MatchCollection.Count eagerly materializes every regex match. Keep
        // enumeration lazy so rejected input cannot allocate beyond the budget.
        var tokens = new List<string>();
        int consumed = 0;
        foreach (Match match in matches)
        {
            if (tokens.Count == MaximumTokens)
            {
                throw new RdfSparqlWorkLimitException($"SPARQL parsing exceeded {MaximumTokens} tokens.");
            }
            if (!String.IsNullOrWhiteSpace(body[consumed..match.Index]))
            {
                throw new InvalidDataException("The SPARQL graph pattern contains an invalid token.");
            }

            tokens.Add(match.Value.StartsWith('[') && match.Value.EndsWith(']') ? "[]" : ExpandPrefixedName(match.Value, prefixes));
            consumed = match.Index + match.Length;
        }
        if (!String.IsNullOrWhiteSpace(body[consumed..]))
        {
            throw new InvalidDataException("The SPARQL graph pattern contains an invalid token.");
        }

        string[] result = tokens.ToArray();
        ValidateGraphGroupNesting(result, enclosingGroups);
        return result;
    }

    private static void ValidateGraphGroupNesting(string[] tokens, int enclosingGroups)
    {
        // Check the complete token stream before recursive parsing can extract a
        // subquery or restart expression parsing inside FILTER/BIND/EXISTS.
        // The outer query group is depth zero; extracted bodies retain that group.
        int groups = enclosingGroups;
        for (int index = 0; index < tokens.Length; index++)
        {
            if (tokens[index].Equals("VALUES", StringComparison.OrdinalIgnoreCase))
            {
                // VALUES braces delimit data, not graph patterns. Validate the
                // header and skip only a non-nested data block, without allocating
                // another set of RDF values. The ordinary parser validates rows.
                index++;
                if (index < tokens.Length && tokens[index] == "(")
                {
                    index++;
                    while (index < tokens.Length && tokens[index].StartsWith('?'))
                    {
                        index++;
                    }
                    RequireToken(tokens, ref index, ")");
                }
                else if (index < tokens.Length && tokens[index].StartsWith('?'))
                {
                    index++;
                }
                else
                {
                    throw new InvalidDataException("VALUES requires a variable or a variable list.");
                }
                RequireToken(tokens, ref index, "{");
                while (index < tokens.Length && tokens[index] != "}")
                {
                    if (tokens[index] == "{")
                    {
                        throw new InvalidDataException("VALUES data cannot contain graph groups.");
                    }
                    index++;
                }
                RequireToken(tokens, ref index, "}");
                index--;
            }
            else if (tokens[index] == "{")
            {
                if (++groups > MaximumGroupDepth + 1)
                {
                    throw new RdfSparqlWorkLimitException(
                        $"A SPARQL query cannot exceed {MaximumGroupDepth} nested graph groups.");
                }
            }
            else if (tokens[index] == "}")
            {
                groups--;
                if (groups < enclosingGroups)
                {
                    throw new InvalidDataException("An unexpected graph group closing brace was found.");
                }
            }
        }
    }

    private static string ExpandPrefixedName(string token, IReadOnlyDictionary<string, string> prefixes)
    {
        if (token.StartsWith("_:", StringComparison.Ordinal))
        {
            return token;
        }
        if (token.StartsWith('"'))
        {
            int datatype = token.LastIndexOf("\"^^", StringComparison.Ordinal);
            return datatype < 0 ? token : token[..(datatype + 3)] + ExpandPrefixedName(token[(datatype + 3)..], prefixes);
        }


        int separator = token.IndexOf(':');
        if (separator < 0 || token.StartsWith('<') || token.StartsWith('"'))
        {
            return token;
        }

        string prefix = token[..separator];
        if (!prefixes.TryGetValue(prefix, out string? iri))
        {
            throw new InvalidDataException($"SPARQL prefix '{prefix}:' is not declared.");
        }

        string local = Regex.Replace(token[(separator + 1)..], @"\\([_~.\-!$&'()*+,;=/?#@%])", "$1");
        return $"<{iri}{local}>";
    }
}
