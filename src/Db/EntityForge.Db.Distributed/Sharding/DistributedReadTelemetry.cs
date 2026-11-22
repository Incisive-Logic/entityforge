using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace EntityForge.Db.Distributed.Sharding;

// Opt-in spans contain stage/association evidence and bounded hashed dependency
// identities, never request values or credentials. Hashes are diagnostic, not certificates.
internal static class DistributedReadTelemetry
{
    internal const string SourceName = "EntityForge.Distributed";
    private static readonly ActivitySource Source = new(SourceName);
    // Diagnostic consumers aggregate ticks in memory. No Activity or per-observation export.
    internal static Action<string, long>? ObserveElapsedTicks;
    // Optional diagnostic timeline, captured at entry so asynchronous callbacks
    // retain their originating trace. Disabled consumers incur no clock read.
    internal static Action<string, long, long, ActivityTraceId>? ObserveInterval;
    internal static Action<string, long>? ObserveWork;
    internal static Action<string, int, int>? ObserveQueue;

    internal static void Queue(string stage, int waiting, int active) => ObserveQueue?.Invoke(stage, waiting, active);

    internal static void Work(string stage, long amount) => ObserveWork?.Invoke(stage, amount);

    internal readonly struct Measurement : IDisposable
    {
        private readonly string stage;
        private readonly Action<string, long>? observer;
        private readonly Action<string, long, long, ActivityTraceId>? interval;
        private readonly ActivityTraceId trace;
        private readonly long started;
        private readonly long profile;

        internal Measurement(string stage, Action<string, long>? observer)
        {
            this.stage = stage;
            this.observer = observer;
            interval = ObserveInterval;
            trace = interval is null ? default : Activity.Current?.TraceId ?? default;
            started = observer is null && interval is null ? 0 : Stopwatch.GetTimestamp();
            profile = DistributedProfileStages.Log.Enter(stage);
        }

        public void Dispose()
        {
            if (profile != 0)
            {
                DistributedProfileStages.Log.End(profile);
            }

            if (observer is not null || interval is not null)
            {
                long ended = Stopwatch.GetTimestamp();
                observer?.Invoke(stage, ended - started);
                interval?.Invoke(stage, started, ended, trace);
            }
        }
    }

    internal static Measurement Measure(string stage) => new(stage, ObserveElapsedTicks);

    internal static Activity? Start(string stage) => Source.StartActivity(stage);

    internal static Task WaitAsync(SemaphoreSlim gate, string stage, CancellationToken token) =>
        Source.HasListeners() || ObserveElapsedTicks is not null ? TraceWaitAsync(gate, stage, token) : gate.WaitAsync(token);

    private static async Task TraceWaitAsync(SemaphoreSlim gate, string stage, CancellationToken token)
    {
        using var activity = Start(stage);
        using var measurement = Measure(stage);
        await gate.WaitAsync(token).ConfigureAwait(false);
    }
}

// Explicitly enabled EventPipe markers associate stack samples with wall-time
// intervals. They carry only fixed stage names and process-local correlation IDs;
// concurrent work during an interval must not be labelled exclusive stage CPU.
[EventSource(Name = "EntityForge-ProfileStages")]
internal sealed class DistributedProfileStages : EventSource
{
    internal static readonly DistributedProfileStages Log = new();
    private long next;

    [NonEvent]
    internal long Enter(string stage)
    {
        if (!IsEnabled() || stage is not ("partition.application.combined-selection" or
            "partition.application.semantic-combined-selection" or "partition.semantic-planning.cohort" or
            "partition.semantic-proposal.owner" or "partition.semantic-planning.build-selected-plan" or
            "partition.selection.encode" or "partition.selection.authority-wait" or "partition.prepare.reconstruct"))
        {
            return 0;
        }

        long id = Interlocked.Increment(ref next);
        Begin(id, stage);
        return id;
    }

    [Event(1, Level = EventLevel.Informational)]
    public void Begin(long id, string stage) => WriteEvent(1, id, stage);

    [Event(2, Level = EventLevel.Informational)]
    public void End(long id) => WriteEvent(2, id);
}
