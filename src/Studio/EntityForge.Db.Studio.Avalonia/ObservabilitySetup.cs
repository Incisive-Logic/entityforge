using Avalonia.Controls;
using Avalonia.Media;

namespace EntityForge.Db.Studio.Avalonia;

internal static class ObservabilitySetup
{
    public static Control Profiler() => Guide(
        "Capture queries for investigation",
        "The profiler reads captured traces from Tempo through your database server. It does not record queries locally. Ask your server administrator to enable tracing, then run a query and search again after export completes.",
        "Configure OTEL_EXPORTER_OTLP_ENDPOINT on the database server to send traces to an OpenTelemetry Collector that forwards to Tempo. Set ENTITYFORGE_TEMPO_URL to the Tempo read endpoint. Restart the server after changing its environment.\n\nSampling, export delay and retention affect which queries appear; an empty search does not prove that no queries ran.");

    public static Control History() => Guide(
        "Retain metrics over time",
        "Live metrics work without a history service. Historical charts require Prometheus and an OpenTelemetry Collector configured by your server administrator.",
        "Configure OTEL_EXPORTER_OTLP_ENDPOINT on the database server and have Prometheus scrape the Collector's metrics endpoint. Set ENTITYFORGE_PROMETHEUS_URL to the Prometheus read endpoint. Restart the server after changing its environment, then allow enough scrapes for rate calculations.");

    private static Control Guide(string title, string summary, string setup) => InspectionView.Card(
        new StackPanel
        {
            Spacing = 10,
            Children =
        {
            InspectionView.Text(title, "ef-section-title"),
            InspectionView.Text(summary),
            new Expander
            {
                Header = "Server setup", Content = new SelectableTextBlock
            {
                Text = setup + "\n\nSee docs/user/studio.md in the source repository for the Studio observability guide.",
                TextWrapping = TextWrapping.Wrap
            }
            }
        }
        });
}
