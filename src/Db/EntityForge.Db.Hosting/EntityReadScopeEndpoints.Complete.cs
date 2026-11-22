using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using EntityForge.Db.Mapping;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EntityForge.Db.Hosting;

internal static partial class EntityReadScopeEndpoints
{
    private static readonly Meter CompleteReadMeter = new("EntityForge.Db.Hosting.Reads");
    private static readonly Histogram<double> CompleteReadPhase = CompleteReadMeter.CreateHistogram<double>("entityforge.db.hosting.read.phase", "ms");

    private static long StartReadPhase() => CompleteReadPhase.Enabled ? Stopwatch.GetTimestamp() : 0;

    private static void EndReadPhase(long start, string phase)
    {
        if (start != 0)
        {
            CompleteReadPhase.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            new KeyValuePair<string, object?>("entityforge.db.phase", phase));
        }
    }

    private sealed record CompleteRequest(OpenRequest Scope, EntityReadRecipe Recipe);

    private static void MapComplete(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/application/read-scopes/complete", (HttpContext context, Guid databaseIncarnation,
            string expectedProfile, HostedEntityApplications runtime) => Run(context, runtime, databaseIncarnation, expectedProfile, async () =>
        {
            long phase = StartReadPhase();
            var request = await Decode<CompleteRequest>(context);
            request.Recipe.Validate(runtime.Profile switch
            {
                DatabaseProfileId.PropertyGraph => (IEntityReadRecipePolicy)EntityForge.Db.Graph.GraphReadRecipePolicy.Instance,
                DatabaseProfileId.Rdf => EntityForge.Db.Rdf.RdfReadRecipePolicy.Instance,
                DatabaseProfileId.Semantic => EntityForge.Db.Semantic.SemanticReadRecipePolicy.Instance,
                _ => throw new ArgumentException("Unknown read profile.")
            });
            var open = request.Scope;
            if (open.Manifests is not { Length: > 0 and <= MaximumMappingTypes } || open.Manifests.Any(m => m is null || m.Profile != runtime.Profile))
            {
                throw new ArgumentException("A profile mapping closure is required.");
            }

            var duration = TimeSpan.FromMilliseconds(open.DurationMilliseconds);
            object provider = runtime.Profile switch
            {
                DatabaseProfileId.PropertyGraph => runtime.Graph,
                DatabaseProfileId.Rdf => runtime.Rdf,
                _ => runtime.Semantic
            };
            EndReadPhase(phase, "decode_validate");
            phase = StartReadPhase();
            var id = await runtime.ReadScopes.OpenAsync(Owner(context), duration, async token => provider is IEntityReadRecipeProvider targeted
                ? await targeted.OpenRecipeAsync(open.Manifests, request.Recipe, open.SourceSystem, open.SourcePartition, duration, token)
                : runtime.Profile switch
                {
                    DatabaseProfileId.PropertyGraph when open.Manifests.Length == 1 => await runtime.Graph.OpenReadAsync(open.Manifests[0], token),
                    DatabaseProfileId.Rdf when open.Manifests.Length == 1 => await runtime.Rdf.OpenReadAsync(open.Manifests[0], token),
                    DatabaseProfileId.Semantic when open.SourceSystem is not null && open.SourcePartition is not null =>
                        await runtime.Semantic.OpenReadAsync(open.Manifests, open.SourceSystem, open.SourcePartition, duration, token),
                    _ => throw new ArgumentException("Invalid complete-read profile.")
                }, context.RequestAborted);
            EndReadPhase(phase, "open");
            IResult result;
            try
            {
                phase = StartReadPhase();
                result = await runtime.ReadScopes.UseAsync(id, Owner(context), async (scope, token) =>
                {
                    async ValueTask<IResult> Read()
                    {
                        using var buffer = new BoundedBuffer(MaximumResponseBytes);
                        using var writer = new Utf8JsonWriter(buffer);
                        writer.WriteStartObject();
                        writer.WriteString("scopeId", request.Recipe.ReadId);
                        writer.WriteString("databaseIncarnation", databaseIncarnation);
                        writer.WriteString("profile", expectedProfile);
                        writer.WriteNumber("snapshotVersion", Version(scope).Value);
                        writer.WritePropertyName("contributionGuard");
                        JsonSerializer.Serialize(writer, request.Recipe.IncludeContributionGuard && scope is ISemanticEntityReadScope semantic ? semantic.ContributionGuard : null, Json);
                        writer.WriteBoolean("includesContributionGuard", runtime.Profile == DatabaseProfileId.Semantic && request.Recipe.IncludeContributionGuard);
                        writer.WritePropertyName("observations");
                        writer.WriteStartArray();
                        await ExecuteRecipe(scope, request.Recipe, writer, token);
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                        writer.Flush();
                        long validation = StartReadPhase();
                        await Validate(scope, token);
                        EndReadPhase(validation, "validate");
                        return Results.Bytes(buffer.ToArray(), "application/json");
                    }
                    return scope is IEntityReadMaterializationScope owned ? await owned.MaterializeAsync(Read) : await Read();
                }, context.RequestAborted);
                EndReadPhase(phase, "execute_encode_validate");
            }
            catch (Exception primary)
            {
                try
                {
                    await runtime.ReadScopes.ReleaseAsync(id, Owner(context));
                }
                catch (Exception cleanup)
                {
                    EntityFailureDiagnostics.Attach(primary, cleanup);
                }
                throw;
            }
            // A complete response never transfers a retained handle to the caller. Do not publish
            // success if validation, encoding or resource release failed.
            phase = StartReadPhase();
            await runtime.ReadScopes.ReleaseAsync(id, Owner(context));
            EndReadPhase(phase, "release");
            return result;
        }));

    private static async ValueTask ExecuteRecipe(object scope, EntityReadRecipe recipe, Utf8JsonWriter writer, CancellationToken token)
    {
        if (scope is IEntityReadRecipeScope prefetched)
        {
            await prefetched.PrefetchRecipeAsync(recipe, token);
        }

        var pending = new Queue<(int Shape, JsonElement Identity)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var observations = new HashSet<string>(StringComparer.Ordinal);
        int scheduled = 1;
        pending.Enqueue((0, recipe.Identity));
        void Add(string kind, JsonElement query, object? value)
        {
            string key = EntityReadRecipe.Key(kind, query);
            if (observations.Contains(key))
            {
                return;
            }

            if (observations.Count == EntityReadRecipe.MaximumObservations)
            {
                throw new InvalidOperationException("Complete read exceeds its observation bound.");
            }

            writer.WriteStartObject();
            writer.WriteString("kind", kind);
            writer.WritePropertyName("query");
            query.WriteTo(writer);
            writer.WritePropertyName("value");
            JsonSerializer.Serialize(writer, value, value?.GetType() ?? typeof(object), Json);
            writer.WriteEndObject();
            // Charge actual output (including framing), rather than a discarded second encoding.
            writer.Flush();
            observations.Add(key);
        }
        void Schedule(int shape, JsonElement identity)
        {
            if (shape < 0)
            {
                return;
            }

            if (++scheduled > EntityReadRecipe.MaximumObservations)
            {
                throw new InvalidOperationException("Complete read exceeds its expansion bound.");
            }

            pending.Enqueue((shape, identity));
        }

        while (pending.TryDequeue(out var next))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(EntityReadRecipe.Key(next.Shape.ToString(System.Globalization.CultureInfo.InvariantCulture), next.Identity)))
            {
                continue;
            }

            var shape = recipe.Shapes[next.Shape];
            if (scope is IGraphEntityReadScope graph)
            {
                var identity = new GraphVertexId(next.Identity.GetGuid());
                var vertex = await graph.GetVertexAsync(identity, token);
                Add("vertex", next.Identity, vertex);
                if (vertex is null || shape.Label is not null && !vertex.Labels.Contains(shape.Label))
                {
                    continue;
                }

                foreach (var link in shape.Links)
                {
                    var edges = await graph.OutgoingAsync(identity, link.Predicate, token);
                    Add("outgoing", EntityReadRecipe.Value(new
                    {
                        identity = identity.Value,
                        label = link.Predicate
                    }), edges);
                    foreach (var edge in edges)
                    {
                        Schedule(link.Target, EntityReadRecipe.Value(edge.To.Value));
                    }

                    if (link.Target >= 0 && graph is IGraphEntityBatchReadScope batched)
                    {
                        await batched.PrefetchVerticesAsync(edges.Select(edge => edge.To).Distinct().ToArray(), token);
                    }
                }
            }
            else if (scope is IRdfEntityReadScope rdf)
            {
                var subject = new RdfIri(next.Identity.GetString()!);
                Add("subject", next.Identity, await rdf.ReadDefaultSubjectAsync(subject, token));
            }
            else if (scope is ISemanticEntityReadScope semantic)
            {
                var identity = SemanticEntityReadDecoder.SemanticIdentity(next.Identity);
                var query = new ProjectionRead(identity, shape.Fields!.Value.Deserialize<ProjectionField[]>(Json)!);
                var projected = await ProjectCompleteFields(semantic, query.ToQuery(), token);
                Add("project", EntityReadRecipe.Value(query), projected);
                if (projected.Document.Roots.Count == 0 || shape.Links.Length == 0)
                {
                    continue;
                }

                var traversal = new SemanticRelationshipTraversalQuery(identity, SemanticRelationshipDirection.Outgoing);
                var edges = await semantic.TraverseAsync(traversal, token);
                Add("traverse", EntityReadRecipe.Value(traversal), edges);
                foreach (var link in shape.Links)
                {
                    foreach (var target in edges.Where(e => e.Relationship.Identifier.AbsoluteUri == link.Predicate).Select(e => e.Target).Distinct())
                    {
                        Schedule(link.Target, EntityReadRecipe.Value(target));
                    }
                }
            }
            else
            {
                throw new NotSupportedException("Unsupported complete-read provider.");
            }
        }
    }
}
