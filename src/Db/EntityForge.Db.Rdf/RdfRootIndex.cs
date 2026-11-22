using EntityForge.Db.Kernel;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    internal const string PredicateRootIndex = "rdf-predicate-v1";

    // A local star needs every mandatory predicate; omitting its owner is safe
    // only when a required predicate is absent. General algebra keeps its union.
    internal static (DatabaseRootIndexColumn Column, string Value)? RootIndexProbe(string query)
    {
        if (LocalFragmentBody(query) is not { } local)
        {
            return null;
        }

        var pattern = ((PatternElement)local.Elements[0]).Pattern;
        return pattern.Predicate?.Constant is EntityForge.Db.Rdf.Contracts.RdfIri iri
            ? (new(PredicateRootIndex, ""), iri.Value) : null;
    }
}

internal sealed partial class RdfProfileRuntime
{
    private readonly DatabaseRootIndexCache _rootIndexes = new();

    DatabaseRootIndexSnapshot IDatabaseTimestampProfile.CaptureRootIndexes(object view, DatabaseRootIndexColumn[] columns)
    {
        var selected = view as TimestampView ?? throw new InvalidDataException("Invalid RDF index view.");
        return new(_rootIndexes.Get(view, columns, column => column.Kind == RdfSparql.PredicateRootIndex && column.Field.Length == 0
            ? DatabaseRootIndexMembership.Build(column, selected.Data.ByPredicate.Keys.Select(p => p.Value))
            : throw new NotSupportedException("Unknown RDF root index.")));
    }
}
