using System.Text.Json.Serialization;

namespace EntityForge.Db.Transport.Contracts;

/// <summary>Stable identifiers used to discover hosted protocol surfaces.</summary>
public static class EntityForgeSurfaceIds
{
    /// <summary>Profile-neutral entity projection queries.</summary>
    public const string EntityProjection = "entity-projection";

    /// <summary>Native property-graph operations.</summary>
    public const string GraphNative = "graph-native";

    /// <summary>Graph GQL query operations.</summary>
    public const string GraphGql = "graph-gql";

    /// <summary>Native RDF operations.</summary>
    public const string RdfNative = "rdf-native";

    /// <summary>RDF SPARQL query operations.</summary>
    public const string RdfSparql = "rdf-sparql";

    /// <summary>Advertised RDF SHACL validation operations.</summary>
    public const string RdfShacl = "rdf-shacl";

    /// <summary>Advertised RDF entailment operations.</summary>
    public const string RdfEntailment = "rdf-entailment";

    /// <summary>Native Semantic operations.</summary>
    public const string SemanticNative = "semantic-native";

    /// <summary>Read-only GQL queries over an asserted Semantic evidence view.</summary>
    public const string SemanticGql = "semantic-gql";

    /// <summary>Read-only SPARQL queries over an asserted Semantic evidence view.</summary>
    public const string SemanticSparql = "semantic-sparql";

    /// <summary>Semantic vocabulary and query-metadata discovery.</summary>
    public const string SemanticCatalog = "semantic-catalog";

    /// <summary>Retained database change-feed operations.</summary>
    public const string ChangeFeed = "change-feed";

    /// <summary>Retention of query snapshots through leases.</summary>
    public const string SnapshotLeases = "snapshot-leases";

    /// <summary>GraphQL projection operations.</summary>
    public const string GraphQlProjection = "graphql-projection";

    /// <summary>Database maintenance operations.</summary>
    public const string Administration = "administration";

    /// <summary>Inspection of authority for the current credential.</summary>
    public const string AccessDiscovery = "effective-access";

    /// <summary>Submission and resolution of application write operations.</summary>
    public const string ApplicationOperations = "application-operations";

    /// <summary>Application-owned retained read scopes.</summary>
    public const string ApplicationReadScopes = "application-read-scopes";
}

/// <summary>Reports the release maturity declared for an individual hosted surface.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EntityForgeSurfaceMaturity>))]
public enum EntityForgeSurfaceMaturity
{
    /// <summary>The host declares this surface stable.</summary>
    Stable,

    /// <summary>The surface is available for preview and may change.</summary>
    Preview,

    /// <summary>The surface is early-stage and may change substantially.</summary>
    Alpha
}

/// <summary>Advertises one available protocol surface and its feature and limit declarations.</summary>
/// <param name="Identifier">Stable surface identifier.</param>
/// <param name="Maturity">Maturity declared by this host.</param>
/// <param name="Features">Specific operations or features advertised within the surface.</param>
/// <param name="Limits">Named numeric bounds, when reported.</param>
/// <param name="TransportBindings">Supported transport bindings, when reported.</param>
public sealed record EntityForgeSurfaceCapability(
    string Identifier,
    EntityForgeSurfaceMaturity Maturity,
    IReadOnlyList<string> Features,
    IReadOnlyDictionary<string, long>? Limits = null,
    IReadOnlyList<string>? TransportBindings = null);

/// <summary>Describes the single database profile served by a host and its typed public surfaces.</summary>
/// <param name="Product">Host product name.</param>
/// <param name="Version">Host-reported version.</param>
/// <param name="Profile">The single database profile served by this host.</param>
/// <param name="Surfaces">Available protocol surfaces; absence is not a grant of support.</param>
/// <param name="HostKind">Optional host-composition discriminator.</param>
/// <param name="DatabaseIncarnation">Database identity when reported; clients must not invent one from the endpoint.</param>
public sealed record EntityForgeHostCapabilitiesTransport(
    string Product,
    string Version,
    string Profile,
    IReadOnlyList<EntityForgeSurfaceCapability> Surfaces,
    string? HostKind = null,
    Guid? DatabaseIncarnation = null);
