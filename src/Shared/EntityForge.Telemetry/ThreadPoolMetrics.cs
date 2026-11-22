using System.Diagnostics.Metrics;

namespace EntityForge.Telemetry;

/// <summary>
/// Process-wide observations of the shared .NET thread pool.
/// </summary>
public static class ThreadPoolMetrics
{
    /// <summary>The Meter name used for runtime thread-pool measurements.</summary>
    public const string MeterName = "EntityForge.Runtime.ThreadPool";

    private static readonly Meter _meter = new(MeterName);

    private static readonly ObservableGauge<int> _workerThreads = _meter.CreateObservableGauge(
        "entityforge.runtime.thread_pool.worker_threads",
        ObserveWorkerThreads,
        unit: "{thread}",
        description: "Thread-pool worker threads currently in use and available.");

    private static readonly ObservableGauge<int> _workerThreadLimits = _meter.CreateObservableGauge(
        "entityforge.runtime.thread_pool.worker_threads.limit",
        ObserveWorkerThreadLimits,
        unit: "{thread}",
        description: "Configured minimum and maximum thread-pool worker thread counts.");

    private static readonly ObservableGauge<int> _threadCount = _meter.CreateObservableGauge(
        "entityforge.runtime.thread_pool.threads",
        () => ThreadPool.ThreadCount,
        unit: "{thread}",
        description: "Thread-pool worker threads that currently exist.");

    private static readonly ObservableGauge<long> _pendingWorkItems = _meter.CreateObservableGauge(
        "entityforge.runtime.thread_pool.work_items.pending",
        () => ThreadPool.PendingWorkItemCount,
        unit: "{work_item}",
        description: "Work items currently queued to the thread pool.");

    private static readonly ObservableCounter<long> _completedWorkItems = _meter.CreateObservableCounter(
        "entityforge.runtime.thread_pool.work_items.completed",
        () => ThreadPool.CompletedWorkItemCount,
        unit: "{work_item}",
        description: "Work items completed by the thread pool since process start.");

    /// <summary>
    /// Initializes the observable instruments without changing thread-pool configuration.
    /// </summary>
    public static void EnsureInitialized()
    {
        _ = _completedWorkItems;
    }

    private static IEnumerable<Measurement<int>> ObserveWorkerThreads()
    {
        ThreadPool.GetAvailableThreads(out int availableWorkerThreads, out _);
        ThreadPool.GetMaxThreads(out int maximumWorkerThreads, out _);

        return
        [
            new Measurement<int>(maximumWorkerThreads - availableWorkerThreads,
                new KeyValuePair<string, object>("state", "used")),
            new Measurement<int>(availableWorkerThreads,
                new KeyValuePair<string, object>("state", "available"))
        ];
    }

    private static IEnumerable<Measurement<int>> ObserveWorkerThreadLimits()
    {
        ThreadPool.GetMinThreads(out int minimumWorkerThreads, out _);
        ThreadPool.GetMaxThreads(out int maximumWorkerThreads, out _);

        return
        [
            new Measurement<int>(minimumWorkerThreads,
                new KeyValuePair<string, object>("limit", "minimum")),
            new Measurement<int>(maximumWorkerThreads,
                new KeyValuePair<string, object>("limit", "maximum"))
        ];
    }
}
