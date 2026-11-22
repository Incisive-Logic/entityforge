using System.Text.RegularExpressions;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparqlUpdate
{
    private const int MaximumSequenceQuads = 100_000;

    private static void ValidateSequenceLength(string update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Length > MaximumCharacters)
        {
            throw new RdfSparqlWorkLimitException($"SPARQL Update exceeded {MaximumCharacters} characters.");
        }
    }

    private static string[] SplitOperations(string update)
    {
        update = RdfSparql.ExpandSyntaxCodepoints(update);
        if (Regex.IsMatch(update, UpdateProlog + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            _ = RdfSparql.CanonicalUpdateProlog(update);
            return [];
        }
        var parts = new List<string>();
        int start = 0, depth = 0;
        for (int i = 0; i < update.Length; i++)
        {
            char c = update[i];
            if (c == '\\' && i + 1 < update.Length && "_~.-!$&'()*+,;=/?#@%".Contains(update[i + 1]))
            {
                // PN_LOCAL_ESC punctuation belongs to the prefixed IRI. In
                // particular, escaped quotes, hashes and semicolons must not
                // start strings/comments or split graph-management commands.
                // The full parser still validates where these escapes occur.
                i++;
            }
            else if (c == '#')
            {
                while (i + 1 < update.Length && update[i + 1] is not ('\r' or '\n'))
                {
                    i++;
                }
            }
            else if (c is '\'' or '"')
            {
                int width = i + 2 < update.Length && update[i + 1] == c && update[i + 2] == c ? 3 : 1;
                string end = new(c, width);
                i += width;
                while (i < update.Length)
                {
                    if (update[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }
                    if (update.AsSpan(i).StartsWith(end, StringComparison.Ordinal))
                    {
                        i += width - 1;
                        break;
                    }
                    i++;
                }
            }
            else if (c == '<')
            {
                // Share the token boundary used by query preprocessing. A
                // comparison must not skip quotes or a following IRI opener,
                // otherwise later braces and operation separators are lost.
                int end = RdfSparql.FindLexicalIriEnd(update, i);
                if (end >= 0)
                {
                    i = end;
                }
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
            }
            else if (c == ';' && depth == 0)
            {
                parts.Add(update[start..i]);
                start = i + 1;
            }
        }
        string tail = update[start..];
        bool hasTrailingProlog = false;
        if (!Regex.IsMatch(tail, "^" + UpdateWhitespace + "$"))
        {
            hasTrailingProlog = Regex.IsMatch(tail, UpdateProlog + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            parts.Add(tail);
        }
        if (parts.Count == 0 || parts.Any(String.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("A SPARQL Update request must contain nonempty operations.");
        }
        if (parts.Count - (hasTrailingProlog ? 1 : 0) > MaximumCommands)
        {
            throw new RdfSparqlWorkLimitException($"SPARQL Update requires 1-{MaximumCommands} operations.");
        }
        string prolog = "";
        var dataLabels = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < parts.Count; i++)
        {
            string localProlog = Regex.Match(parts[i], UpdateProlog, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Groups["prolog"].Value;
            string body = parts[i][localProlog.Length..];
            if (Regex.IsMatch(body, "^" + UpdateWhitespace + "INSERT" + UpdateWhitespace + @"DATA\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                foreach (string label in RdfSparql.UpdateDataBlankLabels(body))
                {
                    if (!dataLabels.Add(label))
                    {
                        throw new InvalidDataException("A blank-node label cannot occur in different INSERT DATA operations in one request.");
                    }
                }
            }
            // Resolve declarations in order before handing a single canonical
            // prolog to the parser, which rejects repeated BASE declarations.
            prolog = RdfSparql.CanonicalUpdateProlog(prolog + "\n" + localProlog);
            if (Regex.IsMatch(body, "^" + UpdateWhitespace + "$"))
            {
                if (i != parts.Count - 1 || !hasTrailingProlog)
                {
                    throw new InvalidDataException("A declaration-only update tail cannot be followed by a semicolon.");
                }
                // Update permits a final Prologue without Update1. Validate it,
                // but do not count or execute it as an operation.
                parts.RemoveAt(i);
                break;
            }
            parts[i] = prolog + "\n" + body;
        }
        if (parts.Count > 1)
        {
            var whereLabels = new HashSet<string>(StringComparer.Ordinal);
            foreach (string operation in parts)
            {
                foreach (string label in RdfSparql.UpdateWhereBlankLabels(operation))
                {
                    if (!whereLabels.Add(label))
                    {
                        throw new InvalidDataException("A blank-node label cannot occur in different WHERE clauses in one update request.");
                    }
                }
            }
        }
        return parts.ToArray();
    }

    private static IReadOnlyList<RdfMutation> CompileSequence(IReadOnlyList<RdfQuad> relation,
        string[] operations, string blankNodeScope, CancellationToken token,
        IReadOnlyDictionary<string, IReadOnlyList<RdfMutation>>? loads = null)
    {
        if (relation.Count > MaximumSequenceQuads)
        {
            throw new RdfSparqlWorkLimitException("An update sequence exceeds 100,000 input quads.");
        }
        var working = relation.ToHashSet();
        var mutations = new List<RdfMutation>();
        for (int i = 0; i < operations.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<RdfMutation> next;
            string scope = blankNodeScope + "-operation-" + i;
            try
            {
                next = Parse(operations[i], scope);
            }
            catch (NotSupportedException)
            {
                var snapshot = working.ToArray();
                using var view = new RelationRdfSparqlDatasetSnapshot(snapshot);
                next = ParseWhereCore(view,
                    plan => RdfSparql.ExecuteModify(snapshot, plan, token), operations[i], scope, loads, token);
            }
            AddBounded(mutations, next);
            foreach (var mutation in next)
            {
                if (mutation is AssertRdfQuad added)
                {
                    working.Add(added.Quad);
                }
                else if (mutation is RetractRdfQuad removed)
                {
                    working.Remove(removed.Quad);
                }
            }
            if (working.Count > MaximumSequenceQuads)
            {
                throw new RdfSparqlWorkLimitException("An update sequence exceeds 100,000 intermediate quads.");
            }
        }
        return mutations;
    }
}
