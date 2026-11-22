using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static void ValidatePredicateType(string type)
    {
        // Canonical type strings can also arrive through programmatic scalar
        // expressions, without the GQL parser's nesting checks. Bound the
        // recursive evaluator before it slices or descends into the string.
        Span<char> closers = stackalloc char[64];
        int depth = 0;
        if (String.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("A scalar value type is required.");
        }
        foreach (char character in type)
        {
            char closer = character switch
            {
                '(' => ')',
                '<' => '>',
                '[' => ']',
                '{' => '}',
                _ => '\0'
            };
            if (closer != '\0')
            {
                if (depth == closers.Length)
                {
                    throw new ArgumentException("Scalar value type descriptions cannot exceed 64 nested levels.");
                }
                closers[depth++] = closer;
            }
            else if (character is ')' or '>' or ']' or '}')
            {
                if (depth == 0 || closers[--depth] != character)
                {
                    throw new ArgumentException("Scalar value type delimiters must be balanced.");
                }
            }
        }
        if (depth != 0)
        {
            throw new ArgumentException("Scalar value type delimiters must be balanced.");
        }

        // Validate the engine's canonical representation, not GQL source
        // syntax. Do this even for null values and empty lists, whose value
        // checks otherwise never visit unknown alternatives or element types.
        int position = 0;
        ReadType();
        if (position != type.Length)
        {
            throw InvalidType();
        }

        ArgumentException InvalidType() => new("Unsupported or malformed scalar value type description.");
        bool Take(char token)
        {
            if (position < type.Length && type[position] == token)
            {
                position++;
                return true;
            }
            return false;
        }
        void Require(char token)
        {
            if (!Take(token))
            {
                throw InvalidType();
            }
        }
        void ReadType()
        {
            if (Take('('))
            {
                ReadType();
                while (Take('|'))
                {
                    ReadType();
                }
                Require(')');
            }
            else if (type.AsSpan(position).StartsWith("RECORD{", StringComparison.Ordinal))
            {
                position += 7;
                var names = new HashSet<string>(StringComparer.Ordinal);
                if (!Take('}'))
                {
                    do
                    {
                        int start = position;
                        while (position < type.Length && type[position] != ':')
                        {
                            position++;
                        }
                        if (position == type.Length || !names.Add(DecodeRecordTypeName(type[start..position])))
                        {
                            throw InvalidType();
                        }
                        Require(':');
                        ReadType();
                    }
                    while (Take(';'));
                    Require('}');
                }
            }
            else if (type.AsSpan(position).StartsWith("LIST<", StringComparison.Ordinal))
            {
                position += 5;
                ReadType();
                Require('>');
                if (Take('['))
                {
                    int start = position;
                    while (position < type.Length && Char.IsAsciiDigit(type[position]))
                    {
                        position++;
                    }
                    if (position == start || position - start > 1 && type[start] == '0')
                    {
                        throw InvalidType();
                    }
                    Require(']');
                }
            }
            else if (type.AsSpan(position).StartsWith("DURATION(YEAR TO MONTH)", StringComparison.Ordinal))
            {
                position += "DURATION(YEAR TO MONTH)".Length;
            }
            else if (type.AsSpan(position).StartsWith("DURATION(DAY TO SECOND)", StringComparison.Ordinal))
            {
                position += "DURATION(DAY TO SECOND)".Length;
            }
            else
            {
                int start = position;
                while (position < type.Length && type[position] is not ('(' or ')' or '<' or '>' or '[' or ']' or '!' or '|' or '{' or '}' or ';'))
                {
                    position++;
                }
                if (type[start..position] is not ("BOOLEAN" or "INTEGER" or "FLOAT" or "DECIMAL" or "STRING" or
                    "DATE" or "LOCAL TIME" or "ZONED TIME" or "LOCAL DATETIME" or "ZONED DATETIME" or "DURATION" or
                    "POINT" or "UUID" or "NULL" or "ANY" or "NOTHING" or "MAP" or "NODE" or "RELATIONSHIP" or "PATH"))
                {
                    throw InvalidType();
                }
            }
            Take('!');
        }
    }

    private static bool MatchesPredicateType(QueryValue value, string type, IterationBudget budget, bool required = false)
    {
        budget.Consume();
        if (type.EndsWith('!'))
        {
            required = true;
            type = type[..^1];
        }
        if (type == "NOTHING")
        {
            return false;
        }
        if (value is UnboundValue or NullValue)
        {
            if (required)
            {
                return false;
            }
            // A union is nullable only when at least one member is nullable.
            if (!type.StartsWith('('))
            {
                return true;
            }
        }
        if (type.StartsWith('('))
        {
            string body = type[1..^1];
            int depth = 0, start = 0;
            for (int index = 0; index <= body.Length; index++)
            {
                if (index == body.Length || body[index] == '|' && depth == 0)
                {
                    if (MatchesPredicateType(value, body[start..index], budget))
                    {
                        return true;
                    }
                    start = index + 1;
                }
                else if (body[index] is '<' or '(' or '{')
                {
                    depth++;
                }
                else if (body[index] is '>' or ')' or '}')
                {
                    depth--;
                }
            }
            return false;
        }
        if (type.StartsWith("RECORD{", StringComparison.Ordinal))
        {
            if (value is not MapValue record)
            {
                return false;
            }
            var fields = RecordTypeFields(type);
            return record.Values.Count == fields.Length && fields.All(field =>
                record.Values.TryGetValue(field.Name, out QueryValue? item) && MatchesPredicateType(item, field.Type, budget));
        }
        if (type.StartsWith("LIST<", StringComparison.Ordinal))
        {
            int close = type.LastIndexOf('>');
            string inner = type[5..close];
            if (value is not ListValue list)
            {
                return false;
            }
            if (ExceedsListTypeMaximum(list, type, close))
            {
                return false;
            }
            return list.Items.All(item => MatchesPredicateType(item, inner, budget));
        }
        if (type is "DURATION(YEAR TO MONTH)" or "DURATION(DAY TO SECOND)")
        {
            return value is LiteralValue { ScalarKind: QueryScalarKind.Duration } duration &&
                duration.DatatypeIdentifier == (type == "DURATION(YEAR TO MONTH)"
                    ? DatabaseValue.Duration.YearToMonthDatatype : DatabaseValue.Duration.DayToSecondDatatype);
        }
        return type == "ANY" || type == (value switch
        {
            MapValue => "MAP",
            NodeValue => "NODE",
            EdgeValue => "RELATIONSHIP",
            PathValue => "PATH",
            LiteralValue literal => literal.ScalarKind switch
            {
                QueryScalarKind.Boolean => "BOOLEAN",
                QueryScalarKind.Integer => "INTEGER",
                QueryScalarKind.Guid => "UUID",
                QueryScalarKind.ZonedTime => "ZONED TIME",
                QueryScalarKind.LocalTime => "LOCAL TIME",
                QueryScalarKind.ZonedDateTime => "ZONED DATETIME",
                QueryScalarKind.LocalDateTime => "LOCAL DATETIME",
                QueryScalarKind.Decimal => "DECIMAL",
                QueryScalarKind.FloatingPoint => "FLOAT",
                QueryScalarKind.String => "STRING",
                QueryScalarKind.Date => "DATE",
                QueryScalarKind.Duration => "DURATION",
                QueryScalarKind.Point => "POINT",
                _ => ""
            },
            _ => ""
        });
    }

    private static bool ExceedsListTypeMaximum(ListValue list, string type, int close)
    {
        if (!type.EndsWith(']'))
        {
            return false;
        }
        string maximum = type[(close + 2)..^1];
        string count = list.Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Canonical unsigned decimal spelling permits exact bounds beyond
        // machine integer sizes without narrowing.
        return count.Length > maximum.Length || count.Length == maximum.Length && StringComparer.Ordinal.Compare(count, maximum) > 0;
    }

    private static void ValidateListCastLength(QueryValue value, string type, IterationBudget budget)
    {
        budget.Consume();
        type = type.TrimEnd('!');
        if (value is not ListValue list || !type.StartsWith("LIST<", StringComparison.Ordinal))
        {
            return;
        }
        int close = type.LastIndexOf('>');
        if (ExceedsListTypeMaximum(list, type, close))
        {
            throw GqlDataError("The list value exceeds the CAST target's maximum length.", "2202F");
        }
        string inner = type[5..close];
        foreach (QueryValue item in list.Items)
        {
            ValidateListCastLength(item, inner, budget);
        }
    }
}
