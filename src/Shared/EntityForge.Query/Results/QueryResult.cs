using EntityForge.Query.Values;

namespace EntityForge.Query.Results;

/// <summary>Base representation for tabular, graph and graph-pattern query results.</summary>
public abstract record QueryResult;

/// <summary>A named column in a tabular query result.</summary>
public sealed record RecordField
{
    /// <summary>Creates a field without normalizing its name.</summary>
    /// <param name="name">The nonblank field name; comparisons use ordinal case-sensitive equality.</param>
    /// <exception cref="ArgumentException">The name is null, empty or whitespace.</exception>
    public RecordField(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Gets the exact field name.</summary>
    public string Name
    {
        get;
    }
}

/// <summary>A query result row indexed by ordinal, case-sensitive field names.</summary>
public sealed class RecordRow
{
    private readonly IReadOnlyDictionary<string, QueryValue> _values;

    /// <summary>Captures field-value pairs without cloning the query values.</summary>
    /// <param name="values">The nonnull sequence of uniquely named fields.</param>
    /// <exception cref="ArgumentException">The sequence or a key is null, or a field name is repeated.</exception>
    public RecordRow(IEnumerable<KeyValuePair<string, QueryValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values is IReadOnlyList<KeyValuePair<string, QueryValue>> { Count: 2 } pair)
        {
            ArgumentNullException.ThrowIfNull(pair[0].Key);
            ArgumentNullException.ThrowIfNull(pair[1].Key);
            if (StringComparer.Ordinal.Equals(pair[0].Key, pair[1].Key))
            {
                throw new ArgumentException("Record field names must be unique.", nameof(values));
            }

            _values = new PairValueMap(pair[0], pair[1]);
        }
        else
        {
            _values = new Dictionary<string, QueryValue>(values, StringComparer.Ordinal);
        }
    }

    /// <summary>Gets the captured fields through a read-only dictionary interface.</summary>
    public IReadOnlyDictionary<string, QueryValue> Values => _values;

    /// <summary>Looks up a field value by its result-field name.</summary>
    /// <param name="fieldName">The field name to resolve.</param>
    /// <param name="value">Receives the field value when found.</param>
    /// <returns>True when this row contains the field.</returns>
    public bool TryGetValue(string fieldName, out QueryValue? value) =>
        _values.TryGetValue(fieldName, out value);

    private sealed class PairValueMap(KeyValuePair<string, QueryValue> first,
        KeyValuePair<string, QueryValue> second) : IReadOnlyDictionary<string, QueryValue>
    {
        public int Count => 2;

        public IEnumerable<string> Keys
        {
            get
            {
                yield return first.Key;
                yield return second.Key;
            }
        }

        public IEnumerable<QueryValue> Values
        {
            get
            {
                yield return first.Value;
                yield return second.Value;
            }
        }

        public QueryValue this[string key]
        {
            get
            {
                if (StringComparer.Ordinal.Equals(first.Key, key))
                {
                    return first.Value;
                }

                if (StringComparer.Ordinal.Equals(second.Key, key))
                {
                    return second.Value;
                }

                throw new KeyNotFoundException();
            }
        }

        public bool ContainsKey(string key) => StringComparer.Ordinal.Equals(first.Key, key) ||
            StringComparer.Ordinal.Equals(second.Key, key);

        public bool TryGetValue(string key, out QueryValue value)
        {
            if (StringComparer.Ordinal.Equals(first.Key, key))
            {
                value = first.Value;
                return true;
            }
            if (StringComparer.Ordinal.Equals(second.Key, key))
            {
                value = second.Value;
                return true;
            }
            value = null!;
            return false;
        }

        public IEnumerator<KeyValuePair<string, QueryValue>> GetEnumerator()
        {
            yield return first;
            yield return second;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>A tabular query result with ordered column declarations and materialized rows.</summary>
public sealed record RecordResult : QueryResult
{
    /// <summary>Captures column declarations and rows in their supplied order.</summary>
    /// <param name="fields">The nonnull column sequence with unique ordinal names.</param>
    /// <param name="rows">The nonnull row sequence; row objects are retained.</param>
    /// <exception cref="ArgumentException">An input sequence is null or column names repeat.</exception>
    /// <remarks>Construction does not check that each row contains exactly the declared columns.</remarks>
    public RecordResult(IEnumerable<RecordField> fields, IEnumerable<RecordRow> rows)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(rows);

        var materializedFields = fields.ToArray();
        if (materializedFields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != materializedFields.Length)
        {
            throw new ArgumentException("Record field names must be unique.", nameof(fields));
        }

        Fields = Array.AsReadOnly(materializedFields);
        Rows = Array.AsReadOnly(rows.ToArray());
    }

    /// <summary>Gets the captured column declarations in output order.</summary>
    public IReadOnlyList<RecordField> Fields
    {
        get;
    }

    /// <summary>Gets the captured rows in output order.</summary>
    public IReadOnlyList<RecordRow> Rows
    {
        get;
    }
}

/// <summary>A materialized collection of graph nodes and directed edges.</summary>
public sealed record GraphResult : QueryResult
{
    /// <summary>Captures node and edge sequences without deduplicating or validating endpoint membership.</summary>
    /// <param name="nodes">The nonnull node sequence to copy.</param>
    /// <param name="edges">The nonnull edge sequence to copy.</param>
    /// <exception cref="ArgumentNullException">Either sequence is null.</exception>
    public GraphResult(IEnumerable<NodeValue> nodes, IEnumerable<EdgeValue> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        Nodes = Array.AsReadOnly(nodes.ToArray());
        Edges = Array.AsReadOnly(edges.ToArray());
    }

    /// <summary>Gets the captured nodes in input order.</summary>
    public IReadOnlyList<NodeValue> Nodes
    {
        get;
    }

    /// <summary>Gets the captured edges in input order.</summary>
    public IReadOnlyList<EdgeValue> Edges
    {
        get;
    }
}

/// <summary>One graph-pattern match, containing named bindings and any returned paths.</summary>
public sealed record GraphPatternMatch
{
    /// <summary>Captures uniquely named bindings and paths without cloning their values.</summary>
    /// <param name="bindings">Bindings with unique ordinal, case-sensitive names.</param>
    /// <param name="paths">The nonnull path sequence, copied in input order.</param>
    /// <exception cref="ArgumentException">A sequence or binding name is null, or a binding name repeats.</exception>
    public GraphPatternMatch(IEnumerable<KeyValuePair<string, QueryValue>> bindings,
        IEnumerable<PathValue> paths)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings is IReadOnlyList<KeyValuePair<string, QueryValue>> { Count: 1 } single)
        {
            ArgumentNullException.ThrowIfNull(single[0].Key);
            Bindings = new SingleValueMap(single[0]);
        }
        else
        {
            Bindings = new Dictionary<string, QueryValue>(bindings, StringComparer.Ordinal);
        }

        PathValue[] pathValues = (paths ?? throw new ArgumentNullException(nameof(paths))).ToArray();
        Paths = pathValues.Length == 0 ? Array.Empty<PathValue>() : Array.AsReadOnly(pathValues);
    }

    /// <summary>Gets the captured bindings through a read-only dictionary interface.</summary>
    public IReadOnlyDictionary<string, QueryValue> Bindings
    {
        get;
    }

    /// <summary>Gets the captured paths in input order.</summary>
    public IReadOnlyList<PathValue> Paths
    {
        get;
    }

    private sealed class SingleValueMap(KeyValuePair<string, QueryValue> value)
        : IReadOnlyDictionary<string, QueryValue>
    {
        public int Count => 1;

        public IEnumerable<string> Keys
        {
            get
            {
                yield return value.Key;
            }
        }

        public IEnumerable<QueryValue> Values
        {
            get
            {
                yield return value.Value;
            }
        }

        public QueryValue this[string key] => StringComparer.Ordinal.Equals(value.Key, key)
            ? value.Value : throw new KeyNotFoundException();

        public bool ContainsKey(string key) => StringComparer.Ordinal.Equals(value.Key, key);

        public bool TryGetValue(string key, out QueryValue result)
        {
            if (StringComparer.Ordinal.Equals(value.Key, key))
            {
                result = value.Value;
                return true;
            }
            result = null!;
            return false;
        }

        public IEnumerator<KeyValuePair<string, QueryValue>> GetEnumerator()
        {
            yield return value;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>A graph-pattern result combining graph data with individual match bindings.</summary>
public sealed record GraphPatternResult : QueryResult
{
    /// <summary>Retains a graph result and captures its supplied match sequence.</summary>
    /// <param name="graph">The nonnull graph data associated with the matches.</param>
    /// <param name="matches">The nonnull match sequence; copied in input order.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public GraphPatternResult(GraphResult graph, IEnumerable<GraphPatternMatch> matches)
    {
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Matches = Array.AsReadOnly((matches ?? throw new ArgumentNullException(nameof(matches))).ToArray());
    }

    /// <summary>Gets the associated graph data.</summary>
    public GraphResult Graph
    {
        get;
    }

    /// <summary>Gets the captured matches in input order.</summary>
    public IReadOnlyList<GraphPatternMatch> Matches
    {
        get;
    }
}
