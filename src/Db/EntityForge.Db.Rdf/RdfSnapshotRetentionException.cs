namespace EntityForge.Db.Rdf;

// Retained readers defer reclamation; this condition is not a storage failure.
internal sealed class RdfSnapshotRetentionException()
    : InvalidOperationException("An RDF checkpoint cannot advance retention while snapshots are leased.");
