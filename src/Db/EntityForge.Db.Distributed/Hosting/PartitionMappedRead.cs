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

internal static class PartitionMappedRead
{
    internal sealed record Request(Guid Generation, DatabaseProfileId Profile, EntityMappingManifest[] Manifests,
        EntityReadRecipe Recipe, DatabasePositionVector? Minimum = null)
    {
        internal Request ForRead(Guid readId) => this with { Recipe = Recipe with { ReadId = readId } };
    }

    internal sealed record Observation(DatabasePartitionIdentity Owner, PartitionReadQuery Query, JsonElement Value, bool CertifiedReference = false);

    internal sealed record Response(Guid Generation, DatabaseProfileId Profile, Guid ReadId,
        DatabasePositionVector Position, Observation[] Observations, DurableTimestampState? Timestamp = null);

    internal sealed record Traversal(DatabaseVersion Version, DurableTimestampState? Timestamp, IReadOnlyList<SemanticRelationshipEvidence> Evidence);

    // Native assertion versions are checked against the selected logical view,
    // not against the older physical position that keeps that view protected.
    internal static IReadOnlyList<SemanticRelationshipEvidence> ReadTraversal(JsonElement value,
        SemanticRelationshipTraversalQuery query, DatabaseVersion protectedPosition, DurableTimestampState? timestamp)
    {
        RdfWireJson.UniqueMembers(value);
        var version = value.GetProperty("version").Deserialize<DatabaseVersion>(PartitionParticipantWire.Json);
        var selectedTime = value.GetProperty("timestamp").Deserialize<DurableTimestampState>(PartitionParticipantWire.Json);
        if (selectedTime != timestamp || version.Value < protectedPosition.Value || timestamp is null && version != protectedPosition)
        {
            throw new InvalidDataException("Semantic traversal changed its selected view.");
        }

        return SemanticEntityReadDecoder.Traverse(value.GetProperty("evidence"), query, version);
    }
}

internal sealed partial class PartitionApplicationClient
{
    internal async ValueTask<PartitionMappedRead.Response> ReadMappedAsync(PartitionMappedRead.Request request, CancellationToken token)
    {
        using var mappedTrace = DistributedReadTelemetry.Start("partition.read.mapped");
        int localEdges = 0, remoteEdges = 0;
        DurableTimestampState? readTimestamp = null;
        if (request.Generation != Generation || request.Profile != Profile || request.Recipe is null || request.Manifests is not { Length: > 0 and <= EntityReadRecipe.MaximumShapes } ||
            request.Manifests.Any(manifest => manifest is null || manifest.Profile != Profile))
        {
            throw new InvalidDataException("Invalid mapped read association.");
        }

        request.Recipe.Validate(Profile switch
        {
            DatabaseProfileId.PropertyGraph => (IEntityReadRecipePolicy)EntityForge.Db.Graph.GraphReadRecipePolicy.Instance,
            DatabaseProfileId.Rdf => EntityForge.Db.Rdf.RdfReadRecipePolicy.Instance,
            DatabaseProfileId.Semantic => EntityForge.Db.Semantic.SemanticReadRecipePolicy.Instance,
            _ => throw new ArgumentException("Unknown read profile.")
        });
        var initial = new Dictionary<string, PartitionReadQuery>(StringComparer.Ordinal);
        PartitionReadQuery? rootSource = null;
        if (_routing.StorageLayout == DatabaseStorageLayout.Current && Profile == DatabaseProfileId.PropertyGraph && request.Recipe.Shapes[0].Links.Length <= PartitionReadBatch.MaximumQueries)
        {
            Guid id = request.Recipe.Identity.GetGuid();
            rootSource = Source(request, 0, id, request.Manifests);
            initial.Add(_routing.RouteGraphVertex(id).Value, rootSource);
        }
        else if (Profile == DatabaseProfileId.Rdf)
        {
            var subject = new RdfIri(request.Recipe.Identity.GetString()!);
            initial.Add(_routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(subject)).Value,
                new("batch", Batch: new(request.Manifests, [new("subject", Subject: subject.Value)])));
        }
        else if (Profile == DatabaseProfileId.Semantic)
        {
            var identity = SemanticEntityReadDecoder.SemanticIdentity(request.Recipe.Identity);
            var fields = request.Recipe.Shapes[0].Fields!.Value.Deserialize<SemanticReadField[]>(EntityReadRecipe.Json)
                ?? throw new InvalidDataException("Missing Semantic fields.");
            var projection = new SemanticProjectionRead(identity, fields);
            initial.Add(_routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(identity)).Value,
                new("batch", Batch: new(request.Manifests, [new("semantic-project", Subject: JsonSerializer.Serialize(projection, PartitionParticipantWire.Json))])));
        }
        void ObserveEdges(int local, int remote)
        {
            localEdges += local;
            remoteEdges += remote;
            mappedTrace!.SetTag("entityforge.local_edges", localEdges);
            mappedTrace.SetTag("entityforge.remote_edges", remoteEdges);
        }
        ValueTask<PartitionMappedRead.Observation[]> Project(PartitionRemoteRead cut, CancellationToken ct)
        {
            readTimestamp = cut.Timestamp;
            return new MappedReadAttempt(this, request, cut, initial, rootSource,
                mappedTrace is null ? null : ObserveEdges).RunAsync(ct);
        }
        var result = await ExecuteReadEpochAsync(Project, request.Minimum, token);
        return new(Generation, Profile, request.Recipe.ReadId, result.Position, result.Value, readTimestamp);
    }

    private static PartitionReadQuery Source(PartitionMappedRead.Request request, int shapeIndex, Guid id, EntityMappingManifest[] manifests)
    {
        var shape = request.Recipe.Shapes[shapeIndex];
        return new("graph-source", id, Source: new(manifests, shape.Label, shape.Links.Select(link => link.Predicate).Distinct(StringComparer.Ordinal).ToArray()));
    }
}

internal sealed partial class PartitionOwnedNode
{
    private async ValueTask ValidateReadMappingsAsync(EntityMappingManifest[] manifests, CancellationToken token, bool nativeExclusion)
    {
        if (manifests.Length is < 1 or > EntityReadRecipe.MaximumShapes || manifests.Any(manifest => manifest is null || manifest.Profile != Domain.Profile))
        {
            throw new InvalidDataException("Mapping profile differs.");
        }

        EntityMappingRegistry registry = _native.Database switch
        {
            IGraphDatabase graph => graph.Mappings(),
            GraphPropertyShard split => split.MappingRegistry,
            IRdfDatabase rdf => rdf.Mappings(),
            SemanticDatabaseEngine semantic => semantic.MappingRegistry,
            _ => throw new InvalidDataException("Unsupported mapping owner.")
        };
        if (_timestampProjection.Value)
        {
            Kernel.PartitionTransactions.ValidateTimestampMappings(manifests);
        }
        else if (nativeExclusion)
        {
            registry.ValidateUnderNativeExclusion(manifests, token);
        }
        else
        {
            await registry.ValidateReadOnlyAsync(manifests, token);
        }
    }

    private async ValueTask<PartitionProjectionValue> ReadMappedNativeAsync(DatabaseVersion version, PartitionReadQuery query, CancellationToken token, bool nativeExclusion)
    {
        if (query.Identity != Guid.Empty || query.Label is not null || query.Subject is not { Length: > 0 and <= PartitionApplicationBinding.MaximumRequestBytes * 2 })
        {
            throw new InvalidDataException("Invalid mapped query framing.");
        }

        if (query.Kind == "mapping")
        {
            var manifests = JsonSerializer.Deserialize<EntityMappingManifest[]>(query.Subject, PartitionParticipantWire.Json) ?? throw new InvalidDataException("Missing mappings.");
            await ValidateReadMappingsAsync(manifests, token, nativeExclusion);
            return new(null);
        }
        if (_native.Database is not SemanticDatabaseEngine engine)
        {
            throw new InvalidDataException("Not a Semantic read owner.");
        }

        using var retained = await engine.AcquireDistributedStateAsync(version, token);
        object value;
        if (query.Kind == "semantic-project")
        {
            var read = JsonSerializer.Deserialize<SemanticProjectionRead>(query.Subject, PartitionParticipantWire.Json) ?? throw new InvalidDataException("Missing projection.");
            if (Routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(read.Identity)) != Domain.Partition)
            {
                throw new InvalidDataException("Projection owner differs.");
            }

            var projection = read.Query();
            value = SemanticEntityReadDecoder.CompleteFields(await retained.ProjectAsync(projection, token), projection);
        }
        else
        {
            var read = JsonSerializer.Deserialize<SemanticRelationshipTraversalQuery>(query.Subject, PartitionParticipantWire.Json) ?? throw new InvalidDataException("Missing traversal.");
            if (read.Direction != SemanticRelationshipDirection.Outgoing || Routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(read.Entity)) != Domain.Partition)
            {
                throw new InvalidDataException("Traversal owner differs.");
            }

            value = new PartitionMappedRead.Traversal(retained.State.Version, _timestampProjectionTime.Value, await retained.TraverseAsync(read, token));
        }
        return new(value, SemanticEntityReadDecoder.WireOptions);
    }
}
