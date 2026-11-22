using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using EntityForge.Semantics;
using EntityForge.Query.Projection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Hosting;

internal static partial class EntityReadScopeEndpoints
{
    private const int MaximumMappingTypes = 64;
    private const int MaximumRequestBytes = 1024 * 1024;
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = SemanticEntityReadDecoder.WireOptions;

    internal sealed record OpenRequest(EntityMappingManifest[] Manifests, int DurationMilliseconds,
        Uri? SourceSystem = null, Uri? SourcePartition = null);

    internal sealed record ProjectionField(SemanticTerm Property, string Name, EntityFieldValueSelection Selection, Uri[]? PreferredProviders);

    internal sealed record ProjectionRead(SemanticEntityIdentity Identity, ProjectionField[] Fields)
    {
        internal SemanticEntityProjectionQuery ToQuery() => new(Identity,
            new EntityProjectionDefinition(Identity.Concept, Fields.Select(f => new EntityFieldProjection(f.Property, f.Name, f.Selection, f.PreferredProviders))));
    }

    internal sealed record ReadRequest(string Kind, JsonElement Query);

    internal sealed record ScopeResponse(Guid ScopeId, Guid DatabaseIncarnation, string Profile,
        long SnapshotVersion, JsonElement? ContributionGuard, JsonElement? Value);

    internal static void Map(IEndpointRouteBuilder routes)
    {
        MapComplete(routes);
        routes.MapPost("/v1/application/read-scopes", (HttpContext context, Guid databaseIncarnation,
            string expectedProfile, HostedEntityApplications runtime) => Run(context, runtime, databaseIncarnation, expectedProfile, async () =>
        {
            var request = await Decode<OpenRequest>(context);
            if (request.Manifests is not { Length: > 0 and <= MaximumMappingTypes } || request.Manifests.Any(m => m is null || m.Profile != runtime.Profile))
            {
                throw new ArgumentException("A profile mapping closure is required.");
            }

            var duration = TimeSpan.FromMilliseconds(request.DurationMilliseconds);
            var id = await runtime.ReadScopes.OpenAsync(Owner(context), duration, async token => runtime.Profile switch
            {
                DatabaseProfileId.PropertyGraph when request.Manifests.Length == 1 => await runtime.Graph.OpenReadAsync(request.Manifests[0], token),
                DatabaseProfileId.Rdf when request.Manifests.Length == 1 => await runtime.Rdf.OpenReadAsync(request.Manifests[0], token),
                DatabaseProfileId.Semantic when request.SourceSystem is not null && request.SourcePartition is not null =>
                    await runtime.Semantic.OpenReadAsync(request.Manifests, request.SourceSystem, request.SourcePartition, duration, token),
                _ => throw new ArgumentException("Invalid profile scope description.")
            }, context.RequestAborted);
            return await runtime.ReadScopes.UseAsync(id, Owner(context), async (scope, token) =>
            {
                await Validate(scope, token);
                return await Encode(Describe(scope, id, databaseIncarnation, expectedProfile, null), token);
            }, context.RequestAborted);
        }));
        routes.MapPost("/v1/application/read-scopes/{scopeId:guid}/read", (HttpContext context, Guid scopeId,
            Guid databaseIncarnation, string expectedProfile, HostedEntityApplications runtime) => Run(context, runtime, databaseIncarnation, expectedProfile, async () =>
        {
            var request = await Decode<ReadRequest>(context);
            return await runtime.ReadScopes.UseAsync(scopeId, Owner(context), async (scope, token) =>
            {
                object? value = (scope, request.Kind) switch
                {
                    (IGraphEntityReadScope graph, "vertex") => await graph.GetVertexAsync(new(request.Query.GetGuid()), token),
                    (IGraphEntityReadScope graph, "outgoing") => await graph.OutgoingAsync(new(request.Query.GetProperty("identity").GetGuid()), request.Query.GetProperty("label").GetString()!, token),
                    (IRdfEntityReadScope rdf, "subject") => await rdf.ReadDefaultSubjectAsync(new(request.Query.GetString()!), token),
                    (ISemanticEntityReadScope semantic, "project") => await ProjectCompleteFields(semantic, request.Query.Deserialize<ProjectionRead>(Json)!.ToQuery(), token),
                    (ISemanticEntityReadScope semantic, "traverse") => await semantic.TraverseAsync(request.Query.Deserialize<SemanticRelationshipTraversalQuery>(Json)!, token),
                    _ => throw new ArgumentException("Unsupported profile read.")
                };
                if (scope is not IEntityReadValidatesResults)
                {
                    await Validate(scope, token);
                }
                // Trusted providers already validate after the read. Other providers retain the
                // explicit gateway check. In both cases serialize while the view is retained.
                return await Encode(new
                {
                    scopeId,
                    databaseIncarnation,
                    profile = expectedProfile,
                    snapshotVersion = Version(scope).Value,
                    value
                }, token);
            }, context.RequestAborted);
        }));
        routes.MapPost("/v1/application/read-scopes/{scopeId:guid}/validate", (HttpContext context, Guid scopeId,
            Guid databaseIncarnation, string expectedProfile, HostedEntityApplications runtime) => Run(context, runtime, databaseIncarnation, expectedProfile, async () =>
                await runtime.ReadScopes.UseAsync(scopeId, Owner(context), async (scope, token) =>
                {
                    await Validate(scope, token);
                    return await Encode(Describe(scope, scopeId, databaseIncarnation, expectedProfile, null), token);
                }, context.RequestAborted)));
        routes.MapPost("/v1/application/read-scopes/{scopeId:guid}/release", (HttpContext context, Guid scopeId,
            Guid databaseIncarnation, string expectedProfile, HostedEntityApplications runtime) => Run(context, runtime, databaseIncarnation, expectedProfile, async () =>
        {
            // Release is independent of caller disconnect/cancellation and waits for in-flight work.
            await runtime.ReadScopes.ReleaseAsync(scopeId, Owner(context));
            return Results.NoContent();
        }));
    }

    private static string Owner(HttpContext context) => context.Items.TryGetValue(EntityForgeDbSecurityMiddleware.OwnerItem, out var owner)
        ? (string)owner! : "explicitly-unsecured-host";

    private static DatabaseVersion Version(IAsyncDisposable scope) => scope switch
    {
        IGraphEntityReadScope graph => graph.Version,
        IRdfEntityReadScope rdf => rdf.Version,
        ISemanticEntityReadScope semantic => semantic.Version,
        _ => throw new InvalidOperationException()
    };

    private static ValueTask Validate(IAsyncDisposable scope, CancellationToken token) => scope switch
    {
        IGraphEntityReadScope graph => graph.ValidateAsync(token),
        IRdfEntityReadScope rdf => rdf.ValidateAsync(token),
        ISemanticEntityReadScope semantic => semantic.ValidateAsync(token),
        _ => throw new InvalidOperationException()
    };

    private static ScopeResponse Describe(IAsyncDisposable scope, Guid id, Guid database, string profile, JsonElement? value) =>
        new(id, database, profile, Version(scope).Value, scope is ISemanticEntityReadScope semantic && semantic.ContributionGuard is { } guard
            ? JsonSerializer.SerializeToElement(guard, Json) : null, value);

    private static async Task<IResult> Run(HttpContext context, HostedEntityApplications runtime, Guid database, string profile, Func<Task<IResult>> action)
    {
        if (profile != runtime.Profile.ToString())
        {
            return Results.Conflict(new
            {
                code = "profile_mismatch"
            });
        }

        Guid actual = runtime.Profile switch
        {
            DatabaseProfileId.PropertyGraph => runtime.Graph.DatabaseIncarnation,
            DatabaseProfileId.Rdf => runtime.Rdf.DatabaseIncarnation,
            _ => runtime.Semantic.DatabaseIncarnation
        };
        if (database != actual)
        {
            return Results.Conflict(new
            {
                code = "database_incarnation_mismatch"
            });
        }

        try
        {
            return await action();
        }
        catch (EntityMappingMismatchException)
        {
            return Results.Conflict(new
            {
                code = "read_mapping_not_established"
            });
        }
        catch (ReadScopeCapacityException)
        {
            return Results.Json(new
            {
                code = "read_scope_capacity"
            }, statusCode: StatusCodes.Status429TooManyRequests);
        }
        catch (ReadScopeUnavailableException)
        {
            return Results.Json(new
            {
                code = "read_scope_unavailable"
            }, statusCode: StatusCodes.Status410Gone);
        }
        catch (Exception failure) when (failure is ArgumentException or JsonException or InvalidDataException)
        {
            return Results.BadRequest(new
            {
                code = "invalid_read_scope_request"
            });
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Keep provider details server-side while retaining the failed action for diagnosis.
            if (context.RequestServices.GetService(typeof(ILoggerFactory)) is ILoggerFactory logs)
            {
                logs.CreateLogger(typeof(EntityReadScopeEndpoints)).LogWarning(failure,
                    "Retained read action {Action} failed.", context.Request.Path);
            }

            return Results.Json(new
            {
                code = "read_scope_failed"
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<T> Decode<T>(HttpContext context)
    {
        if (context.Request.ContentLength > MaximumRequestBytes)
        {
            throw new InvalidDataException("Oversized scope request.");
        }

        using var buffer = new BoundedBuffer(MaximumRequestBytes);
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        return JsonSerializer.Deserialize<T>(buffer.ToArray(), Json) ?? throw new InvalidDataException("Missing scope request.");
    }

    private static async ValueTask<IResult> Encode(object value, CancellationToken token)
    {
        using var buffer = new BoundedBuffer(MaximumResponseBytes);
        await JsonSerializer.SerializeAsync(buffer, value, value.GetType(), Json, token);
        return Results.Bytes(buffer.ToArray(), "application/json");
    }

    private static async ValueTask<EntityProjectionProjectionResult> ProjectCompleteFields(
        ISemanticEntityReadScope scope, SemanticEntityProjectionQuery query, CancellationToken cancellation)
    {
        var result = await scope.ProjectAsync(query, cancellation).ConfigureAwait(false);
        return SemanticEntityReadDecoder.CompleteFields(result, query);
    }

    private sealed class BoundedBuffer(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        private void Check(int count)
        {
            if (Length + count > maximum)
            {
                throw new InvalidDataException("Read scope message exceeds its byte limit.");
            }
        }
    }
}
