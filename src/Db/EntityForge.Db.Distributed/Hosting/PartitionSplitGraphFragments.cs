using EntityForge.Db.Gql;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionOwnedNode
{
    private PartitionQueryRelation CaptureSplitFrontier(GraphPropertyShard split, DatabaseVersion version,
        BoundedGraphPatternQuery query, PartitionGraphQueryFrontier frontier, CancellationToken token, EntityForge.Db.Gql.GqlPropertyProjection? projection = null)
    {
        if (!BoundedGraphPatternExecutor.SupportsFragments(query) || frontier.Stage < 0 || frontier.Stage > query.Relationships.Count ||
            frontier.Vertices is { } ids && (ids.Length > PartitionQueryLimits.ExchangePageFacts || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length) ||
            frontier.Edges is { } edges && (edges.Length > PartitionQueryLimits.ExchangePageFacts || edges.Any(id => id == Guid.Empty) || edges.Distinct().Count() != edges.Length))
        {
            throw new InvalidDataException("Invalid split fragment input.");
        }

        using var lease = split.Acquire(version);
        var columns = projection is null ? null : GraphProjectionColumns(query, frontier.Stage, projection, true);
        if (frontier.RoleOperator == PartitionGraphRoleOperator.PropertyRoots)
        {
            if (split.Configuration.IsTopology || frontier.Stage != 0 || frontier.Vertices is not null || frontier.Edges is not null)
            {
                throw new InvalidDataException("Invalid property root operator.");
            }

            var equality = BoundedGraphPatternExecutor.FragmentRootEquality(query);
            var candidates = equality is { } equal ? GraphPropertyShard.Find(lease.State, false, equal.Property, equal.Value, PartitionQueryLimits.MaximumFacts)
                : lease.State.Bags.Keys.Where(id => !id.Edge).Select(id => id.Id);
            var bags = new List<PartitionTopologyProperties.Bag>();
            foreach (var id in candidates)
            {
                token.ThrowIfCancellationRequested();
                var bag = lease.State.Bags[new(false, id)];
                if (BoundedGraphPatternExecutor.FragmentMatches(System.Collections.Immutable.ImmutableHashSet<string>.Empty, bag, query, 0, propertiesOnly: true))
                {
                    bags.Add(new(false, id, columns is null ? bag : columns.Project(bag)));
                }

                if (bags.Count > PartitionQueryLimits.MaximumFacts)
                {
                    throw new InvalidOperationException("Property roots exceed their fact bound.");
                }
            }
            return new(PropertyBags: bags.ToArray());
        }
        if (frontier.Vertices is null || frontier.Edges is null)
        {
            throw new InvalidDataException("Missing role fragment identities.");
        }

        if (frontier.RoleOperator == PartitionGraphRoleOperator.Properties)
        {
            if (split.Configuration.IsTopology || frontier.Edges.Length != 0 ||
                frontier.Vertices.Any(id => Routing.RouteGraphProperties(id) != Domain.Partition))
            {
                throw new InvalidDataException("Property fragment ownership differs.");
            }

            return new(PropertyBags: frontier.Vertices.Where(id => lease.State.Bags.ContainsKey(new(false, id)))
                .Select(id => new PartitionTopologyProperties.Bag(false, id, columns is null ? lease.State.Bags[new(false, id)] : columns.Project(lease.State.Bags[new(false, id)]))).ToArray());
        }
        if (frontier.RoleOperator != PartitionGraphRoleOperator.Topology || !split.Configuration.IsTopology)
        {
            throw new InvalidDataException("Topology fragment belongs to another role.");
        }

        var view = lease.State.Topology;
        var vertices = frontier.Vertices.Select(id => view.GetVertex(new(id))).OfType<GraphVertex>().ToArray();
        var authoritative = frontier.Edges.Select(id => view.Edges.TryGetValue(new(id), out var edge) &&
            edge.StorageRole.HasFlag(GraphEdgeStorageRole.Authoritative) ? edge :
            throw new InvalidDataException("Missing authoritative split edge.")).ToArray();
        var adjacency = new Dictionary<GraphEdgeId, GraphEdgeReference>();
        if (frontier.Stage < query.Relationships.Count)
        {
            var relationship = query.Relationships[frontier.Stage];
            foreach (var vertex in vertices)
            {
                token.ThrowIfCancellationRequested();
                foreach (var edge in relationship.Direction == GraphPatternDirection.Outgoing ? view.Outgoing(vertex.Id, relationship.Label) : view.Incoming(vertex.Id, relationship.Label))
                {
                    adjacency.TryAdd(edge.Id, new(edge.Id, edge.From, edge.To, edge.Label));
                    if (vertices.Length + authoritative.Length + adjacency.Count > PartitionQueryLimits.MaximumFacts)
                    {
                        throw new InvalidOperationException("Topology fragment exceeds its fact bound.");
                    }
                }
            }
        }
        return new(Vertices: vertices, Edges: authoritative, Adjacency: adjacency.Values.ToArray());
    }
}

internal sealed partial class PartitionApplicationClient
{
    private async ValueTask<QueryResultEnvelope> ReadSplitGraphFragmentsAsync(BoundedGraphPatternQuery query,
        IPartitionTransactionParticipant[] owners,
        Func<int, PartitionGraphQueryFrontier, bool, CancellationToken, ValueTask<PartitionQueryData>> read, CancellationToken token,
        EntityForge.Db.Gql.GqlPropertyProjection? projection = null)
    {
        int topology = Array.FindIndex(owners, o => o.Domain.Partition == _routing.GraphTopologyOwner);
        if (topology < 0)
        {
            throw new InvalidDataException("Missing topology owner.");
        }

        var propertyOwners = Enumerable.Range(0, owners.Length).Where(i => i != topology).ToArray();
        var bags = new Dictionary<Guid, IReadOnlyDictionary<string, DatabaseValue>>();
        var roots = new PartitionQueryData[propertyOwners.Length];
        await PartitionRemoteRead.RunSnapshotRoundAsync(Enumerable.Range(0, propertyOwners.Length), async (i, ct) =>
            roots[i] = await read(propertyOwners[i], new(0, RoleOperator: PartitionGraphRoleOperator.PropertyRoots), false, ct), token);
        for (int i = 0; i < roots.Length; i++)
        {
            AddBags(roots[i], propertyOwners[i], null);
        }

        GraphEdgeReference[] previous = [];
        BoundedGraphPatternExecutor.FragmentBindings? bindings = null;
        for (int stage = 0; stage <= query.Relationships.Count; stage++)
        {
            var targets = stage == 0 ? bags.Keys.ToArray() : previous.Select(e => query.Relationships[stage - 1].Direction == GraphPatternDirection.Outgoing ? e.To.Value : e.From.Value).Distinct().ToArray();
            var edgeIds = previous.Select(e => e.Id.Value).Distinct().ToArray();
            if (stage != 0)
            {
                bags.Clear();
            }

            var jobs = new List<(int Owner, PartitionGraphQueryFrontier Selection)>();
            var vertexChunks = targets.Chunk(PartitionQueryLimits.ExchangePageFacts).ToArray();
            var edgeChunks = edgeIds.Chunk(PartitionQueryLimits.ExchangePageFacts).ToArray();
            for (int i = 0; i < Math.Max(vertexChunks.Length, edgeChunks.Length); i++)
            {
                jobs.Add((topology, new(stage, i < vertexChunks.Length ? vertexChunks[i] : [], i < edgeChunks.Length ? edgeChunks[i] : [], PartitionGraphRoleOperator.Topology)));
            }

            if (stage != 0)
            {
                foreach (int owner in propertyOwners)
                {
                    foreach (var chunk in targets.Where(id => _routing.RouteGraphProperties(id) == owners[owner].Domain.Partition).Chunk(PartitionQueryLimits.ExchangePageFacts))
                    {
                        jobs.Add((owner, new(stage, chunk, [], PartitionGraphRoleOperator.Properties)));
                    }
                }
            }

            var responses = new PartitionQueryData[jobs.Count];
            await PartitionRemoteRead.RunSnapshotRoundAsync(Enumerable.Range(0, jobs.Count), async (i, ct) =>
                responses[i] = await read(jobs[i].Owner, jobs[i].Selection,
                    stage == query.Relationships.Count && jobs.Count(j => j.Owner == jobs[i].Owner) == 1, ct), token);
            var vertices = new Dictionary<GraphVertexId, GraphVertex>();
            var edges = new Dictionary<GraphEdgeId, GraphEdge>();
            var adjacency = new Dictionary<GraphEdgeId, GraphEdgeReference>();
            for (int i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                var response = responses[i];
                if (job.Owner != topology)
                {
                    AddBags(response, job.Owner, job.Selection.Vertices);
                    continue;
                }
                if (response.GraphPropertyBags().Any() || response.GraphBindings().Any() || response.GraphScalarRows().Any())
                {
                    throw new InvalidDataException("Unexpected topology fragment family.");
                }

                var received = new HashSet<GraphVertexId>();
                foreach (var vertex in response.GraphVertices())
                {
                    if (vertex.Properties.Count != 0 || !job.Selection.Vertices!.Contains(vertex.Id.Value) || !received.Add(vertex.Id) || !vertices.TryAdd(vertex.Id, vertex))
                    {
                        throw new InvalidDataException("Topology vertex association differs.");
                    }
                }

                foreach (var edge in response.GraphEdges())
                {
                    if (!job.Selection.Edges!.Contains(edge.Id.Value) || !edge.StorageRole.HasFlag(GraphEdgeStorageRole.Authoritative) || edge.Properties.Count != 0 || !edges.TryAdd(edge.Id, edge))
                    {
                        throw new InvalidDataException("Topology edge association differs.");
                    }
                }

                foreach (var edge in response.GraphAdjacency())
                {
                    if (stage == query.Relationships.Count)
                    {
                        throw new InvalidDataException("Unexpected final adjacency.");
                    }

                    var relationship = query.Relationships[stage];
                    var source = relationship.Direction == GraphPatternDirection.Outgoing ? edge.From : edge.To;
                    if (!received.Contains(source) || relationship.Label is not null && edge.Label != relationship.Label || adjacency.TryGetValue(edge.Id, out var other) && other != edge)
                    {
                        throw new InvalidDataException("Split adjacency association differs.");
                    }

                    adjacency.TryAdd(edge.Id, new(edge.Id, edge.From, edge.To, edge.Label));
                }
            }
            if (edges.Count != edgeIds.Length)
            {
                throw new InvalidDataException("Missing authoritative edges.");
            }

            foreach (var edge in previous)
            {
                if (!edges.TryGetValue(edge.Id, out var authoritative) || authoritative.From != edge.From || authoritative.To != edge.To || authoritative.Label != edge.Label)
                {
                    throw new InvalidDataException("Split adjacency disagrees with authoritative state.");
                }
            }

            if (bags.Keys.Any(id => !vertices.ContainsKey(new(id))))
            {
                throw new InvalidDataException("A property bag has no live topology.");
            }

            var selected = new Dictionary<GraphVertexId, GraphVertex>();
            var scalarSelected = new Dictionary<GraphVertexId, GraphFragmentRow>();
            var outputColumns = projection is null ? null : PartitionOwnedNode.GraphProjectionColumns(query, stage, projection, false);
            foreach (var vertex in vertices.Values)
            {
                if (!bags.TryGetValue(vertex.Id.Value, out var properties))
                {
                    throw new InvalidDataException("Live topology has no property bag.");
                }

                if (BoundedGraphPatternExecutor.FragmentMatches(vertex.Labels, properties, query, stage))
                {
                    if (projection is null)
                    {
                        selected.Add(vertex.Id, new(vertex.Id, vertex.Labels, properties));
                    }
                    else
                    {
                        scalarSelected.Add(vertex.Id, new(vertex.Id, outputColumns!.Project(properties)));
                    }
                }
            }
            RequireFacts(vertices.Count + bags.Count + edges.Count + adjacency.Count);
            if (stage == 0)
            {
                bindings = projection is null ? new(query, selected.Values.ToArray()) : new(query, scalarSelected.Values.ToArray());
            }
            else if (projection is null)
            {
                bindings!.Advance(edges.Values.ToArray(), selected, token);
            }
            else
            {
                bindings!.Advance(edges.Values.ToArray(), scalarSelected, token);
            }

            previous = stage == query.Relationships.Count ? [] : adjacency.Values.Where(e => selected.ContainsKey(query.Relationships[stage].Direction == GraphPatternDirection.Outgoing ? e.From : e.To) || scalarSelected.ContainsKey(query.Relationships[stage].Direction == GraphPatternDirection.Outgoing ? e.From : e.To)).ToArray();
            if (previous.Length == 0)
            {
                if (stage < query.Relationships.Count)
                {
                    if (projection is null)
                    {
                        bindings!.Advance([], new Dictionary<GraphVertexId, GraphVertex>(), token);
                    }
                    else
                    {
                        bindings!.Advance([], new Dictionary<GraphVertexId, GraphFragmentRow>(), token);
                    }
                }

                break;
            }
        }
        return bindings!.Complete(token, projection is null ? null :
            property => EntityForge.Db.Gql.GraphGqlPropertyProjectionShaper.Project(projection, property));

        void AddBags(PartitionQueryData response, int owner, Guid[]? input)
        {
            if (response.GraphVertices().Any() || response.GraphEdges().Any() || response.GraphAdjacency().Any() || response.GraphBindings().Any() || response.GraphScalarRows().Any())
            {
                throw new InvalidDataException("Unexpected property fragment family.");
            }

            foreach (var bag in response.GraphPropertyBags())
            {
                if (bag.Edge || bag.Identity == Guid.Empty || _routing.RouteGraphProperties(bag.Identity) != owners[owner].Domain.Partition ||
                    input is not null && !input.Contains(bag.Identity) || !bags.TryAdd(bag.Identity, bag.Properties))
                {
                    throw new InvalidDataException("Property fragment association differs.");
                }

                RequireFacts(bags.Count);
            }
        }
    }
}
