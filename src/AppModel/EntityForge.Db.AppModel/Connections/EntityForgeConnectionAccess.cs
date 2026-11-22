using System.Net;
using System.Text.Json;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.AppModel.Connections;

public sealed partial class EntityForgeConnectionSession
{
    private async Task<(AccessObservation Observation, HttpStatusCode? Status)> ReadAccessDiscoveryAsync(
        HttpClient client, bool supported, Guid? incarnation, CancellationToken cancellationToken)
    {
        EntityForgeAccessReport? report = null;
        string? error = null;
        HttpStatusCode? status = null;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        request.CancelAfter(DiscoveryTimeout);
        try
        {
            if (!supported)
            {
                error = "This host does not advertise effective-access discovery. Permissions are unknown; the server checks each request.";
            }
            else
            {
                using var response = await client.GetAsync("v1/security/access",
                    HttpCompletionOption.ResponseHeadersRead, request.Token).ConfigureAwait(false);
                status = response.StatusCode;
                error = InspectionError(response.StatusCode);
                if (error is null)
                {
                    using var json = await ReadInspection(response, request.Token).ConfigureAwait(false);
                    report = json.RootElement.Deserialize(InspectionJsonContext.Default.EntityForgeAccessReport)
                        ?? throw new InvalidDataException("Empty access report.");
                    if (report.Version != 1 || report.DatabaseIncarnation == Guid.Empty ||
                        incarnation is { } expected && expected != report.DatabaseIncarnation)
                    {
                        throw new InvalidDataException("Access report does not match this database.");
                    }
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or
            JsonException or InvalidDataException or ObjectDisposedException)
        {
            report = null;
            error = exception is OperationCanceledException
                ? "Access check stopped or timed out. Permissions are unknown."
                : "Could not retrieve a valid access report. Permissions are unknown.";
        }
        return (new(report, DateTimeOffset.UtcNow, error), status);
    }
}
