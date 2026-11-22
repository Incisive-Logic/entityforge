using Antlr4.Runtime.Tree;
using EntityForge.Db.Query.Syntax.Gql;
using System.Text.Json;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    internal static string QualifiedLocalGraphKey(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (reference.Length > 1024)
        {
            throw new ArgumentException("A qualified graph reference accepts at most 1,024 characters.", nameof(reference));
        }
        reference = reference.Trim();
        var root = GqlSyntaxParser.Parse("USE " + reference + " RETURN 1 AS catalogValue", out _);
        var clauses = Descendants<GQLParser.UseGraphClauseContext>(root).ToArray();
        if (clauses.Length != 1 || clauses[0].Stop.StopIndex + 1 != 4 + reference.Length ||
            clauses[0].graphExpression().graphReference() is not { } graph)
        {
            throw new ArgumentException("A qualified graph registration requires one dotted graph reference.", nameof(reference));
        }
        return QualifiedLocalGraphKey(graph);
    }

    private static string QualifiedLocalGraphKey(GQLParser.GraphReferenceContext graph)
        => QualifiedLocalGraphKey(graph.catalogObjectParentReference(), graph.graphName()?.GetText());

    private static string QualifiedLocalGraphKey(GQLParser.CatalogObjectParentReferenceContext? parent, string? graphName)
    {
        if (parent is null || parent.schemaReference() is not null || parent.objectName().Length == 0 || graphName is null)
        {
            throw new ArgumentException("Only dotted object-qualified graph references are supported; schema paths and graph parameters are not supported.");
        }
        string[] names = parent.objectName().Select(name => GraphGqlIdentifiers.DecodeIdentifier(name.GetText()))
            .Append(GraphGqlIdentifiers.DecodeIdentifier(graphName)).ToArray();
        if (names.Any(name => String.IsNullOrWhiteSpace(name) || name.Contains('\0')))
        {
            throw new ArgumentException("Graph reference components must be nonempty identifiers without NUL.");
        }
        // Single decoded identifiers cannot contain NUL. Preserve the segment
        // boundaries so sales.orders never aliases the identifier "sales.orders".
        return "\0qualified:" + JsonSerializer.Serialize(names);
    }

    internal static bool IsCanonicalLocalGraphKey(string name)
    {
        if (String.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        const string prefix = "\0qualified:";
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return name.Length <= 1024 && !name.Contains('\0');
        }
        if (name.Length > 4096)
        {
            return false;
        }
        try
        {
            var names = JsonSerializer.Deserialize<string[]>(name[prefix.Length..]);
            return names is { Length: >= 2 and <= 64 } &&
                names.All(part => !String.IsNullOrWhiteSpace(part) && part.Length <= 1024 && !part.Contains('\0')) &&
                name == prefix + JsonSerializer.Serialize(names);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static (string? Name, string Statement) SelectLocalGraph(string source, string? defaultGraphName, Func<string, bool>? containsGraph)
    {
        var root = GqlSyntaxParser.Parse(source, out _);
        var clauses = Descendants<GQLParser.UseGraphClauseContext>(root).OrderBy(clause => clause.Start.StartIndex).ToArray();
        string? selected = defaultGraphName;
        if (clauses.Length == 0)
        {
            return (selected, source);
        }
        string? Resolve(GQLParser.UseGraphClauseContext clause)
        {
            var expression = clause.graphExpression();
            if (expression.currentGraph() is not null)
            {
                return selected;
            }
            if (containsGraph is null)
            {
                throw new ArgumentException("Named graph selection requires a configured local GQL graph catalog.");
            }
            if (expression.graphReference()?.homeGraph() is not null)
            {
                return defaultGraphName;
            }
            if (expression.graphReference() is { } qualified && qualified.catalogObjectParentReference() is not null)
            {
                string key = QualifiedLocalGraphKey(qualified);
                return containsGraph(key) ? key :
                    throw new ArgumentException("The selected qualified graph is not registered in the local GQL graph catalog.");
            }
            string? name = expression.objectNameOrBindingVariable()?.GetText() ??
                expression.graphReference()?.delimitedGraphName()?.GetText();
            if (name is null)
            {
                throw new ArgumentException("Parameterized and computed graph references are not supported by the local graph catalog.");
            }
            name = GraphGqlIdentifiers.DecodeIdentifier(name);
            if (name != defaultGraphName && !containsGraph(name))
            {
                throw new ArgumentException("The selected graph is not registered in the local GQL graph catalog.");
            }
            return name;
        }
        if (clauses[0].Start.StartIndex == root.Start.StartIndex)
        {
            selected = Resolve(clauses[0]);
        }
        if (selected != defaultGraphName &&
            (Descendants<GQLParser.NextStatementContext>(root).Any(IsOutsideNestedScope) ||
             Descendants<GQLParser.QueryConjunctionContext>(root).Any(IsOutsideNestedScope)))
        {
            // USE scopes a focused statement; it must not accidentally redirect a
            // sibling ambient NEXT/UNION branch to the selected physical store.
            throw new ArgumentException("Named graph selection currently requires one outer linear statement; put composition inside USE graph { ... }.");
        }
        foreach (var clause in clauses.Reverse())
        {
            if (!String.Equals(Resolve(clause), selected, StringComparison.Ordinal))
            {
                throw new ArgumentException("Changing graphs within one request is not supported.");
            }
            if (clause.graphExpression().currentGraph() is null)
            {
                source = source.Remove(clause.Start.StartIndex, clause.Stop.StopIndex - clause.Start.StartIndex + 1)
                    .Insert(clause.Start.StartIndex, "USE CURRENT_GRAPH");
            }
        }
        return (selected, source);

        static bool IsOutsideNestedScope(IParseTree node)
        {
            for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            {
                if (parent is GQLParser.NestedQuerySpecificationContext or GQLParser.NestedProcedureSpecificationContext)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
