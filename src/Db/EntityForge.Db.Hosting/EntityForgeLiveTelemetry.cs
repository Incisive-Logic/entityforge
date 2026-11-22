using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EntityForge.Db.Hosting;

internal sealed record LiveMetric(string Meter, string Name, string? Unit, string Kind,
    long Samples, double Sum, double Minimum, double Maximum, double Last, DateTimeOffset LastObservedAt);

internal sealed record LiveTelemetryReport(int Version, Guid ProcessSession, string Scope,
    DateTimeOffset StartedAt, DateTimeOffset CapturedAt, int MaximumMetrics, long DroppedMeasurements, IReadOnlyList<LiveMetric> Metrics);

// Independent listener over the same instruments consumed by the OTLP exporter. No query text,
// credentials or high-cardinality tags are retained. Multiple databases in this process are aggregated.
internal sealed class EntityForgeLiveTelemetry : IDisposable
{
    private const int MaximumMetrics = 128;
    private readonly object _gate = new();
    private readonly MeterListener _listener = new();

    private sealed record MetricKey(string Meter, string Name, string Kind, string? Unit);

    private sealed class Accumulator(MetricKey key, double value, DateTimeOffset now)
    {
        public MetricKey Key = key;
        public long Samples = 1;
        public double Sum = value, Minimum = value, Maximum = value, Last = value;
        public DateTimeOffset LastObservedAt = now;

        public LiveMetric Snapshot() => new(Key.Meter, Key.Name, Key.Unit, Key.Kind, Samples, Sum, Minimum, Maximum, Last, LastObservedAt);
    }

    private readonly Dictionary<MetricKey, Accumulator> _metrics = [];
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly Guid _session = Guid.NewGuid();
    private long _dropped;

    public EntityForgeLiveTelemetry()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            string type = instrument.GetType().Name;
            if (instrument.Meter.Name.StartsWith("EntityForge.Db", StringComparison.Ordinal) &&
                (type.StartsWith("Counter`", StringComparison.Ordinal) || type.StartsWith("UpDownCounter`", StringComparison.Ordinal) || type.StartsWith("Histogram`", StringComparison.Ordinal)))
            {
                listener.EnableMeasurementEvents(instrument, new MetricKey(instrument.Meter.Name, instrument.Name, type.Split('`')[0], instrument.Unit));
            }
        };
        _listener.SetMeasurementEventCallback<double>((_, v, _, key) => Record((MetricKey)key!, v));
        _listener.SetMeasurementEventCallback<float>((_, v, _, key) => Record((MetricKey)key!, v));
        _listener.SetMeasurementEventCallback<long>((_, v, _, key) => Record((MetricKey)key!, v));
        _listener.SetMeasurementEventCallback<int>((_, v, _, key) => Record((MetricKey)key!, v));
        _listener.SetMeasurementEventCallback<short>((_, v, _, key) => Record((MetricKey)key!, v));
        _listener.SetMeasurementEventCallback<byte>((_, v, _, key) => Record((MetricKey)key!, v));
        _listener.SetMeasurementEventCallback<decimal>((_, v, _, key) => Record((MetricKey)key!, (double)v));
        _listener.Start();
    }

    private void Record(MetricKey key, double value)
    {
        lock (_gate)
        {
            if (!double.IsFinite(value) || (!_metrics.ContainsKey(key) && _metrics.Count >= MaximumMetrics))
            {
                _dropped++;
                return;
            }
            var now = DateTimeOffset.UtcNow;
            if (_metrics.TryGetValue(key, out var previous))
            {
                double sum = previous.Sum + value;
                if (!double.IsFinite(sum))
                {
                    _dropped++;
                    return;
                }
                previous.Samples++;
                previous.Sum = sum;
                previous.Minimum = Math.Min(previous.Minimum, value);
                previous.Maximum = Math.Max(previous.Maximum, value);
                previous.Last = value;
                previous.LastObservedAt = now;
            }
            else
            {
                _metrics.Add(key, new(key, value, now));
            }
        }
    }

    public LiveTelemetryReport Capture()
    {
        lock (_gate)
        {
            return new(1, _session, "process", _started, DateTimeOffset.UtcNow, MaximumMetrics, _dropped,
            _metrics.Values.Select(m => m.Snapshot()).OrderBy(m => m.Meter).ThenBy(m => m.Name).ToArray());
        }
    }

    public void Dispose() => _listener.Dispose();

    public static void Map(IEndpointRouteBuilder endpoints) => endpoints.MapGet("/v1/telemetry",
        async (HttpContext context, IEntityForgeDbAuthorizationPolicy policy, EntityForgeLiveTelemetry telemetry) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!await policy.AuthorizeAsync(context, EntityForgeDbAdministrativeOperation.ReadStatus, context.RequestAborted))
            {
                return Results.StatusCode(403);
            }

            return Results.Ok(telemetry.Capture());
        });
}
