using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    internal static string PrepareParameterized(string source, IReadOnlyDictionary<string, JsonElement>? parameters)
        => PrepareParameterized(source, parameters, out _);

    internal static string PrepareParameterized(string source, IReadOnlyDictionary<string, JsonElement>? parameters,
        out bool omitsResult)
    {
        if (PreparedSources.TryGet(source, out var prepared))
        {
            omitsResult = prepared.OmitsResult;
            return prepared.Source;
        }
        string result = PrepareParameterizedCore(source, parameters, out omitsResult, out bool reusable);
        if (reusable)
        {
            PreparedSources.Store(source, new(result, omitsResult));
        }
        return result;
    }

    private static string PrepareParameterizedCore(string source, IReadOnlyDictionary<string, JsonElement>? parameters,
        out bool omitsResult, out bool reusable)
    {
        reusable = true;
        if (!source.Contains('$'))
        {
            return Prepare(source, out omitsResult);
        }
        var root = GqlSyntaxParser.Parse(source, out var tokens);
        string original = source;
        // Repeated path expansion needs its selector count before lowering.
        // Bind only these grammar positions, preserving value parameters and
        // identifiers for their normal compiler handling.
        var counts = Descendants<GQLParser.NumberOfPathsContext>(root).Cast<ParserRuleContext>()
            .Concat(Descendants<GQLParser.NumberOfGroupsContext>(root))
            .Where(count => count.GetText().StartsWith('$')).OrderByDescending(count => count.Start.StartIndex);
        foreach (var count in counts)
        {
            // Selector values alter lowering. Never share these preparations
            // across parameter values, or retain a request's parameter objects.
            reusable = false;
            string name = GraphGqlIdentifiers.DecodeIdentifier(count.GetText()[1..]);
            if (parameters is null || !parameters.TryGetValue(name, out var value))
            {
                throw new ArgumentException($"Missing GQL parameter '${name}'.");
            }
            string literal = value.GetRawText();
            if (literal.Length > GraphGqlQueryCompiler.MaximumQueryCharacters)
            {
                throw new ArgumentException("The path-count parameter exceeds the query character limit.");
            }
            if (value.ValueKind != JsonValueKind.Number || !BigInteger.TryParse(literal,
                NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) || number.Sign < 0)
            {
                var failure = new ArgumentException("Path selection requires a non-negative integer number of paths or groups.");
                failure.Data["EntityForge.GqlStatus"] = "22G0F";
                throw failure;
            }
            source = source.Remove(count.Start.StartIndex, count.Stop.StopIndex - count.Start.StartIndex + 1)
                .Insert(count.Start.StartIndex, number.ToString(CultureInfo.InvariantCulture));
        }
        // Reuse only a tree whose offsets still describe the complete source.
        // Selector substitution changes that source and must be parsed again.
        return source == original ? Prepare(source, root, tokens, out omitsResult) : Prepare(source, out omitsResult);
    }
}
