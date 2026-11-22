namespace EntityForge.Db.Transport.Contracts;

/// <summary>Server filesystem information, not the Studio client's disk or a physical-device inventory.</summary>
/// <param name="CapturedAt">Time the host captured this report.</param>
/// <param name="DataDirectory">Server-side storage directory inspected.</param>
/// <param name="FileSystem">Reported filesystem format, or null when unavailable.</param>
/// <param name="TotalBytes">Reported filesystem capacity in bytes, or null when unavailable.</param>
/// <param name="AvailableBytes">Reported available space in bytes, or null when unavailable.</param>
/// <param name="CapacityError">Sanitized explanation when capacity information could not be obtained.</param>
/// <param name="ProbeRunning">Whether a probe was running at capture time.</param>
/// <param name="NextProbeAllowedAt">Earliest time another probe may be admitted.</param>
/// <param name="LastProbe">Most recent completed probe, or null when none has completed.</param>
public sealed record StorageProfileReport(DateTimeOffset CapturedAt, string DataDirectory,
    string? FileSystem, long? TotalBytes, long? AvailableBytes, string? CapacityError,
    bool ProbeRunning, DateTimeOffset? NextProbeAllowedAt, StorageProbeResult? LastProbe);

/// <summary>A small buffered scratch-file experiment. Read throughput includes the OS cache.</summary>
/// <param name="StartedAt">Probe start time.</param>
/// <param name="CompletedAt">Probe completion time.</param>
/// <param name="DurableWriteSamples">Number of flush-to-disk write samples.</param>
/// <param name="DurableWriteBytes">Payload bytes per durable-write sample.</param>
/// <param name="MedianDurableWriteMs">Median sampled write-and-flush duration in milliseconds.</param>
/// <param name="P95DurableWriteMs">95th-percentile sampled write-and-flush duration in milliseconds.</param>
/// <param name="P99DurableWriteMs">99th-percentile sampled write-and-flush duration in milliseconds.</param>
/// <param name="MaximumDurableWriteMs">Slowest sampled write-and-flush duration in milliseconds.</param>
/// <param name="SequentialBytes">Byte count used for the sequential throughput measurement.</param>
/// <param name="SequentialWriteMiBPerSecond">Observed sequential write throughput in mebibytes per second.</param>
/// <param name="CachedReadMiBPerSecond">Observed buffered read throughput, including operating-system cache effects.</param>
public sealed record StorageProbeResult(DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    int DurableWriteSamples, int DurableWriteBytes, double MedianDurableWriteMs,
    double P95DurableWriteMs, double P99DurableWriteMs, double MaximumDurableWriteMs,
    long SequentialBytes, double SequentialWriteMiBPerSecond, double CachedReadMiBPerSecond);
