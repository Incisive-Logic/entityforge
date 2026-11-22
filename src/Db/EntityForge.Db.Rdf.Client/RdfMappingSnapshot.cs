using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

// Complete default-graph subject state at one retained database-owned planning snapshot.
internal interface IRdfMappingSnapshot
{
    IReadOnlyList<RdfQuad> ReadDefaultSubject(RdfIri subject);
}
