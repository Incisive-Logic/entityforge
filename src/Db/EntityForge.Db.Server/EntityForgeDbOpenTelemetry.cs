using EntityForge.Telemetry;
using EntityForge.Db.Telemetry;
using EntityForge.Db.Kernel;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace EntityForge.Db.Server;

internal sealed class EntityForgeDbOpenTelemetry : IDisposable
{
    private readonly TracerProvider _traces;
    private readonly MeterProvider _metrics;

    private EntityForgeDbOpenTelemetry(Uri endpoint, string instance, int exportIntervalMilliseconds)
    {
        ResourceBuilder resource = ResourceBuilder.CreateDefault().AddService(
            "EntityForge.Db.Server", serviceNamespace: TelemetryEnvironment.ServiceNamespace, serviceInstanceId: instance);
        _traces = Sdk.CreateTracerProviderBuilder().SetResourceBuilder(resource)
            .SetSampler(new ParentBasedSampler(new AlwaysOnSampler()))
            .AddHttpClientInstrumentation()
            .AddAspNetCoreInstrumentation(options => options.Filter = context =>
                !context.Request.Path.StartsWithSegments("/health") &&
                !context.Request.Path.StartsWithSegments("/v1/observability") &&
                context.Request.Path != "/v1/telemetry" &&
                !context.Request.Path.StartsWithSegments("/studio"))
            .AddSource(EntityForgeDbTelemetry.ActivitySourceName, DatabaseKernelTelemetry.ActivitySourceName,
                DatabaseStorageTelemetry.ActivitySourceName, DatabaseChangeJournalTelemetry.ActivitySourceName,
                "EntityForge.Db.Semantic.Projections", "EntityForge.Db.Requests", "EntityForge.Db.Query")
            .AddOtlpExporter(options => Configure(options, endpoint, "v1/traces"))
            .Build();
        _metrics = Sdk.CreateMeterProviderBuilder().SetResourceBuilder(resource)
            .AddMeter(EntityForgeDbTelemetry.MeterName, DatabaseKernelTelemetry.MeterName,
                "EntityForge.Db.Semantic.Projections", DatabaseChangeJournalTelemetry.MeterName,
                DatabaseStorageTelemetry.MeterName, PhysicalPageTelemetry.MeterName,
                DatabaseMaintenanceCoordinator.MeterName, "EntityForge.Db.Requests", "EntityForge.Db.Query", "EntityForge.Db.Hosting.Reads",
                "System.Runtime", "System.Net.Http",
                "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel")
            .AddOtlpExporter((options, reader) =>
            {
                Configure(options, endpoint, "v1/metrics");
                reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                    exportIntervalMilliseconds;
            }).Build();
    }

    public static EntityForgeDbOpenTelemetry? StartFromEnvironment(string instance)
    {
        Uri? endpoint = TelemetryEnvironment.ReadOtlpEndpoint();
        if (endpoint is null)
        {
            return null;
        }

        int interval = TelemetryEnvironment.ReadMetricExportInterval();
        return new(endpoint, instance, interval);
    }

    public void Dispose()
    {
        _metrics.Dispose();
        _traces.Dispose();
    }

    private static void Configure(OtlpExporterOptions options, Uri endpoint, string signalPath)
    {
        Uri endpointBase = endpoint.AbsoluteUri.EndsWith('/') ? endpoint : new(endpoint.AbsoluteUri + "/");
        options.Endpoint = new(endpointBase, signalPath);
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
    }
}
