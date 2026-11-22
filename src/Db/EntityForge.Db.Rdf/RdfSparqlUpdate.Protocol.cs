using System.Text.RegularExpressions;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparqlUpdate
{
    internal static string WithProtocolDataset(string update, IReadOnlyList<string> defaults, IReadOnlyList<string> named)
    {
        ValidateSequenceLength(update);
        if (defaults.Count + named.Count == 0)
        {
            return update;
        }
        if (defaults.Count + named.Count > 16)
        {
            throw new RdfSparqlWorkLimitException("An update accepts at most 16 dataset parameters.");
        }
        string Clause(string value, bool isNamed)
        {
            return (isNamed ? " USING NAMED " : " USING ") + RdfSyntax.FormatTerm(new RdfIri(value));
        }
        string clauses = String.Concat(defaults.Select(value => Clause(value, false))) +
            String.Concat(named.Select(value => Clause(value, true))) + " ";
        string result = String.Join(";\n", SplitOperations(update).Select(operation => RdfSparql.ApplyProtocolUpdateDataset(operation, clauses)));
        ValidateSequenceLength(result);
        return result;
    }
}

public static partial class RdfSparql
{
    internal static string ApplyProtocolUpdateDataset(string operation, string clauses)
    {
        var (body, _) = ParseProlog(operation);
        if (Regex.IsMatch(body, @"^\s*WITH\b(?!:)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Protocol update dataset parameters cannot accompany WITH or USING clauses.");
        }
        int depth = 0;
        Match? where = null;
        foreach (Match token in Token().Matches(body))
        {
            if (token.Value == "{")
            {
                depth++;
            }
            else if (token.Value == "}")
            {
                depth--;
            }
            else if (depth == 0 && token.Value.Equals("USING", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Protocol update dataset parameters cannot accompany WITH or USING clauses.");
            }
            else if (depth == 0 && token.Value.Equals("WHERE", StringComparison.OrdinalIgnoreCase))
            {
                where = token;
            }
        }
        if (where is null)
        {
            return operation;
        }
        string prolog = CanonicalUpdateProlog(operation);
        if (Regex.IsMatch(body[..where.Index], @"^\s*DELETE\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            try
            {
                // Validate the restricted QuadPattern before expanding the shorthand.
                RdfSparqlUpdate.ParseCommandSet(BindUpdateIris(operation).Source);
            }
            catch (Exception exception) when (exception is VDS.RDF.RdfException or VDS.RDF.Update.SparqlUpdateException)
            {
                throw new InvalidDataException("DELETE WHERE syntax is invalid.", exception);
            }
            string pattern = body[(where.Index + where.Length)..];
            return prolog + " DELETE " + pattern + clauses + " WHERE " + pattern;
        }
        return prolog + body[..where.Index] + clauses + body[where.Index..];
    }
}
