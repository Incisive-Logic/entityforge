namespace EntityForge.Db.Kernel;

/// <summary>Explicit qualification for bounded resource reads, including absence evidence.</summary>
/// <remarks>Reservation support alone does not qualify a profile for scoped reads.</remarks>
public interface IDatabaseProfileScopedReads : IDatabaseProfilePartitionReservations
{
}
