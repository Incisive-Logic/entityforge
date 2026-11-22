namespace EntityForge.Telemetry;

/// <summary>Common exporter settings for EntityForge application hosts.</summary>
public static class TelemetryEnvironment
{
    /// <summary>The default metric export interval of 5,000 milliseconds.</summary>
    public const int DefaultMetricExportIntervalMilliseconds = 5000;
    /// <summary>The service namespace assigned to EntityForge telemetry resources.</summary>
    public const string ServiceNamespace = "EntityForge";

    /// <summary>Reads OTEL_EXPORTER_OTLP_ENDPOINT, returning null when unset; rejects non-HTTP(S) URLs and embedded credentials, query or fragment.</summary>
    public static Uri ReadOtlpEndpoint()
    {
        string value = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out Uri endpoint) ||
            endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException("OTEL_EXPORTER_OTLP_ENDPOINT must be an HTTP(S) base URL without credentials, query or fragment.");
        }

        return endpoint;
    }

    /// <summary>Reads a positive OTEL_METRIC_EXPORT_INTERVAL in milliseconds, defaulting to 5,000 when unset.</summary>
    public static int ReadMetricExportInterval()
    {
        string value = Environment.GetEnvironmentVariable("OTEL_METRIC_EXPORT_INTERVAL");
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultMetricExportIntervalMilliseconds;
        }

        if (!int.TryParse(value, out int interval) || interval <= 0)
        {
            throw new InvalidOperationException("OTEL_METRIC_EXPORT_INTERVAL must be a positive integer.");
        }

        return interval;
    }
}
