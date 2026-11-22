using EntityForge.Db.Rdf;
using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal static class PartitionApplicationReadWire
{
    internal const string Path = "/entityforge/v2/partition-operations/read";
    internal const string RetentionPath = "/entityforge/v2/partition-operations/retain";
    internal const int MaximumQueries = 256;

    internal sealed record Request(Guid Generation, DatabaseProfileId Profile, Guid RequestId,
        PartitionReadQuery[] Queries, DatabasePositionVector? MinimumPosition = null);

    internal sealed record Response(Guid Generation, DatabaseProfileId Profile, Guid RequestId,
        DatabasePositionVector Position, byte[][] Values, PartitionReadConsistency Consistency = PartitionReadConsistency.CompleteCut);

    internal sealed record RetentionRequest(Guid Generation, DatabaseProfileId Profile, long Floor);

    internal sealed record RetentionResponse(Guid Generation, DatabaseProfileId Profile, long Floor);
}

internal sealed partial class PartitionApplicationClient
{
    internal async ValueTask<PartitionApplicationReadWire.Response> ReadAsync(PartitionApplicationReadWire.Request request, CancellationToken token)
    {
        if (request.Generation != Generation || request.Profile != Profile || request.RequestId == Guid.Empty ||
            request.Queries is null || request.Queries.Length is < 1 or > PartitionApplicationReadWire.MaximumQueries ||
            request.Queries.Any(query => query is null || query.Batch is not null || query.Projection is not null || query.Source is not null || query.SnapshotPrepared is not null))
        {
            throw new InvalidDataException("Invalid bounded read request.");
        }

        var selected = request.Queries.Select(query => (Query: query, Owner: ReadOwner(query))).ToArray();
        using var scopeTrace = DistributedReadTelemetry.Start("partition.read.request_scope");
        if (scopeTrace is not null)
        {
            scopeTrace.SetTag("entityforge.db.profile", Profile.ToString());
            scopeTrace.SetTag("entityforge.scope_resources", JsonSerializer.Serialize(selected.Select(item => new
            {
                Incarnation = _owners[item.Owner].Domain.Identity.Incarnation.ToString("N"),
                Resource = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                    Profile == DatabaseProfileId.PropertyGraph ? item.Query.Identity.ToString("N") :
                        RdfPhysicalCodec.ResourceIdentity(new RdfIri(item.Query.Subject!)))))
            }).ToArray()));
        }
        // Keep validation short; independent raw projections follow the complete certification round.
        var projection = new PartitionReadProjection(selected.Select(item => (item.Owner.Value, item.Query)));
        var result = await ExecuteReadEpochAsync((read, ct) => ExecutePublicProjectionAsync(read, projection, request.Queries, ct), request.MinimumPosition, token);
        return new(Generation, Profile, request.RequestId, result.Position, result.Value, PartitionReadConsistency.Projection);
    }

    private async ValueTask<byte[][]> ExecutePublicProjectionAsync(PartitionRemoteRead read,
        PartitionReadProjection projection, PartitionReadQuery[] queries, CancellationToken token, bool completeFinal = false)
    {
        var values = await projection.ExecuteAsync(read, token, completeFinal);
        if (_routing.StorageLayout != DatabaseStorageLayout.GraphTopologyProperties)
        {
            return values;
        }
        // Native topology leaves are internal facts. Public Graph vertices require
        // their bags, joined before releasing this same protected read attempt.
        var live = Enumerable.Range(0, queries.Length).Where(index =>
        {
            using var document = JsonDocument.Parse(values[index]);
            return document.RootElement.ValueKind != JsonValueKind.Null;
        }).ToArray();
        if (live.Length == 0)
        {
            return values;
        }

        var bags = await new PartitionReadProjection(live.Select(index =>
            (_routing.RouteGraphProperties(queries[index].Identity).Value,
                new PartitionReadQuery("vertex-properties", queries[index].Identity)))).ExecuteAsync(read, token);
        if (values.Sum(value => (long)value.Length) + bags.Sum(value => (long)value.Length) > PartitionApplicationWire.MaximumBodyBytes / 2)
        {
            throw new InvalidOperationException("The complete read exceeds its exchange budget.");
        }

        for (int item = 0; item < live.Length; item++)
        {
            int index = live[item];
            using var topology = JsonDocument.Parse(values[index]);
            using var bag = JsonDocument.Parse(bags[item]);
            var vertex = GraphWireJson.Vertex(topology.RootElement, new(queries[index].Identity));
            values[index] = JsonSerializer.SerializeToUtf8Bytes(
                PartitionTopologyProperties.WithProperties(vertex, bag.RootElement), PartitionParticipantWire.Json);
        }
        if (values.Sum(value => (long)value.Length) > PartitionApplicationWire.MaximumBodyBytes / 2)
        {
            throw new InvalidOperationException("The complete read exceeds its response budget.");
        }

        return values;
    }

    private LogicalShardId ReadOwner(PartitionReadQuery query) => Profile switch
    {
        DatabaseProfileId.PropertyGraph when query.Kind == "vertex" && query.Identity != Guid.Empty && query.Subject is null && query.Label is null =>
            _routing.RouteGraphVertex(query.Identity),
        DatabaseProfileId.Rdf when query.Kind == "subject" && query.Identity == Guid.Empty && query.Label is null && query.Subject is { Length: > 0 and <= 4096 } subject =>
            _routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(new RdfIri(subject))),
        _ => throw new NotSupportedException("This public projection requires a Graph vertex or RDF default-graph subject.")
    };

    internal ValueTask RetainAsync(long floor, CancellationToken token) => PartitionRemoteRetention.RetainAsync(_authority,
        _owners.Values.Select(owner => owner as IPartitionMaintenanceParticipant
            ?? throw new NotSupportedException("An owner does not support remote maintenance.")).ToArray(),
        _routing, DateTimeOffset.FromUnixTimeMilliseconds(floor), token, static () => ValueTask.CompletedTask);
}
