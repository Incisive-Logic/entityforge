using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using EntityForge.Db.Query.Planning;
using EntityForge.Query.Projection;
using EntityForge.Query.Results;
using EntityForge.Query.Values;
using EntityForge.Semantics;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Client;

/// <summary>Returns a snapshot-pinned neutral entity projection and optional continuation.</summary>
/// <param name="Document">Neutral projection document returned by the query.</param>
/// <param name="SnapshotVersion">Exact snapshot used for this result.</param>
/// <param name="NextContinuation">Opaque next-page token, or null when no continuation is supplied.</param>
public sealed record EntityProjectionQueryResult(
    EntityProjection Document,
    DatabaseVersion SnapshotVersion,
    string? NextContinuation = null);

/// <summary>Queries weakly typed entities identically through embedded or remote transports.</summary>
public interface IEntityProjectionClient
{
    /// <summary>Projects matching entities and requested relationships at one database snapshot.</summary>
    /// <param name="request">The root type, identity constraints, selections and optional snapshot-bound continuation.</param>
    /// <param name="cancellationToken">Cancels query execution or the transport request.</param>
    /// <returns>The result document and the snapshot and continuation needed to request another page.</returns>
    ValueTask<EntityProjectionQueryResult> QueryAsync(
        EntityProjectionQueryRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Reports a stable EntityForgeDB error returned by a remote host.</summary>
public sealed class EntityForgeRemoteException(
    HttpStatusCode statusCode, string code, string message, bool isTransient)
    : EntityForgeDbException(message)
{
    /// <summary>The HTTP status returned by the remote host.</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>The host's machine-readable problem code, or a fallback when no structured code was returned.</summary>
    public string Code { get; } = code;

    /// <summary>Indicates that the failure condition may be temporary; it does not by itself make a write safe to replay.</summary>
    public bool IsTransient { get; } = isTransient;
}

internal static class EntityForgeRemoteErrors
{
    public static async ValueTask ThrowIfFailureAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string code = "remote_failure";
        string message = $"EntityForgeDB returned HTTP {(int)response.StatusCode}.";
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("code", out JsonElement coded) &&
                coded.ValueKind is JsonValueKind.String)
            {
                code = coded.GetString() ?? code;
            }
            else if (document.RootElement.TryGetProperty("title", out JsonElement title) &&
                title.ValueKind is JsonValueKind.String)
            {
                code = title.GetString() ?? code;
            }

            if (document.RootElement.TryGetProperty("detail", out JsonElement detail) &&
                detail.ValueKind is JsonValueKind.String)
            {
                message = detail.GetString() ?? message;
            }
        }
        catch (JsonException)
        {

        }
        bool isTransient = response.StatusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
        throw new EntityForgeRemoteException(response.StatusCode, code, message, isTransient);
    }
}

/// <summary>Applies common safety and shape invariants before any profile executes an entity query.</summary>
public static class EntityProjectionQueryPolicy
{
    /// <summary>Maximum explicitly requested root count for one projection page.</summary>
    public const int MaximumRoots = 10_000;

    /// <summary>Checks page bounds, continuation prerequisites and the uniqueness and depth of projected selections.</summary>
    /// <param name="request">The projection request to validate without executing it.</param>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="ArgumentException">Required identifiers are blank, selections conflict, directions are unsupported, or relationships exceed four hops.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page size or requested snapshot is outside the supported range.</exception>
    public static void Validate(EntityProjectionQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Type);
        if (request.First is <= 0 or > MaximumRoots)
        {
            throw new ArgumentOutOfRangeException(nameof(request.First));
        }

        if (request.After is not null && request.First is null)
        {
            throw new ArgumentException("A continuation requires a positive page size.", nameof(request.After));
        }

        if (request.After is not null && request.SnapshotVersion is null)
        {
            throw new ArgumentException("A continuation requires its result snapshot version.", nameof(request.After));
        }

        if (request.SnapshotVersion is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.SnapshotVersion));
        }

        ValidateShape(request.Fields ?? [], request.Relationships ?? [], 0);
    }

    private static void ValidateShape(IReadOnlyList<EntityProjectionFieldSelection> fields,
        IReadOnlyList<EntityProjectionRelationshipSelection> relationships, int depth)
    {
        string[] names = fields.Select(item => item.Name).Concat(relationships.Select(item => item.Name)).ToArray();
        if (names.Any(String.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
        {
            throw new ArgumentException("Entity field and relationship names must be non-empty and unique.");
        }

        foreach (EntityProjectionFieldSelection field in fields)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(field.Source);
        }

        foreach (EntityProjectionRelationshipSelection relationship in relationships)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relationship.Source);
            ArgumentException.ThrowIfNullOrWhiteSpace(relationship.TargetType);
            if (relationship.Direction is not ("outgoing" or "incoming"))
            {
                throw new ArgumentException("Entity relationship direction must be 'outgoing' or 'incoming'.");
            }

            if (depth >= 4)
            {
                throw new ArgumentException("Entity relationships are bounded to four hops.");
            }

            ValidateShape(relationship.Fields ?? [], relationship.Relationships ?? [], depth + 1);
        }
    }
}

/// <summary>Binds a profile-neutral continuation position to its query shape and retained snapshot.</summary>
public static class EntityProjectionContinuationPolicy
{
    /// <summary>Checks that a continuation belongs to this query shape and snapshot, then extracts its provider position.</summary>
    /// <param name="request">The unchanged query carrying the continuation to decode.</param>
    /// <param name="snapshotVersion">The retained snapshot at which the query will execute.</param>
    /// <returns>The provider position, or null when the request has no continuation.</returns>
    /// <exception cref="EntityQueryContinuationException">The token is invalid or identifies another query or snapshot.</exception>
    public static string? DecodePosition(EntityProjectionQueryRequest request, long snapshotVersion)
    {
        if (request.After is null)
        {
            return null;
        }

        EntityQueryContinuation continuation = EntityQueryContinuationCodec.Decode(request.After);
        if (continuation.Snapshot != snapshotVersion)
        {
            throw new EntityQueryContinuationException(EntityQueryContinuationFailure.SnapshotMismatch,
                "The continuation token belongs to a different snapshot.");
        }

        if (!StringComparer.Ordinal.Equals(continuation.QueryFingerprint, Fingerprint(request)))
        {
            throw new EntityQueryContinuationException(EntityQueryContinuationFailure.QueryMismatch,
                "The continuation token belongs to a different entity query.");
        }

        return continuation.Position;
    }

    /// <summary>Binds a provider continuation position to the query fingerprint and retained snapshot.</summary>
    /// <param name="request">The query whose shape must remain unchanged during paging.</param>
    /// <param name="snapshotVersion">The snapshot to retain for the next page.</param>
    /// <param name="position">The provider's next position, or null when paging is complete.</param>
    /// <returns>An opaque continuation token, or null when there is no next page.</returns>
    public static string? Encode(EntityProjectionQueryRequest request, long snapshotVersion, string? position) =>
        position is null ? null : EntityQueryContinuationCodec.Encode(new(
            snapshotVersion, Fingerprint(request), position));

    private static string Fingerprint(EntityProjectionQueryRequest request)
    {
        EntityProjectionQueryRequest stable = request with
        {
            SnapshotVersion = null,
            After = null
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(stable))));
    }
}

/// <summary>Converts between the in-process entity model and its versioned transport representation.</summary>
public static class EntityProjectionTransport
{
    /// <summary>Converts an entity projection, semantic values and evidence into the HTTP transport representation.</summary>
    /// <param name="result">The in-process result to encode.</param>
    /// <returns>The transport document, including its snapshot and continuation.</returns>
    public static EntityProjectionTransportResponse Encode(EntityProjectionQueryResult result) => new(
        result.Document.SourceQueryId, result.SnapshotVersion.Value, result.Document.Roots.Select(EncodeNode).ToArray(),
        result.Document.Evidence.Select(item => new EntityProjectionEvidenceTransport(
            item.Reference.Identifier.AbsoluteUri, item.ProviderIdentifier.AbsoluteUri,
            item.SourceGraph?.AbsoluteUri, item.SourceRecordIdentifier, item.SnapshotIdentifier,
            item.ObservedAt, item.ValidFrom, item.ValidTo)).ToArray(),
        result.NextContinuation);

    /// <summary>Reconstructs the neutral entity projection from a hosted transport response.</summary>
    /// <param name="response">The result document received from the host.</param>
    /// <returns>The decoded roots, evidence and snapshot-bound paging information.</returns>
    /// <exception cref="ArgumentNullException">The response is null.</exception>
    public static EntityProjectionQueryResult Decode(EntityProjectionTransportResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        EvidenceRecord[] evidence = response.Evidence.Select(item => new EvidenceRecord(
            new EvidenceReference(new Uri(item.Identifier)), new Uri(item.Provider),
            sourceGraph: item.SourceGraph is null ? null : new Uri(item.SourceGraph),
            sourceRecordIdentifier: item.SourceRecord, snapshotIdentifier: item.Snapshot,
            observedAt: item.ObservedAt,
            validFrom: item.ValidFrom, validTo: item.ValidTo)).ToArray();
        return new(new EntityProjection(response.QueryId, response.Entities.Select(DecodeNode), evidence),
            new DatabaseVersion(response.SnapshotVersion), response.NextContinuation);
    }

    private static EntityProjectionNodeTransport EncodeNode(EntityProjectionNode node) => new(
        Identity(node.Identity), node.Identity is BlankNodeValue, Scope(node.Identity),
        node.Types.Select(item => item.Identifier.AbsoluteUri).ToArray(),
        node.Fields.ToDictionary(item => item.Name,
            item => (IReadOnlyList<EntityProjectionValueTransport>)item.Values.Select(EncodeValue).ToArray()),
        node.Relationships.ToDictionary(item => item.Name,
            item => (IReadOnlyList<EntityProjectionItemTransport>)item.Items.Select(EncodeItem).ToArray()));

    private static EntityProjectionNode DecodeNode(EntityProjectionNodeTransport node) => new(
        Identity(node.Identity, node.BlankIdentity, node.IdentityScope),
        node.Types.Select(item => new SemanticTerm(new Uri(item), SemanticTermRole.Concept)),
        node.Fields.Select(item => new EntityProjectionField(item.Key, item.Value.Select(DecodeValue))),
        node.Relationships.Select(item => new EntityProjectionRelationship(item.Key, item.Value.Select(DecodeItem))));

    private static EntityProjectionValueTransport EncodeValue(EntityProjectionValue value) => value.Value switch
    {
        LiteralValue literal => new("literal", literal.LexicalValue, literal.ScalarKind.ToString(),
            literal.DatatypeIdentifier?.AbsoluteUri, literal.Language, value.SourceTerm.Identifier.AbsoluteUri,
            value.ProjectedTerm.Identifier.AbsoluteUri,
            value.Evidence.Select(item => item.Identifier.AbsoluteUri).ToArray()),
        IdentifierValue identifier => new("identifier", identifier.Identifier, null, null, null,
            value.SourceTerm.Identifier.AbsoluteUri, value.ProjectedTerm.Identifier.AbsoluteUri,
            value.Evidence.Select(item => item.Identifier.AbsoluteUri).ToArray()),
        JsonValue json => new("json", json.Value.Value, null, null, null,
            value.SourceTerm.Identifier.AbsoluteUri, value.ProjectedTerm.Identifier.AbsoluteUri,
            value.Evidence.Select(item => item.Identifier.AbsoluteUri).ToArray()),
        _ => throw new NotSupportedException($"Entity value '{value.Value.GetType().Name}' has no transport representation.")
    };

    private static EntityProjectionValue DecodeValue(EntityProjectionValueTransport value)
    {
        QueryValue decoded = value.Kind switch
        {
            "literal" => new LiteralValue(value.LexicalValue,
                Enum.TryParse(value.ScalarKind, out QueryScalarKind kind) && Enum.IsDefined(kind) ? kind :
                    throw new InvalidDataException($"Entity transport scalar kind '{value.ScalarKind}' is unsupported."),
                value.Datatype is null ? null : new Uri(value.Datatype), value.Language),
            "identifier" => new IdentifierValue(value.LexicalValue),
            "json" => new JsonValue(value.LexicalValue),
            _ => throw new NotSupportedException($"Entity transport value kind '{value.Kind}' is unsupported.")
        };
        SemanticTerm source = new(new Uri(value.SourceTerm), SemanticTermRole.Property);
        return new(decoded, source, new(new Uri(value.ProjectedTerm), SemanticTermRole.Property),
            value.Evidence.Select(item => new EvidenceReference(new Uri(item))));
    }

    private static EntityProjectionItemTransport EncodeItem(EntityProjectionItem item) => item switch
    {
        EntityProjectionNode node => new(Identity(node.Identity), node.Identity is BlankNodeValue,
            Scope(node.Identity), EncodeNode(node)),
        EntityProjectionReference reference => new(Identity(reference.Identity), reference.Identity is BlankNodeValue,
            Scope(reference.Identity)),
        _ => throw new NotSupportedException($"Entity item '{item.GetType().Name}' has no transport representation.")
    };

    private static EntityProjectionItem DecodeItem(EntityProjectionItemTransport item) => item.Entity is null
        ? new EntityProjectionReference(Identity(item.Identity, item.BlankIdentity, item.IdentityScope))
        : DecodeNode(item.Entity);

    private static string Identity(GraphElementIdentity identity) => identity switch
    {
        IdentifierValue value => value.Identifier,
        BlankNodeValue value => value.Identifier,
        _ => throw new NotSupportedException($"Identity '{identity.GetType().Name}' has no transport representation.")
    };

    private static Guid? Scope(GraphElementIdentity identity) => identity is BlankNodeValue blank
        ? blank.ResultScopeId : null;

    private static GraphElementIdentity Identity(string value, bool blank, Guid? scope) => blank
        ? new BlankNodeValue(value, scope is { } id && id != Guid.Empty ? id :
            throw new InvalidDataException("An RDF blank-node transport identity requires a non-empty scope."))
        : new IdentifierValue(value);
}

/// <summary>Queries an EntityForgeDB host while returning the same neutral documents as embedded clients.</summary>
/// <param name="httpClient">Caller-owned client configured with the server base address and credentials.</param>
public sealed class HttpEntityProjectionClient(HttpClient httpClient) : IEntityProjectionClient
{
    /// <inheritdoc />
    /// <exception cref="EntityForgeRemoteException">The host rejects the query.</exception>
    public async ValueTask<EntityProjectionQueryResult> QueryAsync(EntityProjectionQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        EntityProjectionQueryPolicy.Validate(request);
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            "/v1/entities/query", request, cancellationToken).ConfigureAwait(false);
        await EntityForgeRemoteErrors.ThrowIfFailureAsync(response, cancellationToken).ConfigureAwait(false);
        EntityProjectionTransportResponse transport = await response.Content
            .ReadFromJsonAsync<EntityProjectionTransportResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("EntityForgeDB returned an empty entity response.");
        return EntityProjectionTransport.Decode(transport);
    }
}
