using System.Globalization;
using System.Collections.Immutable;
using System.Security.Cryptography;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

internal sealed record GraphValueFrequency(IReadOnlyDictionary<string, long> TrackedValues, long OverflowOccurrences);

internal sealed record GraphQueryStatistics(
    DatabaseVersion Version,
    long VertexCount,
    long EdgeCount,
    IReadOnlyDictionary<string, long> VertexLabelCardinalities,
    IReadOnlyDictionary<string, long> VertexPropertyCardinalities,
    IReadOnlyDictionary<string, long> EdgeLabelCardinalities,
    IReadOnlyDictionary<string, long> EdgePropertyCardinalities,
    IReadOnlyDictionary<string, GraphValueFrequency> VertexValueFrequencies,
    IReadOnlyDictionary<string, GraphValueFrequency> EdgeValueFrequencies,
    long OverflowVertexLabels,
    long OverflowVertexProperties,
    long OverflowEdgeLabels,
    long OverflowEdgeProperties)
{
    internal const int MaximumTrackedKeysPerFamily = 4_096;
    internal const int MaximumTrackedValuesPerProperty = 32;

    public static GraphQueryStatistics Empty
    {
        get;
    }
    = new(default, 0, 0,
        new Dictionary<string, long>(StringComparer.Ordinal),
        new Dictionary<string, long>(StringComparer.Ordinal),
        new Dictionary<string, long>(StringComparer.Ordinal),
        new Dictionary<string, long>(StringComparer.Ordinal),
        new Dictionary<string, GraphValueFrequency>(StringComparer.Ordinal),
        new Dictionary<string, GraphValueFrequency>(StringComparer.Ordinal), 0, 0, 0, 0);

    public static GraphQueryStatistics From(DatabaseVersion version,
        IEnumerable<GraphVertex> vertices, IEnumerable<GraphEdge> edges) => Empty.Apply(version,
        new(vertices.Select(vertex => (GraphCommittedEffect)new GraphVertexCreated(vertex))
            .Concat(edges.Select(edge => (GraphCommittedEffect)new GraphEdgeCreated(edge))).ToArray()));

    public long? EstimateVertexValue(string property, DatabaseValue value)
    {
        // Persisted URI frequencies use lexical keys, while native candidate equality uses Uri.Equals.
        // Do not present a lexical count as an exact count of native-equal candidates.
        if (value is DatabaseValue.Binary or DatabaseValue.Uri)
        {
            return null;
        }

        if (!VertexValueFrequencies.TryGetValue(property, out GraphValueFrequency? frequency))
        {
            return null;
        }

        if (frequency.TrackedValues.TryGetValue(ValueKey(value), out long count))
        {
            return count;
        }

        return frequency.OverflowOccurrences == 0 ? 0 : null;
    }

    // An untracked key with overflow is unknown, not absent. Total vertices is a
    // safe bound for candidate selection even when one vertex has many properties.
    internal long VertexPropertyBound(string property) => VertexPropertyCardinalities.TryGetValue(property, out long count)
        ? count : OverflowVertexProperties == 0 ? 0 : VertexCount;

    internal long VertexLabelBound(string label) => VertexLabelCardinalities.TryGetValue(label, out long count)
        ? count : OverflowVertexLabels == 0 ? 0 : VertexCount;

    internal bool HasExactLabelCount(string label) => VertexLabelCardinalities.ContainsKey(label) || OverflowVertexLabels == 0;

    public double AverageDegree(string? label) => VertexCount == 0 ? 0D :
        (label is null ? EdgeCount : EdgeLabelCardinalities.GetValueOrDefault(label)) / (double)VertexCount;

    public GraphQueryStatistics Apply(DatabaseVersion version, GraphCommittedEffects effects)
    {
        if (effects.Effects.Count == 0)
        {
            return this with
            {
                Version = version
            };
        }

        var vertexLabels = new Cardinalities(VertexLabelCardinalities);
        var vertexProperties = new Cardinalities(VertexPropertyCardinalities);
        var edgeLabels = new Cardinalities(EdgeLabelCardinalities);
        var edgeProperties = new Cardinalities(EdgePropertyCardinalities);
        var vertexValues = new Frequencies(VertexValueFrequencies);
        var edgeValues = new Frequencies(EdgeValueFrequencies);
        long vertices = VertexCount, edges = EdgeCount;
        long overflowVertexLabels = OverflowVertexLabels;
        long overflowVertexProperties = OverflowVertexProperties;
        long overflowEdgeLabels = OverflowEdgeLabels;
        long overflowEdgeProperties = OverflowEdgeProperties;
        foreach (GraphCommittedEffect effect in effects.Effects)
        {
            switch (effect)
            {
                case GraphVertexCreated created:
                    vertices++;
                    foreach (string label in created.Vertex.Labels)
                    {
                        Change(vertexLabels, label, 1, ref overflowVertexLabels);
                    }

                    foreach ((string property, DatabaseValue value) in created.Vertex.Properties)
                    {
                        Change(vertexProperties, property, 1, ref overflowVertexProperties);
                        ChangeValue(vertexValues, vertexProperties, property, value, 1);
                    }
                    break;
                case GraphVertexDeleted deleted:
                    vertices--;
                    foreach (string label in deleted.Vertex.Labels)
                    {
                        Change(vertexLabels, label, -1, ref overflowVertexLabels);
                    }

                    foreach ((string property, DatabaseValue value) in deleted.Vertex.Properties)
                    {
                        ChangeValue(vertexValues, vertexProperties, property, value, -1);
                        Change(vertexProperties, property, -1, ref overflowVertexProperties);
                    }
                    break;
                case GraphEdgeCreated created:
                    edges++;
                    Change(edgeLabels, created.Edge.Label, 1, ref overflowEdgeLabels);
                    foreach ((string property, DatabaseValue value) in created.Edge.Properties)
                    {
                        Change(edgeProperties, property, 1, ref overflowEdgeProperties);
                        ChangeValue(edgeValues, edgeProperties, property, value, 1);
                    }
                    break;
                case GraphEdgeDeleted deleted:
                    edges--;
                    Change(edgeLabels, deleted.Edge.Label, -1, ref overflowEdgeLabels);
                    foreach ((string property, DatabaseValue value) in deleted.Edge.Properties)
                    {
                        ChangeValue(edgeValues, edgeProperties, property, value, -1);
                        Change(edgeProperties, property, -1, ref overflowEdgeProperties);
                    }
                    break;
                case GraphVertexPropertyChanged changed:
                    ChangeProperty(vertexProperties, vertexValues, changed.Property, changed.Before, changed.After, ref overflowVertexProperties);
                    break;
                case GraphEdgePropertyChanged changed:
                    ChangeProperty(edgeProperties, edgeValues, changed.Property, changed.Before, changed.After, ref overflowEdgeProperties);
                    break;
                case GraphVertexLabelsChanged changed:
                    foreach (string removed in changed.Before.Except(changed.After, StringComparer.Ordinal))
                    {
                        Change(vertexLabels, removed, -1, ref overflowVertexLabels);
                    }

                    foreach (string added in changed.After.Except(changed.Before, StringComparer.Ordinal))
                    {
                        Change(vertexLabels, added, 1, ref overflowVertexLabels);
                    }

                    break;
            }
        }
        if (vertices < 0 || edges < 0)
        {
            throw new InvalidDataException("Graph query statistics cannot become negative.");
        }

        return new(version, vertices, edges, vertexLabels.Build(), vertexProperties.Build(), edgeLabels.Build(), edgeProperties.Build(),
            vertexValues.Build(), edgeValues.Build(), overflowVertexLabels, overflowVertexProperties,
            overflowEdgeLabels, overflowEdgeProperties);
    }

    private static void ChangeProperty(Cardinalities cardinalities, Frequencies frequencies,
        string property, DatabaseValue? before, DatabaseValue? after, ref long overflow)
    {
        if (before is not null)
        {
            ChangeValue(frequencies, cardinalities, property, before, -1);
        }

        if (before is null && after is not null)
        {
            Change(cardinalities, property, 1, ref overflow);
        }

        if (before is not null && after is null)
        {
            Change(cardinalities, property, -1, ref overflow);
        }

        if (after is not null)
        {
            ChangeValue(frequencies, cardinalities, property, after, 1);
        }
    }

    private static void ChangeValue(Frequencies frequencies,
        Cardinalities trackedProperties, string property, DatabaseValue value, int delta)
    {
        if (!trackedProperties.ContainsKey(property))
        {
            return;
        }

        if (!frequencies.TryGetValue(property, out MutableFrequency? frequency))
        {
            if (delta < 0)
            {
                throw new InvalidDataException("A graph value statistic became negative.");
            }

            frequency = new();
            frequencies.Add(property, frequency);
        }
        string key = ValueKey(value);
        if (frequency.Values.TryGetValue(key, out long current))
        {
            long changed = current + delta;
            if (changed < 0)
            {
                throw new InvalidDataException("A tracked graph value statistic became negative.");
            }

            frequency.Values[key] = changed;
        }
        else if (delta > 0 && frequency.Values.Count < MaximumTrackedValuesPerProperty)
        {
            frequency.Values.Add(key, delta);
        }
        else
        {
            frequency.Overflow += delta;
            if (frequency.Overflow < 0)
            {
                throw new InvalidDataException("A graph value-statistics overflow count became negative.");
            }
        }
    }

    // Builders are private to one statistics transition. Published families and distributions
    // remain immutable and can be shared by older snapshots and staged predecessors.
    private sealed class Cardinalities(IReadOnlyDictionary<string, long> source)
    {
        private ImmutableDictionary<string, long>.Builder? _changed;

        private IReadOnlyDictionary<string, long> Current => _changed is null ? source : _changed;

        private ImmutableDictionary<string, long>.Builder Writable => _changed ??=
            (source as ImmutableDictionary<string, long> ?? source.ToImmutableDictionary(StringComparer.Ordinal)).ToBuilder();

        internal int Count => Current.Count;

        internal bool ContainsKey(string key) => Current.ContainsKey(key);

        internal bool TryGetValue(string key, out long value) => Current.TryGetValue(key, out value);

        internal long this[string key] { set => Writable[key] = value; }

        internal void Add(string key, long value) => Writable.Add(key, value);

        internal IReadOnlyDictionary<string, long> Build() => _changed is null ? source : _changed.ToImmutable();
    }

    private sealed class Frequencies(IReadOnlyDictionary<string, GraphValueFrequency> source)
    {
        private Dictionary<string, MutableFrequency>? _changed;

        internal bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MutableFrequency? value)
        {
            if (_changed is not null && _changed.TryGetValue(key, out value))
            {
                return true;
            }

            if (!source.TryGetValue(key, out var prior))
            {
                value = null;
                return false;
            }
            value = new(prior.TrackedValues.ToDictionary(StringComparer.Ordinal), prior.OverflowOccurrences);
            (_changed ??= new(StringComparer.Ordinal)).Add(key, value);
            return true;
        }

        internal void Add(string key, MutableFrequency value) => (_changed ??= new(StringComparer.Ordinal)).Add(key, value);

        internal IReadOnlyDictionary<string, GraphValueFrequency> Build()
        {
            if (_changed is null)
            {
                return source;
            }

            var result = (source as ImmutableDictionary<string, GraphValueFrequency> ??
                source.ToImmutableDictionary(StringComparer.Ordinal)).ToBuilder();
            foreach (var (key, value) in _changed)
            {
                result[key] = new(value.Values, value.Overflow);
            }

            return result.ToImmutable();
        }
    }

    private static string ValueKey(DatabaseValue value)
    {
        byte[] digest = value switch
        {
            DatabaseValue.Date calendar => DatabaseUtf8Hash.Hash(calendar.Value.ToString()),
            DatabaseValue.Duration duration => DatabaseUtf8Hash.Hash(duration.Qualifier == DatabaseDurationQualifier.Unqualified
                ? duration.Value.ToString()
                : ((byte)duration.Qualifier).ToString(CultureInfo.InvariantCulture) + ":" + duration.Value),
            DatabaseValue.LocalDateTime time => DatabaseUtf8Hash.Hash(time.Value.ToString()),
            DatabaseValue.LocalTime time => DatabaseUtf8Hash.Hash(time.Value.ToString()),
            DatabaseValue.Uuid uuid => DatabaseUtf8Hash.Hash(uuid.Value.ToString("D")),
            DatabaseValue.Decimal exact => DatabaseUtf8Hash.Hash(exact.Value.ToString("G29", CultureInfo.InvariantCulture)),
            DatabaseValue.ZonedTime time => DatabaseUtf8Hash.Hash(time.Value.ToString()),
            DatabaseValue.ZonedDateTime time => DatabaseUtf8Hash.Hash(time.Value.ToString()),
            DatabaseValue.Point point => DatabaseUtf8Hash.Hash(point.Value.ToString()),
            DatabaseValue.String item => DatabaseUtf8Hash.Hash(item.Value),
            DatabaseValue.Int64 item => DatabaseUtf8Hash.Hash(item.Value.ToString(CultureInfo.InvariantCulture)),
            DatabaseValue.Double item => DatabaseUtf8Hash.Hash(DoubleKey(item.Value)),
            DatabaseValue.Boolean item => SHA256.HashData(new byte[] { item.Value ? (byte)1 : (byte)0 }),
            DatabaseValue.DateTimeOffset item => DatabaseUtf8Hash.Hash(
                item.Value.UtcTicks.ToString(CultureInfo.InvariantCulture)),
            DatabaseValue.Uri item => DatabaseUtf8Hash.Hash(item.Value.AbsoluteUri),
            DatabaseValue.Binary item => SHA256.HashData(item.Value.Span),
            DatabaseValue.Json item => DatabaseUtf8Hash.Hash(item.Value.Value),
            DatabaseValue.Array item => DatabaseUtf8Hash.Hash(String.Join(";", item.Value.Select(ValueKey))),
            _ => throw new InvalidDataException($"Unsupported graph statistics value kind '{value.Kind}'.")
        };
        return $"{((byte)value.Kind).ToString(CultureInfo.InvariantCulture)}:{Convert.ToHexString(digest)}";
    }

    private static string DoubleKey(double value) => Double.IsNaN(value) ? "NaN"
        : value == 0D ? "0" : value.ToString("R", CultureInfo.InvariantCulture);

    private static void Change(Cardinalities values, string key, int delta, ref long overflow)
    {
        if (values.TryGetValue(key, out long current))
        {
            long changed = current + delta;
            if (changed < 0)
            {
                throw new InvalidDataException("A tracked graph statistic became negative.");
            }

            values[key] = changed;
            return;
        }
        if (delta > 0 && values.Count < MaximumTrackedKeysPerFamily)
        {
            values.Add(key, delta);
            return;
        }
        overflow += delta;
        if (overflow < 0)
        {
            throw new InvalidDataException("A graph statistics overflow count became negative.");
        }
    }

    private sealed class MutableFrequency
    {
        public MutableFrequency() : this(new Dictionary<string, long>(StringComparer.Ordinal), 0)
        {
        }

        public MutableFrequency(Dictionary<string, long> values, long overflow)
        {
            Values = values;
            Overflow = overflow;
        }

        public Dictionary<string, long> Values
        {
            get;
        }

        public long Overflow
        {
            get; set;
        }
    }
}
