using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Client;

/// <summary>Executes the shared portable Semantic entity-query transport contract.</summary>
public interface IPortableEntityQueryClient
{
    /// <summary>Executes a portable Semantic query and returns its entity projection at the server-selected snapshot.</summary>
    /// <param name="request">The query document, parameters and optional snapshot/paging constraints.</param>
    /// <param name="cancellationToken">Cancels the request and response decoding.</param>
    /// <returns>The projected entities, evidence, snapshot version and optional continuation.</returns>
    /// <remarks>Continue paging using the returned snapshot and continuation together; do not change the query between pages.</remarks>
    ValueTask<EntityProjectionQueryResult> ExecuteAsync(
        PortableEntityQueryRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Executes portable Semantic entity queries through the hosted HTTP surface.</summary>
/// <param name="client">Caller-owned HTTP client with the server base address and required authentication configured.</param>
public sealed class HttpPortableEntityQueryClient(HttpClient client) : IPortableEntityQueryClient
{
    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="EntityForgeRemoteException">The server rejects the query.</exception>
    /// <exception cref="InvalidDataException">The server returns no entity result.</exception>
    public async ValueTask<EntityProjectionQueryResult> ExecuteAsync(
        PortableEntityQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "v1/semantic/entities/query", request, PortableEntityQueryClientJsonContext.Default.PortableEntityQueryRequest,
            cancellationToken).ConfigureAwait(false);
        await EntityForgeRemoteErrors.ThrowIfFailureAsync(response, cancellationToken).ConfigureAwait(false);
        EntityProjectionTransportResponse transport = await response.Content.ReadFromJsonAsync(
            PortableEntityQueryClientJsonContext.Default.EntityProjectionTransportResponse, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("EntityForgeDB returned no entity result.");
        return EntityProjectionTransport.Decode(transport);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PortableEntityQueryRequest))]
[JsonSerializable(typeof(EntityProjectionTransportResponse))]
internal sealed partial class PortableEntityQueryClientJsonContext : JsonSerializerContext;
