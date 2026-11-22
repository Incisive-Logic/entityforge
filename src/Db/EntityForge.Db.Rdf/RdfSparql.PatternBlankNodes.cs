namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private sealed class PatternBlankScope(int start)
    {
        internal TermPattern Label(string label) => new("$blank:" + start + ":" + label, null, null);
    }

    private static void ValidatePatternBlankScopes(Group root)
    {
        var scopes = new Dictionary<string, string>(StringComparer.Ordinal);
        void Term(TermPattern? term)
        {
            if (term?.Variable is { } variable && variable.StartsWith("$blank:", StringComparison.Ordinal))
            {
                int separator = variable.IndexOf(':', 7);
                string label = variable[(separator + 1)..];
                string scope = variable[..separator];
                if (scopes.TryGetValue(label, out string? previous) && previous != scope)
                {
                    throw new InvalidDataException("A blank-node label cannot be reused in different basic graph patterns.");
                }
                scopes[label] = scope;
            }
            if (term?.Quoted is { } triple)
            {
                Term(triple.Subject);
                Term(triple.Object);
            }
        }
        void Filter(FilterExpression filter)
        {
            switch (filter)
            {
                case ExistsExpression exists:
                    Visit(exists.Group);
                    break;
                case BinaryBooleanExpression binary:
                    Filter(binary.Left);
                    Filter(binary.Right);
                    break;
                case NotExpression not:
                    Filter(not.Operand);
                    break;
            }
        }
        void Visit(Group group)
        {
            foreach (var element in group.Elements)
            {
                switch (element)
                {
                    case PatternElement pattern:
                        Term(pattern.Pattern.Subject);
                        Term(pattern.Pattern.Object);
                        break;
                    case NestedGroupElement nested:
                        Visit(nested.Group);
                        break;
                    case OptionalElement optional:
                        Visit(optional.Group);
                        break;
                    case MinusElement minus:
                        Visit(minus.Group);
                        break;
                    case UnionElement union:
                        foreach (var branch in union.Groups)
                        {
                            Visit(branch);
                        }
                        break;
                        // Subqueries have independently parsed scopes and project
                        // only visible variables before joining their caller.
                }
            }
            foreach (var filter in group.Filters)
            {
                Filter(filter);
            }
        }
        Visit(root);
    }
}
