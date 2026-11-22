using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Client;

/// <summary>Reads filesystem capacity and requests bounded storage-performance probes from a host.</summary>
public interface IStorageProfileClient
{
    /// <summary>Reads current capacity information and the most recent probe, without running a new probe.</summary>
    /// <param name="cancellation">Cancels the request.</param>
    /// <returns>The current storage report; capacity fields may be unavailable.</returns>
    Task<StorageProfileReport> GetAsync(CancellationToken cancellation = default);

    /// <summary>Requests an administrator-authorized probe of the server's database storage directory.</summary>
    /// <param name="cancellation">Cancels waiting for the probe; an in-progress operating-system flush may finish first.</param>
    /// <returns>The storage report after the probe completes.</returns>
    /// <remarks>The host enforces a cooldown and free-space requirement. Probes create temporary I/O load and are not a durability guarantee.</remarks>
    Task<StorageProfileReport> ProbeAsync(CancellationToken cancellation = default);
}

/// <summary>Uses the hosted storage-profile endpoints through a caller-owned HTTP client.</summary>
/// <param name="client">HTTP client configured with the host base address and bearer credentials.</param>
public sealed class HttpStorageProfileClient(HttpClient client) : IStorageProfileClient
{
    /// <inheritdoc />
    /// <exception cref="EntityForgeRemoteException">The host rejects access to storage diagnostics.</exception>
    /// <exception cref="InvalidDataException">The response contains no storage report.</exception>
    public Task<StorageProfileReport> GetAsync(CancellationToken cancellation = default) => SendAsync(false, cancellation);

    /// <inheritdoc />
    /// <exception cref="EntityForgeRemoteException">The host rejects the probe, including authorization or cooldown failures.</exception>
    /// <exception cref="InvalidDataException">The response contains no storage report.</exception>
    public Task<StorageProfileReport> ProbeAsync(CancellationToken cancellation = default) => SendAsync(true, cancellation);

    private async Task<StorageProfileReport> SendAsync(bool probe, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(probe ? HttpMethod.Post : HttpMethod.Get,
            probe ? "v1/storage-profile/probe" : "v1/storage-profile");
        using var response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
        await EntityForgeRemoteErrors.ThrowIfFailureAsync(response, cancellation).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync(StorageProfileJsonContext.Default.StorageProfileReport,
            cancellation).ConfigureAwait(false) ?? throw new InvalidDataException("The server returned no storage profile.");
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(StorageProfileReport))]
internal sealed partial class StorageProfileJsonContext : JsonSerializerContext;
