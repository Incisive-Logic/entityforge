using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Hosting;

internal sealed class HostedEntityApplications : IAsyncDisposable
{
    private readonly IGraphEntityClient? _graph;
    private readonly IRdfEntityClient? _rdf;
    private readonly ISemanticEntityClient? _semantic;

    internal IGraphEntityClient Graph => _graph ?? throw new InvalidOperationException("Not a Graph application destination.");

    internal IRdfEntityClient Rdf => _rdf ?? throw new InvalidOperationException("Not an RDF application destination.");

    internal ISemanticEntityClient Semantic => _semantic ?? throw new InvalidOperationException("Not a Semantic application destination.");

    internal DatabaseProfileId Profile
    {
        get;
    }

    internal Guid DatabaseIncarnation
    {
        get;
    }

    internal HostedEntityReadScopes ReadScopes { get; } = new();

    internal EntityPreparationHost<GraphEntityWrite, DatabaseCommitReceipt>? GraphPreparations
    {
        get;
    }

    internal EntityPreparationHost<RdfEntityWrite, DatabaseCommitReceipt>? RdfPreparations
    {
        get;
    }

    internal EntityPreparationHost<SemanticEntityWrite, SessionCommitResult>? SemanticPreparations
    {
        get;
    }

    internal HostedEntityApplications(IGraphEntityClient graph)
    {
        _graph = graph;
        Profile = DatabaseProfileId.PropertyGraph;
        DatabaseIncarnation = graph.DatabaseIncarnation;
        GraphPreparations = new(DatabaseIncarnation, HostedEntityReadScopes.MaximumScopes,
            HostedEntityReadScopes.MaximumDuration, graph.DeliverAsync, receipt: result => result);
    }

    internal HostedEntityApplications(IRdfEntityClient rdf)
    {
        _rdf = rdf;
        Profile = DatabaseProfileId.Rdf;
        DatabaseIncarnation = rdf.DatabaseIncarnation;
        RdfPreparations = new(DatabaseIncarnation, HostedEntityReadScopes.MaximumScopes, HostedEntityReadScopes.MaximumDuration, rdf.DeliverAsync, receipt: result => result);
    }

    internal HostedEntityApplications(ISemanticEntityClient semantic)
    {
        _semantic = semantic;
        Profile = DatabaseProfileId.Semantic;
        DatabaseIncarnation = semantic.DatabaseIncarnation;
        SemanticPreparations = new(DatabaseIncarnation, HostedEntityReadScopes.MaximumScopes, HostedEntityReadScopes.MaximumDuration, semantic.DeliverAsync, receipt: result => result.Receipt);
    }

    internal async ValueTask<byte[]> PrepareRdfAsync(string owner, Guid operation, Func<CancellationToken, ValueTask<RdfEntityWrite>> prepare, CancellationToken token) =>
        EntityPreparationToken.Encode(await RdfPreparations!.AcquireAsync(owner, operation, HostedEntityReadScopes.MaximumDuration, prepare, token), Profile);

    internal async ValueTask<byte[]> PrepareSemanticAsync(string owner, Guid operation, Func<CancellationToken, ValueTask<SemanticEntityWrite>> prepare, CancellationToken token) =>
        EntityPreparationToken.Encode(await SemanticPreparations!.AcquireAsync(owner, operation, HostedEntityReadScopes.MaximumDuration, prepare, token), Profile);

    internal async ValueTask<byte[]> PrepareGraphAsync(string owner, Guid operation, Func<CancellationToken, ValueTask<GraphEntityWrite>> prepare, CancellationToken token)
    {
        // Reserve ownership before acquisition. Both portable and retained writes use this path;
        // retaining portable preparations also gives HTTP clients one release contract.
        var handle = await GraphPreparations!.AcquireAsync(owner, operation, HostedEntityReadScopes.MaximumDuration, prepare, token).ConfigureAwait(false);
        return EntityPreparationToken.Encode(handle, Profile);
    }

    public async ValueTask DisposeAsync()
    {
        List<Exception> failures = [];
        try
        {
            await ReadScopes.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
        if (GraphPreparations is not null)
        {
            try
            {
                await GraphPreparations.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }

        if (RdfPreparations is not null)
        {
            try
            {
                await RdfPreparations.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }

        if (SemanticPreparations is not null)
        {
            try
            {
                await SemanticPreparations.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }

        if (failures.Count != 0)
        {
            throw new AggregateException(failures);
        }
    }
}
