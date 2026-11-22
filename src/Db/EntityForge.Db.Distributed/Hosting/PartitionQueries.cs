using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using System.Text.Json;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Db.Query;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed record PartitionQueryRequest(Guid Generation, DatabaseProfileId Profile, string Language,
    string? Statement = null, PortableEntityQueryRequest? EntityQuery = null,
    DatabasePositionVector? MinimumPosition = null, PartitionGraphQlQuery? GraphQl = null,
    bool Stream = false, bool IncludeSupportingGraph = false);

internal sealed partial class PartitionApplicationClient
{
    private readonly PartitionQueryPages _queryPages = new();

    internal void CloseQueryPages() => _queryPages.Clear();

    internal async ValueTask<PartitionQueryResult> QueryAsync(PartitionQueryRequest request, CancellationToken token,
        Action<byte[]>? ownResponse = null)
    {
        token.ThrowIfCancellationRequested();
        if (request.Language == "gql" && request.Statement is { } statement && GqlProcedures is { } procedures)
        {
            request = request with
            {
                Statement = procedures.Expand(statement)
            };
        }
        if (request.GraphQl is { } graphql)
        {
            if (request.Profile != DatabaseProfileId.Semantic || request.Language != "graphql" ||
                request.Statement is not null || request.EntityQuery is not null ||
                graphql.Query is not { Length: > 0 and <= PartitionQueryLimits.MaximumStatementCharacters })
            {
                throw new NotSupportedException("GraphQL uses the bounded Semantic entity facade.");
            }

            var compiled = new GraphQlEntityQueryCompiler(graphql.Variables).Compile(graphql.Query, graphql.OperationName);
            var native = await QueryAsync(request with
            {
                Language = "entity",
                GraphQl = null,
                EntityQuery = compiled.Request
            }, token);
            var projected = native.NativeResult as PortableEntityQueryResponse ?? native.Value.Deserialize<PortableEntityQueryResponse>(PartitionParticipantWire.Json)
                ?? throw new InvalidDataException("Missing entity projection.");
            var value = GraphQlEntityQueryResponseMapper.Map(compiled,
                projected with
                {
                    NextContinuation = native.NextContinuation
                }, null, native.Position);
            var response = native.WithOutput(BoundedElement(value, request.Stream)) with
            {
                Language = "graphql"
            };
            CompletePublicResponse(response, request.Stream, ownResponse);
            return response;
        }
        if (request.EntityQuery is { } entity)
        {
            if (entity.First is { } first)
            {
                PartitionQueryPages.ValidateSize(first);
            }

            var canonical = request with
            {
                EntityQuery = entity with
                {
                    After = null
                }
            };
            if (entity.After is not null)
            {
                var resumed = _queryPages.Resume(canonical, entity.After, entity.First ?? throw new ArgumentException("Page size is required."));
                CompletePublicResponse(resumed, request.Stream, ownResponse);
                return resumed;
            }
            var result = await ExecuteQueryAsync(request with
            {
                EntityQuery = entity with
                {
                    First = null
                }
            }, token);
            var paged = entity.First is { } size ? _queryPages.Start(canonical, result, size) : result;
            CompletePublicResponse(paged, request.Stream, ownResponse);
            return paged;
        }
        return await ExecuteQueryAsync(request, token, ownResponse);
    }

    internal async ValueTask<PartitionQueryResult> ExecuteQueryAsync(PartitionQueryRequest request, CancellationToken token,
        Action<byte[]>? ownResponse = null)
    {
        if (request.Generation != Generation || request.Profile != Profile)
        {
            throw new InvalidDataException("Query destination differs.");
        }

        if (request.IncludeSupportingGraph && (Profile != DatabaseProfileId.PropertyGraph || request.Language != "gql"))
        {
            throw new ArgumentException("Supporting graph output requires GQL.");
        }

        CompiledGraphGqlQuery? graphQuery = null;
        CompiledPortableEntityQuery? semanticQuery = null;
        if (Profile == DatabaseProfileId.PropertyGraph && request.Language == "gql" && request.EntityQuery is null)
        {
            GraphGqlStandardSyntax.Validate(request.Statement ?? throw new ArgumentException("Missing GQL."));
            graphQuery = PartitionQueryCompilation.CompileGraph(request.Statement ?? throw new ArgumentException("Missing GQL.")).BindExecutionClock();
        }
        else if (Profile == DatabaseProfileId.Rdf && request.Language == "sparql" && request.EntityQuery is null)
        {
            if (request.Statement is not { Length: > 0 and <= PartitionQueryLimits.MaximumStatementCharacters })
            {
                throw new ArgumentException("A bounded SPARQL statement is required.");
            }

            _ = RdfSparql.QueryForm(request.Statement);
        }
        else if (Profile == DatabaseProfileId.Semantic && request.Language == "entity" && request.Statement is null && request.EntityQuery is { } entity)
        {
            if (entity.SnapshotVersion is not null || entity.SnapshotLeaseId is not null || entity.After is not null)
            {
                throw new NotSupportedException("Native scalar snapshot and continuation tokens are not distributed snapshot capabilities.");
            }

            semanticQuery = new PortableEntityQueryRequestCompiler().Compile(entity);
        }
        else
        {
            throw new NotSupportedException("Query language does not match the profile.");
        }

        if (IndexedServingProperties is not null && graphQuery is { ClockBound: false } && _routing.StorageLayout == DatabaseStorageLayout.Current)
        {
            return await ExecuteIndexedServingAsync(request, graphQuery, token, ownResponse);
        }

        var owners = _owners.Values.OrderBy(o => o.Domain.Partition.Value, StringComparer.Ordinal).ToArray();
        bool fragments = owners.Length > 1 && graphQuery is { RequiresCompleteGraph: false } && BoundedGraphPatternExecutor.SupportsFragments(graphQuery.Query);
        bool profileFragments = owners.Length > 1 && CompactProfileOperators &&
            PartitionProfileFragments.Supports(request.Language, request.Statement, request.EntityQuery);
        bool rdfFrontier = profileFragments && Profile == DatabaseProfileId.Rdf && RdfSparql.SupportsBindingFrontier(request.Statement!);
        bool semanticFrontier = owners.Length > 1 && CompactProfileOperators && !profileFragments &&
            Profile == DatabaseProfileId.Semantic && PartitionSemanticFrontier.Supports(request.EntityQuery);
        QueryAttempt? attempt = null;
        ValueTask<PartitionProjectionValue> Project(PartitionRemoteRead captured, CancellationToken cancellation)
        {
            attempt = new(this, request, graphQuery, semanticQuery, owners,
                fragments, profileFragments, rdfFrontier, semanticFrontier);
            return attempt.RunAsync(captured, cancellation);
        }
        (PartitionProjectionValue Value, DatabasePositionVector Position) result;
        while (true)
        {
            try
            {
                result = await ExecuteReadEpochAsync<PartitionProjectionValue>(Project, request.MinimumPosition, token);
                break;
            }
            catch (PartitionProfileFragmentFallbackException) when (profileFragments)
            {
                profileFragments = false; // The failed attempt has already drained its jobs and borrow.
                semanticFrontier = CompactProfileOperators && Profile == DatabaseProfileId.Semantic &&
                    PartitionSemanticFrontier.Supports(request.EntityQuery);
            }
        }
        var response = new PartitionQueryResult(Generation, Profile, request.Language, result.Position, result.Value,
            Statistics: attempt!.Statistics());
        using (DistributedReadTelemetry.Start("partition.query.result.response-budget"))
        {
            CompletePublicResponse(response, request.Stream, ownResponse);
        }

        return response;
    }

    private static void RequireFacts(int count)
    {
        if (count > PartitionQueryLimits.MaximumFacts)
        {
            throw new InvalidOperationException("Distributed query exceeds its fact budget.");
        }
    }

    private static PartitionProjectionValue WithoutNativeSnapshot(object value, bool stream = false)
    {
        using var shaping = DistributedReadTelemetry.Start("partition.query.result.strip-snapshot");
        // Keep the original pre-shaping bound, including the private scalar field.
        PartitionProjectionValue.Measure(value, OutputBound(stream));
        return PartitionPublicQueryProjection.WithoutNativeSnapshot(value);
    }

    private static void CompletePublicResponse(PartitionQueryResult response, bool stream,
        Action<byte[]>? owner)
    {
        if (owner is null)
        {
            PartitionProjectionValue.Measure(response, OutputBound(stream));
        }
        else
        {
            owner(new PartitionProjectionValue(response).Encode(OutputBound(stream)));
        }
    }

    private static int OutputBound(bool stream) => stream ? PartitionQueryLimits.MaximumStreamBytes : PartitionParticipantWire.MaximumResponseBytes;

    private static PartitionProjectionValue BoundedElement(object value, bool stream = false)
    {
        using var encoding = DistributedReadTelemetry.Start("partition.query.result.encode-parse");
        var projected = new PartitionProjectionValue(value);
        encoding?.SetTag("bytes", PartitionProjectionValue.Measure(projected, OutputBound(stream)));
        return projected;
    }

    private static SemanticCatalogSnapshot MergeCatalogs(IReadOnlyList<SemanticCatalogSnapshot> catalogs)
    {
        foreach (var group in catalogs.SelectMany(c => c.Terms).GroupBy(t => t.Identifier))
        {
            if (group.Distinct().Count() != 1)
            {
                throw new InvalidDataException("Semantic term roles differ across owners.");
            }
        }

        foreach (var group in catalogs.SelectMany(c => c.IdentitySchemes).GroupBy(s => s.Identifier))
        {
            if (group.Distinct().Count() != 1)
            {
                throw new InvalidDataException("Semantic identity schemes differ across owners.");
            }
        }
        // Conflicting vocabulary interpretations cannot be resolved by choosing an arbitrary owner.
        var vocabularies = catalogs.SelectMany(c => c.Vocabularies).GroupBy(v => v.Identifier).Select(group =>
        {
            var encodings = group.Select(v => Convert.ToHexString(SemanticVocabularySerialization.Serialize(v))).Distinct().ToArray();
            if (encodings.Length != 1)
            {
                throw new InvalidDataException("Semantic vocabulary differs across query owners.");
            }

            return group.First();
        }).ToArray();
        return new(catalogs.SelectMany(c => c.Models).Distinct().ToArray(),
            catalogs.SelectMany(c => c.Terms).Distinct().ToArray(),
            catalogs.SelectMany(c => c.IdentitySchemes).Distinct().ToArray(), PublishedVocabularies: vocabularies);
    }
}
