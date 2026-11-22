using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Semantic;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Engine;

internal static class SemanticSourcePartitionGuard
{
    public static void Validate(SemanticDatabaseState state,
        SemanticChangeSessionStart start, IReadOnlyList<SemanticEntityChange> changes)
    {
        foreach (SemanticEntityChange change in changes)
        {
            var owner = new SourceOwner(change.Subject, change.Provenance.SourceSystem,
                change.Provenance.SourceRecord);
            if (state.SourceMemberships.TryGetMembership(owner, out SourceMembership? membership) &&
                membership!.SourcePartition != start.Source.SourcePartition)
            {
                throw new SemanticSessionValidationException(
                    "A Semantic source owner cannot be implicitly moved between source partitions.");
            }
        }
    }
}
