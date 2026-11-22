using EntityForge.Db.Rdf.Contracts;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Update;
using VDS.RDF.Update.Commands;
using System.Text.RegularExpressions;
using EntityForge.Db;

namespace EntityForge.Db.Rdf;

/// <summary>Reports the durable result of one bounded SPARQL Update transaction.</summary>
/// <param name="Version">Committed RDF database version.</param>
/// <param name="MutationCount">Number of materialized RDF mutations in the update.</param>
public sealed record RdfSparqlUpdateCommit(DatabaseVersion Version, int MutationCount);

/// <summary>Compiles a deliberately bounded SPARQL Update request into native atomic RDF mutations.</summary>
public static partial class RdfSparqlUpdate
{
    // A caller controls the operation ID and may already have stored native
    // blank identifiers derived from it. Generate an independent namespace;
    // successful local receipts and distributed plans retain the resulting IDs.
    internal static string NewBlankNodeScope() => "update" + Guid.NewGuid().ToString("N");

    /// <summary>Maximum update commands accepted in one atomic request.</summary>
    public const int MaximumCommands = 16;

    /// <summary>Maximum mutations accepted in one atomic request.</summary>
    public const int MaximumMutations = 4_096;

    /// <summary>Maximum characters accepted in one update request.</summary>
    public const int MaximumCharacters = 1_048_576;

    /// <summary>Parses supported data commands without executing arbitrary dotNetRDF update operations.</summary>
    public static IReadOnlyList<RdfMutation> Parse(string update, string blankNodeScope)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(blankNodeScope);
        if (update.Length > MaximumCharacters)
        {
            throw new RdfSparqlWorkLimitException($"SPARQL Update exceeded {MaximumCharacters} characters.");
        }

        string[] operations = SplitOperations(update);
        if (operations.Length == 0)
        {
            return [];
        }
        foreach (string operation in operations)
        {
            var header = Regex.Match(operation, UpdateProlog + @"\s*(?<command>WITH|CLEAR|ADD|COPY|MOVE|CREATE|DROP|LOAD|INSERT|DELETE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            bool dataCommand = header.Groups["command"].Value.ToUpperInvariant() is "INSERT" or "DELETE" &&
                Regex.IsMatch(operation[header.Length..], "^" + UpdateWhitespace + @"DATA\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (header.Success && !dataCommand)
            {
                throw new NotSupportedException("This update requires dataset-aware evaluation.");
            }
        }

        if (operations.Length > 1)
        {
            var sequence = new List<RdfMutation>();
            for (int index = 0; index < operations.Length; index++)
            {
                AddBounded(sequence, Parse(operations[index], blankNodeScope + "-operation-" + index));
            }
            return sequence;
        }

        SparqlUpdateCommandSet commands;
        IReadOnlyDictionary<string, string> iris;
        try
        {
            var bound = RdfSparql.BindUpdateIris(operations[0], data: true);
            iris = bound.Iris;
            commands = ParseCommandSet(bound.Source);
        }
        catch (Exception exception) when (exception is RdfException or SparqlUpdateException)
        {
            throw new InvalidDataException("SPARQL Update syntax is invalid.", exception);
        }
        if (commands.CommandCount is 0 or > MaximumCommands)
        {
            throw new RdfSparqlWorkLimitException(
                $"SPARQL Update requires 1-{MaximumCommands} commands.");
        }

        var mutations = new List<RdfMutation>();
        foreach (SparqlUpdateCommand command in commands.Commands)
        {
            bool assertion;
            InsertDataCommand materialized;
            switch (command)
            {
                case InsertDataCommand insert:
                    assertion = true;
                    materialized = insert;
                    break;
                case DeleteDataCommand delete:
                    assertion = false;
                    materialized = new InsertDataCommand(delete.DataPattern);
                    break;
                default:
                    throw new NotSupportedException(
                        $"SPARQL Update command '{command.CommandType}' is outside the bounded update surface.");
            }
            foreach (RdfQuad quad in Materialize(materialized, blankNodeScope, iris))
            {
                mutations.Add(assertion ? new AssertRdfQuad(quad) : new RetractRdfQuad(quad));
                if (mutations.Count > MaximumMutations)
                {
                    throw new RdfSparqlWorkLimitException(
                        $"SPARQL Update exceeded {MaximumMutations} mutations.");
                }
            }
        }
        return mutations;
    }

    internal static IReadOnlyList<RdfMutation> ParseWhere(RdfDatabaseEngine database, string update,
        string blankNodeScope, IReadOnlyDictionary<string, IReadOnlyList<RdfMutation>>? loads = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        cancellation.ThrowIfCancellationRequested();
        ValidateSequenceLength(update);
        string[] operations = SplitOperations(update);
        if (operations.Length == 0)
        {
            return [];
        }
        if (operations.Length > 1)
        {
            using var snapshot = database.AcquireSnapshot();
            var relation = database.ReadSparqlPrefix(new(), snapshot, MaximumSequenceQuads + 1, false, cancellation);
            return CompileSequence(relation, operations, blankNodeScope, cancellation, loads);
        }
        using var view = new NativeRdfSparqlDatasetSnapshot(database, database.AcquireSnapshot());
        return ParseWhereCore(view, plan => RdfSparql.ExecuteModify(database, plan, cancellation), operations[0], blankNodeScope, loads, cancellation);
    }

    internal static IReadOnlyList<RdfMutation> ParseWhere(IReadOnlyList<RdfQuad> relation, string update,
        string blankNodeScope, CancellationToken token, IReadOnlyDictionary<string, IReadOnlyList<RdfMutation>>? loads = null)
    {
        ValidateSequenceLength(update);
        string[] operations = SplitOperations(update);
        if (operations.Length == 0)
        {
            return [];
        }
        if (operations.Length > 1)
        {
            return CompileSequence(relation, operations, blankNodeScope, token, loads);
        }
        using var view = new RelationRdfSparqlDatasetSnapshot(relation);
        return ParseWhereCore(view,
            plan => RdfSparql.ExecuteModify(relation, plan, token), operations[0], blankNodeScope, loads, token);
    }

    private static IReadOnlyList<RdfMutation> ParseWhereCore(RdfSparqlDatasetSnapshot dataset,
        Func<RdfModifyPlan, IReadOnlyList<RdfMutation>> modify, string update, string blankNodeScope,
        IReadOnlyDictionary<string, IReadOnlyList<RdfMutation>>? loads = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(blankNodeScope);
        if (update.Length > MaximumCharacters)
        {
            throw new RdfSparqlWorkLimitException($"SPARQL Update exceeded {MaximumCharacters} characters.");
        }

        if (Regex.IsMatch(update, UpdateProlog + @"\s*(?:INSERT|DELETE)" + UpdateWhitespace + @"DATA\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return Parse(update, blankNodeScope);
        }

        if (loads is not null && loads.TryGetValue(blankNodeScope, out var loaded))
        {
            return loaded;
        }
        if (TryGraphManagement(dataset, update, cancellation) is { } management)
        {
            cancellation.ThrowIfCancellationRequested();
            return management;
        }

        Match shorthand = DeleteWhereShape().Match(update);
        if (shorthand.Success)
        {
            // Validate QuadPattern syntax (in particular, no filters or blank nodes)
            // before reusing the native template evaluator. Never execute the
            // reference parser's update commands against a secondary store.
            try
            {
                var parsed = ParseCommandSet(RdfSparql.BindUpdateIris(update).Source);
                if (parsed.CommandCount != 1 || parsed.Commands.Single() is not DeleteCommand)
                {
                    throw new InvalidDataException("DELETE WHERE requires one quad pattern.");
                }
            }
            catch (Exception exception) when (exception is RdfException or SparqlUpdateException)
            {
                throw new InvalidDataException("DELETE WHERE syntax is invalid.", exception);
            }
            string prolog = shorthand.Groups["prolog"].Value;
            string where = shorthand.Groups["where"].Value;
            return modify(ParseModify(prolog + " DELETE { " + where + " } WHERE { " + where + " }"));
        }

        return modify(ParseModify(update));
    }

    private static void AddBounded(List<RdfMutation> target, IEnumerable<RdfMutation> source)
    {
        foreach (RdfMutation mutation in source)
        {
            target.Add(mutation);
            if (target.Count > MaximumMutations)
            {
                throw new RdfSparqlWorkLimitException($"SPARQL Update exceeded {MaximumMutations} mutations.");
            }
        }
    }

    private static IEnumerable<RdfQuad> Materialize(InsertDataCommand command, string blankNodeScope,
        IReadOnlyDictionary<string, string> iris)
    {
        RdfTerm Restore(INode node, bool predicate = false)
        {
            RdfTerm term = RdfSyntax.FromNode(node, blankNodeScope);
            return term switch
            {
                RdfIri { Value: RdfSparql.DataPredicateShorthandIri } => predicate
                    ? new RdfIri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type")
                    : throw new InvalidDataException("The shorthand 'a' is only permitted as a predicate."),
                RdfLiteral { Datatype: RdfSparql.DataPredicateShorthandIri } =>
                    throw new InvalidDataException("The shorthand 'a' cannot name a datatype."),
                RdfIri iri when iris.TryGetValue(iri.Value, out string? original) => new RdfIri(original),
                RdfLiteral literal when literal.Datatype is { } datatype && iris.TryGetValue(datatype, out string? original) =>
                    new RdfLiteral(literal.LexicalForm, original, literal.Language),
                _ => term
            };
        }
        var store = new TripleStore();
        store.ExecuteUpdate(new SparqlUpdateCommandSet(command));
        foreach (IGraph graph in store.Graphs)
        {
            RdfTerm? graphName = graph.Name is null ? null : Restore(graph.Name);
            foreach (Triple triple in graph.Triples)
            {
                yield return new RdfQuad(
                    Restore(triple.Subject),
                    (RdfIri)Restore(triple.Predicate, predicate: true),
                    Restore(triple.Object), graphName).Validate();
            }
        }
    }

    private const string UpdateWhitespaceUnit = @"(?:\s|#[^\r\n]*(?:\r\n|\r|\n|$))";
    private const string UpdateWhitespace = UpdateWhitespaceUnit + "*";

    private const string UpdateProlog = @"^(?<prolog>" + UpdateWhitespace + "(?:(?:PREFIX" + UpdateWhitespaceUnit + "+(?:" + RdfSparql.PrefixName + "|):" + UpdateWhitespace + "<[^>]*>|BASE" + UpdateWhitespace + "<[^>]*>)" + UpdateWhitespace + @")*)";

    [System.Text.RegularExpressions.GeneratedRegex(
        UpdateProlog +
        @"(?:(?:DELETE\s*\{(?<delete>.*?)\})\s*)?(?:(?:INSERT\s*\{(?<insert>.*?)\})\s*)?" +
        @"WHERE\s*\{(?<where>.*)\}\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ModifyShape();

    [GeneratedRegex(
        UpdateProlog +
        @"\s*DELETE\s+WHERE\s*\{(?<where>.*)\}\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DeleteWhereShape();

}
