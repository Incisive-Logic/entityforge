using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

internal sealed class EmbeddedRdfEntityReadScope(IRdfDatabase database, RdfSnapshotLease lease) : IRdfEntityReadScope
{
    public DatabaseVersion Version => lease.Version;

    public ValueTask ValidateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!lease.IsActive)
        {
            throw new RdfSnapshotTooOldException(Version);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<IReadOnlyList<RdfQuad>> ReadDefaultSubjectAsync(RdfIri subject, CancellationToken cancellationToken = default)
    {
        await ValidateAsync(cancellationToken);
        return database.Find(new(Subject: subject, MatchDefaultGraphOnly: true), lease);
    }

    public ValueTask DisposeAsync()
    {
        lease.Dispose();
        return ValueTask.CompletedTask;
    }
}
