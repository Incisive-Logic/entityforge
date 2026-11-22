using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Query.Projection;
using EntityForge.Semantics;

namespace EntityForge.Db.Distributed.Hosting;

public sealed partial class HttpPartitionEntityClient
{
    /// <summary>Materializes the complete bounded Graph mapping at one verified partition cut.</summary>
    public async ValueTask<PartitionReadResult<T?>> ReadGraphAsync<T>(object identity, GraphEntityMap<T>? map = null,
        DatabasePositionVector? minimumPosition = null, CancellationToken token = default)
    {
        if (Profile != DatabaseProfileId.PropertyGraph)
        {
            throw new InvalidOperationException("Not a Graph destination.");
        }

        var capture = new HttpGraphEntityClient(_http, Generation);
        var store = map is null ? capture.EntityStore<T>() : capture.EntityStore(map);
        var selected = store.Mapping.Map;
        var result = await ReadMappedAsync([store.Mapping.Manifest], GraphReadRecipeBuilder.Create(selected, identity), minimumPosition, token);
        await using var scope = new DetachedMappedScope(result);
        var value = await GraphEntityMaterialization.ReadEntityAsync(scope, selected, identity, token);
        return new(value is null ? default : value.Value, result.Position);
    }

    /// <summary>Materializes the complete default-graph RDF mapping at one verified partition cut.</summary>
    public async ValueTask<PartitionReadResult<T?>> ReadRdfAsync<T>(object identity, RdfEntityMap<T>? map = null,
        DatabasePositionVector? minimumPosition = null, CancellationToken token = default)
    {
        if (Profile != DatabaseProfileId.Rdf)
        {
            throw new InvalidOperationException("Not an RDF destination.");
        }

        var capture = new HttpRdfEntityClient(_http, Generation);
        var store = map is null ? capture.EntityStore<T>() : capture.EntityStore(map);
        var selected = store.Mapping.Map;
        var result = await ReadMappedAsync([store.Mapping.Manifest], RdfEntityMaterialization.ReadRecipe(selected, identity), minimumPosition, token);
        await using var scope = new DetachedMappedScope(result);
        var value = await RdfEntityMaterialization.ReadEntityAsync(scope, selected, identity, token);
        return new(value is null ? default : value.Value, result.Position);
    }

    /// <summary>Materializes a Semantic aggregate and references at one partition cut, without inventing a scalar database version.</summary>
    public async ValueTask<PartitionReadResult<T?>> ReadSemanticAsync<T>(object identity, SemanticEntityMap<T>? map = null,
        SemanticApplicationIdentity? application = null, DatabasePositionVector? minimumPosition = null, CancellationToken token = default) where T : class
    {
        if (Profile != DatabaseProfileId.Semantic)
        {
            throw new InvalidOperationException("Not a Semantic destination.");
        }

        var capture = new HttpSemanticEntityClient(_http, Generation);
        var store = map is null ? capture.EntityStore<T>(application) : capture.EntityStore(map, application);
        var description = store.DescribePartitionRead(identity);
        var result = await ReadMappedAsync(description.Manifests, description.Recipe, minimumPosition, token);
        await using var scope = new DetachedMappedScope(result);
        return new(await store.ReadPartitionValueAsync(identity, scope, token), result.Position);
    }

    private async ValueTask<PartitionMappedRead.Response> ReadMappedAsync(EntityMappingManifest[] manifests, EntityReadRecipe recipe,
        DatabasePositionVector? minimum, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new PartitionMappedRead.Request(Generation, Profile, manifests, recipe, minimum), PartitionParticipantWire.Json);
        if (bytes.Length > PartitionApplicationWire.MaximumBodyBytes)
        {
            throw new ArgumentException("Mapped read framing exceeds its bound.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, PartitionLifecycleWire.Prefix + "read-mapped"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
        message.Content = new ByteArrayContent(bytes);
        message.Content.Headers.ContentType = new("application/json");
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var result = JsonSerializer.Deserialize<PartitionMappedRead.Response>(await PartitionParticipantWire.ReadAsync(stream, PartitionApplicationWire.MaximumBodyBytes, token), PartitionParticipantWire.Json)
            ?? throw new InvalidDataException("Missing mapped read response.");
        if (result.Generation != Generation || result.Profile != Profile || result.ReadId != recipe.ReadId || result.Position is null ||
            result.Position.Positions.Any(position => position.Identity.Generation != Generation || position.Identity.Profile != Profile) ||
            minimum is not null && !result.Position.Covers(minimum) || result.Observations is null || result.Observations.Length > EntityReadRecipe.MaximumObservations ||
            result.Observations.Any(item => item is null || item.Query is null || item.Value.ValueKind == JsonValueKind.Undefined || item.CertifiedReference && item.Query.Kind != "outgoing" || !result.Position.Positions.Any(position => position.Identity == item.Owner)) ||
            result.Observations.Select(item => item.Query).Distinct().Count() != result.Observations.Length)
        {
            throw new InvalidDataException("Invalid mapped read association or complete-cut evidence.");
        }

        return result;
    }

    // The existing materializers require a scalar scope property. Its private
    // placeholder never escapes this adapter: callers receive the actual vector.
    private sealed class DetachedMappedScope(PartitionMappedRead.Response response) : IGraphEntityReadScope, IRdfEntityReadScope, ISemanticEntityReadScope
    {
        public DatabaseVersion Version => new(0);

        public SemanticSourcePosition? ContributionGuard => throw new InvalidOperationException("This detached read did not request a source guard.");

        private PartitionMappedRead.Observation Take(PartitionReadQuery query) => response.Observations.SingleOrDefault(item => item.Query == query)
            ?? throw new InvalidDataException("The mapped read omitted a required observation.");

        public ValueTask<GraphVertex?> GetVertexAsync(GraphVertexId identity, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var json = Take(new("vertex", identity.Value)).Value;
            return ValueTask.FromResult(json.ValueKind == JsonValueKind.Null ? null : GraphWireJson.Vertex(json, identity));
        }

        public ValueTask<IReadOnlyList<GraphEdge>> OutgoingAsync(GraphVertexId identity, string label, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var observation = Take(new("outgoing", identity.Value, Label: label));
            var json = observation.Value;
            var edges = json.EnumerateArray().Select(element =>
            {
                var reference = GraphWireJson.EdgeReference(element);
                if (observation.CertifiedReference)
                {
                    if (reference.From != identity || reference.Label != label)
                    {
                        throw new InvalidDataException("Invalid certified source reference.");
                    }

                    return new GraphEdge(reference.Id, reference.From, reference.To, reference.Label, new Dictionary<string, DatabaseValue>());
                }
                var authoritative = Take(new("edge", reference.Id.Value)).Value;
                var edge = GraphWireJson.Edge(authoritative, reference.Id);
                if (edge.From != identity || edge.To != reference.To || edge.Label != label || reference.From != identity)
                {
                    throw new InvalidDataException("Contradictory Graph adjacency.");
                }

                return edge;
            }).ToArray();
            return ValueTask.FromResult<IReadOnlyList<GraphEdge>>(edges);
        }

        public ValueTask<IReadOnlyList<RdfQuad>> ReadDefaultSubjectAsync(RdfIri subject, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var json = Take(new("subject", Subject: subject.Value)).Value;
            var quads = json.EnumerateArray().Select(RdfWireJson.Quad).ToArray();
            if (quads.Any(quad => quad.Subject != subject || quad.Graph is not null))
            {
                throw new InvalidDataException("RDF subject association differs.");
            }

            return ValueTask.FromResult<IReadOnlyList<RdfQuad>>(quads);
        }

        public ValueTask<EntityProjectionProjectionResult> ProjectAsync(SemanticEntityProjectionQuery query, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var json = Take(new("semantic-project", Subject: JsonSerializer.Serialize(SemanticProjectionRead.From(query), PartitionParticipantWire.Json))).Value;
            return ValueTask.FromResult(SemanticEntityReadDecoder.Projection(json, query));
        }

        public ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> TraverseAsync(SemanticRelationshipTraversalQuery query, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var item = Take(new("semantic-traverse", Subject: JsonSerializer.Serialize(query, PartitionParticipantWire.Json)));
            var json = item.Value;
            return ValueTask.FromResult(PartitionMappedRead.ReadTraversal(json, query, response.Position.Positions.Single(position => position.Identity == item.Owner).Version, response.Timestamp));
        }

        public ValueTask ValidateAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
