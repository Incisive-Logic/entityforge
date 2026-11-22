using System.Text.RegularExpressions;
using EntityForge.Db.Rdf.Contracts;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Update;
using VDS.RDF.Update.Commands;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparqlUpdate
{
    private static IReadOnlyList<RdfMutation>? TryGraphManagement(
        RdfSparqlDatasetSnapshot dataset, string update, CancellationToken cancellation)
    {
        if (!Regex.IsMatch(update, UpdateProlog + @"\s*(?:CREATE|CLEAR|DROP|ADD|COPY|MOVE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return null;
        }
        SparqlUpdateCommandSet parsed;
        IReadOnlyDictionary<string, string> iris;
        try
        {
            var bound = RdfSparql.BindUpdateIris(update);
            iris = bound.Iris;
            parsed = ParseCommandSet(bound.Source);
        }
        catch (Exception exception) when (exception is RdfException or SparqlUpdateException)
        {
            throw new InvalidDataException("SPARQL graph-management syntax is invalid.", exception);
        }
        if (parsed.CommandCount != 1)
        {
            throw new NotSupportedException("Graph management requires one atomic operation per request.");
        }
        RdfIri? Name(IRefNode? node) => node switch
        {
            null => null,
            IUriNode iri => new RdfIri(iris.GetValueOrDefault(iri.Uri.OriginalString, iri.Uri.OriginalString)),
            _ => throw new InvalidDataException("Graph management requires IRI graph names.")
        };
        IReadOnlyList<RdfQuad> Read(RdfIri? name)
        {
            cancellation.ThrowIfCancellationRequested();
            return dataset.ReadPrefix(new(Graph: name, MatchDefaultGraphOnly: name is null), MaximumMutations + 1, false, cancellation);
        }
        var mutations = new List<RdfMutation>();
        void Remove(IEnumerable<RdfQuad> quads) => AddBounded(mutations, quads.Select(quad =>
            {
                cancellation.ThrowIfCancellationRequested();
                return (RdfMutation)new RetractRdfQuad(quad);
            }));
        var command = parsed.Commands.Single();
        if (command is CreateCommand create)
        {
            if (dataset.ReadPrefix(new(Graph: Name(create.TargetGraphName)), 1, false, cancellation).Count > 0 && !create.Silent)
            {
                throw new InvalidDataException("The named graph already exists.");
            }
            // SPARQL Update 4.4.1 permits stores without empty-graph retention
            // to implement creation of an absent graph without a stored slot.
            return mutations;
        }
        // This store has no separately registered empty graphs. Removing the
        // triples removes the named graph; DROP DEFAULT preserves the default slot.
        if (command is DropCommand drop)
        {
            command = new ClearCommand(drop.TargetGraphName, drop.Mode, drop.Silent);
        }
        if (command is ClearCommand clear)
        {
            if (clear.Mode == ClearMode.Graph)
            {
                var quads = Read(Name(clear.TargetGraphName));
                Remove(quads);
            }
            if (clear.Mode is ClearMode.Default or ClearMode.All)
            {
                Remove(Read(null));
            }
            if (clear.Mode is ClearMode.Named or ClearMode.All)
            {
                Remove(dataset.ReadPrefix(new(), MaximumMutations + 1, true, cancellation));
            }
            return mutations;
        }
        if (command is not BaseTransferCommand transfer || command is not (AddCommand or CopyCommand or MoveCommand))
        {
            throw new NotSupportedException("Unsupported graph-management operation.");
        }
        RdfIri? source = Name(transfer.SourceGraphName);
        RdfIri? destination = Name(transfer.DestinationGraphName);
        if (source == destination)
        {
            return mutations;
        }
        var input = Read(source);
        if (source is not null && input.Count == 0)
        {
            if (transfer.Silent)
            {
                return mutations;
            }
            throw new InvalidDataException("The source graph has no stored triples; empty graph registration is not supported.");
        }
        if (command is CopyCommand or MoveCommand)
        {
            Remove(Read(destination));
        }
        if (command is MoveCommand)
        {
            Remove(input);
        }
        // Preserve RDF node identity while changing only graph membership.
        AddBounded(mutations, input.Select(quad => (RdfMutation)new AssertRdfQuad(quad with { Graph = destination })));
        return mutations;
    }
}
