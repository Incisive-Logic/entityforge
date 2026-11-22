using EntityForge.Db.Rdf.Contracts;
using System.Text.RegularExpressions;
using System.Text;

namespace EntityForge.Db.Rdf;

internal sealed record RdfModifyPlan(string Prolog, string Query, string? Delete, string? Insert,
    RdfIri? WithGraph, bool WithDefault);

public static partial class RdfSparql
{
    internal static (string Template, string Where) SeparateModifyWhere(string update)
    {
        var (body, _) = ParseProlog(update);
        int depth = 0;
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
            else if (depth == 0 && token.Value.Equals("WHERE", StringComparison.OrdinalIgnoreCase))
            {
                string group = body[(token.Index + token.Length)..].Trim();
                if (!group.StartsWith('{'))
                {
                    throw new InvalidDataException("A modification requires a WHERE group.");
                }
                int groupDepth = 0;
                bool closed = false;
                foreach (Match item in Token().Matches(group))
                {
                    if (item.Value == "{")
                    {
                        groupDepth++;
                    }
                    else if (item.Value == "}" && --groupDepth == 0)
                    {
                        closed = String.IsNullOrWhiteSpace(group[(item.Index + item.Length)..]);
                        break;
                    }
                }
                if (!closed)
                {
                    throw new InvalidDataException("A modification WHERE group cannot have trailing clauses.");
                }
                return (CanonicalUpdateProlog(update) + body[..token.Index] + " WHERE {}",
                    group);
            }
        }
        throw new InvalidDataException("A modification requires a WHERE group.");
    }

    internal static string CanonicalUpdateProlog(string prolog)
    {
        // Update sequences inherit earlier declarations and may replace their
        // bindings. Canonicalize that scope before parsing generated queries.
        var (_, prefixes) = ParseProlog(prolog, allowPrefixReplacement: true);
        var result = new StringBuilder();
        if (prefixes.TryGetValue("$base", out var baseIri))
        {
            result.Append("BASE <").Append(baseIri).Append("> ");
        }
        foreach (var pair in prefixes.Where(pair => pair.Key != "$base"))
        {
            result.Append("PREFIX ").Append(pair.Key).Append(": <").Append(pair.Value).Append("> ");
        }
        return result.ToString();
    }

    internal static (string Source, RdfIri? Target) SeparateModifyWith(string update)
    {
        var (body, prefixes) = ParseProlog(update);
        var with = Regex.Match(body, @"^\s*WITH\s+(?<graph><[^>]*>|" + QualifiedName + @")(?=\s)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!with.Success)
        {
            return (update, null);
        }
        var target = ParseTerm(ExpandPrefixedName(with.Groups["graph"].Value, prefixes)) as RdfIri ??
            throw new InvalidDataException("WITH requires an IRI graph name.");
        var source = new StringBuilder();
        if (prefixes.TryGetValue("$base", out var baseIri))
        {
            source.Append("BASE <").Append(baseIri).Append("> ");
        }
        foreach (var pair in prefixes.Where(pair => pair.Key != "$base"))
        {
            source.Append("PREFIX ").Append(pair.Key).Append(": <").Append(pair.Value).Append("> ");
        }
        return (source.Append(body[with.Length..]).ToString(), target);
    }

    internal static string? NormalizeSingleTemplateUsing(string update)
    {
        var (body, prefixes) = ParseProlog(update);
        string[] tokens = Tokenize(body, prefixes);
        if (tokens.Length < 4 || !(tokens[0].Equals("DELETE", StringComparison.OrdinalIgnoreCase) ||
            tokens[0].Equals("INSERT", StringComparison.OrdinalIgnoreCase)) || tokens[1] != "{")
        {
            return null;
        }
        int depth = 0;
        for (int i = 1; i < tokens.Length - 1; i++)
        {
            if (tokens[i] == "{")
            {
                depth++;
            }
            else if (tokens[i] == "}" && --depth == 0)
            {
                if (!tokens[i + 1].Equals("USING", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                string prolog = prefixes.TryGetValue("$base", out var baseIri) ? "BASE <" + baseIri + "> " : "";
                // The grammar parser mishandles USING in its delete-only branch
                // and in its insert-only branch when BASE is present. Empty
                // complementary templates select the equivalent combined form.
                return tokens[0].Equals("INSERT", StringComparison.OrdinalIgnoreCase)
                    ? prolog + " DELETE {} " + String.Join(" ", tokens)
                    : prolog + String.Join(" ", tokens[..(i + 1)]) + " INSERT {} " + String.Join(" ", tokens[(i + 1)..]);
            }
        }
        return null;
    }

    internal static IReadOnlyList<RdfMutation> ExecuteModify(RdfDatabaseEngine database, RdfModifyPlan plan,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        using var view = new RdfSparqlReadView(database, database.AcquireSnapshot(), null, cancellation);
        return ExecuteModify(view, plan);
    }

    internal static IReadOnlyList<RdfMutation> ExecuteModify(IReadOnlyList<RdfQuad> relation, RdfModifyPlan plan, CancellationToken token)
    {
        using var view = new RdfSparqlReadView(null, null, relation, token);
        return ExecuteModify(view, plan);
    }

    private static IReadOnlyList<RdfMutation> ExecuteModify(RdfSparqlReadView source, RdfModifyPlan plan)
    {
        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows;
        if (plan.WithDefault)
        {
            var named = source.Find(new()).Where(quad => quad.Graph is not null).ToArray();
            if (named.Length > MaximumPathExpansions)
            {
                throw new RdfSparqlWorkLimitException("WITH dataset selection exceeds 100000 named facts.");
            }
            using var selected = new RdfSparqlReadView(null, null, named.Concat(named.Where(quad => quad.Graph == plan.WithGraph)
                .Select(quad => quad with { Graph = null })).ToArray(), source.Cancellation);
            rows = Execute(selected, plan.Query);
        }
        else
        {
            rows = Execute(source, plan.Query);
        }
        return InstantiateModify(rows, plan, source.Cancellation);
    }

    internal static async ValueTask<IReadOnlyList<RdfMutation>> ExecuteModifyAsync(IReadOnlyList<RdfQuad> relation,
        RdfModifyPlan plan, RdfSparqlHttpResources? resources, CancellationToken cancellation)
    {
        if (plan.WithDefault)
        {
            var named = relation.Where(quad => quad.Graph is not null).ToArray();
            relation = named.Concat(named.Where(quad => quad.Graph == plan.WithGraph).Select(quad => quad with { Graph = null })).ToArray();
        }
        var result = (RdfSparqlSelectResult)await ExecuteRelationAsync(relation, plan.Query, resources, cancellation).ConfigureAwait(false);
        return InstantiateModify(result.Bindings, plan, cancellation);
    }

    private static IReadOnlyList<RdfMutation> InstantiateModify(IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows,
        RdfModifyPlan plan, CancellationToken cancellation)
    {
        // Both templates consume the very same solutions, including UUID/BNODE,
        // RAND and query-clock values produced by the WHERE expression.
        var mutations = new List<RdfMutation>();
        var (_, prefixes) = ParseProlog(plan.Prolog);
        void Add(string? template, bool insert)
        {
            if (template is null)
            {
                return;
            }
            var parsed = ParseConstructTemplate(template, prefixes, allowNamedGraphs: true);
            if (!insert && parsed.BlankVariables.Length != 0)
            {
                throw new InvalidDataException("DELETE templates cannot contain blank nodes.");
            }
            foreach (var quad in InstantiateTemplate(rows, parsed.Patterns, parsed.BlankVariables, plan.WithGraph))
            {
                cancellation.ThrowIfCancellationRequested();
                mutations.Add(insert ? new AssertRdfQuad(quad) : new RetractRdfQuad(quad));
                if (mutations.Count > RdfSparqlUpdate.MaximumMutations)
                {
                    throw new RdfSparqlWorkLimitException("SPARQL Update exceeds its atomic mutation limit.");
                }
            }
        }
        Add(plan.Delete, false);
        Add(plan.Insert, true);
        return mutations;
    }
}
