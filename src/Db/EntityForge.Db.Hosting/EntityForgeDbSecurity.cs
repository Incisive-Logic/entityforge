using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Hosting;

/// <summary>Classifies the authority required by one hosted HTTP request.</summary>
public enum EntityForgeDbAccessKind
{
    /// <summary>Discovery or health access that does not require a bearer credential.</summary>
    Public,

    /// <summary>Read access available to reader, writer and administrator credentials.</summary>
    Read,

    /// <summary>Mutation access available to writer and administrator credentials.</summary>
    Write,

    /// <summary>Maintenance access reserved for administrator credentials.</summary>
    Administration
}

/// <summary>Configures the bounded built-in bearer-token boundary for a standalone public-preview host.</summary>
public sealed class EntityForgeDbBearerSecurityOptions
{
    /// <summary>Default maximum number of requests executing inside the database host.</summary>
    public const int DefaultMaximumConcurrentRequests = 128;

    /// <summary>Minimum UTF-8 token length accepted by the built-in boundary.</summary>
    public const int MinimumTokenBytes = 32;

    internal byte[] AdministratorTokenHash
    {
        get;
    }

    internal byte[]? WriterTokenHash
    {
        get;
    }

    internal byte[]? ReaderTokenHash
    {
        get;
    }

    /// <summary>Creates a role-hierarchical token boundary. Administrators can write/read; writers can read.</summary>
    public EntityForgeDbBearerSecurityOptions(string administratorToken, string? writerToken = null,
        string? readerToken = null, bool requireHttps = true,
        int maximumConcurrentRequests = DefaultMaximumConcurrentRequests)
    {
        AdministratorTokenHash = Hash(administratorToken, nameof(administratorToken));
        WriterTokenHash = writerToken is null ? null : Hash(writerToken, nameof(writerToken));
        ReaderTokenHash = readerToken is null ? null : Hash(readerToken, nameof(readerToken));
        if ((WriterTokenHash is not null && CryptographicOperations.FixedTimeEquals(
                AdministratorTokenHash, WriterTokenHash)) ||
            (ReaderTokenHash is not null && (CryptographicOperations.FixedTimeEquals(
                AdministratorTokenHash, ReaderTokenHash) || WriterTokenHash is not null &&
                CryptographicOperations.FixedTimeEquals(WriterTokenHash, ReaderTokenHash))))
        {
            throw new ArgumentException("Bearer tokens assigned to different roles must be distinct.");
        }

        if (maximumConcurrentRequests <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentRequests));
        }

        RequireHttps = requireHttps;
        MaximumConcurrentRequests = maximumConcurrentRequests;
    }

    /// <summary>Requires transport security before accepting a protected request.</summary>
    public bool RequireHttps
    {
        get;
    }

    /// <summary>Maximum concurrently executing protected requests.</summary>
    public int MaximumConcurrentRequests
    {
        get;
    }

    private static byte[] Hash(string token, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        byte[] bytes = Encoding.UTF8.GetBytes(token);
        if (bytes.Length < MinimumTokenBytes)
        {
            throw new ArgumentException($"Bearer tokens require at least {MinimumTokenBytes} UTF-8 bytes.", parameter);
        }

        return SHA256.HashData(bytes);
    }
}

/// <summary>One secret-free security decision suitable for application-owned durable audit storage.</summary>
/// <param name="AtUtc">UTC time of the security observation.</param>
/// <param name="RequestId">Request correlation identifier.</param>
/// <param name="Method">HTTP method.</param>
/// <param name="Resource">Resource category recorded by authorization; does not include secret request payloads.</param>
/// <param name="RequiredAccess">Access level required by the request.</param>
/// <param name="Outcome">Authorization or security decision classification.</param>
/// <param name="StatusCode">HTTP status associated with the decision.</param>
public sealed record EntityForgeDbSecurityAuditEvent(DateTimeOffset AtUtc, string RequestId, string Method,
    string Resource, EntityForgeDbAccessKind RequiredAccess, string Outcome, int StatusCode);

/// <summary>Receives security decisions without request bodies, tokens, identities or resource identifiers.</summary>
public interface IEntityForgeDbSecurityAuditSink
{
    /// <summary>Records one bounded security decision.</summary>
    ValueTask WriteAsync(EntityForgeDbSecurityAuditEvent auditEvent,
        CancellationToken cancellationToken = default);
}

/// <summary>Writes bounded security decisions through the configured structured application logger.</summary>
public sealed class LoggingEntityForgeDbSecurityAuditSink(
    ILogger<LoggingEntityForgeDbSecurityAuditSink> logger) : IEntityForgeDbSecurityAuditSink
{
    /// <inheritdoc />
    public ValueTask WriteAsync(EntityForgeDbSecurityAuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation(new EventId(9300, "EntityForgeDbSecurityDecision"),
            "EntityForgeDB security {Outcome}: {Method} {Resource} required {RequiredAccess}, status {StatusCode}, request {RequestId}",
            auditEvent.Outcome, auditEvent.Method, auditEvent.Resource, auditEvent.RequiredAccess,
            auditEvent.StatusCode, auditEvent.RequestId);
        return ValueTask.CompletedTask;
    }
}

internal sealed class BearerEntityForgeDbAuthorizationPolicy : IEntityForgeDbAuthorizationPolicy
{
    public ValueTask<bool> AuthorizeAsync(HttpContext context, EntityForgeDbAdministrativeOperation operation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(context.Items.TryGetValue(EntityForgeDbSecurityMiddleware.AccessItem,
            out object? value) && value is EntityForgeDbAccessKind.Administration);
    }
}

internal sealed class EntityForgeDbSecurityMiddleware
{
    internal const string OwnerItem = "EntityForge.Db.Security.Owner";
    internal const string AccessItem = "EntityForge.Db.Security.Access";
    internal const string GraphQlWriteItem = "EntityForge.Db.Security.GraphQlWrite";
    internal const string GraphQlDeniedItem = "EntityForge.Db.Security.GraphQlDenied";
    private const string BearerPrefix = "Bearer ";
    private readonly RequestDelegate _next;
    private readonly EntityForgeDbBearerSecurityOptions _options;
    private readonly IEntityForgeDbSecurityAuditSink _audit;
    private readonly SemaphoreSlim _admission;

    public EntityForgeDbSecurityMiddleware(RequestDelegate next, EntityForgeDbBearerSecurityOptions options,
        IEntityForgeDbSecurityAuditSink audit)
    {
        _next = next;
        _options = options;
        _audit = audit;
        _admission = new(options.MaximumConcurrentRequests, options.MaximumConcurrentRequests);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        EntityForgeDbAccessKind required = Classify(context.Request);
        if (required is EntityForgeDbAccessKind.Public)
        {
            SecureHeaders(context.Response);
            await _next(context).ConfigureAwait(false);
            return;
        }
        if (_options.RequireHttps && !context.Request.IsHttps)
        {
            await RejectAsync(context, required, StatusCodes.Status426UpgradeRequired,
                "transport_security_required").ConfigureAwait(false);
            return;
        }
        EntityForgeDbAccessKind? granted = Authenticate(context.Request);
        if (granted is null || !Permits(granted.Value, required))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await RejectAsync(context, required, StatusCodes.Status401Unauthorized,
                "authentication_required").ConfigureAwait(false);
            return;
        }
        if (!await _admission.WaitAsync(TimeSpan.Zero, context.RequestAborted).ConfigureAwait(false))
        {
            await RejectAsync(context, required, StatusCodes.Status429TooManyRequests,
                "request_concurrency_limit").ConfigureAwait(false);
            return;
        }
        try
        {
            context.Items[AccessItem] = granted.Value;
            context.Items[OwnerItem] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()[BearerPrefix.Length..])));
            SecureHeaders(context.Response);
            try
            {
                await _next(context).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                bool aborted = failure is OperationCanceledException && context.RequestAborted.IsCancellationRequested;
                try
                {
                    await AuditAsync(context, context.Items.ContainsKey(GraphQlWriteItem) ? EntityForgeDbAccessKind.Write : required,
                        aborted ? "aborted" : "failed", aborted ? 499 : StatusCodes.Status500InternalServerError).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    /* Preserve the endpoint failure if the audit sink also fails. */
                }
                throw;
            }
            await AuditAsync(context, context.Items.ContainsKey(GraphQlWriteItem) ? EntityForgeDbAccessKind.Write : required,
                context.Items.ContainsKey(GraphQlDeniedItem) ? "denied" : "allowed", context.Response.StatusCode).ConfigureAwait(false);
        }
        finally
        {
            _admission.Release();
        }
    }

    private EntityForgeDbAccessKind? Authenticate(HttpRequest request)
    {
        string authorization = request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string token = authorization[BearerPrefix.Length..];
        if (String.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        if (CryptographicOperations.FixedTimeEquals(hash, _options.AdministratorTokenHash))
        {
            return EntityForgeDbAccessKind.Administration;
        }

        if (_options.WriterTokenHash is not null &&
            CryptographicOperations.FixedTimeEquals(hash, _options.WriterTokenHash))
        {
            return EntityForgeDbAccessKind.Write;
        }

        if (_options.ReaderTokenHash is not null &&
            CryptographicOperations.FixedTimeEquals(hash, _options.ReaderTokenHash))
        {
            return EntityForgeDbAccessKind.Read;
        }

        return null;
    }

    private async Task RejectAsync(HttpContext context, EntityForgeDbAccessKind required, int status, string code)
    {
        SecureHeaders(context.Response);
        await AuditAsync(context, required, "denied", status).ConfigureAwait(false);
        await Results.Problem(statusCode: status, title: code,
            extensions: new Dictionary<string, object?> { ["code"] = code })
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private ValueTask AuditAsync(HttpContext context, EntityForgeDbAccessKind required, string outcome, int status) =>
        _audit.WriteAsync(new(DateTimeOffset.UtcNow, context.TraceIdentifier, context.Request.Method,
            Resource(context.Request.Path), required, outcome, status), CancellationToken.None);

    private static bool Permits(EntityForgeDbAccessKind granted, EntityForgeDbAccessKind required) =>
        granted >= required;

    private static EntityForgeDbAccessKind Classify(HttpRequest request)
    {
        // Endpoint routing accepts trailing slashes. Apply the same equivalence
        // before exact-path checks so a mutation cannot fall through to a read prefix.
        PathString path = new(request.Path.Value?.TrimEnd('/'));
        if (path.StartsWithSegments("/health") || path == "/v1/capabilities" || path == "/openapi/v1.json")
        {
            return EntityForgeDbAccessKind.Public;
        }

        if (path.StartsWithSegments("/v1/storage-profile") || path == "/v1/status" || path == "/v1/telemetry" || path.StartsWithSegments("/v1/observability") || path.StartsWithSegments("/v1/checkpoints") ||
            path.StartsWithSegments("/v1/backups") || path.StartsWithSegments("/v1/archives") || path.StartsWithSegments("/v1/administration") ||
            path.StartsWithSegments("/v1/semantic/property-storage/optimization"))
        {
            return EntityForgeDbAccessKind.Administration;
        }

        if (path == "/sparql/update" && (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)))
        {
            return EntityForgeDbAccessKind.Read;
        }
        if (path.StartsWithSegments("/sparql/graphs"))
        {
            return HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
                ? EntityForgeDbAccessKind.Read : EntityForgeDbAccessKind.Write;
        }
        if (path == "/v1/rdf/sparql/update" || path == "/sparql/update" || path == "/v1/semantic/vocabularies" ||
            (path.StartsWithSegments("/v1/semantic/contribution-revisions") && !HttpMethods.IsGet(request.Method)) ||
            path.StartsWithSegments("/v1/semantic/sessions") ||
            path == "/v1/graph/gql/update")
        {
            return EntityForgeDbAccessKind.Write;
        }

        if (path.StartsWithSegments("/v1/application/read-scopes"))
        {
            return EntityForgeDbAccessKind.Read;
        }

        if (HttpMethods.IsGet(request.Method) || path.StartsWithSegments("/v1/semantic") ||
            path.StartsWithSegments("/v1/entities/query") || path == "/graphql" || path == "/sparql" ||
            path.StartsWithSegments("/v1/graph/gql/query") || path == "/v1/graph/gql/explain" ||
            path.StartsWithSegments("/v1/graph/gql/capabilities") || path == "/v1/rdf/quads" ||
            path == "/v1/rdf/quads/stream" || path.StartsWithSegments("/v1/rdf/quads/continuations") || path.StartsWithSegments("/v1/rdf/sparql") ||
            path.StartsWithSegments("/v1/rdf/shacl"))
        {
            return EntityForgeDbAccessKind.Read;
        }

        return EntityForgeDbAccessKind.Write;
    }

    private static string Resource(PathString path)
    {
        path = new(path.Value?.TrimEnd('/'));
        if (path.StartsWithSegments("/v1/storage-profile"))
        {
            return "storage-profile";
        }

        if (path.StartsWithSegments("/v1/application/read-scopes"))
        {
            return "application-read-scope";
        }

        if (path.StartsWithSegments("/v1/application/operations"))
        {
            return "application-operation";
        }

        if (path.StartsWithSegments("/v1/graph"))
        {
            return "graph";
        }

        if (path.StartsWithSegments("/v1/rdf") || path.StartsWithSegments("/sparql"))
        {
            return "rdf";
        }

        if (path.StartsWithSegments("/v1/semantic/property-storage/optimization"))
        {
            return "property-optimization";
        }

        if (path.StartsWithSegments("/v1/semantic") || path.StartsWithSegments("/v1/entities/query") ||
            path == "/graphql")
        {
            return "query";
        }

        if (path.StartsWithSegments("/v1/semantic/sessions"))
        {
            return "semantic-session";
        }

        if (path.StartsWithSegments("/v1/semantic/snapshot-leases"))
        {
            return "snapshot-lease";
        }

        if (path.StartsWithSegments("/v1/semantic/changes"))
        {
            return "change-feed";
        }

        if (path.StartsWithSegments("/v1/archives"))
        {
            return "archive";
        }

        if (path.StartsWithSegments("/v1/backups"))
        {
            return "backup";
        }

        if (path.StartsWithSegments("/v1/checkpoints"))
        {
            return "checkpoint";
        }

        if (path.StartsWithSegments("/v1/administration"))
        {
            return "administration-job";
        }

        if (path.StartsWithSegments("/v1/status"))
        {
            return "status";
        }

        if (path == "/v1/telemetry")
        {
            return "telemetry";
        }

        return "other";
    }

    private static void SecureHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.Append("Referrer-Policy", "no-referrer");
        if (response.HttpContext.Request.IsHttps)
        {
            response.Headers.StrictTransportSecurity = "max-age=31536000";
        }
    }
}
