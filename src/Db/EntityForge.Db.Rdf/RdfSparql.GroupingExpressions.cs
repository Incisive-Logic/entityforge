namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static Group ApplyGroupingExpressions(Group body, Modifiers modifiers)
    {
        if (modifiers.GroupExpressions.Count == 0)
        {
            return body;
        }
        var elements = body.Elements.ToList();
        foreach (var expression in modifiers.GroupExpressions)
        {
            if (elements.Any(element => IntroducesVariable(element, expression.Variable)))
            {
                throw new InvalidDataException("A grouping expression cannot replace an in-scope variable.");
            }
            elements.Add(expression);
        }
        // Group filters apply to the original graph pattern. A grouping alias
        // is introduced only after that pattern has produced its solutions.
        return new([new NestedGroupElement(body), .. elements.Skip(body.Elements.Count)], []);
    }
}
