using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Results;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlMutationCompiler
{
    private static IReadOnlyList<GraphMutation> BindRowEdgeMerge(CompiledGraphGqlMutation compiled, QueryResultEnvelope selected,
        IEnumerable<GraphVertex> snapshotVertices, IEnumerable<GraphEdge> snapshot, CancellationToken cancellation,
        List<GraphPatternMatch>? output = null)
    {
        var merge = compiled.RowMerge!;
        var patterns = merge.Creation.Edges;
        var input = (GraphPatternResult)selected.Result;
        if (input.Matches.Count >= MaximumMatchedMutations)
        {
            throw new ArgumentException("MERGE selection reaches its bounded row limit.");
        }
        var edges = snapshot.ToDictionary(edge => edge.Id);
        var vertices = snapshotVertices.ToDictionary(vertex => vertex.Id);
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
                GraphVertexId? vertexId = change switch
                {
                    CreateVertex value => value.Vertex,
                    SetVertexProperty value => value.Vertex,
                    RemoveVertexProperty value => value.Vertex,
                    AddVertexLabel value => value.Vertex,
                    RemoveVertexLabel value => value.Vertex,
                    _ => null
                };
                if (vertexId is { } vid)
                {
                    if (change is CreateVertex createdVertex)
                    {
                        vertices.Add(vid, new(vid, createdVertex.Labels.ToHashSet(StringComparer.Ordinal),
                            new Dictionary<string, DatabaseValue>(StringComparer.Ordinal)));
                    }
                    else
                    {
                        var vertex = vertices[vid];
                        var values = vertex.Properties.ToDictionary(StringComparer.Ordinal);
                        var labels = vertex.Labels.ToHashSet(StringComparer.Ordinal);
                        switch (change)
                        {
                            case SetVertexProperty assignedVertex:
                                values[assignedVertex.Name] = assignedVertex.Value;
                                break;
                            case RemoveVertexProperty removedVertex:
                                values.Remove(removedVertex.Name);
                                break;
                            case AddVertexLabel added:
                                labels.Add(added.Label);
                                break;
                            case RemoveVertexLabel removed:
                                labels.Remove(removed.Label);
                                break;
                        }
                        vertices[vid] = new(vid, labels, values);
                    }
                    continue;
                }
                if (change is CreateEdge created)
                {
                    edges.Add(created.Edge, new(created.Edge, created.From, created.To, created.Label,
                        new Dictionary<string, DatabaseValue>(StringComparer.Ordinal)));
                    continue;
                }
                GraphEdgeId? id = change switch
                {
                    SetEdgeProperty value => value.Edge,
                    RemoveEdgeProperty value => value.Edge,
                    _ => null
                };
                if (id is null)
                {
                    continue;
                }
                var edge = edges[id.Value];
                var properties = edge.Properties.ToDictionary(StringComparer.Ordinal);
                if (change is SetEdgeProperty assigned)
                {
                    properties[assigned.Name] = assigned.Value;
                }
                else if (change is RemoveEdgeProperty removed)
                {
                    properties.Remove(removed.Name);
                }
                edges[id.Value] = new(edge.Id, edge.From, edge.To, edge.Label, properties);
            }
        }
        for (int index = 0; index < input.Matches.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var row = view.Refresh(input.Matches[index]);
            GraphVertexId Endpoint(string name) => row.Bindings.GetValueOrDefault(name) is NodeValue node
                ? new(Identity(node)) : throw new ArgumentException("A MERGE endpoint must be a non-null bound node.");
            Dictionary<string, DatabaseValue> Values(IReadOnlyDictionary<string, GraphScalarExpression> expressions) =>
                expressions.ToDictionary(pair => pair.Key, pair => AssignmentValue(GraphScalarEvaluator.Evaluate(pair.Value,
                (binding, property) => binding == "__row" ? row.Bindings.GetValueOrDefault(property, UnboundValue.Instance)
                    : GraphGqlPropertyProjectionShaper.Evaluate(row, new(binding[9..], property, null)), nodeLookup.Budget()))
                ?? throw new ArgumentException("MERGE properties cannot be null."), StringComparer.Ordinal);
            var properties = patterns.Select(pattern => Values(pattern.Properties)).ToArray();
            var nodes = merge.Creation.Nodes.ToDictionary(node => node.Binding, StringComparer.Ordinal);
            var bound = nodes.Values.Where(node => node.Existing).ToDictionary(node => node.Binding, node => Endpoint(node.Binding), StringComparer.Ordinal);
            var constraints = nodes.Values.Where(node => !node.Existing).ToDictionary(node => node.Binding, node => Values(node.Properties), StringComparer.Ordinal);
            bool MatchesNode(string name, GraphVertexId id, IReadOnlyDictionary<string, GraphVertexId> bindings)
            {
                if (bindings.TryGetValue(name, out var existing))
                {
                    return id == existing;
                }
                return vertices.TryGetValue(id, out var vertex) && nodes[name].Labels.All(vertex.Labels.Contains) &&
                    constraints[name].All(pair => vertex.Properties.TryGetValue(pair.Key, out var value) && MergeValuesEqual(value, pair.Value));
            }
            var matched = new List<(Dictionary<string, GraphVertexId> Nodes, GraphEdge[] Edges)>();
            void Search(int step, Dictionary<string, GraphVertexId> bindings, List<GraphEdge> path)
            {
                if (step == patterns.Count)
                {
                    if (matched.Count >= MaximumMatchedMutations)
                    {
                        throw new ArgumentException("MERGE reaches its bounded pattern-match limit.");
                    }
                    matched.Add((bindings, path.ToArray()));
                    return;
                }
                var pattern = patterns[step];
                foreach (var edge in edges.Values)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (++examined > 100_000)
                    {
                        throw new ArgumentException("MERGE exceeds 100,000 examined relationship candidates.");
                    }
                    if (path.Any(previous => previous.Id == edge.Id) || edge.Label != pattern.Label ||
                        !properties[step].All(pair => edge.Properties.TryGetValue(pair.Key, out var value) && MergeValuesEqual(value, pair.Value)))
                    {
                        continue;
                    }
                    void Orientation(GraphVertexId from, GraphVertexId to)
                    {
                        if ((pattern.From == pattern.To && from != to) || !MatchesNode(pattern.From, from, bindings) ||
                            !MatchesNode(pattern.To, to, bindings))
                        {
                            return;
                        }
                        var next = new Dictionary<string, GraphVertexId>(bindings, StringComparer.Ordinal)
                        {
                            [pattern.From] = from,
                            [pattern.To] = to
                        };
                        path.Add(edge);
                        Search(step + 1, next, path);
                        path.RemoveAt(path.Count - 1);
                    }
                    Orientation(edge.From, edge.To);
                    if (merge.Undirected && edge.From != edge.To)
                    {
                        Orientation(edge.To, edge.From);
                    }
                }
            }
            Search(0, bound, []);
            bool created = matched.Count == 0;
            if (created)
            {
                string key = "entityforge:gql-row-edge-merge:" + merge.Creation.Transaction.ToString("N") + ":" + index.ToString(CultureInfo.InvariantCulture);
                foreach (var node in nodes.Values.Where(node => !node.Existing))
                {
                    GraphVertexId vertex = new(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key + ":node:" + node.Binding)).AsSpan(0, 16)));
                    bound.Add(node.Binding, vertex);
                    Add(new GraphMutation[] { new CreateVertex(vertex, node.Labels) }
                        .Concat(constraints[node.Binding].Select(pair => new SetVertexProperty(vertex, pair.Key, pair.Value))));
                }
                var path = new List<GraphEdge>();
                for (int step = 0; step < patterns.Count; step++)
                {
                    var pattern = patterns[step];
                    string edgeKey = step == 0 ? key : key + ":edge:" + step.ToString(CultureInfo.InvariantCulture);
                    GraphEdgeId id = new(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(edgeKey)).AsSpan(0, 16)));
                    Add(new GraphMutation[] { new CreateEdge(id, bound[pattern.From], bound[pattern.To], pattern.Label) }
                        .Concat(properties[step].Select(pair => new SetEdgeProperty(id, pair.Key, pair.Value))));
                    path.Add(edges[id]);
                }
                matched.Add((bound, path.ToArray()));
            }
            var action = RowMergeActions(merge, created);
            var actionRows = matched.Select(match =>
            {
                var facts = new List<GraphMutation>();
                var identities = new Dictionary<string, Guid>(StringComparer.Ordinal);
                foreach (var node in nodes.Values.Where(node => !node.Existing))
                {
                    var vertex = vertices[match.Nodes[node.Binding]];
                    identities.Add(node.Binding, vertex.Id.Value);
                    facts.Add(new CreateVertex(vertex.Id, vertex.Labels.ToArray()));
                    facts.AddRange(vertex.Properties.Select(pair => new SetVertexProperty(vertex.Id, pair.Key, pair.Value)));
                }
                for (int step = 0; step < patterns.Count; step++)
                {
                    if (patterns[step].Binding is { } binding)
                    {
                        var edge = match.Edges[step];
                        identities.Add(binding, edge.Id.Value);
                        facts.Add(new CreateEdge(edge.Id, edge.From, edge.To, edge.Label));
                        facts.AddRange(edge.Properties.Select(pair => new SetEdgeProperty(edge.Id, pair.Key, pair.Value)));
                    }
                }
                if (identities.Count == 0)
                {
                    return row;
                }
                var value = ((GraphPatternResult)CreationBindings(facts.ToArray(), identities).Result).Matches.Single();
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
}
