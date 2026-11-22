using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Gql;

internal sealed record GqlRowMerge(GqlRowCreation Creation, CompiledGraphGqlMutation? OnCreate, CompiledGraphGqlMutation? OnMatch,
    bool Undirected = false, IReadOnlyList<CompiledGraphGqlMutation>? After = null);

internal static partial class GraphGqlMutationCompiler
{
    private static CompiledGraphGqlMutation? CompileRowMerge(string source, Guid? transaction)
    {
        string masked = GraphGqlQueryCompiler.MaskQuotedStrings(source);
        int depth = 0, scanned = 0;
        Match? boundary = null;
        foreach (Match candidate in GqlRegex.Matches(masked, @"\s+MERGE\s+(?=\()", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
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
        string body = source[(boundary.Index + boundary.Length)..];
        if (transaction is null || transaction == Guid.Empty)
        {
            throw new ArgumentException("MERGE requires a nonempty transaction identity.");
        }
        var selected = new GraphGqlQueryCompiler().Compile(new(source[..boundary.Index] +
            " WITH * RETURN * LIMIT " + MaximumMatchedMutations.ToString(CultureInfo.InvariantCulture)));
        return CompileMergeStage(selected, body, transaction.Value);
    }

    private static CompiledGraphGqlMutation CompileMergeStage(CompiledGraphGqlQuery selected, string body, Guid transaction,
        IReadOnlySet<string>? inputScope = null)
    {
        var actions = TopLevelMutationClauses(body).Select(clause =>
        {
            var branch = GqlRegex.Match(GraphGqlQueryCompiler.MaskQuotedStrings(body[..clause.Index]),
                @"\s+ON\s+(?<branch>MATCH|CREATE)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return (Start: branch.Success ? branch.Index : clause.Index, Content: clause.Index + clause.Length,
                Kind: clause.Groups["kind"].Value.ToUpperInvariant(), Branch: branch.Groups["branch"].Value.ToUpperInvariant());
        }).ToArray();
        if (actions.Length > 18 || actions.Any(action => action.Kind == "DELETE" || action.Branch.Length != 0 && action.Kind != "SET"))
        {
            throw new ArgumentException("MERGE accepts ON CREATE/ON MATCH SET and up to sixteen following SET/REMOVE clauses.");
        }
        string pattern = actions.Length == 0 ? body : body[..actions[0].Start];
        string maskedPattern = GraphGqlQueryCompiler.MaskQuotedStrings(pattern);
        bool undirected = false;
        if (!GqlRegex.IsMatch(maskedPattern, @"\)\s*<-\s*\["))
        {
            var undirectedEnd = GqlRegex.Match(maskedPattern, @"\]\s*-(?<after>\s*\()");
            if (undirectedEnd.Success)
            {
                undirected = true;
                pattern = pattern.Insert(undirectedEnd.Groups["after"].Index, ">");
            }
        }
        var compiled = CompileCreationStage(selected, pattern, transaction, inputScope);
        var creation = compiled.RowCreation!;
        bool singleNode = creation.Nodes.Count == 1 && !creation.Nodes[0].Existing && creation.Edges.Count == 0;
        bool connectedEdges = creation.Edges.Count is > 0 and <= 16 && SplitTopLevel(pattern).Count() == 1 &&
            (!undirected || creation.Edges.Count == 1);
        if ((!singleNode && !connectedEdges) || creation.Actions is not null ||
            creation.Nodes.Any(node => node.Properties.ContainsKey("id")) || creation.Edges.Any(edge => edge.Properties.ContainsKey("id")))
        {
            throw new ArgumentException("Read-stage MERGE requires one new node or a connected pattern of at most sixteen directed relationships (one for undirected MERGE), with optional ON CREATE/ON MATCH SET actions.");
        }
        var scope = (inputScope ?? GraphGqlQueryCompiler.CallColumns(compiled.Match!).ToHashSet(StringComparer.Ordinal)).Concat(creation.Nodes.Select(node => node.Binding))
            .Concat(creation.Edges.Select(edge => edge.Binding).OfType<string>()).ToHashSet(StringComparer.Ordinal);
        CompiledGraphGqlMutation? onCreate = null, onMatch = null;
        var after = new List<CompiledGraphGqlMutation>();
        for (int index = 0; index < actions.Length; index++)
        {
            var clause = actions[index];
            string action = body[clause.Content..(index + 1 < actions.Length ? actions[index + 1].Start : body.Length)];
            var mutations = SplitTopLevel(action).SelectMany(item => ParseSetOrRemove(item, clause.Kind == "SET", scope)).ToArray();
            if (mutations.Any(mutation => !scope.Contains(mutation.Binding)))
            {
                throw new ArgumentException("A MERGE action target must be in scope.");
            }
            var step = new CompiledGraphGqlMutation(compiled.Match, mutations, []);
            if (clause.Branch.Length == 0)
            {
                after.Add(step);
                if (after.Count > 16)
                {
                    throw new ArgumentException("MERGE accepts at most sixteen following SET/REMOVE clauses.");
                }
                continue;
            }
            if (after.Count != 0)
            {
                throw new ArgumentException("ON CREATE/ON MATCH must precede following SET/REMOVE clauses.");
            }
            bool create = clause.Branch == "CREATE";
            if (create ? onCreate is not null : onMatch is not null)
            {
                throw new ArgumentException("MERGE accepts one ON CREATE and one ON MATCH action.");
            }
            if (create)
            {
                onCreate = step;
            }
            else
            {
                onMatch = step;
            }
        }
        return compiled with
        {
            RowCreation = null,
            RowMerge = new(creation, onCreate, onMatch, undirected, after)
        };
    }

    private static CompiledGraphGqlMutation? RowMergeActions(GqlRowMerge merge, bool created)
    {
        var branch = created ? merge.OnCreate : merge.OnMatch;
        var steps = new List<CompiledGraphGqlMutation>();
        if (branch is not null)
        {
            steps.Add(branch);
        }
        steps.AddRange(merge.After ?? []);
        return steps.Count == 0 ? null : steps[0] with
        {
            Steps = steps
        };
    }

    internal static IReadOnlyList<GraphMutation> BindRowMerge(CompiledGraphGqlMutation compiled, QueryResultEnvelope selected,
        IEnumerable<GraphVertex> snapshot, CancellationToken cancellation, IEnumerable<GraphEdge>? snapshotEdges = null,
        List<GraphPatternMatch>? output = null)
    {
        var merge = compiled.RowMerge!;
        if (merge.Creation.Edges.Count != 0)
        {
            return BindRowEdgeMerge(compiled, selected, snapshot, snapshotEdges ?? throw new InvalidOperationException("MERGE requires captured relationships."), cancellation, output);
        }
        var node = merge.Creation.Nodes[0];
        var input = (GraphPatternResult)selected.Result;
        if (input.Matches.Count >= MaximumMatchedMutations)
        {
            throw new ArgumentException("MERGE selection reaches its bounded row limit.");
        }
        var vertices = snapshot.ToDictionary(vertex => vertex.Id);
        var mutations = new List<GraphMutation>();
        var view = new GqlMutationValueView();
        var nodeLookup = new GqlNodeLookup(input.Graph, view);
        long examined = 0;
        void Add(IEnumerable<GraphMutation> changes)
        {
            foreach (var change in changes)
            {
                if (mutations.Count >= MaximumMatchedMutations)
                {
                    throw new ArgumentException("MERGE exceeds 10,000 native mutations.");
                }
                mutations.Add(change);
                GraphVertexId? id = change switch
                {
                    CreateVertex value => value.Vertex,
                    SetVertexProperty value => value.Vertex,
                    RemoveVertexProperty value => value.Vertex,
                    AddVertexLabel value => value.Vertex,
                    RemoveVertexLabel value => value.Vertex,
                    _ => null
                };
                if (id is null)
                {
                    continue;
                }
                if (change is CreateVertex created)
                {
                    vertices.Add(created.Vertex, new(created.Vertex, created.Labels.ToHashSet(StringComparer.Ordinal),
                        new Dictionary<string, DatabaseValue>(StringComparer.Ordinal)));
                    continue;
                }
                var vertex = vertices[id.Value];
                var properties = vertex.Properties.ToDictionary(StringComparer.Ordinal);
                var labels = vertex.Labels.ToHashSet(StringComparer.Ordinal);
                switch (change)
                {
                    case SetVertexProperty assigned:
                        properties[assigned.Name] = assigned.Value;
                        break;
                    case RemoveVertexProperty removed:
                        properties.Remove(removed.Name);
                        break;
                    case AddVertexLabel added:
                        labels.Add(added.Label);
                        break;
                    case RemoveVertexLabel removed:
                        labels.Remove(removed.Label);
                        break;
                }
                vertices[id.Value] = new(vertex.Id, labels, properties);
            }
        }
        for (int index = 0; index < input.Matches.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var row = view.Refresh(input.Matches[index]);
            var properties = node.Properties.ToDictionary(pair => pair.Key, pair => AssignmentValue(GraphScalarEvaluator.Evaluate(pair.Value,
                (binding, property) => binding == "__row" ? row.Bindings.GetValueOrDefault(property, UnboundValue.Instance)
                    : GraphGqlPropertyProjectionShaper.Evaluate(row, new(binding[9..], property, null)), nodeLookup.Budget()))
                ?? throw new ArgumentException("MERGE properties cannot be null."), StringComparer.Ordinal);
            var matched = new List<GraphVertex>();
            foreach (var vertex in vertices.Values)
            {
                cancellation.ThrowIfCancellationRequested();
                if (++examined > 100_000)
                {
                    throw new ArgumentException("MERGE exceeds 100,000 examined node candidates.");
                }
                if (node.Labels.All(vertex.Labels.Contains) && properties.All(pair =>
                    vertex.Properties.TryGetValue(pair.Key, out var value) && MergeValuesEqual(value, pair.Value)))
                {
                    matched.Add(vertex);
                }
            }
            bool created = matched.Count == 0;
            if (created)
            {
                string key = "entityforge:gql-row-merge:" + merge.Creation.Transaction.ToString("N") + ":" + index.ToString(CultureInfo.InvariantCulture);
                GraphVertexId id = new(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16)));
                Add(new GraphMutation[] { new CreateVertex(id, node.Labels) }.Concat(properties.Select(pair => new SetVertexProperty(id, pair.Key, pair.Value))));
                matched.Add(vertices[id]);
            }
            var action = RowMergeActions(merge, created);
            var actionRows = matched.Select(vertex =>
            {
                var facts = new GraphMutation[] { new CreateVertex(vertex.Id, vertex.Labels.ToArray()) }
                    .Concat(vertex.Properties.Select(pair => new SetVertexProperty(vertex.Id, pair.Key, pair.Value))).ToArray();
                var value = ((GraphPatternResult)CreationBindings(facts, new Dictionary<string, Guid> { [node.Binding] = vertex.Id.Value }).Result).Matches.Single();
                return view.Refresh(new GraphPatternMatch(row.Bindings.Concat(value.Bindings), row.Paths));
            }).ToArray();
            if (action is not null)
            {
                var changes = Bind(action, new(selected.QueryId, new GraphPatternResult(GqlNodeLookup.IncludeBindings(view.Refresh(input.Graph), actionRows), actionRows)));
                Add(changes);
                view.Apply(changes);
            }
            output?.AddRange(actionRows.Select(view.Refresh));
        }
        return mutations;
    }

    private static bool MergeValuesEqual(DatabaseValue actual, DatabaseValue expected)
    {
        if (actual is DatabaseValue.Array first && expected is DatabaseValue.Array second)
        {
            return first.Value.Count == second.Value.Count && first.Value.Zip(second.Value).All(pair => MergeValuesEqual(pair.First, pair.Second));
        }
        QueryValue? Numeric(DatabaseValue value) => value switch
        {
            DatabaseValue.Int64 number => new LiteralValue(number.Value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer),
            DatabaseValue.Double number => GraphScalarEvaluator.FloatingValue(number.Value),
            _ => null
        };
        if (Numeric(actual) is { } left && Numeric(expected) is { } right)
        {
            return GraphScalarEvaluator.Boolean(GraphScalarEvaluator.Evaluate(new(null, null, null, "=",
                [new(null, null, left), new(null, null, right)]), (_, _) => UnboundValue.Instance)) == true;
        }
        return actual.Equals(expected);
    }
}
