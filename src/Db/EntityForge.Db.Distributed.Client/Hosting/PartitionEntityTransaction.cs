using EntityForge.Db.Mapping;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;

namespace EntityForge.Db.Distributed.Hosting;

public sealed partial class HttpPartitionEntityClient
{
    /// <summary>Creates one ordinary transaction for this profile. Saves and deletes share one atomic outcome.</summary>
    public PartitionEntityTransaction CreateTransaction(Guid? transactionId = null) =>
        new(this, _http, transactionId ?? EntityOperationIds.New());
}

/// <summary>Captures one or more entity changes for one atomic Graph, RDF or Semantic transaction.</summary>
/// <remarks>Operations must be sequential. After the first commit attempt the captured input is immutable;
/// another explicit commit attempt uses the same identity and bytes.</remarks>
public sealed class PartitionEntityTransaction
{
    private readonly HttpPartitionEntityClient _client;
    private readonly HttpClient _http;
    private readonly SemanticEntityWriteOperation _semanticOperation;
    private readonly List<ReadOnlyMemory<byte>> _requests = [];
    private bool _frozen;
    private int _executing;
    private long _bytes;
    private DatabaseDistributedCommitReceipt? _receipt;

    internal PartitionEntityTransaction(HttpPartitionEntityClient client, HttpClient http, Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A transaction identity is required.", nameof(id));
        }

        _client = client;
        _http = http;
        TransactionId = id;
        _semanticOperation = new(id, DateTimeOffset.UtcNow);
    }

    /// <summary>The identity shared by every change and the final outcome.</summary>
    public Guid TransactionId
    {
        get;
    }

    /// <summary>The number of captured saves and deletes.</summary>
    public int Count => _requests.Count;

    /// <summary>Captures a save using the profile's conventional mapping.</summary>
    public PartitionEntityTransaction Save<T>(T entity) where T : class
    {
        return _client.Profile switch
        {
            DatabaseProfileId.PropertyGraph => Add(new HttpGraphEntityClient(_http, _client.Generation).EntityStore<T>()
                .CaptureSave(entity, new(TransactionId)).Export()),
            DatabaseProfileId.Rdf => Add(new HttpRdfEntityClient(_http, _client.Generation).EntityStore<T>()
                .CaptureSave(entity, new(TransactionId)).Export()),
            DatabaseProfileId.Semantic => Add(new HttpSemanticEntityClient(_http, _client.Generation).EntityStore<T>()
                .CaptureSave(entity, _semanticOperation).Export()),
            _ => throw new NotSupportedException("Unsupported transaction profile.")
        };
    }

    /// <summary>Captures a Graph save using an explicit map.</summary>
    public PartitionEntityTransaction Save<T>(T entity, GraphEntityMap<T> map)
    {
        Require(DatabaseProfileId.PropertyGraph);
        return Add(new HttpGraphEntityClient(_http, _client.Generation)
        .EntityStore(map).CaptureSave(entity, new(TransactionId)).Export());
    }

    /// <summary>Captures an RDF save using an explicit map.</summary>
    public PartitionEntityTransaction Save<T>(T entity, RdfEntityMap<T> map)
    {
        Require(DatabaseProfileId.Rdf);
        return Add(new HttpRdfEntityClient(_http, _client.Generation)
        .EntityStore(map).CaptureSave(entity, new(TransactionId)).Export());
    }

    /// <summary>Captures a Semantic save using an explicit map and source application.</summary>
    public PartitionEntityTransaction Save<T>(T entity, SemanticAggregateMap<T> map,
        SemanticApplicationIdentity? application = null, SemanticSourceProgressPrecondition? guard = null) where T : class
    {
        Require(DatabaseProfileId.Semantic);
        return Add(new HttpSemanticEntityClient(_http, _client.Generation)
        .EntityStore(map, application).CaptureSave(entity, _semanticOperation, guard).Export());
    }

    /// <summary>Captures deletion using the profile's conventional mapping.</summary>
    public PartitionEntityTransaction Delete<T>(object identity) where T : class
    {
        return _client.Profile switch
        {
            DatabaseProfileId.PropertyGraph => Add(new HttpGraphEntityClient(_http, _client.Generation).EntityStore<T>()
                .CaptureDelete(identity, new(TransactionId)).Export()),
            DatabaseProfileId.Rdf => Add(new HttpRdfEntityClient(_http, _client.Generation).EntityStore<T>()
                .CaptureDelete(identity, new(TransactionId)).Export()),
            DatabaseProfileId.Semantic => Add(new HttpSemanticEntityClient(_http, _client.Generation).EntityStore<T>()
                .CaptureDelete(identity, _semanticOperation).Export()),
            _ => throw new NotSupportedException("Unsupported transaction profile.")
        };
    }

    /// <summary>Captures a Graph deletion using an explicit map.</summary>
    public PartitionEntityTransaction Delete<T>(object identity, GraphEntityMap<T> map)
    {
        Require(DatabaseProfileId.PropertyGraph);
        return Add(new HttpGraphEntityClient(_http, _client.Generation)
        .EntityStore(map).CaptureDelete(identity, new(TransactionId)).Export());
    }

    /// <summary>Captures an RDF deletion using an explicit map.</summary>
    public PartitionEntityTransaction Delete<T>(object identity, RdfEntityMap<T> map)
    {
        Require(DatabaseProfileId.Rdf);
        return Add(new HttpRdfEntityClient(_http, _client.Generation)
        .EntityStore(map).CaptureDelete(identity, new(TransactionId)).Export());
    }

    /// <summary>Captures a Semantic deletion using an explicit map and source application.</summary>
    public PartitionEntityTransaction Delete<T>(object identity, SemanticAggregateMap<T> map,
        SemanticApplicationIdentity? application = null, SemanticSourceProgressPrecondition? guard = null) where T : class
    {
        Require(DatabaseProfileId.Semantic);
        return Add(new HttpSemanticEntityClient(_http, _client.Generation)
        .EntityStore(map, application).CaptureDelete(identity, _semanticOperation, guard).Export());
    }

    /// <summary>Returns owned captured requests for durable retention of this exact transaction.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Capture()
    {
        if (_requests.Count == 0)
        {
            throw new InvalidOperationException("A transaction requires at least one change.");
        }

        _frozen = true;
        return _requests.Select(r => (ReadOnlyMemory<byte>)r.ToArray()).ToArray();
    }

    /// <summary>Commits all captured changes once. No member can succeed independently.</summary>
    public async ValueTask<DatabaseDistributedCommitReceipt> CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _executing, 1) != 0)
        {
            throw new InvalidOperationException("A transaction cannot execute concurrent commit attempts.");
        }

        try
        {
            return _receipt ??= await _client.DeliverAsync(TransactionId, Capture(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _executing, 0);
        }
    }

    private void Require(DatabaseProfileId profile)
    {
        if (_client.Profile != profile)
        {
            throw new InvalidOperationException("The mapping belongs to another profile.");
        }
    }

    private PartitionEntityTransaction Add(byte[] owned)
    {
        if (_frozen)
        {
            throw new InvalidOperationException("A captured transaction cannot be changed.");
        }

        if (_requests.Count >= PartitionApplicationBinding.MaximumRequests ||
            owned.Length > PartitionApplicationBinding.MaximumRequestBytes - _bytes)
        {
            throw new InvalidOperationException("The transaction exceeds its request or byte bound.");
        }

        _requests.Add(owned);
        _bytes += owned.Length;
        return this;
    }
}
