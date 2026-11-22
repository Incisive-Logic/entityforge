using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    internal static RdfQuadPattern? QueryUpdateAnchor(string query)
    {
        if (ParseDataset(query) is not null)
        {
            return null;
        }
        var body = GetSelectPlan(query).Body;
        if (body.Filters.Count != 0 || body.Elements.Count == 0 || body.Elements.Any(e => e is not PatternElement))
        {
            return null;
        }

        var patterns = body.Elements.Cast<PatternElement>().Select(e => e.Pattern).ToArray();
        string? subject = patterns[0].Subject.Variable;
        if (subject is null || patterns.Any(p => p.Subject.Variable != subject || p.Subject.Quoted is not null ||
            p.Subject.Constant is not null || p.Path is not null || p.Graph is not null ||
            p.Predicate?.Constant is not RdfIri || p.Object.Quoted is not null))
        {
            return null;
        }

        var anchor = patterns.Where(p => p.Object.Constant is not null)
            .OrderBy(p => p.Object.Constant is RdfLiteral ? 0 : 1).FirstOrDefault();
        return anchor is null ? null : new(Predicate: (RdfIri)anchor.Predicate!.Constant!, Object: anchor.Object.Constant, MatchDefaultGraphOnly: true);
    }
}

public static partial class RdfSparqlUpdate
{
    internal static RdfQuadPattern? QueryUpdateAnchor(string update)
    {
        if (SplitOperations(update).Length > 1)
        {
            return null;
        }
        var match = ModifyShape().Match(update);
        if (!match.Success)
        {
            return null;
        }

        return RdfSparql.QueryUpdateAnchor($"{match.Groups["prolog"].Value} SELECT * WHERE {{ {match.Groups["where"].Value} }}");
    }
}
