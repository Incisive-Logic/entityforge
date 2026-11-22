using System.Text.Json;
using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Query.Projection;
using EntityForge.Semantics;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionApplicationClient
{
    // Observation state is private to one protected read attempt. The next epoch
    // receives a fresh instance; no decoded values survive a rejected snapshot.
    private sealed class MappedReadAttempt(PartitionApplicationClient client,
        PartitionMappedRead.Request request, PartitionRemoteRead cut,
        Dictionary<string, PartitionReadQuery> initial, PartitionReadQuery? rootSource,
        Action<int, int>? observeEdges)
    {
        private readonly Dictionary<PartitionReadQuery, PartitionMappedRead.Observation> _observations = new();
        private readonly Dictionary<PartitionReadQuery, JsonElement> _decoded = new();
        private readonly HashSet<string> _initializedMappings = new(StringComparer.Ordinal);
        private readonly HashSet<PartitionReadQuery> _joined = new();
        private readonly object _observationSync = new();
        private long _ownedBytes;

        internal async ValueTask<PartitionMappedRead.Observation[]> RunAsync(CancellationToken ct)
        {
            if (request.Minimum is { } minimum && !cut.Position.Covers(minimum))
            {
                throw new InvalidOperationException("The cut does not cover the requested position.");
            }

            if (client.Profile == DatabaseProfileId.PropertyGraph)
            {
                await ReadGraphAsync(ct);
            }
            else if (client.Profile == DatabaseProfileId.Rdf)
            {
                var subject = new RdfIri(request.Recipe.Identity.GetString()!);
                await ReadAsync(client._routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(subject)), new("subject", Subject: subject.Value), ct);
            }
            else
            {
                LogicalShardId Owner(SemanticEntityIdentity identity) => client._routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(identity));
                await SemanticReadExpansionWalker.WalkAsync(new(request.Recipe, [new(0, SemanticEntityReadDecoder.SemanticIdentity(request.Recipe.Identity))]), _ => true,
                    async (projection, cancellation) =>
                    {
                        var query = new PartitionReadQuery("semantic-project", Subject: JsonSerializer.Serialize(projection, PartitionParticipantWire.Json));
                        var document = await ReadAsync(Owner(projection.Identity), query, cancellation);
                        return SemanticEntityReadDecoder.Projection(document, projection.Query());
                    }, async (traversal, cancellation) =>
                    {
                        var owner = Owner(traversal.Entity);
                        var query = new PartitionReadQuery("semantic-traverse", Subject: JsonSerializer.Serialize(traversal, PartitionParticipantWire.Json));
                        var document = await ReadAsync(owner, query, cancellation);
                        return PartitionMappedRead.ReadTraversal(document, traversal, cut.Position.Positions.Single(position => position.Identity == client._owners[owner].Domain.Identity).Version, cut.Timestamp);
                    }, ct);
            }
            return _observations.Values.ToArray();
        }

        private async ValueTask PrefetchAsync(LogicalShardId owner, IEnumerable<PartitionReadQuery> queries, CancellationToken cancellation)
        {
            PartitionReadQuery[] missing;
            lock (_observationSync)
            {
                missing = queries.Distinct().Where(query => !_observations.ContainsKey(query)).ToArray();
            }

            foreach (var batch in missing.Chunk(PartitionReadBatch.MaximumQueries))
            {
                PartitionReadQuery selected;
                lock (_observationSync)
                {
                    if (_observations.Count + batch.Length > EntityReadRecipe.MaximumObservations)
                    {
                        throw new InvalidOperationException("Mapped read exceeds its observation bound.");
                    }

                    var requestBatch = new PartitionReadBatch(_initializedMappings.Contains(owner.Value) ? [] : request.Manifests, batch);
                    selected = !_initializedMappings.Contains(owner.Value) && initial.TryGetValue(owner.Value, out var ready) &&
                        ready.Batch is { } first && first.Queries.SequenceEqual(batch) ? ready : new("batch", Batch: requestBatch);
                }
                var response = await cut.ReadValueAsync(owner.Value, selected, cancellation);
                if (response.ValueKind != JsonValueKind.Array || response.GetArrayLength() != batch.Length)
                {
                    throw new InvalidDataException("Read batch response differs from its selected queries.");
                }

                lock (_observationSync)
                {
                    if (_observations.Count + batch.Length > EntityReadRecipe.MaximumObservations)
                    {
                        throw new InvalidOperationException("Mapped frontier exceeds its observation bound.");
                    }

                    _initializedMappings.Add(owner.Value);
                    for (int index = 0; index < batch.Length; index++)
                    {
                        var observation = new PartitionMappedRead.Observation(client._owners[owner].Domain.Identity, batch[index], response[index]);
                        // Include query and identity framing, not only native values.
                        if ((_ownedBytes += PartitionProjectionValue.Measure(observation, PartitionApplicationWire.MaximumBodyBytes / 2)) > PartitionApplicationWire.MaximumBodyBytes / 2)
                        {
                            throw new InvalidOperationException("Mapped read exceeds its response bound.");
                        }

                        _decoded.Add(batch[index], response[index]);
                        _observations.Add(batch[index], observation);
                    }
                }
            }
        }

        private async ValueTask<JsonElement> ReadAsync(LogicalShardId owner, PartitionReadQuery query, CancellationToken cancellation)
        {
            await PrefetchAsync(owner, [query], cancellation);
            if (client._routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties && (query.Kind is "vertex" or "edge") && !_joined.Contains(query) && _decoded[query].ValueKind != JsonValueKind.Null)
            {
                var propertyQuery = new PartitionReadQuery(query.Kind + "-properties", query.Identity);
                var propertyOwner = client._routing.RouteGraphProperties(query.Identity, query.Kind == "edge");
                await PrefetchAsync(propertyOwner, [propertyQuery], cancellation);
                var combined = query.Kind == "vertex"
                    ? JsonSerializer.SerializeToElement(PartitionTopologyProperties.WithProperties(GraphWireJson.Vertex(_decoded[query], new(query.Identity)), _decoded[propertyQuery]), PartitionParticipantWire.Json)
                    : JsonSerializer.SerializeToElement(PartitionTopologyProperties.WithProperties(GraphWireJson.Edge(_decoded[query], new(query.Identity)), _decoded[propertyQuery]), PartitionParticipantWire.Json);
                lock (_observationSync)
                {
                    _ownedBytes += PartitionProjectionValue.Measure(combined, PartitionApplicationWire.MaximumBodyBytes / 2);
                    if (_ownedBytes > PartitionApplicationWire.MaximumBodyBytes / 2)
                    {
                        throw new InvalidOperationException("Joined mapped view exceeds its byte budget.");
                    }

                    _observations[query] = _observations[query] with
                    {
                        Value = combined
                    };
                    _decoded[query] = combined;
                    _joined.Add(query);
                }
            }
            return _decoded[query];
        }

        private async ValueTask ReadSourceAsync(LogicalShardId owner, PartitionReadQuery query, CancellationToken cancellation)
        {
            var document = await cut.ReadValueAsync(owner.Value, query, cancellation);
            MergeSource(owner, query, document);
        }

        private void MergeSource(LogicalShardId owner, PartitionReadQuery query, JsonElement document)
        {
            lock (_observationSync)
            {
                _initializedMappings.Add(owner.Value);
                if (document.ValueKind == JsonValueKind.Null)
                {
                    return; // Capacity fallback uses this same cut.
                }

                var values = document.Deserialize<PartitionSourceObservation[]>(PartitionParticipantWire.Json)
                    ?? throw new InvalidDataException("Missing source projection.");
                if (values.Length is < 1 or > EntityReadRecipe.MaximumObservations || values.Any(item => item is null || item.Query is null) || values.Select(item => item.Query).Distinct().Count() != values.Length)
                {
                    throw new InvalidDataException("Invalid source observation count.");
                }

                var allowed = new HashSet<PartitionReadQuery> { new("vertex", query.Identity) };
                foreach (string predicate in query.Source!.Predicates)
                {
                    allowed.Add(new("outgoing", query.Identity, Label: predicate));
                }

                foreach (var value in values.Where(item => item.Query.Kind == "outgoing" && allowed.Contains(item.Query)))
                {
                    foreach (var edge in value.Value.EnumerateArray().Select(GraphWireJson.EdgeReference))
                    {
                        if (client._routing.RouteGraphEdge(edge.Id.Value) == owner)
                        {
                            allowed.Add(new("edge", edge.Id.Value));
                        }
                    }
                }

                if (!values.Any(item => item.Query == new PartitionReadQuery("vertex", query.Identity)) || values.Any(item => !allowed.Contains(item.Query)))
                {
                    throw new InvalidDataException("Source projection returned unrequested observations.");
                }

                foreach (var value in values)
                {
                    if (_observations.TryGetValue(value.Query, out var prior))
                    {
                        if (prior.CertifiedReference != value.CertifiedReference || prior.Value.GetRawText() != value.Value.GetRawText())
                        {
                            throw new InvalidDataException("Source observations disagree within a cut.");
                        }

                        continue;
                    }
                    if (value.CertifiedReference && value.Query.Kind != "outgoing")
                    {
                        throw new InvalidDataException("Only source adjacency certifies references.");
                    }

                    var observation = new PartitionMappedRead.Observation(client._owners[owner].Domain.Identity, value.Query, value.Value, value.CertifiedReference);
                    if (_observations.Count >= EntityReadRecipe.MaximumObservations ||
                        (_ownedBytes += PartitionProjectionValue.Measure(observation, PartitionApplicationWire.MaximumBodyBytes / 2)) > PartitionApplicationWire.MaximumBodyBytes / 2)
                    {
                        throw new InvalidOperationException("Mapped source exceeds its response bound.");
                    }

                    _decoded.Add(value.Query, value.Value);
                    _observations.Add(value.Query, observation);
                }
            }
        }

        private async ValueTask ReadSourcesAsync(LogicalShardId owner, PartitionReadQuery[] sources, CancellationToken cancellation)
        {
            foreach (var group in sources.Chunk(PartitionReadBatch.MaximumQueries))
            {
                bool initialized;
                lock (_observationSync)
                {
                    initialized = _initializedMappings.Contains(owner.Value);
                }

                var query = new PartitionReadQuery("batch", Batch: new(initialized ? [] : request.Manifests, group));
                var values = await cut.ReadValueAsync(owner.Value, query, cancellation);
                if (values.ValueKind == JsonValueKind.Null)
                {
                    // A large optional bundle falls back at the SAME cut.
                    foreach (var source in group)
                    {
                        await ReadSourceAsync(owner, source, cancellation);
                    }

                    continue;
                }
                if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != group.Length)
                {
                    throw new InvalidDataException("Source frontier differs from its selected queries.");
                }

                for (int index = 0; index < group.Length; index++)
                {
                    MergeSource(owner, group[index], values[index]);
                }
            }
        }

        private async ValueTask ReadGraphAsync(CancellationToken ct)
        {
            var pending = new Queue<(int Shape, Guid Id)>();
            pending.Enqueue((0, request.Recipe.Identity.GetGuid()));
            var visited = new HashSet<(int, Guid)>();
            int scheduled = 1;
            while (pending.Count != 0)
            {
                var frontier = pending.ToArray().Distinct().Where(item => !visited.Contains(item)).ToArray();
                pending.Clear();
                bool grouped = client._routing.StorageLayout == DatabaseStorageLayout.Current && frontier.Length > 1 && client.ParallelReadOwners > 1;
                if (grouped)
                {
                    foreach (var wave in frontier.Where(item => request.Recipe.Shapes[item.Shape].Links.Length <= PartitionReadBatch.MaximumQueries)
                        .GroupBy(item => client._routing.RouteGraphVertex(item.Id)).Chunk(client.ParallelReadOwners))
                    {
                        await Task.WhenAll(wave.Select(group => ReadSourcesAsync(group.Key,
                            group.Select(item => Source(request, item.Shape, item.Id, [])).ToArray(), ct).AsTask()));
                    }
                }

                foreach (var next in frontier)
                {
                    if (!visited.Add(next))
                    {
                        continue;
                    }

                    var owner = client._routing.RouteGraphVertex(next.Id);
                    var shape = request.Recipe.Shapes[next.Shape];
                    if (client._routing.StorageLayout == DatabaseStorageLayout.Current && !grouped && shape.Links.Length <= PartitionReadBatch.MaximumQueries)
                    {
                        await ReadSourceAsync(owner, next.Shape == 0 && next.Id == request.Recipe.Identity.GetGuid() && rootSource is not null
                            ? rootSource : Source(request, next.Shape, next.Id, _initializedMappings.Contains(owner.Value) ? [] : request.Manifests), ct);
                    }

                    var vertexJson = await ReadAsync(owner, new("vertex", next.Id), ct);
                    if (vertexJson.ValueKind == JsonValueKind.Null)
                    {
                        continue;
                    }

                    var vertex = GraphWireJson.Vertex(vertexJson, new(next.Id));
                    if (shape.Label is not null && !vertex.Labels.Contains(shape.Label))
                    {
                        continue;
                    }

                    await PrefetchAsync(owner, shape.Links.Select(link => new PartitionReadQuery("outgoing", next.Id, Label: link.Predicate)), ct);
                    foreach (var link in shape.Links)
                    {
                        var adjacency = await ReadAsync(owner, new("outgoing", next.Id, Label: link.Predicate), ct);
                        var references = adjacency.EnumerateArray().Select(GraphWireJson.EdgeReference).ToArray();
                        if (references.Length > EntityReadRecipe.MaximumObservations - scheduled)
                        {
                            throw new InvalidOperationException("Mapped Graph frontier exceeds its bound.");
                        }

                        if (observeEdges is not null)
                        {
                            int local = references.Count(edge => client._routing.RouteGraphEdge(edge.Id.Value) == owner);
                            observeEdges(local, references.Length - local);
                        }
                        bool certified = _observations[new("outgoing", next.Id, Label: link.Predicate)].CertifiedReference;
                        if (!certified)
                        {
                            foreach (var wave in references.GroupBy(edge => client._routing.RouteGraphEdge(edge.Id.Value)).Chunk(client.ParallelReadOwners))
                            {
                                await Task.WhenAll(wave.Select(group => PrefetchAsync(group.Key,
                                    group.Select(edge => new PartitionReadQuery("edge", edge.Id.Value)), ct).AsTask()));
                            }
                        }

                        foreach (var edge in references)
                        {
                            if (++scheduled > EntityReadRecipe.MaximumObservations)
                            {
                                throw new InvalidOperationException("Mapped Graph frontier exceeds its bound.");
                            }

                            if (edge.From.Value != next.Id || edge.Label != link.Predicate)
                            {
                                throw new InvalidDataException("Source reference association differs.");
                            }

                            if (!certified)
                            {
                                var authoritative = await ReadAsync(client._routing.RouteGraphEdge(edge.Id.Value), new("edge", edge.Id.Value), ct);
                                var actual = GraphWireJson.Edge(authoritative, edge.Id);
                                if (actual.From.Value != next.Id || actual.To != edge.To || actual.Label != link.Predicate)
                                {
                                    throw new InvalidDataException("Graph adjacency differs from its authoritative edge.");
                                }
                            }
                            if (link.Target >= 0)
                            {
                                pending.Enqueue((link.Target, edge.To.Value));
                            }
                        }
                    }
                }
            }
        }
    }
}
