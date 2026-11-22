using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

internal sealed class LocalRdfMappingSnapshot(IRdfDatabase database, RdfSnapshotLease snapshot) : IRdfMappingSnapshot
{
    public IReadOnlyList<RdfQuad> ReadDefaultSubject(RdfIri subject) =>
        database.Find(new RdfQuadPattern(Subject: subject, MatchDefaultGraphOnly: true), snapshot);
}
