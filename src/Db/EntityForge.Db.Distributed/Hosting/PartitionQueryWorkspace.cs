using EntityForge.Db.Gql;
using System.Text.Json;
using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Distributed.Hosting;

// Query-owned, reconstructible data only. No entry survives its snapshot or
// keeps native history alive independently of the enclosing read job.
internal sealed class PartitionQueryWorkspace(PartitionOwnedNode node, PartitionQueryInterpretation? interpretation = null) : IDisposable
{
    internal PartitionOperatorInterpretation? OperatorIdentity => interpretation?.OperatorIdentity;

    private readonly Dictionary<PartitionQueryWorkKey, PartitionQueryRelation> _relations = new();
    private long _bytes;

    internal bool TryGet(PartitionQueryWorkKey key, out PartitionQueryRelation relation) =>
        _relations.TryGetValue(key, out relation!);

    internal long Add(PartitionQueryWorkKey key, PartitionQueryRelation relation, long? locallyMeasuredBytes = null)
    {
        long size = locallyMeasuredBytes ?? PartitionProjectionValue.Measure(relation, PartitionQueryLimits.MaximumExchangeBytes, PartitionQueryJson.Options);
        if (size < 0 || size > PartitionQueryLimits.MaximumExchangeBytes - _bytes)
        {
            throw new InvalidOperationException("Query workspace exceeds its retained byte budget.");
        }

        node.ReserveQueryBytes(size);
        try
        {
            _relations.Add(key, relation);
            _bytes += size;
        }
        catch
        {
            node.ReleaseQueryBytes(size);
            throw;
        }
        return size;
    }

    public void Dispose()
    {
        _relations.Clear();
        node.ReleaseQueryBytes(_bytes);
        _bytes = 0;
    }
}

internal static class PartitionQueryCompilation
{
    private const int MaximumEntries = 256;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CompiledGraphGqlQuery> ConcurrentGraph = new(StringComparer.Ordinal);
    private static readonly Queue<string> GraphInsertionOrder = new();

    internal static CompiledGraphGqlQuery CompileGraph(string statement)
    {
        if (statement.Length > PartitionQueryLimits.MaximumStatementCharacters)
        {
            throw new ArgumentException("Query is too large.");
        }

        if (ConcurrentGraph.TryGetValue(statement, out var hit))
        {
            return hit;
        }

        var candidate = new GraphGqlQueryCompiler().CompileStandard(new(statement));
        lock (GraphInsertionOrder)
        {
            if (ConcurrentGraph.TryGetValue(statement, out hit))
            {
                return hit;
            }

            if (ConcurrentGraph.Count >= MaximumEntries)
            {
                ConcurrentGraph.TryRemove(GraphInsertionOrder.Dequeue(), out _);
            }

            ConcurrentGraph.TryAdd(statement, candidate);
            GraphInsertionOrder.Enqueue(statement);
        }
        return candidate;
    }
}

internal sealed partial class PartitionOwnedNode
{
    private const long MaximumQueryWorkspaceBytes = 64L * 1024 * 1024;
    private long _queryWorkspaceBytes;

    internal long RetainedQueryWorkspaceBytes => Interlocked.Read(ref _queryWorkspaceBytes);

    private const int MaximumActiveQueryJobs = 128;
    private readonly SemaphoreSlim _queryJobCapacity = new(MaximumActiveQueryJobs, MaximumActiveQueryJobs);

    internal int ActiveQueryJobCount => MaximumActiveQueryJobs - _queryJobCapacity.CurrentCount;

    internal IDisposable EnterQueryJob()
    {
        if (!_queryJobCapacity.Wait(0))
        {
            throw new InvalidOperationException("Owner query job capacity is exhausted.");
        }

        return new QueryContext(() => _queryJobCapacity.Release());
    }

    private readonly AsyncLocal<PartitionQueryWorkspace?> _queryWorkspace = new();

    internal void ReserveQueryBytes(long bytes)
    {
        if (Interlocked.Add(ref _queryWorkspaceBytes, bytes) <= MaximumQueryWorkspaceBytes)
        {
            return;
        }

        Interlocked.Add(ref _queryWorkspaceBytes, -bytes);
        throw new InvalidOperationException("Owner query workspace capacity is exhausted.");
    }

    internal void ReleaseQueryBytes(long bytes) => Interlocked.Add(ref _queryWorkspaceBytes, -bytes);

    internal IDisposable EnterQueryWorkspace(PartitionQueryWorkspace workspace)
    {
        var previous = _queryWorkspace.Value;
        _queryWorkspace.Value = workspace;
        return new QueryContext(() => _queryWorkspace.Value = previous);
    }

    internal IDisposable EnterQueryTimestamp(object view, DurableTimestampState time)
    {
        var context = Kernel.PartitionTransactions.EnterTimestampProjection(view);
        _timestampProjection.Value = true;
        _timestampProjectionTime.Value = time;
        _operatorView.Value = view;
        return new QueryContext(() =>
        {
            _timestampProjection.Value = false;
            _timestampProjectionTime.Value = null;
            _operatorView.Value = null;
            context.Dispose();
        });
    }

    private sealed class QueryContext(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}

// Transport-owned JSON cells are appended directly; assembling pages never
// serializes and reparses the whole relation a second time.
internal sealed class PartitionQueryData
{
    internal readonly Dictionary<string, List<JsonElement>> Arrays = new(StringComparer.Ordinal);
    internal JsonElement? Catalog;
    internal bool ProfileFallback;

    internal IReadOnlyList<JsonElement> Rows(string name) => Arrays.TryGetValue(name, out var rows) ? rows : [];

    // Native pages contain ordinary snapshot values, never pooled storage or a
    // native reader. Only the query owns these references; joins never mutate them.
    private List<PartitionQueryRelation>? _nativeGraphPages;

    internal bool HasNativeGraphPages => _nativeGraphPages is not null;

    internal int AddNativeGraphPage(PartitionQueryRelation relation)
    {
        if (Arrays.Count != 0 || relation.Quads is not null || relation.Fields is not null ||
            relation.Relationships is not null || relation.Catalog is not null || relation.RdfBindings is not null ||
            relation.SemanticRows is not null || relation.SemanticEvidence is not null || relation.ProfileFallback)
        {
            throw new InvalidDataException("Native Graph fragment carries another relation shape.");
        }

        if (relation.ScalarRows is not null && (relation.Vertices is not null || relation.GraphBindings is not null))
        {
            throw new InvalidDataException("Scalar and entity rows cannot mix.");
        }

        (_nativeGraphPages ??= []).Add(relation);
        return FactCount(relation);
    }

    internal static int FactCount(PartitionQueryRelation relation) => checked(
        (relation.Vertices?.Length ?? 0) + (relation.Edges?.Length ?? 0) + (relation.Adjacency?.Length ?? 0) +
        (relation.GraphBindings?.Length ?? 0) + (relation.PropertyBags?.Length ?? 0) + (relation.Quads?.Length ?? 0) +
        (relation.Fields?.Length ?? 0) + (relation.Relationships?.Length ?? 0) + (relation.RdfBindings?.Length ?? 0) +
        (relation.SemanticRows?.Length ?? 0) + (relation.SemanticEvidence?.Length ?? 0) + (relation.ScalarRows?.Length ?? 0) +
        (relation.Continuations?.Sum(c => checked(c.Identities.Length + c.Dependencies.Length + FactCount(c.Relation))) ?? 0));

    internal static int JsonArrayFacts(string name, JsonElement values) => name != "continuations" ? values.GetArrayLength() :
        values.EnumerateArray().Sum(value =>
        {
            var continuation = value.Deserialize<PartitionQueryContinuation>(PartitionQueryJson.Options) ?? throw new InvalidDataException("Missing continuation.");
            return checked(continuation.Identities.Length + continuation.Dependencies.Length + FactCount(continuation.Relation));
        });

    internal int AddNativeProfilePage(PartitionQueryRelation relation)
    {
        if (Arrays.Count != 0 || relation.Vertices is not null || relation.Edges is not null || relation.Adjacency is not null ||
            relation.GraphBindings is not null || relation.PropertyBags is not null || relation.ScalarRows is not null)
        {
            throw new InvalidDataException("Native profile page carries Graph data.");
        }

        if (relation.Catalog is not null && _nativeGraphPages?.Any(p => p.Catalog is not null) == true)
        {
            throw new InvalidDataException("Duplicate profile interpretation catalogue.");
        }

        (_nativeGraphPages ??= []).Add(relation);
        ProfileFallback |= relation.ProfileFallback;
        return FactCount(relation);
    }

    internal bool HasRows(string name) => _nativeGraphPages is { } pages ? pages.Any(p => NativeRows(p, name) is not null) : Arrays.ContainsKey(name);

    internal IEnumerable<T> TypedRows<T>(string name) => _nativeGraphPages is { } pages
        ? pages.SelectMany(p => NativeRows(p, name)?.Cast<T>() ?? [])
        : Rows(name).Select(item => item.Deserialize<T>(PartitionQueryJson.Options) ?? throw new InvalidDataException("Missing profile row."));

    internal EntityForge.Db.Semantic.SemanticCatalogSnapshot? TypedCatalog() => _nativeGraphPages is { } pages
        ? pages.Select(p => p.Catalog).FirstOrDefault(p => p is not null)
        : Catalog?.Deserialize<EntityForge.Db.Semantic.SemanticCatalogSnapshot>(PartitionQueryJson.Options);

    private static System.Collections.IEnumerable? NativeRows(PartitionQueryRelation relation, string name) => name switch
    {
        "rdfBindings" => relation.RdfBindings,
        "semanticRows" => relation.SemanticRows,
        "semanticEvidence" => relation.SemanticEvidence,
        "quads" => relation.Quads,
        "fields" => relation.Fields,
        "relationships" => relation.Relationships,
        _ => throw new InvalidDataException("Unsupported profile rows.")
    };

    internal IEnumerable<PartitionTopologyProperties.Bag> GraphPropertyBags() => _nativeGraphPages is { } pages
        ? pages.SelectMany(p => p.PropertyBags ?? []) : Rows("propertyBags").Select(item =>
        {
            GraphWireJson.UniqueMembers(item);
            return new PartitionTopologyProperties.Bag(item.GetProperty("edge").GetBoolean(), item.GetProperty("identity").GetGuid(),
                GraphWireJson.Properties(item.GetProperty("properties")));
        });

    internal IEnumerable<PartitionQueryContinuation> Continuations() => _nativeGraphPages is { } pages
        ? pages.SelectMany(p => p.Continuations ?? []) : Rows("continuations").Select(item => item.Deserialize<PartitionQueryContinuation>(PartitionQueryJson.Options) ?? throw new InvalidDataException("Missing continuation."));

    internal IEnumerable<GraphVertex> GraphVertices() => _nativeGraphPages is { } pages
        ? pages.SelectMany(p => p.Vertices ?? [])
        : Rows("vertices").Select(item => GraphWireJson.Vertex(item, new(item.GetProperty("id").GetProperty("value").GetGuid())));

    internal IEnumerable<GraphFragmentRow> GraphScalarRows() => _nativeGraphPages is { } pages
        ? pages.SelectMany(p => p.ScalarRows ?? []) : Rows("scalarRows").Select(item =>
        {
            GraphWireJson.UniqueMembers(item);
            return new GraphFragmentRow(new(item.GetProperty("id").GetProperty("value").GetGuid()), GraphWireJson.Properties(item.GetProperty("values")));
        });

    internal IEnumerable<GraphEdge> GraphEdges() => _nativeGraphPages is { } pages
        ? pages.SelectMany(p => p.Edges ?? [])
        : Rows("edges").Select(item => GraphWireJson.Edge(item, new(item.GetProperty("id").GetProperty("value").GetGuid())));

    internal IEnumerable<Guid> GraphBindings() => _nativeGraphPages is { } pages
        ? pages.SelectMany(p => p.GraphBindings ?? []) : Rows("graphBindings").Select(item => item.GetGuid());

    internal IEnumerable<GraphEdgeReference> GraphAdjacency() => _nativeGraphPages is { } pages
        ? pages.SelectMany(p => p.Adjacency ?? []) : Rows("adjacency").Select(GraphWireJson.EdgeReference);
}
