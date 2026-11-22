using System.Text;
using System.Text.RegularExpressions;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private sealed record QueryDataset(string Query, IReadOnlyList<RdfIri> DefaultGraphs, IReadOnlyList<RdfIri> NamedGraphs);

    private static QueryDataset? ParseDataset(string query)
    {
        var (body, prefixes) = ParseProlog(query);
        var defaults = new List<RdfIri>();
        var named = new List<RdfIri>();
        var cleaned = new StringBuilder();
        int copied = 0;
        int braces = 0;
        int parentheses = 0;
        bool quoted = false;
        bool iri = false;
        bool construct = Regex.IsMatch(body, @"^\s*CONSTRUCT\s*\{", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        bool templateClosed = false;
        for (int index = 0; index < body.Length; index++)
        {
            char character = body[index];
            if (quoted)
            {
                if (character == '\\')
                {
                    index++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }
                continue;
            }
            if (iri)
            {
                if (character == '>')
                {
                    iri = false;
                }
                continue;
            }
            if (character == '"')
            {
                quoted = true;
                continue;
            }
            // Probe the remaining span without copying every suffix: many
            // comparison signs or malformed openers must stay cheap to scan.
            if (character == '<' && Regex.IsMatch(body.AsSpan(index), "^<[^<>\\\"{}|^`\\u0000-\\u0020]*>"))
            {
                iri = true;
                continue;
            }
            // Names are indivisible lexical tokens. A hyphen before FROM is
            // legal inside a prefixed name; escaped punctuation in that name
            // must not change the query's nesting depth either.
            var nameToken = DatasetNameToken().Match(body, index);
            if (nameToken.Success)
            {
                index += nameToken.Length - 1;
                continue;
            }
            if (character == '(')
            {
                parentheses++;
            }
            if (character == ')')
            {
                parentheses--;
            }
            if (character == '{')
            {
                if (braces == 0 && parentheses == 0 && (!construct || templateClosed))
                {
                    break;
                }
                braces++;
            }
            if (character == '}')
            {
                braces--;
                templateClosed = braces == 0;
            }
            if (braces != 0 || parentheses != 0 || index > 0 && (Char.IsLetterOrDigit(body[index - 1]) || "_?:.$".Contains(body[index - 1])))
            {
                continue;
            }
            var clause = DatasetClause().Match(body, index);
            if (!clause.Success || clause.Index != index)
            {
                continue;
            }
            if (copied != 0 && !String.IsNullOrWhiteSpace(body[copied..index]))
            {
                throw new InvalidDataException("Dataset clauses must be adjacent and precede the WHERE group.");
            }
            if (copied == 0)
            {
                string header = body[..index].Trim();
                if (header.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    // SELECT and its modifier can abut a variable, wildcard
                    // or parenthesis. Use the same tokens as ordinary SELECT
                    // parsing rather than imposing whitespace on the header.
                    string[] headerTokens = Tokenize(header, prefixes);
                    int headerIndex = 0;
                    RequireToken(headerTokens, ref headerIndex, "SELECT");
                    if (headerIndex < headerTokens.Length && headerTokens[headerIndex].ToUpperInvariant() is "DISTINCT" or "REDUCED")
                    {
                        headerIndex++;
                    }
                    _ = ParseSelectProjection(String.Join(" ", headerTokens[headerIndex..]), prefixes);
                }
                else if (header.StartsWith("DESCRIBE", StringComparison.OrdinalIgnoreCase))
                {
                    // DESCRIBE is handled by the outer query-form parser;
                    // this tokenizer consumes its target expressions only.
                    string[] targets = Tokenize(header["DESCRIBE".Length..].Trim(), prefixes);
                    bool wildcard = targets.Length == 1 && targets[0] == "*";
                    if (!wildcard && (targets.Length == 0 || targets.Any(target =>
                        !target.StartsWith('?') && !(target.StartsWith('<') && target.EndsWith('>')))))
                    {
                        throw new InvalidDataException("DESCRIBE dataset clauses must follow its targets and precede solution modifiers.");
                    }
                }
                else if (header.StartsWith("ASK", StringComparison.OrdinalIgnoreCase) && !header.Equals("ASK", StringComparison.OrdinalIgnoreCase) ||
                    construct && !header.EndsWith('}'))
                {
                    throw new InvalidDataException("Dataset clauses must precede WHERE.");
                }
            }
            string[] tokens = Tokenize(clause.Groups["graph"].Value, prefixes);
            int position = 0;
            if (tokens.Length != 1 || ParsePatternTerm(tokens, ref position).Constant is not RdfIri graph)
            {
                throw new InvalidDataException("A dataset clause requires an IRI.");
            }
            (clause.Groups["named"].Success ? named : defaults).Add(graph);
            if (defaults.Count + named.Count > 16)
            {
                throw new RdfSparqlWorkLimitException("A query accepts at most 16 dataset clauses.");
            }
            cleaned.Append(body, copied, index - copied).Append(' ');
            index += clause.Length - 1;
            copied = index + 1;
        }
        if (defaults.Count + named.Count == 0)
        {
            return null;
        }
        // DESCRIBE's WHERE clause is optional. Query-level VALUES can follow
        // its dataset directly, even when no solution modifier is present.
        bool describeWithoutGroup = body.StartsWith("DESCRIBE", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(body[copied..], @"^\s*(?:(?:GROUP|HAVING|ORDER|LIMIT|OFFSET|VALUES)\b|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!describeWithoutGroup && !Regex.IsMatch(body[copied..], @"^\s*(?:WHERE\s*)?\{", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("A dataset clause must be followed by its query group.");
        }
        cleaned.Append(body, copied, body.Length - copied);
        var prolog = new StringBuilder();
        if (prefixes.TryGetValue("$base", out string? baseIri))
        {
            prolog.Append("BASE <").Append(baseIri).Append("> ");
        }
        foreach (var prefix in prefixes.Where(pair => pair.Key != "$base"))
        {
            prolog.Append("PREFIX ").Append(prefix.Key).Append(": <").Append(prefix.Value).Append("> ");
        }
        return new(prolog.Append(cleaned).ToString(), defaults.Distinct().ToArray(), named.Distinct().ToArray());
    }

    private static RdfSparqlReadView DatasetView(RdfSparqlReadView source, QueryDataset dataset)
    {
        var relation = new HashSet<RdfQuad>();
        int examined = 0;
        foreach (var graph in dataset.DefaultGraphs.Concat(dataset.NamedGraphs).Distinct())
        {
            bool asDefault = dataset.DefaultGraphs.Contains(graph);
            bool asNamed = dataset.NamedGraphs.Contains(graph);
            var blanks = new Dictionary<RdfBlankNode, RdfBlankNode>();
            RdfTerm MergeTerm(RdfTerm term) => term switch
            {
                RdfBlankNode blank => blanks.TryGetValue(blank, out var mapped) ? mapped : blanks[blank] = FreshBlankNode(),
                RdfTripleTerm triple => new RdfTripleTerm(MergeTerm(triple.Subject), triple.Predicate, MergeTerm(triple.Object)),
                _ => term
            };
            foreach (var quad in source.Find(new(Graph: graph)))
            {
                source.CheckCancellation();
                if (++examined > MaximumPathExpansions)
                {
                    throw new RdfSparqlWorkLimitException("Dataset selection exceeded 100,000 source quads.");
                }
                if (asDefault)
                {
                    relation.Add(new(MergeTerm(quad.Subject), quad.Predicate, MergeTerm(quad.Object)));
                }
                if (asNamed)
                {
                    relation.Add(quad);
                }
            }
        }
        // Materializing a selected dataset changes its graph view, not the
        // identity of the local snapshot from which those statements were read.
        return new(new RelationRdfSparqlDatasetSnapshot(relation.ToArray(), source.Version), source.Cancellation)
        {
            Services = source.Services
        };
    }

    // IRIREF ends at '>'; the next keyword needs no whitespace separator.
    // Prefixed names still require their lexical boundary (longest match).
    [GeneratedRegex(@"\GFROM\b\s*(?:(?<named>NAMED)\b\s*)?(?<graph><[^>]*>|" + QualifiedName + @"(?=\s|\{|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DatasetClause();

    [GeneratedRegex(@"\G(?:\?" + VariableName + "|" + QualifiedName + ")", RegexOptions.CultureInvariant)]
    private static partial Regex DatasetNameToken();
}
