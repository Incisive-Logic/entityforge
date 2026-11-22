using EntityForge.Db.Rdf;
using EntityForge.Db.Kernel;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

// Receiver-derived, fixed leaf closure. This does not authorize traversal or mapping lookup.
internal sealed class PartitionScopedRead
{
    internal DatabasePartitionReadScope Native
    {
        get;
    }

    private readonly HashSet<PartitionReadQuery> _queries;

    internal PartitionScopedRead(DatabaseProfileId profile, DistributedRoutingBinding routing, LogicalShardId owner,
        IReadOnlyList<PartitionReadQuery> queries)
    {
        if (queries is null || queries.Count is < 1 or > DatabasePartitionReadScope.MaximumResources)
        {
            throw new ArgumentException("A bounded nonempty projection scope is required.");
        }

        var owned = queries.ToArray();
        var resources = new List<string>();
        foreach (var query in owned)
        {
            if (query is null || query.Batch is not null || query.Projection is not null || query.Source is not null ||
                query.SnapshotPrepared is not null || query.Label is not null)
            {
                throw new InvalidDataException("Unsupported projection scope.");
            }

            if (profile == DatabaseProfileId.PropertyGraph && query.Kind == "vertex" && query.Identity != Guid.Empty &&
                query.Subject is null && routing.RouteGraphVertex(query.Identity) == owner)
            {
                resources.Add(query.Identity.ToString("N"));
            }
            else if (profile == DatabaseProfileId.Rdf && query.Kind == "subject" && query.Identity == Guid.Empty &&
                query.Subject is { Length: > 0 and <= 4096 } subject &&
                routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(new RdfIri(subject))) == owner)
            {
                resources.Add(RdfPhysicalCodec.ResourceIdentity(new RdfIri(subject)));
            }
            else
            {
                throw new InvalidDataException("The projection scope is unsupported or belongs to another owner.");
            }
        }
        Native = new(profile, resources);
        _queries = new(owned);
    }

    internal void RequireQuery(PartitionReadQuery query)
    {
        if (_queries.Contains(query))
        {
            return;
        }

        if (query is
            {
                Kind: "batch", Identity: var id, Subject: null, Label: null, Projection: null, Source: null, SnapshotPrepared: null,
                Batch: { Manifests.Length: 0, Queries.Length: > 0 and <= PartitionReadBatch.MaximumQueries } batch
            } &&
            id == Guid.Empty && batch.Queries.All(_queries.Contains))
        {
            return;
        }

        throw new InvalidDataException("A scoped pin cannot read outside its declared leaf projection.");
    }
}
