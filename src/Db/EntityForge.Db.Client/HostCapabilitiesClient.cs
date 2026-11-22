using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Client;

/// <summary>A known or future database profile identifier.</summary>
/// <param name="Identifier">Profile wire identifier; unknown future identifiers are preserved.</param>
public readonly record struct EntityForgeDatabaseProfile(string Identifier)
{
    /// <summary>Identifies the semantics-integrated database profile.</summary>
    public static EntityForgeDatabaseProfile Semantic { get; } = new("semantic");

    /// <summary>Identifies the native property-graph profile.</summary>
    public static EntityForgeDatabaseProfile PropertyGraph { get; } = new("property-graph");

    /// <summary>Identifies the RDF dataset profile.</summary>
    public static EntityForgeDatabaseProfile Rdf { get; } = new("rdf");

    /// <summary>Returns the profile's wire identifier without changing its spelling.</summary>
    public override string ToString() => Identifier;
}

/// <summary>Describes the host-advertised maturity of an individual API surface.</summary>
public enum EntityForgeCapabilityMaturity
{
    /// <summary>The host advertises the surface as stable.</summary>
    Stable,

    /// <summary>The surface is available for preview evaluation.</summary>
    Preview,

    /// <summary>The surface is experimental and may change.</summary>
    Alpha
}

/// <summary>Describes one advertised surface, its supported features, limits and transport bindings.</summary>
/// <param name="Identifier">The ordinal surface identifier used for capability checks.</param>
/// <param name="Maturity">The host's declared maturity for this surface.</param>
/// <param name="Features">Feature identifiers supported by this surface.</param>
/// <param name="Limits">Named numerical bounds reported by the host.</param>
/// <param name="TransportBindings">Available transport bindings for the surface.</param>
public sealed record EntityForgeSurface(
    string Identifier,
    EntityForgeCapabilityMaturity Maturity,
    IReadOnlyList<string> Features,
    IReadOnlyDictionary<string, long> Limits,
    IReadOnlyList<string> TransportBindings);

/// <summary>Application-facing capabilities decoded from the versioned host document.</summary>
/// <param name="Product">Product name reported by the host.</param>
/// <param name="Version">Host software version, independent of transport contract versions.</param>
/// <param name="Profile">Data model selected for the database.</param>
/// <param name="Surfaces">Advertised API surfaces and their individual limits and maturity.</param>
/// <param name="HostKind">Optional host deployment classification.</param>
/// <param name="DatabaseIncarnation">Optional database incarnation used to distinguish replaced databases.</param>
public sealed record EntityForgeHostCapabilities(
    string Product,
    string Version,
    EntityForgeDatabaseProfile Profile,
    IReadOnlyList<EntityForgeSurface> Surfaces,
    string? HostKind = null,
    Guid? DatabaseIncarnation = null)
{
    /// <summary>Checks whether the host advertises a surface with the exact supplied identifier.</summary>
    /// <param name="surfaceIdentifier">The case-sensitive surface identifier.</param>
    /// <returns>True when an advertised surface has an ordinally equal identifier.</returns>
    public bool Supports(string surfaceIdentifier) =>
        Surfaces.Any(item => StringComparer.Ordinal.Equals(item.Identifier, surfaceIdentifier));
}

/// <summary>Discovers the connected host's versioned capability document.</summary>
public interface IEntityForgeHostClient
{
    /// <summary>Retrieves the host's current advertised capabilities.</summary>
    /// <param name="cancellationToken">Cancels the request or response deserialization.</param>
    /// <returns>The decoded profile, version and supported surfaces.</returns>
    /// <remarks>Discovery does not prove current readiness or authorise subsequent operations.</remarks>
    ValueTask<EntityForgeHostCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reads host capabilities over HTTP using a caller-owned client.</summary>
/// <param name="client">HTTP client configured with the database base address.</param>
public sealed class HttpEntityForgeHostClient(HttpClient client) : IEntityForgeHostClient
{
    /// <inheritdoc />
    /// <exception cref="InvalidDataException">The response has no capability document or an unknown maturity value.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async ValueTask<EntityForgeHostCapabilities> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await client.GetAsync("v1/capabilities", cancellationToken)
            .ConfigureAwait(false);
        await EntityForgeRemoteErrors.ThrowIfFailureAsync(response, cancellationToken).ConfigureAwait(false);
        EntityForgeHostCapabilitiesTransport transport = await response.Content.ReadFromJsonAsync(
            HostCapabilitiesClientJsonContext.Default.EntityForgeHostCapabilitiesTransport,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("EntityForgeDB returned no capability document.");
        return new(transport.Product, transport.Version, new(transport.Profile),
            (transport.Surfaces ?? []).Select(item => new EntityForgeSurface(item.Identifier,
                item.Maturity switch
                {
                    EntityForgeSurfaceMaturity.Stable => EntityForgeCapabilityMaturity.Stable,
                    EntityForgeSurfaceMaturity.Preview => EntityForgeCapabilityMaturity.Preview,
                    EntityForgeSurfaceMaturity.Alpha => EntityForgeCapabilityMaturity.Alpha,
                    _ => throw new InvalidDataException($"Unknown capability maturity '{item.Maturity}'."),
                }, item.Features ?? [], item.Limits ?? new Dictionary<string, long>(),
                item.TransportBindings ?? [])).ToArray(), transport.HostKind, transport.DatabaseIncarnation);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(EntityForgeHostCapabilitiesTransport))]
internal sealed partial class HostCapabilitiesClientJsonContext : JsonSerializerContext;
