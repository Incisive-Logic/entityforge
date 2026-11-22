using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static TermPattern ParseGraphNode(string[] tokens, ref int index, TermPattern? graph,
        PatternBlankScope blanks, List<Pattern> patterns, int depth)
    {
        if (index >= tokens.Length || tokens[index] is not ("[" or "("))
        {
            return ParsePatternTerm(tokens, ref index, blanks: blanks);
        }
        if (depth >= MaximumExpressionDepth)
        {
            throw new RdfSparqlWorkLimitException("SPARQL graph-node nesting exceeds its depth limit.");
        }
        int start = index;
        string open = tokens[index++];
        var node = new TermPattern("$structure:" + start, null, null);
        if (open == "[")
        {
            ParsePredicateObjectList(tokens, ref index, node, graph, blanks, patterns, depth + 1);
            if (index >= tokens.Length || tokens[index++] != "]")
            {
                throw new InvalidDataException("A blank-node property list must end with ']'.");
            }
            return node;
        }
        const string rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        var nil = new TermPattern(null, new RdfIri(rdf + "nil"), null);
        if (index < tokens.Length && tokens[index] == ")")
        {
            index++;
            return nil;
        }
        TermPattern current = node;
        while (index < tokens.Length && tokens[index] != ")")
        {
            TermPattern item = ParseGraphNode(tokens, ref index, graph, blanks, patterns, depth + 1);
            TermPattern next = index < tokens.Length && tokens[index] == ")" ? nil :
                new("$structure:" + start + ":rest:" + index, null, null);
            patterns.Add(new(current, new(null, new RdfIri(rdf + "first"), null), item, graph, null));
            patterns.Add(new(current, new(null, new RdfIri(rdf + "rest"), null), next, graph, null));
            current = next;
        }
        if (index >= tokens.Length || tokens[index++] != ")")
        {
            throw new InvalidDataException("An RDF collection must end with ')'.");
        }
        return node;
    }
}
