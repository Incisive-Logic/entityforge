using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EntityForge.Query.Values;
using EntityForge.Query.Querying;

namespace EntityForge.Db.Gql;

internal sealed partial class GraphGqlQueryCompiler
{
    private sealed class ProjectionExpressionParser
    {
        private static readonly Regex TokenPattern = new("\\G\\s*(\"(?:\\\\.|[^\"])*\"|[0-9]+(?:\\.[0-9]+)?(?:[eE][+-]?[0-9]+)?|" + GraphGqlIdentifiers.Pattern + "(?:\\." + GraphGqlIdentifiers.Pattern + ")?|\\|\\||::|\\.\\.|=~|<>|!=|<=|>=|[-+*/%^&!(),=<>:{}.|\\[\\]])", RegexOptions.CultureInvariant);
        private readonly string[] _tokens;
        private readonly IReadOnlySet<string>? _rowScope;
        private readonly List<(string Name, string Id)> _locals = [];
        private int _nextLocal;
        private int _position;
        private readonly Action<string, IReadOnlyList<(string Name, string Id)>>? _operand;

        public ProjectionExpressionParser(string source, IReadOnlySet<string>? rowScope = null,
            Action<string, IReadOnlyList<(string Name, string Id)>>? operand = null)
        {
            _rowScope = rowScope;
            _operand = operand;
            var tokens = new List<string>();
            int position = 0;
            while (position < source.TrimEnd().Length)
            {
                Match match = TokenPattern.Match(source, position);
                if (!match.Success || tokens.Count == 256)
                {
                    throw Invalid("Invalid or oversized projection expression.");
                }
                tokens.Add(match.Groups[1].Value);
                position += match.Length;
            }
            _tokens = tokens.ToArray();
        }

        public GraphScalarExpression Parse()
        {
            GraphScalarExpression value = Expression(0, 0);
            if (_position != _tokens.Length)
            {
                throw Invalid("Unexpected projection expression token.");
            }
            return value;
        }

        private GraphScalarExpression Expression(int minimum, int depth)
        {
            if (depth > 16 || _position >= _tokens.Length)
            {
                throw Invalid("Projection expression is incomplete or exceeds 16 nested levels.");
            }
            string token = _tokens[_position++];
            GraphScalarExpression value;
            if (token.Equals("CASE", StringComparison.OrdinalIgnoreCase))
            {
                GraphScalarExpression? subject = _position < _tokens.Length && _tokens[_position].Equals("WHEN", StringComparison.OrdinalIgnoreCase)
                    ? null : Expression(0, depth + 1);
                var branches = new List<GraphScalarExpression>();
                while (Take("WHEN"))
                {
                    GraphScalarExpression condition = subject is null ? Expression(0, depth + 1) : SimpleCaseCondition(subject, depth + 1);
                    while (subject is not null && Take(","))
                    {
                        condition = new(null, null, null, "OR", [condition, SimpleCaseCondition(subject, depth + 1)]);
                    }
                    branches.Add(condition);
                    Require("THEN");
                    branches.Add(Expression(0, depth + 1));
                }
                if (branches.Count == 0)
                {
                    throw Invalid("CASE requires at least one WHEN branch.");
                }
                branches.Add(Take("ELSE") ? Expression(0, depth + 1) : new(null, null, UnboundValue.Instance));
                Require("END");
                value = new(null, null, null, "CASE", branches);
            }
            else if (token.Equals("NOT", StringComparison.OrdinalIgnoreCase))
            {
                value = new(null, null, null, "NOT", [Expression(4, depth + 1)]);
            }
            else if (token is "+" or "-")
            {
                // Parse the signed token together: the magnitude of Int64.MinValue
                // cannot be represented as a positive Int64 intermediate.
                if (_position < _tokens.Length && _tokens[_position].All(Char.IsAsciiDigit) &&
                    Int64.TryParse(token + _tokens[_position], NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture, out long signed))
                {
                    _position++;
                    value = new(null, null, new LiteralValue(signed.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer));
                }
                else
                {
                    value = new(null, null, null, "unary" + token, [Expression(9, depth + 1)]);
                }
            }
            else if (token == "(")
            {
                value = Expression(0, depth + 1);
                Require(")");
            }
            else if (token.Equals("PATH", StringComparison.OrdinalIgnoreCase) && Take("["))
            {
                var elements = new List<GraphScalarExpression> { Expression(0, depth + 1) };
                while (Take(","))
                {
                    elements.Add(Expression(0, depth + 1));
                }
                Require("]");
                if (elements.Count % 2 == 0)
                {
                    throw Invalid("A path constructor requires alternating nodes and edges, ending in a node.");
                }
                value = new(null, null, null, "__GQL_PATH_CONSTRUCT", elements);
            }
            else if (token == "[" && IterationHeader())
            {
                value = Iteration("COMPREHENSION", depth + 1);
            }
            else if (token == "[")
            {
                var items = new List<GraphScalarExpression>();
                if (!Take("]"))
                {
                    do
                    {
                        items.Add(Expression(0, depth + 1));
                    }
                    while (Take(","));
                    Require("]");
                }
                value = new(null, null, null, "LIST", items);
            }
            else if (token == "{")
            {
                var items = new List<GraphScalarExpression>();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                if (!Take("}"))
                {
                    do
                    {
                        if (_position >= _tokens.Length)
                        {
                            throw Invalid("A map requires a key.");
                        }
                        string key = _tokens[_position++];
                        if (key.StartsWith('"'))
                        {
                            key = JsonSerializer.Deserialize<string>(key)!;
                        }
                        else if (!GraphGqlIdentifiers.Whole.IsMatch(key))
                        {
                            throw Invalid("A map key must be an identifier or string.");
                        }
                        if (!keys.Add(key))
                        {
                            throw Invalid("Map keys must be unique.");
                        }
                        Require(":");
                        items.Add(new(null, null, new LiteralValue(key)));
                        items.Add(Expression(0, depth + 1));
                    }
                    while (Take(","));
                    Require("}");
                }
                value = new(null, null, null, "MAP", items);
            }
            else if (token.ToUpperInvariant() is "REDUCE" or "ALLREDUCE" && Take("("))
            {
                value = Reduction(depth + 1, token.ToUpperInvariant());
            }
            else if (token.ToUpperInvariant() is "ANY" or "ALL" or "NONE" or "SINGLE" && Take("("))
            {
                value = Iteration(token.ToUpperInvariant(), depth + 1);
            }
            else if (token.Equals("CAST", StringComparison.OrdinalIgnoreCase) && Take("("))
            {
                var operand = Expression(0, depth + 1);
                Require("AS");
                string target = PredicateType(0);
                Require(")");
                value = new(null, null, null, "CAST", [operand, new(null, null, new LiteralValue(target))]);
            }
            else if (token == "__YIELD_COLUMN" && Take("("))
            {
                if (_position >= _tokens.Length || !_tokens[_position].StartsWith('"'))
                {
                    throw Invalid("A lowered YIELD requires a column name.");
                }
                string column = JsonSerializer.Deserialize<string>(_tokens[_position++])!;
                Require(")");
                if (_rowScope is null || !_rowScope.Contains(column))
                {
                    throw Invalid("YIELD references an unavailable column: " + column);
                }
                _operand?.Invoke(column, _locals);
                value = new("__row", column, null);
            }
            else if (token.Equals("PROPERTY_EXISTS", StringComparison.OrdinalIgnoreCase) && Take("("))
            {
                GraphScalarExpression entity = Expression(0, depth + 1);
                Require(",");
                if (_position >= _tokens.Length || !(_tokens[_position].StartsWith('"') || GraphGqlIdentifiers.StrictWhole.IsMatch(_tokens[_position])))
                {
                    throw Invalid("PROPERTY_EXISTS requires a property name identifier as its second argument.");
                }
                string property = _tokens[_position++];
                var key = new GraphScalarExpression(null, null, new LiteralValue(property.StartsWith('"')
                    ? JsonSerializer.Deserialize<string>(property)! : property));
                Require(")");
                value = new(null, null, null, "PROPERTY_EXISTS", [entity, key]);
            }
            else if (token.Equals("TRIM", StringComparison.OrdinalIgnoreCase) && Take("("))
            {
                value = TrimExpression(depth + 1);
            }
            else if (_position < _tokens.Length && _tokens[_position] == "(")
            {
                _position++;
                var args = new List<GraphScalarExpression>();
                if (_position < _tokens.Length && _tokens[_position] != ")")
                {
                    do
                    {
                        if (token.Equals("NORMALIZE", StringComparison.OrdinalIgnoreCase) && args.Count == 1 &&
                            _position < _tokens.Length && IsNormalizationForm(_tokens[_position]))
                        {
                            args.Add(new(null, null, new LiteralValue(_tokens[_position++].ToUpperInvariant())));
                        }
                        else
                        {
                            args.Add(Expression(0, depth + 1));
                        }
                    }
                    while (Take(","));
                }
                Require(")");
                string name = token.ToUpperInvariant();
                name = name switch
                {
                    "TOLOWER" => "LOWER",
                    "TOUPPER" => "UPPER",
                    "ELEMENT_ID" => "ELEMENTID",
                    _ => name
                };
                bool valid = name switch
                {
                    "COALESCE" => args.Count > 0,
                    "DATE" or "DURATION" or "POINT" => args.Count == 1,
                    "UUID" => args.Count is >= 0 and <= 2,
                    "FORMAT" => args.Count is 1 or 2,
                    "UUID.MOSTSIGNIFICANTBITS" or "UUID.LEASTSIGNIFICANTBITS" => args.Count == 1,
                    "DATETIME" or "ZONED_DATETIME" or "DATETIME.FROMEPOCHMILLIS" => args.Count == 1,
                    "DATETIME.FROMEPOCH" => args.Count == 2,
                    "DATETIME.TRUNCATE" => args.Count is 2 or 3,
                    "TIME" or "ZONED_TIME" => args.Count == 1,
                    "TIME.TRUNCATE" => args.Count is 2 or 3,
                    "LOCALTIME" or "LOCAL_TIME" => args.Count == 1,
                    "LOCALDATETIME" or "LOCAL_DATETIME" => args.Count == 1,
                    "LOCALDATETIME.TRUNCATE" => args.Count is 2 or 3,
                    "LOCALTIME.TRUNCATE" => args.Count is 2 or 3,
                    "POINT.DISTANCE" => args.Count == 2,
                    "POINT.WITHINBBOX" => args.Count == 3,
                    "DATE.TRUNCATE" => args.Count is 2 or 3,
                    "DURATION.BETWEEN" or "DURATION_BETWEEN" or "DURATION.INDAYS" or "DURATION.INMONTHS" or "DURATION.INSECONDS" => args.Count == 2,
                    "COLL.DISTINCT" or "COLL.SORT" or "COLL.MIN" or "COLL.MAX" => args.Count == 1,
                    "COLL.FLATTEN" => args.Count is 1 or 2,
                    "COLL.INDEXOF" or "COLL.REMOVE" => args.Count == 2,
                    "COLL.INSERT" => args.Count == 3,
                    "STRING.INDEXOF" or "STRING.JOIN" => args.Count == 2,
                    "STRING.REGEXREPLACE" => args.Count == 3,
                    "NULLIF" or "LEFT" or "RIGHT" or "POWER" or "MOD" or "LOG" or "ATAN2" or "SPLIT" => args.Count == 2,
                    "PI" or "E" or "RAND" or "RANDOMUUID" => args.Count == 0,
                    "DATE.REALTIME" or "DATETIME.REALTIME" or "ZONED_DATETIME.REALTIME" or "TIME.REALTIME" or "ZONED_TIME.REALTIME" or "LOCALTIME.REALTIME" or "LOCAL_TIME.REALTIME" or "LOCALDATETIME.REALTIME" or "LOCAL_DATETIME.REALTIME" => args.Count is 0 or 1,
                    "ROUND" => args.Count is >= 1 and <= 3,
                    "SUBSTRING" => args.Count is 2 or 3,
                    "RANGE" => args.Count is 2 or 3,
                    "HEAD" or "LAST" or "TAIL" or "ISEMPTY" or "CARDINALITY" => args.Count == 1,
                    "__GQL_PATH_JOIN" => args.Count is >= 4 and <= 18,
                    "__GQL_PATH_FROM_EDGES" => args.Count == 2,
                    "__GQL_PATH_MULTIPLICITY" or "__GQL_PATH_DECOMPOSITIONS" => args.Count == 4,
                    "__GQL_PATHS_DISTINCT" => args.Count is >= 2 and <= 16,
                    "KEYS" or "PROPERTIES" or "VALUETYPE" => args.Count == 1,
                    "LABELS" or "TYPE" or "ELEMENTID" or "LENGTH" or "PATH_LENGTH" or "ELEMENTS" or "NODES" or "RELATIONSHIPS" or "STARTNODE" or "ENDNODE" => args.Count == 1,
                    "TOFLOAT" or "TOFLOATORNULL" or "TOFLOATLIST" or "ISNAN" or "TOINTEGER" or "TOINTEGERORNULL" or "TOINTEGERLIST" or
                    "TOSTRING" or "TOSTRINGORNULL" or "TOSTRINGLIST" or
                    "TOBOOLEAN" or "TOBOOLEANORNULL" or "TOBOOLEANLIST" => args.Count == 1,
                    "NORMALIZE" => args.Count == 1 || args.Count == 2 && args[1].Literal is LiteralValue form && IsNormalizationForm(form.LexicalValue),
                    "REPLACE" => args.Count is 3 or 4,
                    "LTRIM" or "RTRIM" or "BTRIM" => args.Count is 1 or 2,
                    "LOWER" or "UPPER" or "REVERSE" or "CHAR_LENGTH" or "CHARACTER_LENGTH" or "SIZE" or
                    "ABS" or "SIGN" or "CEIL" or "CEILING" or "FLOOR" or "SQRT" or "EXP" or "LN" or "LOG10" or
                    "COT" or "HAVERSIN" or "SINH" or "COSH" or "TANH" or "COTH" or
                    "SIN" or "COS" or "TAN" or "ASIN" or "ACOS" or "ATAN" or "DEGREES" or "RADIANS" => args.Count == 1,
                    _ => false
                };
                if (!valid)
                {
                    throw Invalid($"Unsupported function or argument count: {token}.");
                }
                if (name == "DURATION_BETWEEN")
                {
                    if (Take("YEAR"))
                    {
                        Require("TO");
                        Require("MONTH");
                        name = "DURATION.INMONTHS";
                    }
                    else if (Take("DAY"))
                    {
                        Require("TO");
                        Require("SECOND");
                    }
                }
                value = new(null, null, null, name, args);
            }
            else if (token.Equals("NULL", StringComparison.OrdinalIgnoreCase) || token.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
            {
                value = new(null, null, UnboundValue.Instance);
            }
            else if (token.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || token.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            {
                value = new(null, null, new LiteralValue(token.ToLowerInvariant(), QueryScalarKind.Boolean));
            }
            else
            {
                if (GraphGqlIdentifiers.StrictWhole.IsMatch(token) &&
                    _position + 1 < _tokens.Length && _tokens[_position] == "." && _tokens[_position + 1].StartsWith('"'))
                {
                    _position++;
                    value = ScopedOperand(token, JsonSerializer.Deserialize<string>(_tokens[_position++])!);
                }
                else
                {
                    value = ScopedOperand(token);
                }
            }
            while (_position < _tokens.Length)
            {
                string op = _tokens[_position].ToUpperInvariant();
                if (op == "{")
                {
                    _position++;
                    var entries = new List<GraphScalarExpression>();
                    bool all = false;
                    if (!Take("}"))
                    {
                        do
                        {
                            bool selector = Take(".");
                            if (selector && Take("*"))
                            {
                                all = true;
                                continue;
                            }
                            if (_position >= _tokens.Length || !GraphGqlIdentifiers.Whole.IsMatch(_tokens[_position]))
                            {
                                throw Invalid("A map projection requires a field name.");
                            }
                            string key = _tokens[_position++];
                            var name = new GraphScalarExpression(null, null, new LiteralValue(key));
                            GraphScalarExpression selected = selector ? new(null, null, null, "INDEX", [value, name]) :
                                Take(":") ? Expression(0, depth + 1) : ScopedOperand(key);
                            entries.Add(name);
                            entries.Add(selected);
                        }
                        while (Take(","));
                        Require("}");
                    }
                    value = new(null, null, null, "MAP_PROJECT", [value,
                        new(null, null, new LiteralValue(all ? "true" : "false", QueryScalarKind.Boolean)), .. entries]);
                    continue;
                }
                if (op == ".")
                {
                    _position++;
                    if (_position < _tokens.Length && _tokens[_position].StartsWith('"'))
                    {
                        string key = JsonSerializer.Deserialize<string>(_tokens[_position++])!;
                        value = new(null, null, null, "PROPERTY", [value, new(null, null, new LiteralValue(key))]);
                        continue;
                    }
                    if (_position >= _tokens.Length || !GqlRegex.IsMatch(_tokens[_position], @"^" + GraphGqlIdentifiers.Pattern + @"(?:\." + GraphGqlIdentifiers.Pattern + @")?$"))
                    {
                        throw Invalid("Property lookup requires a field name.");
                    }
                    foreach (string key in _tokens[_position++].Split('.'))
                    {
                        value = new(null, null, null, "PROPERTY", [value, new(null, null, new LiteralValue(key))]);
                    }
                    continue;
                }
                if (op == "[")
                {
                    _position++;
                    bool omittedStart = Take("..");
                    GraphScalarExpression start = omittedStart ? new(null, null, new LiteralValue("0", QueryScalarKind.Integer)) : Expression(0, depth + 1);
                    if (omittedStart || Take(".."))
                    {
                        GraphScalarExpression end = _position < _tokens.Length && _tokens[_position] == "]"
                            ? new(null, null, new LiteralValue(Int32.MaxValue.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer))
                            : Expression(0, depth + 1);
                        value = new(null, null, null, "SLICE", [value, start, end]);
                    }
                    else
                    {
                        value = new(null, null, null, "INDEX", [value, start]);
                    }
                    Require("]");
                    continue;
                }
                if (op == ":" && minimum <= 4)
                {
                    _position++;
                    value = LabelPredicate(value, 0, depth + 1);
                    continue;
                }
                if (op is "IS" or "::" && minimum <= 4)
                {
                    _position++;
                    bool negated = op == "IS" && Take("NOT");
                    value = IsPredicate(value, op == "::", negated, depth);
                    continue;
                }
                if (op is "STARTS" or "ENDS" or "CONTAINS" && minimum <= 4)
                {
                    _position++;
                    if (op != "CONTAINS")
                    {
                        Require("WITH");
                        op += "WITH";
                    }
                    value = new(null, null, null, op, [value, Expression(5, depth + 1)]);
                    continue;
                }
                int precedence = op switch
                {
                    "OR" or "XOR" => 1,
                    "AND" => 3,
                    "=~" or "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=" or "IN" => 4,
                    "||" => 5,
                    "+" or "-" => 6,
                    "*" or "/" or "%" => 7,
                    "^" => 8,
                    _ => -1
                };
                if (precedence < minimum)
                {
                    break;
                }
                _position++;
                GraphScalarExpression right = Expression(precedence + 1, depth + 1);
                value = new(null, null, null, op == "^" ? "POWER" : op, [value, right]);
                if (op is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=")
                {
                    // A chain compares adjacent operands, not the preceding
                    // Boolean result. Parentheses start a separate expression.
                    while (_position < _tokens.Length && _tokens[_position] is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=")
                    {
                        string comparison = _tokens[_position++];
                        GraphScalarExpression next = Expression(5, depth + 1);
                        value = new(null, null, null, "AND", [value, new(null, null, null, comparison, [right, next])]);
                        right = next;
                    }
                }
            }
            return value;
        }

        private GraphScalarExpression TypePredicate(GraphScalarExpression value, bool negated)
            => new(null, null, null, negated ? "ISNOTTYPED" : "ISTYPED",
                [value, new(null, null, new LiteralValue(PredicateType(0))),
                    new(null, null, new LiteralValue("false", QueryScalarKind.Boolean))]);

        private string PredicateType(int depth)
        {
            string type = PredicateTypeAtom(depth);
            var alternatives = new List<string> { type };
            while (_position + 1 < _tokens.Length && _tokens[_position] == "|" &&
                _tokens[_position + 1].ToUpperInvariant() is "LIST" or "ARRAY" or "RECORD" or "BOOLEAN" or "BOOL" or "SIGNED" or "INTEGER" or "INT" or "INT64" or "INTEGER64" or
                    "FLOAT" or "FLOAT64" or "DECIMAL" or "DEC" or "DATE" or "LOCAL" or "ZONED" or "TIME" or "TIMESTAMP" or "DURATION" or "POINT" or "UUID" or "STRING" or "VARCHAR" or "NULL" or "ANY" or "NOTHING" or "MAP" or "NODE" or "VERTEX" or "RELATIONSHIP" or "EDGE" or "PATH" or "{")
            {
                _position++;
                alternatives.Add(PredicateTypeAtom(depth + 1));
            }
            return alternatives.Count == 1 ? type : "(" + String.Join('|', alternatives) + ")";
        }

        private string PredicateTypeAtom(int depth)
        {
            if (_position >= _tokens.Length || depth > 16)
            {
                throw Invalid("A type name is required within 16 nested levels.");
            }
            string type = _tokens[_position++].ToUpperInvariant();
            bool declaredRecord = type == "{" || type == "RECORD" && Take("{");
            if (declaredRecord)
            {
                var fields = new List<string>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                if (!Take("}"))
                {
                    do
                    {
                        if (_position >= _tokens.Length || !(_tokens[_position].StartsWith('"') || GraphGqlIdentifiers.Whole.IsMatch(_tokens[_position])))
                        {
                            throw Invalid("A record type requires a field name.");
                        }
                        string name = GraphGqlIdentifiers.DecodeName(_tokens[_position++]);
                        if (!names.Add(name))
                        {
                            throw Invalid("Record type field names must be distinct.");
                        }
                        if (!Take("::"))
                        {
                            Take("TYPED");
                        }
                        string fieldType = PredicateType(depth + 1);
                        string encodedName = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(name));
                        fields.Add(encodedName + ":" + fieldType);
                    }
                    while (Take(","));
                    Require("}");
                }
                type = "RECORD{" + String.Join(';', fields) + "}";
            }
            if (type == "SIGNED")
            {
                if (!Take("INTEGER64"))
                {
                    Require("INTEGER");
                }
                type = "INTEGER";
            }
            bool parsedUnion = false;
            type = type switch
            {
                "BOOL" => "BOOLEAN",
                "INT" or "INT64" or "INTEGER64" => "INTEGER",
                "FLOAT64" => "FLOAT",
                "DEC" => "DECIMAL",
                "VARCHAR" => "STRING",
                "RECORD" => "MAP",
                "ARRAY" => "LIST",
                _ => type
            };
            type = type switch
            {
                "VERTEX" => "NODE",
                "EDGE" => "RELATIONSHIP",
                _ => type
            };
            if (type == "ANY")
            {
                if (Take("RECORD"))
                {
                    type = "MAP";
                }
                else if (Take("NODE") || Take("VERTEX"))
                {
                    type = "NODE";
                }
                else if (Take("EDGE") || Take("RELATIONSHIP"))
                {
                    type = "RELATIONSHIP";
                }
                else
                {
                    Take("VALUE");
                }
            }
            if (type == "ANY" && Take("<"))
            {
                string union = PredicateType(depth + 1);
                Require(">");
                type = union;
                parsedUnion = true;
            }
            if (type == "ZONED")
            {
                if (Take("DATETIME"))
                {
                    type = "ZONED DATETIME";
                }
                else
                {
                    Require("TIME");
                    type = "ZONED TIME";
                }
            }
            if (type == "TIMESTAMP")
            {
                bool zoned = Take("WITH");
                if (zoned || Take("WITHOUT"))
                {
                    Require("TIME");
                    Require("ZONE");
                }
                type = zoned ? "ZONED DATETIME" : "LOCAL DATETIME";
            }
            if (type == "TIME")
            {
                bool zoned = Take("WITH");
                if (!zoned)
                {
                    Require("WITHOUT");
                }
                Require("TIME");
                Require("ZONE");
                type = zoned ? "ZONED TIME" : "LOCAL TIME";
            }
            if (type == "LOCAL")
            {
                if (Take("DATETIME"))
                {
                    type = "LOCAL DATETIME";
                }
                else
                {
                    Require("TIME");
                    type = "LOCAL TIME";
                }
            }
            if (type == "DURATION")
            {
                Require("(");
                bool months = Take("YEAR");
                if (!months)
                {
                    Require("DAY");
                }
                Require("TO");
                Require(months ? "MONTH" : "SECOND");
                Require(")");
                type = months ? "DURATION(YEAR TO MONTH)" : "DURATION(DAY TO SECOND)";
            }
            else if (type == "LIST")
            {
                if (Take("<"))
                {
                    type += "<" + PredicateType(depth + 1);
                    Require(">");
                    type += ">";
                }
                else
                {
                    type = "LIST<ANY>";
                }
                type = ListMaximum(type);
            }
            else if (!parsedUnion && !declaredRecord && type is not ("BOOLEAN" or "INTEGER" or "FLOAT" or "DECIMAL" or "DATE" or "LOCAL TIME" or "ZONED TIME" or "LOCAL DATETIME" or "ZONED DATETIME" or "DURATION" or "POINT" or "UUID" or "STRING" or "NULL" or "ANY" or "NOTHING" or "MAP" or "NODE" or "RELATIONSHIP" or "PATH"))
            {
                throw Invalid($"Unsupported predicate type '{type}'.");
            }
            bool required = Take("!");
            if (!required && Take("NOT"))
            {
                Require("NULL");
                required = true;
            }
            type += required ? "!" : "";
            int nesting = depth;
            while (Take("LIST") || Take("ARRAY"))
            {
                if (++nesting > 16)
                {
                    throw Invalid("A type name is required within 16 nested levels.");
                }
                type = ListMaximum("LIST<" + type + ">");
                if (Take("NOT"))
                {
                    Require("NULL");
                    type += "!";
                }
            }
            return type;
        }

        private string ListMaximum(string type)
        {
            if (!Take("["))
            {
                return type;
            }
            if (_position >= _tokens.Length || !_tokens[_position].All(Char.IsAsciiDigit))
            {
                throw Invalid("A list type maximum must be an unsigned integer.");
            }
            string maximum = _tokens[_position++].TrimStart('0');
            Require("]");
            return type + "[" + (maximum.Length == 0 ? "0" : maximum) + "]";
        }

        private GraphScalarExpression IsPredicate(GraphScalarExpression value, bool typed, bool negated, int depth)
        {
            if (typed || Take("TYPED") || Take("::"))
            {
                value = TypePredicate(value, negated);
            }
            else if (Take("DIRECTED"))
            {
                value = new(null, null, null, "ISDIRECTED", [value]);
                if (negated)
                {
                    value = new(null, null, null, "NOT", [value]);
                }
            }
            else if (_position < _tokens.Length && _tokens[_position].ToUpperInvariant() is "SOURCE" or "DESTINATION")
            {
                string endpoint = _tokens[_position++].ToUpperInvariant();
                Require("OF");
                if (_position >= _tokens.Length)
                {
                    throw Invalid("An endpoint predicate requires an edge variable.");
                }
                value = new(null, null, null, "IS" + endpoint, [value, ScopedOperand(_tokens[_position++])]);
                if (negated)
                {
                    value = new(null, null, null, "NOT", [value]);
                }
            }
            else if (Take("LABELED"))
            {
                value = LabelPredicate(value, 0, depth + 1);
                if (negated)
                {
                    value = new(null, null, null, "NOT", [value]);
                }
            }
            else if (Take("NULL"))
            {
                value = new(null, null, null, negated ? "ISNOTNULL" : "ISNULL", [value]);
            }
            else if (_position < _tokens.Length && _tokens[_position].ToUpperInvariant() is "TRUE" or "FALSE" or "UNKNOWN")
            {
                string truth = _tokens[_position++].ToUpperInvariant();
                value = new(null, null, null, negated ? "ISNOTTRUTH" : "ISTRUTH",
                    [value, new(null, null, truth == "UNKNOWN" ? UnboundValue.Instance :
                        new LiteralValue(truth.ToLowerInvariant(), QueryScalarKind.Boolean))]);
            }
            else
            {
                string form = _position < _tokens.Length && IsNormalizationForm(_tokens[_position])
                    ? _tokens[_position++].ToUpperInvariant() : "NFC";
                Require("NORMALIZED");
                value = new(null, null, null, negated ? "ISNOTNORMALIZED" : "ISNORMALIZED",
                    [value, new(null, null, new LiteralValue(form))]);
            }
            return value;
        }

        private GraphScalarExpression SimpleCaseCondition(GraphScalarExpression subject, int depth)
        {
            if (Take("IS"))
            {
                bool negated = Take("NOT");
                return IsPredicate(subject, false, negated, depth);
            }
            string op = "=";
            if (_position < _tokens.Length && _tokens[_position].ToUpperInvariant() is "STARTS" or "ENDS" or "CONTAINS")
            {
                op = _tokens[_position++].ToUpperInvariant();
                if (op != "CONTAINS")
                {
                    Require("WITH");
                    op += "WITH";
                }
            }
            else if (_position < _tokens.Length && _tokens[_position] is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=" or "=~")
            {
                op = _tokens[_position++];
            }
            return new(null, null, null, op, [subject, Expression(0, depth)]);
        }

        private GraphScalarExpression LabelPredicate(GraphScalarExpression entity, int minimum, int depth)
        {
            if (depth > 16 || _position >= _tokens.Length)
            {
                throw Invalid("A label expression is incomplete or exceeds its nesting bound.");
            }
            GraphScalarExpression value;
            if (Take("!"))
            {
                value = new(null, null, null, "NOT", [LabelPredicate(entity, 3, depth + 1)]);
            }
            else if (Take("("))
            {
                value = LabelPredicate(entity, 0, depth + 1);
                Require(")");
            }
            else
            {
                string label = _tokens[_position++];
                if (label != "%" && !GqlRegex.IsMatch(label, @"^" + GraphGqlIdentifiers.NamePattern + @"$"))
                {
                    throw Invalid("A label predicate requires a label identifier or %.");
                }
                value = new(null, null, null, label.StartsWith('"') ? "HASLABEL_EXACT" : "HASLABEL",
                    [entity, new(null, null, new LiteralValue(GraphGqlIdentifiers.DecodeName(label)))]);
            }
            while (_position < _tokens.Length)
            {
                string op = _tokens[_position];
                int precedence = op == "|" ? 1 : op is "&" or ":" ? 2 : -1;
                if (precedence < minimum)
                {
                    break;
                }
                _position++;
                value = new(null, null, null, op == "|" ? "OR" : "AND", [value, LabelPredicate(entity, precedence + 1, depth + 1)]);
            }
            return value;
        }

        private GraphScalarExpression ScopedOperand(string token, string? propertyKey = null)
        {
            string[] parts = propertyKey is null ? token.Split('.') : [token, propertyKey];
            _operand?.Invoke(parts[0], _locals);
            if (GraphGqlIdentifiers.Whole.IsMatch(parts[0]))
            {
                var local = _locals.LastOrDefault(item => item.Name == parts[0]);
                if (local.Id is not null)
                {
                    GraphScalarExpression value = new(null, null, null, "LOCAL", [new(null, null, new LiteralValue(local.Id))]);
                    return parts.Length == 1 ? value : new(null, null, null, "PROPERTY", [value, new(null, null, new LiteralValue(parts[1]))]);
                }
                if (_rowScope is not null)
                {
                    if (!_rowScope.Contains(parts[0]))
                    {
                        throw Invalid($"Variable '{parts[0]}' is outside the row scope.");
                    }
                    return parts.Length == 1 ? new("__row", parts[0], null) : new("__entity_" + parts[0], parts[1], null);
                }
            }
            return propertyKey is null ? ParseProjectionOperand(token) : new(token, propertyKey, null);
        }

        private bool IterationHeader() => _position + 1 < _tokens.Length &&
            GraphGqlIdentifiers.Whole.IsMatch(_tokens[_position]) &&
            _tokens[_position + 1].Equals("IN", StringComparison.OrdinalIgnoreCase);

        private GraphScalarExpression Reduction(int depth, string operation)
        {
            if (_position >= _tokens.Length || !GraphGqlIdentifiers.Whole.IsMatch(_tokens[_position]))
            {
                throw Invalid("REDUCE requires an accumulator name.");
            }
            string accumulator = _tokens[_position++];
            Require("=");
            GraphScalarExpression initial = Expression(0, depth);
            Require(",");
            if (!IterationHeader())
            {
                throw Invalid("REDUCE requires an iteration variable and IN.");
            }
            string variable = _tokens[_position++];
            if (variable == accumulator)
            {
                throw Invalid("REDUCE accumulator and iteration variable must have different names.");
            }
            Require("IN");
            GraphScalarExpression input = Expression(0, depth);
            Require("|");
            string accumulatorId = "local" + _nextLocal++;
            string itemId = "local" + _nextLocal++;
            _locals.Add((accumulator, accumulatorId));
            _locals.Add((variable, itemId));
            GraphScalarExpression body = Expression(0, depth);
            GraphScalarExpression? predicate = null;
            if (operation == "ALLREDUCE")
            {
                Require(",");
                predicate = Expression(0, depth);
            }
            Require(")");
            _locals.RemoveRange(_locals.Count - 2, 2);
            List<GraphScalarExpression> arguments = [input, initial,
                new(null, null, new LiteralValue(accumulatorId)), new(null, null, new LiteralValue(itemId)), body];
            if (predicate is not null)
            {
                arguments.Add(predicate);
            }
            return new(null, null, null, operation, arguments);
        }

        private GraphScalarExpression Iteration(string operation, int depth)
        {
            if (!IterationHeader())
            {
                throw Invalid("A list iteration requires a variable followed by IN.");
            }
            string name = _tokens[_position++];
            Require("IN");
            GraphScalarExpression input = Expression(0, depth);
            string local = "local" + _nextLocal++;
            _locals.Add((name, local));
            bool filtered = Take("WHERE");
            if (!filtered && operation != "COMPREHENSION")
            {
                throw Invalid("A list predicate requires WHERE.");
            }
            GraphScalarExpression predicate = filtered ? Expression(0, depth) :
                new(null, null, new LiteralValue("true", QueryScalarKind.Boolean));
            var arguments = new List<GraphScalarExpression> { input, new(null, null, new LiteralValue(local)), predicate };
            if (operation == "COMPREHENSION")
            {
                arguments.Add(Take("|") ? Expression(0, depth) : ScopedOperand(name));
            }
            Require(operation == "COMPREHENSION" ? "]" : ")");
            _locals.RemoveAt(_locals.Count - 1);
            return new(null, null, null, operation, arguments);
        }

        private GraphScalarExpression TrimExpression(int depth)
        {
            string function = Take("LEADING") ? "LTRIM" : Take("TRAILING") ? "RTRIM" : Take("BOTH") ? "BTRIM" : "TRIM";
            GraphScalarExpression characters = new(null, null, new LiteralValue(" "));
            GraphScalarExpression source;
            if (Take("FROM"))
            {
                source = Expression(0, depth);
            }
            else
            {
                GraphScalarExpression first = Expression(0, depth);
                if (function == "TRIM" && Take(","))
                {
                    var count = Expression(0, depth);
                    Require(")");
                    return new(null, null, null, "TRIMLIST", [first, count]);
                }
                if (Take("FROM"))
                {
                    characters = first;
                    source = Expression(0, depth);
                }
                else
                {
                    if (function != "TRIM")
                    {
                        throw Invalid("A directed TRIM expression requires FROM.");
                    }
                    source = first;
                }
            }
            Require(")");
            return new(null, null, null, function == "LTRIM" ? "TRIMLEADING" : function == "RTRIM" ? "TRIMTRAILING" : "TRIMBOTH", [source, characters]);
        }

        private static bool IsNormalizationForm(string value) => value.ToUpperInvariant() is "NFC" or "NFD" or "NFKC" or "NFKD";

        private bool Take(string token)
        {
            if (_position >= _tokens.Length || !_tokens[_position].Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            _position++;
            return true;
        }

        private void Require(string token)
        {
            if (!Take(token))
            {
                throw Invalid($"Expected '{token}' in projection.");
            }
        }
    }
}
