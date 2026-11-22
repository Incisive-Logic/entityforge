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

internal sealed partial class PartitionApplicationClient
{
    // Each snapshot attempt owns its counters and complete-owner output. A rejected
    // attempt is discarded only after RunAsync has drained its query job.
    private sealed class QueryAttempt(PartitionApplicationClient client, PartitionQueryRequest request,
        CompiledGraphGqlQuery? graphQuery, CompiledPortableEntityQuery? semanticQuery,
        IPartitionTransactionParticipant[] owners, bool fragments, bool profileFragments,
        bool rdfFrontier, bool semanticFrontier)
    {
        private long _exchangeBytes;
        private int _pageCount, _factCount, _ownersSearched, _ownersSkipped;
        private bool _completedByOwner;
        private PartitionProjectionValue? _ownerResult;

        private bool ProjectedGraph => !request.IncludeSupportingGraph;

        internal async ValueTask<PartitionProjectionValue> RunAsync(PartitionRemoteRead captured, CancellationToken cancellation)
        {
            var read = captured.ForTimestampQuery(client._timestampClock);
            bool supportsQueryHome = fragments || profileFragments || semanticFrontier ||
                graphQuery is { RequiresCompleteGraph: false } && BoundedGraphPatternExecutor.SupportsFrontierExchange(graphQuery.Query);
            bool fitsClaimLimit = owners.Length <= GrpcEtcdShardPlacementAuthority.MaximumQueryClaims;
            var home = supportsQueryHome && fitsClaimLimit
                ? client.QueryHome ?? (client.QueryHomeProvider is { } provider ? await provider(cancellation) : null)
                : null;
            bool[]? rootOwners = null;
            PartitionRootIndexRound? rootIndex = null;
            PartitionProjectionValue?[]? rootPages = null;
            if (!rdfFrontier && !semanticFrontier && home is not null && client.RootIndexProbe(request, graphQuery, semanticQuery, profileFragments) is { } probe)
            {
                using var rootRouting = DistributedReadTelemetry.Start("partition.query.root-index");
                var selection = new PartitionQueryExchangeRequest(request.Language, request.Statement, request.EntityQuery,
                    GraphFrontier: graphQuery is null ? null : new(0), GraphFragments: fragments,
                    ProfileFragments: profileFragments, IncludeSupportingGraph: request.IncludeSupportingGraph, ContinueLocal: fragments && client.OwnerLocalTraversal);
                var demand = new PartitionRootIndexDemand(Guid.NewGuid(), probe.Column, probe.Value, PartitionQueryOwnedSelection.Capture(selection).Value)
                {
                    AdmissionTime = read.Timestamp
                };
                rootIndex = await client.ReadRootDirectoryAsync(demand, home, cancellation);
                read = read.AtTimestamp(rootIndex.Time);
                rootOwners = rootIndex.Owners.Select(owner => owner.PageIdentities.Contains(demand.Id) || owner.Deferred!.Contains(demand.Id)).ToArray();
                rootPages = rootIndex.Owners.Select(owner => owner.NativePages?.TryGetValue(demand.Id, out var native) == true ? native :
                    owner.Pages!.TryGetValue(demand.Id, out var encoded) ? PartitionQueryOperatorWire.Decode(encoded).Result.Value : null).ToArray();
                _ownersSearched = rootOwners.Count(selected => selected);
                _ownersSkipped = owners.Length - _ownersSearched;
                rootRouting?.SetTag("owners", owners.Length);
                rootRouting?.SetTag("selected", rootOwners.Count(selected => selected));
            }
            bool hasPagedRoots = rootPages?.Any(page =>
                page?.Native is PartitionQueryExchangePage { NextOffset: not null }) == true;
            bool requiresOwnerCompletion = hasPagedRoots || client.CompleteOwnerCommands && (fragments || profileFragments);
            var job = read.BeginQuery(home, requiresOwnerCompletion,
                rootIndex?.Owners.Select(owner => owner.Pending).ToArray());
            Exception? queryFailure = null;
            try
            {
                var (relations, rdfResult, frontier, fragmentResult) = await ReadRelationsAsync(job, rootPages, rootOwners, rootIndex, cancellation);
                PartitionProjectionValue value = await CompleteResultAsync(relations, rdfResult, frontier, fragmentResult, cancellation);
                using (DistributedReadTelemetry.Start("partition.query.result.value-budget"))
                {
                    PartitionProjectionValue.Measure(value, OutputBound(request.Stream));
                }

                return value;
            }
            catch (Exception failure)
            {
                queryFailure = failure;
                throw;
            }
            finally
            {
                try
                {
                    await job.CompleteAsync(queryFailure is not null);
                }
                catch (Exception cleanup) when (queryFailure is not null)
                {
                    throw new AggregateException("Query and cleanup failed.", queryFailure, cleanup);
                }
            }
        }

        private async ValueTask<PartitionQueryData> ReadRelationAsync(PartitionRemoteRead.QueryRead job,
            PartitionProjectionValue?[]? rootPages, int index, PartitionQueryExchangeRequest selection, bool final, CancellationToken ct)
        {
            var data = new PartitionQueryData();
            var arrays = data.Arrays;
            int offset = 0;
            do
            {
                var projected = offset == 0 && rootPages?[index] is { } rootPage && (selection.GraphFrontier?.Stage == 0 || selection.ProfileFragments)
                    ? rootPage
                    : await job.ReadValueAsync(owners[index].Domain.Partition.Value, selection with
                    {
                        Offset = offset,
                        CompleteQuery = owners.Length == 1 && !selection.GraphFragments &&
                            !(client.Profile == DatabaseProfileId.Rdf && RdfSparql.UsesService(request.Statement!))
                    }, finish: final, ct);
                Interlocked.Increment(ref _pageCount);
                using var pageBudget = DistributedReadTelemetry.Start("partition.query.fragment.page");
                if (selection.GraphFrontier is { } pageFrontier)
                {
                    pageBudget?.SetTag("stage", pageFrontier.Stage);
                }

                if (projected.Native is PartitionQueryExchangePage { Result: { } completed } completePage)
                {
                    if (owners.Length != 1 || offset != 0 || completePage.NextOffset is not null)
                    {
                        throw new InvalidDataException("Invalid complete operator result.");
                    }

                    long completeBytes = projected.QueryPageBytes ?? PartitionProjectionValue.Measure(completePage,
                        PartitionQueryLimits.ExchangePageBytes, PartitionQueryJson.Options);
                    if (Interlocked.Add(ref _exchangeBytes, completeBytes) > PartitionQueryLimits.MaximumExchangeBytes)
                    {
                        throw new InvalidOperationException("Distributed query exceeds its total exchange budget.");
                    }

                    _ownerResult = completed;
                    break;
                }
                if ((selection.GraphFragments || selection.ProfileFragments || selection.SemanticFrontier is not null) && projected.Native is PartitionQueryExchangePage native)
                {
                    // Preserve the canonical page budget and typed evidence. Re-encoding
                    // a JSON DOM can expand escaped dates beyond an admitted page.
                    // Typed remote decoding computes QueryPageBytes locally.
                    if (native.Result is not null)
                    {
                        throw new InvalidDataException("Unexpected complete fragment result.");
                    }

                    long nativeBytes = projected.QueryPageBytes ?? PartitionProjectionValue.Measure(native, PartitionQueryLimits.ExchangePageBytes, PartitionQueryJson.Options);
                    pageBudget?.SetTag("bytes", nativeBytes);
                    if (Interlocked.Add(ref _exchangeBytes, nativeBytes) > PartitionQueryLimits.MaximumExchangeBytes)
                    {
                        throw new InvalidOperationException("Distributed query exceeds its total exchange budget.");
                    }

                    using var nativePage = DistributedReadTelemetry.Start("partition.query.fragment.native-page");
                    RequireFacts(Interlocked.Add(ref _factCount, selection.GraphFragments ? data.AddNativeGraphPage(native.Relation) : data.AddNativeProfilePage(native.Relation)));
                    if (native.NextOffset is not { } nativeNext)
                    {
                        break;
                    }

                    if (nativeNext <= offset || nativeNext > PartitionQueryLimits.MaximumFacts)
                    {
                        throw new InvalidDataException("Invalid exchange continuation.");
                    }

                    offset = nativeNext;
                    continue;
                }
                if (data.HasNativeGraphPages)
                {
                    throw new InvalidDataException("Graph fragment page representation changed.");
                }

                var page = projected.Element;
                long pageBytes = PartitionProjectionValue.Measure(page, PartitionQueryLimits.ExchangePageBytes);
                pageBudget?.SetTag("bytes", pageBytes);
                if (Interlocked.Add(ref _exchangeBytes, pageBytes) > PartitionQueryLimits.MaximumExchangeBytes)
                {
                    throw new InvalidOperationException("Distributed query exceeds its total exchange budget.");
                }

                if (page.TryGetProperty("result", out var complete))
                {
                    if (owners.Length != 1 || offset != 0 || page.GetProperty("nextOffset").ValueKind != JsonValueKind.Null)
                    {
                        throw new InvalidDataException("Invalid complete operator result.");
                    }

                    _ownerResult = new(complete);
                    break;
                }
                foreach (var property in page.GetProperty("relation").EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        if (!arrays.TryGetValue(property.Name, out var items))
                        {
                            arrays.Add(property.Name, items = []);
                        }

                        items.AddRange(property.Value.EnumerateArray());
                        RequireFacts(Interlocked.Add(ref _factCount, PartitionQueryData.JsonArrayFacts(property.Name, property.Value)));
                    }
                    else if (property.Name == "catalog" && property.Value.ValueKind != JsonValueKind.Null)
                    {
                        data.Catalog = property.Value;
                    }
                    else if (property.Name == "profileFallback" && property.Value.ValueKind == JsonValueKind.True)
                    {
                        data.ProfileFallback = true;
                    }
                }

                if (page.GetProperty("nextOffset").ValueKind == JsonValueKind.Null)
                {
                    break;
                }

                int next = page.GetProperty("nextOffset").GetInt32();
                if (next <= offset || next > PartitionQueryLimits.MaximumFacts)
                {
                    throw new InvalidDataException("Invalid exchange continuation.");
                }

                offset = next;
            } while (true);
            return data;
        }

        private async ValueTask<(PartitionQueryData[] Relations, PartitionProjectionValue? RdfResult,
            GraphReadView? Frontier, EntityForge.Query.Results.QueryResultEnvelope? Fragments)> ReadRelationsAsync(
            PartitionRemoteRead.QueryRead job, PartitionProjectionValue?[]? rootPages,
            bool[]? rootOwners, PartitionRootIndexRound? rootIndex, CancellationToken cancellation)
        {
            var relations = new PartitionQueryData[owners.Length];
            PartitionProjectionValue? rdfResult = null;
            GraphReadView? frontier = null;
            EntityForge.Query.Results.QueryResultEnvelope? fragmentResult = null;
            if (semanticFrontier)
            {
                relations = await client.ReadSemanticFrontierAsync(request.EntityQuery!, owners,
                    (index, stage, ct) => ReadRelationAsync(job, rootPages, index, new(request.Language, request.Statement, request.EntityQuery,
                        SemanticFrontier: stage), false, ct), cancellation);
            }
            else if (rdfFrontier)
            {
                var validate = RdfSparql.CreateBindingFragmentOwnerValidator(request.Statement!);
                var result = await RdfSparql.ExecuteBindingFrontierAsync(request.Statement!, async (stage, ct) =>
                {
                    var parts = new IReadOnlyList<RdfBindingFragment>[owners.Length];
                    await PartitionRemoteRead.RunSnapshotRoundAsync(Enumerable.Range(0, owners.Length), async (index, inner) =>
                    {
                        var data = await ReadRelationAsync(job, rootPages, index, new(request.Language, request.Statement,
                            ProfileFragments: true, RdfFrontier: stage), false, inner);
                        if (!data.HasRows("rdfBindings"))
                        {
                            throw new InvalidDataException("Missing RDF frontier response.");
                        }

                        var rows = data.TypedRows<RdfBindingFragment>("rdfBindings").ToArray();
                        foreach (var row in rows)
                        {
                            inner.ThrowIfCancellationRequested();
                            var subject = validate(row);
                            if (client._routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(subject)) != owners[index].Domain.Partition)
                            {
                                throw new InvalidDataException("RDF frontier subject ownership differs.");
                            }
                        }
                        parts[index] = rows;
                    }, ct);
                    return parts.SelectMany(p => p).ToArray();
                }, cancellation);
                var select = (RdfSparqlSelectResult)result;
                rdfResult = BoundedElement(new
                {
                    form = "select",
                    bindings = select.Bindings
                }, request.Stream);
            }
            else if (client._routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties && fragments)
            {
                fragmentResult = await client.ReadSplitGraphFragmentsAsync(graphQuery!.Query, owners,
                    (index, stage, final, ct) => ReadRelationAsync(job, rootPages, index, new(request.Language, request.Statement, GraphFrontier: stage, GraphFragments: true, IncludeSupportingGraph: request.IncludeSupportingGraph), final, ct), cancellation, ProjectedGraph ? graphQuery.Projection : null);
            }
            else if (client._routing.StorageLayout == DatabaseStorageLayout.Current && owners.Length > 1 && graphQuery is { RequiresCompleteGraph: false } && (fragments || BoundedGraphPatternExecutor.SupportsFrontierExchange(graphQuery.Query)))
            {
                var exchange = await client.ReadGraphQueryFrontierAsync(graphQuery.Query, owners,
                    (index, stage, final, ct) => ReadRelationAsync(job, rootPages, index, new(request.Language, request.Statement, GraphFrontier: stage, GraphFragments: fragments, IncludeSupportingGraph: request.IncludeSupportingGraph, ContinueLocal: fragments && client.OwnerLocalTraversal), final, ct), cancellation, fragments, graphQuery.Projection, preserveGraph: !ProjectedGraph, rootOwners: rootOwners);
                frontier = exchange.View;
                fragmentResult = exchange.Bindings;
            }
            else
            {
                await PartitionRemoteRead.RunSnapshotRoundAsync(Enumerable.Range(0, owners.Length), async (index, ct) =>
                {
                    if (rootOwners is not null && !rootOwners[index])
                    {
                        var empty = new PartitionQueryData();
                        empty.AddNativeProfilePage(client.Profile == DatabaseProfileId.Rdf ? new(RdfBindings: []) :
                            new(SemanticRows: [], SemanticEvidence: [], Catalog: rootIndex!.Owners[index].Catalog?.Deserialize<SemanticCatalogSnapshot>(PartitionQueryJson.Options)
                                ?? throw new InvalidDataException("Missing indexed Semantic interpretation.")));
                        relations[index] = empty;
                    }
                    else
                    {
                        relations[index] = await ReadRelationAsync(job, rootPages, index, new(request.Language, request.Statement, request.EntityQuery, ProfileFragments: profileFragments, IncludeSupportingGraph: request.IncludeSupportingGraph), true, ct);
                    }
                }, cancellation);
            }

            if (profileFragments && !rdfFrontier && relations.Any(relation => relation.ProfileFallback))
            {
                throw new PartitionProfileFragmentFallbackException();
            }

            return (relations, rdfResult, frontier, fragmentResult);
        }

        private async ValueTask<PartitionProjectionValue> CompleteResultAsync(PartitionQueryData[] relations,
            PartitionProjectionValue? rdfResult, GraphReadView? frontier,
            EntityForge.Query.Results.QueryResultEnvelope? fragmentResult, CancellationToken cancellation)
        {
            PartitionProjectionValue value;
            if (rdfResult is { } rdfComplete)
            {
                value = rdfComplete;
            }
            else if (_ownerResult is { } completeResult)
            {
                _completedByOwner = true;
                value = completeResult.Native is JsonElement decoded ? WithoutNativeSnapshot(decoded, request.Stream) : completeResult;
            }
            else if (fragmentResult is not null)
            {
                using var mapping = DistributedReadTelemetry.Start("partition.query.result.project-map");
                if (!ProjectedGraph && client._routing.StorageLayout != DatabaseStorageLayout.Current && graphQuery!.Projection is not null)
                {
                    fragmentResult = GraphGqlPropertyProjectionShaper.Apply(fragmentResult, graphQuery.Projection);
                }

                value = GraphOutput(fragmentResult, graphQuery!, request.IncludeSupportingGraph, request.Stream, cancellation);
            }
            else if (profileFragments)
            {
                value = client.MergeProfileFragments(request, semanticQuery, owners, relations, cancellation);
            }
            else if (graphQuery is not null)
            {
                value = await CompleteGraphAsync(graphQuery, relations, frontier, cancellation);
            }
            else if (client.Profile == DatabaseProfileId.Rdf)
            {
                value = await CompleteRdfAsync(relations, cancellation).ConfigureAwait(false);
            }
            else
            {
                value = await CompleteSemanticAsync(relations, cancellation);
            }

            return value;
        }

        internal PartitionQueryStatistics Statistics() =>
            new(StrategyName(), _exchangeBytes, _pageCount, _factCount, _ownersSearched, _ownersSkipped);

        private string StrategyName()
        {
            if (client._routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties)
            {
                return fragments ? "graph-topology-property-fragments" : "graph-topology-property-relation";
            }

            if (_completedByOwner)
            {
                return "complete-owner-query";
            }

            if (semanticFrontier)
            {
                return "semantic-identity-frontier";
            }

            if (profileFragments)
            {
                if (client.Profile != DatabaseProfileId.Rdf)
                {
                    return "semantic-projection-fragments";
                }

                return rdfFrontier ? "rdf-binding-frontier" : "rdf-fragment-bindings";
            }
            if (fragments)
            {
                return "graph-fragment-bindings";
            }

            if (graphQuery is not null)
            {
                return graphQuery.Union is not null ? "graph-union-complete-relation" : BoundedGraphPatternExecutor.SupportsFrontierExchange(graphQuery.Query)
                    ? "graph-frontier-exchange" : "graph-pattern-pruning";
            }

            if (semanticQuery is not null)
            {
                return PartitionQuerySelection.IsDirectScalar(request.EntityQuery)
                    ? "semantic-direct-concept-pruning" : "complete-relation-fallback";
            }

            return RdfSparql.ExchangePatterns(request.Statement!) is null
                ? "complete-relation-fallback" : "rdf-pattern-pruning";
        }

        private async ValueTask<PartitionProjectionValue> CompleteGraphAsync(CompiledGraphGqlQuery query,
            PartitionQueryData[] relations, GraphReadView? frontier, CancellationToken cancellation)
        {
            var vertices = new Dictionary<GraphVertexId, GraphVertex>();
            var edges = new Dictionary<GraphEdgeId, GraphEdge>();
            for (int index = 0; frontier is null && index < relations.Length; index++)
            {
                foreach (var item in relations[index].Rows("vertices"))
                {
                    var id = new GraphVertexId(item.GetProperty("id").GetProperty("value").GetGuid());
                    if (client._routing.RouteGraphVertex(id.Value) != owners[index].Domain.Partition || !vertices.TryAdd(id, GraphWireJson.Vertex(item, id)))
                    {
                        throw new InvalidDataException("Graph vertex exchange ownership differs.");
                    }
                }
                foreach (var item in relations[index].Rows("edges"))
                {
                    var id = new GraphEdgeId(item.GetProperty("id").GetProperty("value").GetGuid());
                    if (client._routing.RouteGraphEdge(id.Value) != owners[index].Domain.Partition || !edges.TryAdd(id, GraphWireJson.Edge(item, id)))
                    {
                        throw new InvalidDataException("Graph edge exchange ownership differs.");
                    }
                }
                RequireFacts(vertices.Count + edges.Count);
            }
            if (client._routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties)
            {
                PartitionTopologyPropertyQueries.Join(client._routing, owners, relations, vertices, edges);
            }

            var view = frontier ?? GraphReadView.Build(vertices, edges);
            ValueTask<EntityForge.Query.Results.QueryResultEnvelope> Execute(CompiledGraphGqlQuery branch) =>
                new BoundedGraphPatternExecutor(null, capturedView: view).ExecuteCapturedResultAsync(
                    branch.Query, new(default, view, view.StatisticsFor(default)), cancellation,
                    branch.Projection is null ? null : property => GraphGqlPropertyProjectionShaper.Project(branch.Projection, property), !ProjectedGraph, branch.StartCandidates);
            var envelope = await query.ExecuteAsync(Execute, cancellation);
            return GraphOutput(envelope, query, request.IncludeSupportingGraph, request.Stream, cancellation, shaped: true);
        }

        private async ValueTask<PartitionProjectionValue> CompleteRdfAsync(PartitionQueryData[] relations, CancellationToken cancellation)
        {
            var quads = new HashSet<RdfQuad>();
            for (int index = 0; index < relations.Length; index++)
            {
                foreach (var item in relations[index].Rows("quads"))
                {
                    var quad = RdfWireJson.Quad(item);
                    if (client._routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(quad.Subject)) != owners[index].Domain.Partition || !quads.Add(quad))
                    {
                        throw new InvalidDataException("RDF exchange ownership differs.");
                    }

                    RequireFacts(quads.Count);
                }
            }

            var answer = await RdfSparql.ExecuteRelationAsync(quads.ToArray(), request.Statement!, client.RdfHttpResources, cancellation).ConfigureAwait(false);
            return answer switch
            {
                RdfSparqlSelectResult select => BoundedElement(new { form = "select", bindings = select.Bindings }, request.Stream),
                RdfSparqlAskResult ask => BoundedElement(new { form = "ask", value = ask.Value }, request.Stream),
                RdfSparqlGraphResult graph => BoundedElement(new { form = graph.Form.ToString().ToLowerInvariant(), quads = graph.Quads }, request.Stream),
                _ => throw new InvalidDataException("Unknown SPARQL result.")
            };
        }

        private async ValueTask<PartitionProjectionValue> CompleteSemanticAsync(PartitionQueryData[] relations, CancellationToken cancellation)
        {
            var fields = new Dictionary<Guid, SemanticFieldEvidence>();
            var links = new Dictionary<Guid, SemanticRelationshipEvidence>();
            var catalogs = new List<SemanticCatalogSnapshot>();
            for (int index = 0; index < relations.Length; index++)
            {
                var relation = new PartitionQueryRelation(
                    Fields: relations[index].Rows("fields").Select(x => x.Deserialize<SemanticFieldEvidence>(PartitionQueryJson.Options)!).ToArray(),
                    Relationships: relations[index].Rows("relationships").Select(x => x.Deserialize<SemanticRelationshipEvidence>(PartitionQueryJson.Options)!).ToArray(),
                    Catalog: relations[index].Catalog?.Deserialize<SemanticCatalogSnapshot>(PartitionQueryJson.Options));
                catalogs.Add(relation.Catalog ?? throw new InvalidDataException("Missing Semantic catalog."));
                foreach (var field in relation.Fields ?? throw new InvalidDataException("Missing Semantic fields."))
                {
                    if (client._routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(field.Subject)) != owners[index].Domain.Partition)
                    {
                        throw new InvalidDataException("Semantic field owner differs.");
                    }

                    if (!fields.TryAdd(field.AssertionId, field))
                    {
                        throw new InvalidDataException("Duplicate Semantic assertion.");
                    }
                }
                foreach (var link in relation.Relationships ?? throw new InvalidDataException("Missing Semantic relationships."))
                {
                    if (client._routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(link.Subject)) != owners[index].Domain.Partition)
                    {
                        throw new InvalidDataException("Semantic relationship owner differs.");
                    }

                    if (!links.TryAdd(link.AssertionId, link))
                    {
                        throw new InvalidDataException("Duplicate Semantic relationship.");
                    }
                }
                RequireFacts(fields.Count + links.Count);
            }
            // Visibility was decided by each owner at the protected cut. The evaluator's
            // private relation has no native clock; provenance remains unchanged.
            var state = SemanticDatabaseState.Empty with
            {
                Fields = fields.Values.Select(f => f with { BeginVersion = default, EndVersion = null }).ToArray(),
                Relationships = links.Values.Select(r => r with { BeginVersion = default, EndVersion = null }).ToArray(),
                Catalog = MergeCatalogs(catalogs)
            };
            var projection = await new PortableEntityQueryExecutor().ExecuteAsync(state, new(state), semanticQuery!.Query,
                semanticQuery.ProjectionOptions, default, cancellation);
            if (projection.NextContinuation is not null)
            {
                throw new NotSupportedException("This result requires a distributed continuation; native continuation is not transferable.");
            }

            return WithoutNativeSnapshot(PortableEntityQueryResponseMapper.Map(projection, 0), request.Stream);
        }
    }
}
