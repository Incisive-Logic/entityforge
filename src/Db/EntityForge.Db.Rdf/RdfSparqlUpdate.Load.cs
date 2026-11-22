using System.Text.RegularExpressions;
using EntityForge.Db.Rdf.Contracts;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Update;
using VDS.RDF.Update.Commands;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparqlUpdate
{
    internal static IEnumerable<(string Scope, string Source, bool Silent, RdfIri? Graph)> ParseLoads(string update, string blankNodeScope)
    {
        ValidateSequenceLength(update);
        var operations = SplitOperations(update);
        for (int index = 0; index < operations.Length; index++)
        {
            if (!Regex.IsMatch(operations[index], UpdateProlog + @"\s*LOAD\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                continue;
            }
            SparqlUpdateCommandSet commands;
            IReadOnlyDictionary<string, string> iris;
            try
            {
                var bound = RdfSparql.BindUpdateIris(operations[index]);
                iris = bound.Iris;
                commands = ParseCommandSet(bound.Source);
            }
            catch (Exception failure) when (failure is RdfException or SparqlUpdateException)
            {
                throw new InvalidDataException("Invalid SPARQL LOAD syntax.", failure);
            }
            if (commands.CommandCount != 1 || commands.Commands.Single() is not LoadCommand load)
            {
                throw new InvalidDataException("Invalid SPARQL LOAD command.");
            }
            string scope = operations.Length == 1 ? blankNodeScope : blankNodeScope + "-operation-" + index;
            RdfIri? graph = load.TargetGraphName switch
            {
                null => null,
                IUriNode name => new(iris.GetValueOrDefault(name.Uri.OriginalString, name.Uri.OriginalString)),
                _ => throw new InvalidDataException("LOAD requires an IRI target graph.")
            };
            string source = iris.GetValueOrDefault(load.SourceUri.OriginalString, load.SourceUri.OriginalString);
            yield return (scope, source, load.Silent, graph);
        }
    }

    internal static async ValueTask<IReadOnlyDictionary<string, IReadOnlyList<RdfMutation>>> PrepareLoadsAsync(
        string update, string blankNodeScope, RdfSparqlHttpResources? resources, CancellationToken cancellation)
    {
        var prepared = new Dictionary<string, IReadOnlyList<RdfMutation>>(StringComparer.Ordinal);
        foreach (var (scope, source, silent, graph) in ParseLoads(update, blankNodeScope))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if (resources is null)
                {
                    throw new InvalidDataException("SPARQL LOAD requires configured HTTP resources.");
                }
                var loaded = await resources.LoadAsync(new Uri(source, UriKind.Absolute), scope + "-load", cancellation).ConfigureAwait(false);
                prepared.Add(scope, loaded.Select(quad => (RdfMutation)new AssertRdfQuad(new(quad.Subject, quad.Predicate, quad.Object, graph))).ToArray());
            }
            catch (Exception failure) when (!cancellation.IsCancellationRequested &&
                failure is HttpRequestException or IOException or InvalidDataException or RdfException or ArgumentException or UriFormatException or OperationCanceledException &&
                failure is not RdfSparqlWorkLimitException)
            {
                if (!silent)
                {
                    throw new RdfSparqlRemoteExecutionException("LOAD", failure);
                }
                prepared.Add(scope, []);
            }
        }
        return prepared;
    }
}
