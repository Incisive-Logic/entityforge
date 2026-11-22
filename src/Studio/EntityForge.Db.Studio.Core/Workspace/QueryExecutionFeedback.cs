using EntityForge.Db.AppModel.Queries;

namespace EntityForge.Db.Studio.Workspace;

/// <summary>Client-observed duration of a run, independent of panel rendering and paging.</summary>
public sealed class QueryExecutionFeedback(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private long? _started;

    public TimeSpan? Duration
    {
        get; private set;
    }

    public EntityQueryExecutionState State
    {
        get; private set;
    }

    public void Update(EntityQueryExecutionState state, bool isPaging)
    {
        if (isPaging)
        {
            return;
        }

        State = state;
        if (state == EntityQueryExecutionState.Idle)
        {
            _started = null;
            Duration = null;
        }
        else if (state == EntityQueryExecutionState.Running)
        {
            if (_started is null)
            {
                _started = _clock.GetTimestamp();
                Duration = null;
            }
        }
        else if (_started is { } started)
        {
            Duration = _clock.GetElapsedTime(started);
            _started = null;
        }
    }
}
