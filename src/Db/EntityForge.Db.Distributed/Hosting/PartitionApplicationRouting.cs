using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal static class PartitionApplicationRouting
{
    internal static PartitionReadQuery RootRead(DatabaseProfileId profile, ReadOnlyMemory<byte> request)
    {
        var value = EntityCapturedRequest.Restore(request.Span, profile);
        return profile switch
        {
            DatabaseProfileId.PropertyGraph => new("vertex", GraphEntityInputCompiler.FromManifest(value.Description.Manifest).CapturedRoot(value).Value),
            DatabaseProfileId.Rdf => new("subject", Subject: RdfEntityInputCompiler.FromManifest(value.Description.Manifest).CapturedRoot(value).Value),
            _ => throw new NotSupportedException("This profile requires a source-aware read recipe.")
        };
    }

    internal static LogicalShardId Owner(Guid generation, DatabaseProfileId profile, DistributedRoutingBinding routing,
        Guid operation, ReadOnlyMemory<byte> request)
    {
        if (profile == DatabaseProfileId.Semantic)
        {
            var captured = SemanticEntityRequest.Restore(request.Span);
            if (captured.OperationId != operation || captured._metadata.DatabaseIncarnation != generation)
            {
                throw new InvalidDataException("Semantic application request association differs.");
            }

            return routing.Shards.Count == 1 ? routing.Shards[0] : PartitionSemanticOwnerPlan.Owner(captured, routing);
        }
        if (profile == DatabaseProfileId.PropertyGraph && routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties)
        {
            throw new NotSupportedException("Split Graph saves require complete cross-role planning.");
        }

        var value = EntityCapturedRequest.Restore(request.Span, profile);
        if (value.Description.OperationId != operation || value.Description.DatabaseIncarnation != generation)
        {
            throw new InvalidDataException("Application request association differs.");
        }

        if (value.Description.ExpectedVersion is not null)
        {
            throw new NotSupportedException("A logical application cannot interpret an unqualified scalar version as a partition position.");
        }

        if (profile == DatabaseProfileId.PropertyGraph && routing.Shards.Count != 1 &&
            (value.Description.Manifest.Members.Any(member => member.Kind == EntityMemberKind.Reference) && !PartitionOwnerOperation.IsOwnerLocal(operation) || value.Description.Delete))
        {
            throw new NotSupportedException("This routing entrance requires scalar dependency closure.");
        }

        return profile switch
        {
            DatabaseProfileId.PropertyGraph => routing.RouteGraphVertex(GraphEntityInputCompiler.FromManifest(value.Description.Manifest).CapturedRoot(value).Value),
            DatabaseProfileId.Rdf => routing.Route(DistributedDatabaseProfile.Rdf,
                ProfilePartitionRouting.RdfOwner(RdfEntityInputCompiler.FromManifest(value.Description.Manifest).CapturedRoot(value))),
            _ => throw new NotSupportedException("Unsupported application profile.")
        };
    }

    internal static byte[] NativeRequest(PartitionExecutionDomain domain, ReadOnlyMemory<byte> request)
    {
        if (domain.Profile == DatabaseProfileId.Semantic)
        {
            return SemanticEntityRequest.Restore(request.Span).ForDatabase(domain.Incarnation).Export();
        }

        return EntityCapturedRequest.Restore(request.Span, domain.Profile).ForDatabase(domain.Incarnation).Export();
    }
}
