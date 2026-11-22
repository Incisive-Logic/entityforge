using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>A completed query projection associated with its verified distributed snapshot.</summary>
[JsonConverter(typeof(PartitionQueryResult.Converter))]
public sealed record PartitionQueryResult
{
    /// <summary>Gets the database generation that produced this result.</summary>
    public Guid Generation
    {
        get; init;
    }

    /// <summary>Gets the profile that executed the query.</summary>
    public DatabaseProfileId Profile
    {
        get; init;
    }

    /// <summary>Gets the language in which the query was submitted.</summary>
    public string Language
    {
        get; init;
    }

    /// <summary>Gets the certified minimum partition positions for the result.</summary>
    public DatabasePositionVector Position
    {
        get; init;
    }

    /// <summary>Gets the opaque continuation for the next page, or null when no continuation remains.</summary>
    public string? NextContinuation
    {
        get; init;
    }

    /// <summary>Gets measured exchange and owner-pruning statistics when available.</summary>
    public PartitionQueryStatistics? Statistics
    {
        get; init;
    }

    private JsonElement _decoded;
    private PartitionProjectionValue? _output;
    private Lazy<JsonElement>? _materialized;

    /// <summary>Owned JSON for applications that inspect the result. Encoding can use the typed projection directly.</summary>
    public JsonElement Value
    {
        get
        {
            if (_output is null)
            {
                return _decoded;
            }

            var lazy = Volatile.Read(ref _materialized);
            if (lazy is null)
            {
                var output = _output;
                lazy = new Lazy<JsonElement>(() => JsonSerializer.Deserialize<JsonElement>(output.Encode(PartitionQueryLimits.MaximumStreamBytes)), LazyThreadSafetyMode.ExecutionAndPublication);
                lazy = Interlocked.CompareExchange(ref _materialized, lazy, null) ?? lazy;
            }
            return lazy.Value;
        }

        init
        {
            _decoded = value;
            _output = null;
            _materialized = null;
        }
    }

    /// <summary>Creates a distributed query result with its database identity, certified positions and optional continuation and statistics.</summary>
    public PartitionQueryResult(Guid Generation, DatabaseProfileId Profile, string Language,
        DatabasePositionVector Position, JsonElement Value, string? NextContinuation = null, PartitionQueryStatistics? Statistics = null)
    {
        this.Generation = Generation;
        this.Profile = Profile;
        this.Language = Language;
        this.Position = Position;
        this.Value = Value;
        this.NextContinuation = NextContinuation;
        this.Statistics = Statistics;
    }

    internal PartitionQueryResult(Guid Generation, DatabaseProfileId Profile, string Language,
        DatabasePositionVector Position, PartitionProjectionValue Value, string? NextContinuation = null, PartitionQueryStatistics? Statistics = null)
        : this(Generation, Profile, Language, Position, default(JsonElement), NextContinuation, Statistics) => _output = Value;

    internal object? NativeResult => PartitionPublicQueryProjection.Native(Output);

    internal PartitionProjectionValue Output => _output ?? new(_decoded);

    // Inspecting lazy JSON must not change the identity/hash of an immutable record.
    /// <summary>Compares query results using their declared result fields.</summary>
    public bool Equals(PartitionQueryResult? other) => other is not null &&
        Generation == other.Generation && Profile == other.Profile && Language == other.Language &&
        EqualityComparer<DatabasePositionVector>.Default.Equals(Position, other.Position) &&
        NextContinuation == other.NextContinuation && Equals(Statistics, other.Statistics) &&
        (_output is null && other._output is null ? _decoded.Equals(other._decoded) : ReferenceEquals(_output, other._output));

    /// <summary>Computes a hash from the query result's declared result fields.</summary>
    public override int GetHashCode() => HashCode.Combine(Generation, Profile, Language, Position,
        NextContinuation, Statistics, _output is null ? _decoded.GetHashCode() : _output.GetHashCode());

    internal PartitionQueryResult WithOutput(PartitionProjectionValue value)
    {
        var copy = this with
        {
        };
        copy._output = value;
        copy._decoded = default;
        copy._materialized = null;
        return copy;
    }

    internal sealed class Converter : JsonConverter<PartitionQueryResult>
    {
        private sealed record Wire(Guid Generation, DatabaseProfileId Profile, string Language,
            DatabasePositionVector Position, JsonElement Value, string? NextContinuation, PartitionQueryStatistics? Statistics);

        public override PartitionQueryResult Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var wire = JsonSerializer.Deserialize<Wire>(ref reader, options) ?? throw new JsonException("Missing query result.");
            return new(wire.Generation, wire.Profile, wire.Language, wire.Position, wire.Value, wire.NextContinuation, wire.Statistics);
        }

        private static string Name(string name, JsonSerializerOptions options) => options.PropertyNamingPolicy?.ConvertName(name) ?? name;

        public override void Write(Utf8JsonWriter writer, PartitionQueryResult value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString(Name(nameof(Generation), options), value.Generation);
            writer.WritePropertyName(Name(nameof(Profile), options));
            JsonSerializer.Serialize(writer, value.Profile, options);
            writer.WriteString(Name(nameof(Language), options), value.Language);
            writer.WritePropertyName(Name(nameof(Position), options));
            JsonSerializer.Serialize(writer, value.Position, options);
            writer.WritePropertyName(Name(nameof(Value), options));
            value.Output.Write(writer);
            writer.WriteString(Name(nameof(NextContinuation), options), value.NextContinuation);
            writer.WritePropertyName(Name(nameof(Statistics), options));
            JsonSerializer.Serialize(writer, value.Statistics, options);
            writer.WriteEndObject();
        }
    }
}
