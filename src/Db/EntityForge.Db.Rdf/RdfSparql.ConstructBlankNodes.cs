using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    internal static void ValidateDeleteTemplate(string prolog, string template)
    {
        var (_, prefixes) = ParseProlog(prolog + " SELECT * {}");
        if (ParseConstructTemplate(template, prefixes, allowNamedGraphs: true).BlankVariables.Length != 0)
        {
            throw new InvalidDataException("A DELETE template cannot contain blank-node labels.");
        }
    }

    private static (PatternElement[] Patterns, string[] BlankVariables) ParseConstructTemplate(
        string template, IReadOnlyDictionary<string, string> prefixes, bool allowNamedGraphs = false)
    {
        string[] tokens = Tokenize(template, prefixes);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < tokens.Length; index++)
        {
            if (!tokens[index].StartsWith("_:", StringComparison.Ordinal) && tokens[index] != "[]")
            {
                continue;
            }
            string label = tokens[index] == "[]" ? "$anonymous" + index : tokens[index];
            if (!labels.TryGetValue(label, out string? variable))
            {
                // '$' cannot occur in a user variable in the bounded parser.
                variable = "$constructBlank" + labels.Count;
                labels.Add(label, variable);
            }
            tokens[index] = "?" + variable;
        }
        int position = 0;
        Group parsed = ParseGroup(tokens, ref position, null, false, 0);
        if (position != tokens.Length || CountPatterns(parsed) > MaximumPatterns)
        {
            throw new InvalidDataException("The CONSTRUCT template exceeds its pattern bounds.");
        }
        PatternElement[] patterns = ConstructPatterns(parsed, allowNamedGraphs).ToArray();
        var blankVariables = labels.Values.ToHashSet(StringComparer.Ordinal);
        void Collect(TermPattern? term)
        {
            if (term?.Variable is { } variable && variable.StartsWith("$structure:", StringComparison.Ordinal))
            {
                blankVariables.Add(variable);
            }
            if (term?.Quoted is { } triple)
            {
                Collect(triple.Subject);
                Collect(triple.Object);
            }
        }
        foreach (var pattern in patterns)
        {
            Collect(pattern.Pattern.Subject);
            Collect(pattern.Pattern.Object);
            if (pattern.Pattern.Predicate?.Variable is { } predicate && blankVariables.Contains(predicate) ||
                pattern.Pattern.Graph?.Variable is { } graph && blankVariables.Contains(graph))
            {
                throw new InvalidDataException("A template blank node cannot name a predicate or graph.");
            }
        }
        return (patterns, blankVariables.ToArray());
    }
}
