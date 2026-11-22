using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Gql;

internal sealed record GqlCreatedNode(string Binding, string[] Labels, IReadOnlyDictionary<string, GraphScalarExpression> Properties, bool Existing);

internal sealed record GqlCreatedEdge(string? Binding, string From, string To, string Label, IReadOnlyDictionary<string, GraphScalarExpression> Properties);

internal sealed record GqlRowCreation(Guid Transaction, IReadOnlyList<GqlCreatedNode> Nodes, IReadOnlyList<GqlCreatedEdge> Edges,
    CompiledGraphGqlMutation? Actions = null);

internal static partial class GraphGqlMutationCompiler
{
    private static CompiledGraphGqlMutation? CompileRowCreation(string source, Guid? transactionId)
    {
        string masked = GraphGqlQueryCompiler.MaskQuotedStrings(source);
        int depth = 0, scanned = 0;
        Match? boundary = null;
        foreach (Match candidate in GqlRegex.Matches(masked, @"\s+(?:CREATE|INSERT)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            for (; scanned < candidate.Index; scanned++)
            {
                if (masked[scanned] is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (masked[scanned] is ')' or ']' or '}')
                {
                    depth--;
                }
            }
            if (depth == 0)
            {
                boundary = candidate;
                break;
            }
        }
        if (boundary is null)
        {
            return null;
        }
        if (transactionId is null || transactionId == Guid.Empty)
        {
            throw new ArgumentException("Creation requires a nonempty transaction identity.");
        }
        var selected = new GraphGqlQueryCompiler().Compile(new(source[..boundary.Index] +
            " WITH * RETURN * LIMIT " + MaximumMatchedMutations.ToString(CultureInfo.InvariantCulture)));
        return CompileCreationStage(selected, source[(boundary.Index + boundary.Length)..], transactionId.Value);
    }

    private static CompiledGraphGqlMutation CompileCreationStage(CompiledGraphGqlQuery selected, string body, Guid transactionId,
        IReadOnlySet<string>? inputScope = null)
    {
        var scope = (inputScope ?? GraphGqlQueryCompiler.CallColumns(selected).ToHashSet(StringComparer.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var names = new HashSet<string>(scope, StringComparer.Ordinal);
        var nodes = new List<GqlCreatedNode>();
        var edges = new List<GqlCreatedEdge>();
        var actions = TopLevelMutationClauses(body);
        if (actions.Count > 16 || actions.Any(clause => clause.Groups["kind"].Value.Equals("DELETE", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Creation may be followed by at most sixteen SET/REMOVE clauses.");
        }
        string pattern = actions.Count == 0 ? body : body[..actions[0].Index];
        IReadOnlyDictionary<string, GraphScalarExpression> Properties(string text)
        {
            var properties = new Dictionary<string, GraphScalarExpression>(StringComparer.Ordinal);
            foreach (string item in String.IsNullOrWhiteSpace(text) ? [] : SplitTopLevel(text))
            {
                var pair = ParsePropertyPair(item);
                if (!properties.TryAdd(pair.Name, GraphGqlQueryCompiler.ParseExpression(pair.Expression, scope)))
                {
                    throw new ArgumentException("Creation properties require unique property names and expressions in the input scope.");
                }
            }
            return properties;
        }
        foreach (string item in ExpandCreationPatterns(pattern, scope))
        {
            if (!GraphGqlQueryCompiler.MaskQuotedStrings(item).Contains("-[", StringComparison.Ordinal))
            {
                var node = GqlRegex.Match(item, @"^\((?<binding>" + GraphGqlIdentifiers.Pattern + @")(?::(?<label>" + GraphGqlIdentifiers.NamePattern + @"))*\s*(?:\{(?<properties>.*)\})?\)$", RegexOptions.Singleline);
                if (!node.Success)
                {
                    throw new ArgumentException("Unsupported node creation pattern.");
                }
                string name = node.Groups["binding"].Value;
                bool existing = scope.Contains(name);
                if (existing && (node.Groups["label"].Success || node.Groups["properties"].Success))
                {
                    throw new ArgumentException("A bound creation endpoint cannot be redeclared with labels or properties.");
                }
                names.Add(name);
                nodes.Add(new(name, node.Groups["label"].Captures.Select(label => GraphGqlIdentifiers.DecodeName(label.Value)).Distinct(StringComparer.Ordinal).ToArray(),
                    Properties(node.Groups["properties"].Value), existing));
            }
            else
            {
                var edge = GqlRegex.Match(item, @"^\((?<from>" + GraphGqlIdentifiers.Pattern + @")\)-\[(?<binding>" + GraphGqlIdentifiers.Pattern + @")?:(?<label>" + GraphGqlIdentifiers.NamePattern + @")\s*(?:\{(?<properties>.*)\})?\]->\((?<to>" + GraphGqlIdentifiers.Pattern + @")\)$", RegexOptions.Singleline);
                if (!edge.Success || edge.Groups["binding"].Success && !names.Add(edge.Groups["binding"].Value))
                {
                    throw new ArgumentException("Creation requires typed relationships with new variables.");
                }
                edges.Add(new(edge.Groups["binding"].Success ? edge.Groups["binding"].Value : null,
                    edge.Groups["from"].Value, edge.Groups["to"].Value, GraphGqlIdentifiers.DecodeName(edge.Groups["label"].Value), Properties(edge.Groups["properties"].Value)));
            }
        }
        if (nodes.All(node => node.Existing) && edges.Count == 0)
        {
            throw new ArgumentException("Creation must introduce a node or relationship.");
        }
        var steps = actions.Select((clause, index) =>
        {
            string action = body[(clause.Index + clause.Length)..(index + 1 < actions.Count ? actions[index + 1].Index : body.Length)];
            bool set = clause.Groups["kind"].Value.Equals("SET", StringComparison.OrdinalIgnoreCase);
            var mutations = SplitTopLevel(action).SelectMany(item => ParseSetOrRemove(item, set, names)).ToArray();
            if (mutations.Any(mutation => !names.Contains(mutation.Binding)))
            {
                throw new ArgumentException("A creation action target must be in the input or created scope.");
            }
            return new CompiledGraphGqlMutation(selected, mutations, []);
        }).ToArray();
        CompiledGraphGqlMutation? compiledActions = steps.Length == 0 ? null : steps[0] with
        {
            Steps = steps
        };
        return new(selected, [], [], RowCreation: new(transactionId, nodes, edges, compiledActions));
    }

    private static IReadOnlyList<GraphMutation> BindRowCreation(GqlRowCreation creation, GraphPatternResult result)
        => BindRowCreation(creation, result, out _);

    private static IReadOnlyList<GraphMutation> BindRowCreation(GqlRowCreation creation, GraphPatternResult result, out GraphPatternResult output)
    {
        var nodeLookup = new GqlNodeLookup(result.Graph);
        if (result.Matches.Count >= MaximumMatchedMutations)
        {
            throw new InvalidOperationException("GQL creation selection reaches its bounded match limit.");
        }
        var mutations = new List<GraphMutation>();
        var actionRows = new List<GraphPatternMatch>();
        var actionNodes = new List<NodeValue>();
        var actionEdges = new List<EdgeValue>();
        void Add(GraphMutation mutation)
        {
            if (mutations.Count == MaximumMatchedMutations)
            {
                throw new ArgumentException("A GQL update exceeds 10,000 native mutations.");
            }
            mutations.Add(mutation);
        }
        for (int rowIndex = 0; rowIndex < result.Matches.Count; rowIndex++)
        {
            var row = result.Matches[rowIndex];
            int firstMutation = mutations.Count;
            var createdNames = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var vertices = new Dictionary<string, GraphVertexId>(StringComparer.Ordinal);
            int elementIndex = 0;
            Dictionary<string, DatabaseValue> Values(IReadOnlyDictionary<string, GraphScalarExpression> properties)
            {
                var values = new Dictionary<string, DatabaseValue>(StringComparer.Ordinal);
                foreach (var pair in properties)
                {
                    var value = GraphScalarEvaluator.Evaluate(pair.Value, (binding, property) => binding == "__row"
                        ? row.Bindings.GetValueOrDefault(property, UnboundValue.Instance)
                        : GraphGqlPropertyProjectionShaper.Evaluate(row, new(binding[9..], property, null)), nodeLookup.Budget());
                    if (AssignmentValue(value) is { } stored)
                    {
                        values.Add(pair.Key, stored);
                    }
                }
                return values;
            }
            Guid NewIdentity()
            {
                int index = elementIndex++;
                string key = "entityforge:gql-row-created:" + creation.Transaction.ToString("N") + ":" +
                    rowIndex.ToString(CultureInfo.InvariantCulture) + ":" + index.ToString(CultureInfo.InvariantCulture);
                return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
            }
            foreach (var node in creation.Nodes)
            {
                if (node.Existing)
                {
                    if (row.Bindings.GetValueOrDefault(node.Binding) is not NodeValue existing)
                    {
                        throw new ArgumentException("A creation endpoint must be a non-null node.");
                    }
                    vertices.Add(node.Binding, new(Identity(existing)));
                    continue;
                }
                var properties = Values(node.Properties);
                GraphVertexId id = new(NewIdentity());
                vertices.Add(node.Binding, id);
                createdNames.Add(node.Binding, id.Value);
                Add(new CreateVertex(id, node.Labels));
                foreach (var property in properties)
                {
                    Add(new SetVertexProperty(id, property.Key, property.Value));
                }
            }
            foreach (var edge in creation.Edges)
            {
                var properties = Values(edge.Properties);
                GraphEdgeId id = new(NewIdentity());
                if (edge.Binding is { } name)
                {
                    createdNames.Add(name, id.Value);
                }
                Add(new CreateEdge(id, vertices[edge.From], vertices[edge.To], edge.Label));
                foreach (var property in properties)
                {
                    Add(new SetEdgeProperty(id, property.Key, property.Value));
                }
            }
            if (creation.Actions is not null || createdNames.Count != 0)
            {
                var created = (GraphPatternResult)CreationBindings(mutations.Skip(firstMutation).ToArray(), createdNames).Result;
                actionRows.Add(new(row.Bindings.Concat(created.Matches.Single().Bindings), row.Paths));
                actionNodes.AddRange(created.Graph.Nodes);
                actionEdges.AddRange(created.Graph.Edges);
            }
            else
            {
                actionRows.Add(row);
            }
        }
        output = new GraphPatternResult(new GraphResult(result.Graph.Nodes.Concat(actionNodes), result.Graph.Edges.Concat(actionEdges)), actionRows);
        if (creation.Actions is not null)
        {
            // Finish CREATE for every input row before subsequent write clauses.
            // The assignment binder handles repeated identities across those rows.
            foreach (var mutation in Bind(creation.Actions, new QueryResultEnvelope(Guid.Empty, output)))
            {
                Add(mutation);
            }
        }
        return mutations;
    }
}
