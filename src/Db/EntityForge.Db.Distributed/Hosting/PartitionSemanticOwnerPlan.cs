using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Mapping;
using EntityForge.Db.Semantic;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Semantics;

namespace EntityForge.Db.Distributed.Hosting;

// Initial direct capability: scalar mapped replacement. Other shapes use the
// existing complete distributed planner; caller hints never establish closure.
internal static class PartitionSemanticOwnerPlan
{
    internal static LogicalShardId Owner(SemanticEntityRequest request, DistributedRoutingBinding routing)
    {
        var metadata = request._metadata;
        var plan = SemanticEntityPlan.FromDefinition(metadata.Definition, metadata.Application,
            metadata.Limits, metadata.RootBinding?.ToBinding());
        var captured = plan.CaptureRouting(EntityValueCapture.Decode(request._payload)!, metadata.Delete);
        if (captured.Subjects.Count != 1 || captured.Targets.Count != 0 ||
            plan.Manifests.Any(map => map.Members.Any(member => member.Kind == EntityMemberKind.Reference)))
        {
            throw new NotSupportedException("Semantic aggregate dependencies require coordinated planning.");
        }

        var owner = routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(captured.Subjects[0]));
        if (captured.SourcePartition != SemanticEntityPlacement.Partition(captured.Subjects[0]) ||
            ProfilePartitionRouting.SemanticHome(routing, captured.SourceSystem, captured.SourcePartition) != owner)
        {
            throw new InvalidDataException("Mapped source and entity placement differ.");
        }

        return owner;
    }

    internal static SemanticOwnerCoverage Validate(SemanticDatabaseState state, SemanticSessionEnvelope session,
        IReadOnlyList<SemanticEntityChange> changes, DistributedRoutingBinding routing, LogicalShardId owner)
    {
        // No remote policy evaluation or relationship dependency may be omitted.
        // Unknown previous coverage is conservative, including old/imported metadata.
        if (state.Governance.Rules.Count != 0 || state.AssertionStatistics.RelationshipCount != 0 ||
            changes.Any(change => change.Relationships.Count != 0 ||
                routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(change.Subject)) != owner))
        {
            throw new NotSupportedException("Semantic dependencies require coordinated planning.");
        }

        var source = session.Start.Source;
        if (ProfilePartitionRouting.SemanticHome(routing, source.SourceSystem, source.SourcePartition) != owner)
        {
            throw new InvalidDataException("Wrong Semantic source owner.");
        }

        if (state.SourceProgress.TryGetValue(new(source.SourceSystem, source.SourcePartition), out var previous) &&
            (previous.Coverage is not { } coverage || coverage.Routing != routing.Fingerprint ||
             coverage.Owners.Any(value => value != owner.Value)))
        {
            throw new NotSupportedException("Previous source coverage is not exclusively local.");
        }

        return new(routing.Fingerprint, [owner.Value]);
    }
}
