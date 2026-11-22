using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>Exact identity and digest of a sealed source recovery image, retained across transfer retries.</summary>
/// <param name="Transfer">The transfer identifier for which the image was sealed.</param>
/// <param name="Source">The source partition identity contained in the image.</param>
/// <param name="Position">The database version captured by the sealed image.</param>
/// <param name="Digest">The integrity digest retained across transfer retries.</param>
public sealed record PartitionSealedImage(Guid Transfer, DatabasePartitionIdentity Source, DatabaseVersion Position, string Digest);
