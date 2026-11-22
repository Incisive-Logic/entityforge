namespace EntityForge.Db.Rdf;

// Keep remote execution failures distinct from malformed caller input. The
// public diagnostic omits endpoint addresses and untrusted response content.
internal sealed class RdfSparqlRemoteExecutionException(string operation, Exception failure)
    : IOException($"SPARQL {operation} could not complete its configured remote operation.", failure);
