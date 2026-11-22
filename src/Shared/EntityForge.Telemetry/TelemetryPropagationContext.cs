using System.Diagnostics;

namespace EntityForge.Telemetry;

/// <summary>
/// Carries an extracted remote trace context across asynchronous runtime code
/// without creating an intermediary activity or exported span.
/// </summary>
public static class TelemetryPropagationContext
{
    private sealed class ContextState
    {
        public ContextState(
            ActivityContext context,
            ContextState previous,
            Activity previousActivity)
        {
            Context = context;
            Previous = previous;
            PreviousActivity = previousActivity;
        }

        public ActivityContext Context
        {
            get;
        }

        public ContextState Previous
        {
            get;
        }

        public Activity PreviousActivity
        {
            get;
        }
    }

    private sealed class ContextScope : IDisposable
    {
        private readonly ContextState _state;
        private bool _disposed;

        public ContextScope(ContextState state)
        {
            _state = state;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (ReferenceEquals(_current.Value, _state))
            {
                _current.Value = _state.Previous;
                Activity.Current = _state.PreviousActivity;
            }
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private static readonly AsyncLocal<ContextState> _current = new();

    /// <summary>Installs a scoped propagation context and returns a handle that restores the previous context on disposal.</summary>
    public static IDisposable Begin(string parentActivityId)
    {
        if (!ActivityHelper.TryParseActivityContext(
            parentActivityId,
            out ActivityContext context))
        {
            return EmptyScope.Instance;
        }

        var state = new ContextState(context, _current.Value, Activity.Current);
        _current.Value = state;
        Activity.Current = null;
        return new ContextScope(state);
    }

    /// <summary>Attempts to read the currently installed propagation context.</summary>
    public static bool TryGetCurrent(out ActivityContext context)
    {
        ContextState state = _current.Value;
        if (state == null)
        {
            context = default;
            return false;
        }

        context = state.Context;
        return true;
    }

    /// <summary>Gets the current propagated or active tracing identifier.</summary>
    public static string GetCurrentActivityId()
    {
        if (Activity.Current?.Id is string activityId)
        {
            return activityId;
        }

        return TryGetCurrent(out ActivityContext context)
            ? $"00-{context.TraceId}-{context.SpanId}-{(context.TraceFlags.HasFlag(ActivityTraceFlags.Recorded) ? "01" : "00")}"
            : null;
    }
}
