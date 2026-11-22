using System.Text.Json;
using EntityForge.Db.Client;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Mapping;
using static EntityForge.Db.Mapping.EntityReadJson;

namespace EntityForge.Db.Graph;

// Internal until the complete HTTP profile client is composed; this is the existing read contract.
internal sealed class HttpGraphEntityReadScope(HttpEntityReadScope scope) : IGraphEntityReadScope, IEntityReadMaterializationScope
{
    ValueTask<T> IEntityReadMaterializationScope.MaterializeAsync<T>(Func<ValueTask<T>> readAndValidate) =>
        scope.MaterializeAsync(readAndValidate);

    internal static async ValueTask<IGraphEntityReadScope> OpenAsync(HttpClient http, Guid database,
        EntityMappingManifest manifest, TimeSpan duration, CancellationToken cancellation = default) =>
        new HttpGraphEntityReadScope(await new HttpEntityReadTransport(http, database, GraphReadRecipePolicy.Instance)
            .OpenAsync([manifest], duration, cancellation: cancellation).ConfigureAwait(false));

    public DatabaseVersion Version => scope.Version;

    public ValueTask ValidateAsync(CancellationToken cancellationToken = default) => scope.ValidateAsync(cancellationToken);

    public ValueTask DisposeAsync() => scope.DisposeAsync();

    public ValueTask<GraphVertex?> GetVertexAsync(GraphVertexId identity, CancellationToken cancellationToken = default) =>
        scope.ReadAsync("vertex", identity.Value, value =>
        {
            if (value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return GraphWireJson.Vertex(value, identity);
        }, cancellationToken);

    public ValueTask<IReadOnlyList<GraphEdge>> OutgoingAsync(GraphVertexId identity, string label, CancellationToken cancellationToken = default) =>
        scope.ReadAsync<IReadOnlyList<GraphEdge>>("outgoing", new
        {
            identity = identity.Value,
            label
        }, value =>
        {
            UniqueMembers(value);
            var edges = Array(value).Select(item => new GraphEdge(new(Identity(Field(item, "id"))),
                new(Identity(Field(item, "from"))), new(Identity(Field(item, "to"))), Text(Field(item, "label")),
                GraphWireJson.Properties(Field(item, "properties")))).ToArray();
            if (edges.Any(edge => edge.From != identity || edge.Label != label) || edges.Select(edge => edge.Id).Distinct().Count() != edges.Length)
            {
                throw new InvalidDataException("Outgoing reply differs from its predicate or repeats an edge.");
            }

            return edges;
        }, cancellationToken);
}
