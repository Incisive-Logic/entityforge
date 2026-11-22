using EntityForge.Db.Gql;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionOwnedNode
{
    private PartitionQueryRelation CaptureGraphQueryFrontier(GraphReadView view, BoundedGraphPatternQuery query,
        PartitionGraphQueryFrontier frontier, CancellationToken token, bool fragments = false, bool continueLocal = false, EntityForge.Db.Gql.GqlPropertyProjection? projection = null)
    {
        var relation = CaptureGraphStage(view, query, frontier, token, fragments, projection);
        return continueLocal && fragments ? ContinueGraphLocally(view, query, frontier.Stage, relation, token, projection) : relation;
    }

    private PartitionQueryRelation CaptureGraphStage(GraphReadView view, BoundedGraphPatternQuery query,
        PartitionGraphQueryFrontier frontier, CancellationToken token, bool fragments, EntityForge.Db.Gql.GqlPropertyProjection? projection,
        int maximumFacts = PartitionQueryLimits.MaximumFacts)
    {
        using var native = DistributedReadTelemetry.Start("partition.query.frontier.native");
        native?.SetTag("stage", frontier.Stage);
        if (frontier.RoleOperator != PartitionGraphRoleOperator.Native || !(fragments ? BoundedGraphPatternExecutor.SupportsFragments(query) : BoundedGraphPatternExecutor.SupportsFrontierExchange(query)) || frontier.Stage < 0 || frontier.Stage > query.Relationships.Count ||
            (frontier.Stage == 0 ? frontier.Vertices is not null || frontier.Edges is not null : frontier.Vertices is null || frontier.Edges is null))
        {
            throw new InvalidDataException("Invalid graph frontier stage.");
        }

        if (frontier.Vertices is { } ids && (ids.Length > PartitionQueryLimits.ExchangePageFacts || ids.Distinct().Count() != ids.Length ||
                ids.Any(id => id == Guid.Empty || Routing.RouteGraphVertex(id) != Domain.Partition)) ||
            frontier.Edges is { } edgeIds && (edgeIds.Length > PartitionQueryLimits.ExchangePageFacts || edgeIds.Distinct().Count() != edgeIds.Length ||
                edgeIds.Any(id => id == Guid.Empty || Routing.RouteGraphEdge(id) != Domain.Partition)))
        {
            throw new InvalidDataException("Graph frontier ownership or size differs.");
        }

        var vertices = (fragments ? BoundedGraphPatternExecutor.FragmentVertices(view, query, frontier.Stage, frontier.Vertices)
            : BoundedGraphPatternExecutor.ExchangeFrontierVertices(view, query, frontier.Stage, frontier.Vertices))
            .Take(maximumFacts + 1).ToArray();
        var edges = (frontier.Edges ?? []).Select(id => view.Edges.TryGetValue(new(id), out var edge) &&
                edge.StorageRole.HasFlag(GraphEdgeStorageRole.Authoritative) ? edge :
                throw new InvalidDataException("Frontier adjacency has no authoritative edge at this snapshot.")).ToArray();
        var adjacency = new Dictionary<GraphEdgeId, GraphEdgeReference>();
        if (frontier.Stage < query.Relationships.Count)
        {
            var next = query.Relationships[frontier.Stage];
            foreach (var vertex in vertices)
            {
                token.ThrowIfCancellationRequested();
                foreach (var edge in next.Direction == GraphPatternDirection.Outgoing
                    ? view.Outgoing(vertex.Id, next.Label) : view.Incoming(vertex.Id, next.Label))
                {
                    adjacency.TryAdd(edge.Id, new(edge.Id, edge.From, edge.To, edge.Label));
                    if (vertices.Length + edges.Length + adjacency.Count > maximumFacts)
                    {
                        throw maximumFacts == PartitionQueryLimits.MaximumFacts ? new InvalidOperationException("Graph frontier exceeds its fact bound.")
                            : new PartitionQueryContinuationCapacityException();
                    }
                }
            }
        }
        if (vertices.Length + edges.Length + adjacency.Count > maximumFacts)
        {
            throw maximumFacts == PartitionQueryLimits.MaximumFacts ? new InvalidOperationException("Graph frontier exceeds its fact bound.")
                            : new PartitionQueryContinuationCapacityException();
        }

        if (fragments)
        {
            bool materialize = BoundedGraphPatternExecutor.FragmentMaterializesVertex(query, frontier.Stage);
            var columns = projection is null ? null : GraphProjectionColumns(query, frontier.Stage, projection, false);
            return new(Vertices: materialize && columns is null ? vertices : null,
                Edges: edges.Select(e => e with { Properties = new Dictionary<string, DatabaseValue>() }).ToArray(),
                Adjacency: adjacency.Values.ToArray(), GraphBindings: !materialize ? vertices.Select(v => v.Id.Value).ToArray() : null,
                ScalarRows: !materialize || columns is null ? null : vertices.Select(v => new GraphFragmentRow(v.Id, columns.Project(v.Properties))).ToArray());
        }
        if (projection is not null)
        {
            var columns = GraphEvaluationColumns(query, projection);
            vertices = vertices.Select(vertex => vertex with { Properties = columns.Project(vertex.Properties) }).ToArray();
            edges = edges.Select(edge => edge with { Properties = columns.Project(edge.Properties) }).ToArray();
        }
        return new(Vertices: vertices, Edges: edges, Adjacency: adjacency.Values.ToArray());
    }
}

internal sealed partial class PartitionApplicationClient
{
    private sealed record GraphFrontierResult(GraphReadView? View, QueryResultEnvelope? Bindings);

    private async ValueTask<GraphFrontierResult> ReadGraphQueryFrontierAsync(BoundedGraphPatternQuery query,
        IPartitionTransactionParticipant[] owners,
        Func<int, PartitionGraphQueryFrontier, bool, CancellationToken, ValueTask<PartitionQueryData>> read, CancellationToken token, bool fragments = false,
        EntityForge.Db.Gql.GqlPropertyProjection? projection = null, bool preserveGraph = false, bool[]? rootOwners = null)
    {
        var vertices = new Dictionary<GraphVertexId, GraphVertex>();
        var scalarIdentities = new HashSet<GraphVertexId>();
        var edges = new Dictionary<GraphEdgeId, GraphEdge>();
        GraphEdgeReference[] previous = [];
        Dictionary<LogicalShardId, int>? ownerIndexes = null;
        BoundedGraphPatternExecutor.FragmentBindings? bindings = null;
        var continuationBuffer = new PartitionGraphContinuationBuffer();
        bool scalar = fragments && projection is not null && !preserveGraph;
        for (int stage = 0; stage <= query.Relationships.Count; stage++)
        {
            var jobs = new List<(int Owner, PartitionGraphQueryFrontier Request)>();
            var prefetched = new Dictionary<int, PartitionQueryData>();
            if (stage == 0)
            {
                jobs.AddRange(Enumerable.Range(0, owners.Length).Where(index => rootOwners is null || rootOwners[index])
                    .Select(index => (index, new PartitionGraphQueryFrontier(0))));
            }
            else
            {
                using var routing = DistributedReadTelemetry.Start("partition.query.routing.group");
                var direction = query.Relationships[stage - 1].Direction;
                var targets = previous.Select(e => direction == GraphPatternDirection.Outgoing ? e.To.Value : e.From.Value).Distinct().ToArray();
                var missingEdges = previous.Where(e => !edges.ContainsKey(e.Id)).Select(e => e.Id.Value).ToArray();
                routing?.SetTag("stage", stage);
                routing?.SetTag("owners", owners.Length);
                routing?.SetTag("vertices", targets.Length);
                routing?.SetTag("edges", missingEdges.Length);
                // Locality is collected only in opt-in traces. Do not add routing
                // hashes to uninstrumented query execution merely to count them.
                if (routing is not null)
                {
                    int allLocal = 0, sameSourceTarget = 0;
                    foreach (var edge in previous)
                    {
                        var source = _routing.RouteGraphVertex((direction == GraphPatternDirection.Outgoing ? edge.From : edge.To).Value);
                        var target = _routing.RouteGraphVertex((direction == GraphPatternDirection.Outgoing ? edge.To : edge.From).Value);
                        if (source == target)
                        {
                            sameSourceTarget++;
                        }

                        if (source == target && source == _routing.RouteGraphEdge(edge.Id.Value))
                        {
                            allLocal++;
                        }
                    }
                    routing.SetTag("paths", previous.Length);
                    routing.SetTag("sourceTargetLocal", sameSourceTarget);
                    routing.SetTag("completeStepLocal", allLocal);
                }
                ownerIndexes ??= owners.Select((owner, index) => (owner.Domain.Partition, index)).ToDictionary(p => p.Partition, p => p.index);
                var vertexBuckets = PartitionGraphCandidateRouting.Group(targets, ownerIndexes, _routing.RouteGraphVertex);
                var edgeBuckets = PartitionGraphCandidateRouting.Group(missingEdges, ownerIndexes, _routing.RouteGraphEdge);
                for (int index = 0; index < owners.Length; index++)
                {
                    var vertexIds = vertexBuckets[index]?.ToArray() ?? [];
                    var edgeIds = edgeBuckets[index]?.ToArray() ?? [];
                    if (continuationBuffer.Take(index, stage, vertexIds, edgeIds) is { } ready)
                    {
                        prefetched.Add(jobs.Count, ready.Data);
                        jobs.Add((index, ready.Request));
                        var covered = ready.Request.Vertices!.ToHashSet();
                        var dependencies = ready.Request.Edges!.ToHashSet();
                        vertexIds = vertexIds.Where(id => !covered.Contains(id)).ToArray();
                        edgeIds = edgeIds.Where(id => !dependencies.Contains(id)).ToArray();
                    }
                    var vertexChunks = vertexIds.Chunk(PartitionQueryLimits.ExchangePageFacts).ToArray();
                    var edgeChunks = edgeIds.Chunk(PartitionQueryLimits.ExchangePageFacts).ToArray();
                    for (int chunk = 0; chunk < Math.Max(vertexChunks.Length, edgeChunks.Length); chunk++)
                    {
                        jobs.Add((index, new(stage, chunk < vertexChunks.Length ? vertexChunks[chunk] : [], chunk < edgeChunks.Length ? edgeChunks[chunk] : [])));
                    }
                }
            }
            var responses = new PartitionQueryData[jobs.Count];
            await PartitionRemoteRead.RunSnapshotRoundAsync(Enumerable.Range(0, jobs.Count), async (index, ct) =>
                responses[index] = prefetched.TryGetValue(index, out var ready) ? ready : await read(jobs[index].Owner, jobs[index].Request,
                    stage == query.Relationships.Count && jobs.Count(job => job.Owner == jobs[index].Owner) == 1, ct), token);
            var next = new Dictionary<GraphEdgeId, GraphEdgeReference>();
            var stageVertices = new Dictionary<GraphVertexId, GraphVertex>();
            var scalarRows = new Dictionary<GraphVertexId, GraphFragmentRow>();
            var scalarColumns = scalar ? PartitionOwnedNode.GraphProjectionColumns(query, stage, projection!, false) : null;
            for (int index = 0; index < jobs.Count; index++)
            {
                var job = jobs[index];
                var response = responses[index];
                var owner = owners[job.Owner].Domain.Partition;
                using var ingestion = DistributedReadTelemetry.Start("partition.query.frontier.ingest");
                ingestion?.SetTag("stage", stage);
                ingestion?.SetTag("owner", job.Owner);
                continuationBuffer.Add(job.Owner, stage, response, query, _routing, owner);
                var receivedVertices = new HashSet<GraphVertexId>();
                foreach (var row in response.GraphScalarRows())
                {
                    if (!scalar || row.Id.Value == Guid.Empty || _routing.RouteGraphVertex(row.Id.Value) != owner ||
                        !receivedVertices.Add(row.Id) || stage != 0 && !job.Request.Vertices!.Contains(row.Id.Value) ||
                        !scalarRows.TryAdd(row.Id, row))
                    {
                        throw new InvalidDataException("Scalar row association differs.");
                    }

                    scalarIdentities.Add(row.Id);
                    if (row.Values.Keys.Any(key => !scalarColumns!.Contains(key)))
                    {
                        throw new InvalidDataException("Unexpected scalar column.");
                    }
                }
                foreach (var vertex in response.GraphVertices())
                {
                    var id = vertex.Id;
                    if (scalar || fragments && !BoundedGraphPatternExecutor.FragmentMaterializesVertex(query, stage) ||
                        _routing.RouteGraphVertex(id.Value) != owner || !receivedVertices.Add(id) ||
                        stage != 0 && !job.Request.Vertices!.Contains(id.Value))
                    {
                        throw new InvalidDataException("Frontier vertex association differs.");
                    }

                    if (!stageVertices.TryAdd(id, vertex))
                    {
                        throw new InvalidDataException("Duplicate fragment vertex.");
                    }

                    vertices.TryAdd(id, vertex);
                }
                foreach (var value in response.GraphBindings())
                {
                    var id = new GraphVertexId(value);
                    if (!fragments || BoundedGraphPatternExecutor.FragmentMaterializesVertex(query, stage) || id.Value == Guid.Empty ||
                        _routing.RouteGraphVertex(id.Value) != owner || !receivedVertices.Add(id) ||
                        stage != 0 && !job.Request.Vertices!.Contains(id.Value))
                    {
                        throw new InvalidDataException("Fragment binding association differs.");
                    }

                    if (scalar)
                    {
                        if (!scalarRows.TryAdd(id, new(id, GraphFragmentRow.NoValues)))
                        {
                            throw new InvalidDataException("Duplicate scalar binding.");
                        }

                        scalarIdentities.Add(id);
                    }
                    else
                    {
                        var vertex = new GraphVertex(id, new HashSet<string>(), new Dictionary<string, DatabaseValue>());
                        if (!stageVertices.TryAdd(id, vertex))
                        {
                            throw new InvalidDataException("Duplicate fragment binding.");
                        }

                        vertices.TryAdd(id, vertex);
                    }
                }
                var receivedEdges = new HashSet<Guid>();
                foreach (var edge in response.GraphEdges())
                {
                    var id = edge.Id;
                    if (_routing.RouteGraphEdge(id.Value) != owner || !receivedEdges.Add(id.Value) ||
                        job.Request.Edges is null || !job.Request.Edges.Contains(id.Value) || !edges.TryAdd(id, edge))
                    {
                        throw new InvalidDataException("Frontier edge association differs.");
                    }
                }
                if (receivedEdges.Count != (job.Request.Edges?.Length ?? 0))
                {
                    throw new InvalidDataException("Missing authoritative frontier edge.");
                }

                foreach (var edge in response.GraphAdjacency())
                {
                    if (stage == query.Relationships.Count)
                    {
                        throw new InvalidDataException("Unexpected final adjacency.");
                    }

                    var relationship = query.Relationships[stage];
                    var source = relationship.Direction == GraphPatternDirection.Outgoing ? edge.From : edge.To;
                    if (!receivedVertices.Contains(source) || relationship.Label is not null && edge.Label != relationship.Label ||
                        next.TryGetValue(edge.Id, out var other) && other != edge)
                    {
                        throw new InvalidDataException("Frontier adjacency association differs.");
                    }

                    next.TryAdd(edge.Id, edge);
                }
                RequireFacts(vertices.Count + scalarIdentities.Count + edges.Count + next.Count);
                ingestion?.SetTag("returnedVertices", receivedVertices.Count);
                ingestion?.SetTag("returnedEdges", receivedEdges.Count);
            }
            // Adjacency routes the lookup; it never substitutes for authoritative
            // edge state. Every selected edge is checked at the original cut.
            foreach (var reference in previous)
            {
                if (!edges.TryGetValue(reference.Id, out var edge) || edge.From != reference.From || edge.To != reference.To || edge.Label != reference.Label)
                {
                    throw new InvalidDataException("Authoritative edge disagrees with frontier adjacency.");
                }
            }

            if (fragments)
            {
                if (stage == 0)
                {
                    bindings = scalar ? new(query, scalarRows.Values.ToArray()) : new(query, stageVertices.Values.ToArray());
                }
                else
                {
                    var selectedEdges = previous.Select(e => e.Id).ToHashSet();
                    // Preserve the authoritative relation's stable enumeration,
                    // while retaining every row occurrence in the binding join.
                    var stageEdges = edges.Values.Where(e => selectedEdges.Contains(e.Id)).ToArray();
                    if (scalar)
                    {
                        bindings!.Advance(stageEdges, scalarRows, token);
                    }
                    else
                    {
                        bindings!.Advance(stageEdges, stageVertices, token);
                    }
                }
            }
            previous = next.Values.ToArray();
            if (previous.Length == 0)
            {
                if (fragments && stage < query.Relationships.Count)
                {
                    if (scalar)
                    {
                        bindings!.Advance([], new Dictionary<GraphVertexId, GraphFragmentRow>(), token);
                    }
                    else
                    {
                        bindings!.Advance([], new Dictionary<GraphVertexId, GraphVertex>(), token);
                    }
                }

                break;
            }
        }
        using var materialize = DistributedReadTelemetry.Start("partition.query.result.materialize");
        return fragments ? new(null, bindings!.Complete(token, projection is null ? null :
            property => EntityForge.Db.Gql.GraphGqlPropertyProjectionShaper.Project(projection, property), preserveGraph)) : new(GraphReadView.Build(vertices, edges), null);
    }
}
