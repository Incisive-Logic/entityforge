using EntityForge.Semantics;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Semantic.Durability;

/// <summary>Immutable dependencies of a selected partition proposal, persisted with its native bytes.</summary>
/// <param name="ReadVersion">The native read version used to select the proposal.</param>
/// <param name="Entities">The entity identities read by the proposal.</param>
/// <param name="Sources">The source partition dependencies read by the proposal.</param>
/// <param name="Fingerprint">The fingerprint binding the immutable selection dependencies.</param>
/// <param name="Mappings">The exact mapping manifests relied upon during selection.</param>
public sealed record SemanticPartitionReadEvidence(DatabaseVersion ReadVersion,
    SemanticEntityIdentity[] Entities, SemanticPartitionSource[] Sources, string Fingerprint,
    EntityMappingManifest[] Mappings)
{
    /// <summary>Gets or initializes conservative source-owner coverage for the selected proposal.</summary>
    public SemanticOwnerCoverage? OwnerCoverage
    {
        get; init;
    }
}

/// <summary>Identifies a source-system partition dependency.</summary>
/// <param name="SourceSystem">The source-system identifier.</param>
/// <param name="SourcePartition">The source partition identifier.</param>
public sealed record SemanticPartitionSource(Uri SourceSystem, Uri SourcePartition);
