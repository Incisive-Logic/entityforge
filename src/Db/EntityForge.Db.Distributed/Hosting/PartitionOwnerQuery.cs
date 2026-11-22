using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using System.Text.Json;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Query;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionOwnedNode
{
    // Complete operators may execute locally only when routing proves their
    // entire domain is local. Multi-owner joins/aggregates retain global evaluation.
    private async ValueTask<PartitionProjectionValue?> TryExecuteOwnerQueryAsync(DatabaseVersion version,
        PartitionQueryExchangeRequest request, CancellationToken token)
    {
        if (Routing.Shards.Count != 1 || request.GraphFrontier is not null)
        {
            throw new InvalidDataException("Complete query execution requires the complete owner domain.");
        }

        object result;
        if (_native.Database is GraphDatabaseEngine graph)
        {
            var compiled = PartitionQueryCompilation.CompileGraph(request.Statement!);
            var view = graph.CaptureDistributedQueryView(version);
            ValueTask<EntityForge.Query.Results.QueryResultEnvelope> Execute(CompiledGraphGqlQuery branch) =>
                new BoundedGraphPatternExecutor(null, capturedView: view).ExecuteCapturedResultAsync(
                    branch.Query, new(version, view, view.StatisticsFor(version)), token,
                    branch.Projection is null ? null : property => GraphGqlPropertyProjectionShaper.Project(branch.Projection, property), request.IncludeSupportingGraph, branch.StartCandidates);
            var answer = await compiled.ExecuteAsync(Execute, token);
            answer = GraphGqlCompletion.Complete(answer, compiled.OmitsResult);
            result = !request.IncludeSupportingGraph && (compiled.Union is not null || compiled.Pipeline is not null || compiled.Projection is not null || answer.Result is EntityForge.Query.Results.RecordResult)
                ? GraphPatternQueryResponseMapper.Project(answer)
                : GraphPatternQueryResponseMapper.Map(answer, 0);
        }
        else if (_native.Database is RdfDatabaseEngine rdf)
        {
            using var snapshot = _timestampProjection.Value && rdf is RdfDatabaseEngine timestampRdf
                ? timestampRdf.AcquireTimestampProjectionLease(version) : await rdf.AcquireSnapshotAsync(version, token);
            token.ThrowIfCancellationRequested();
            result = RdfSparql.ExecuteAtSnapshot(rdf, request.Statement!, snapshot) switch
            {
                RdfSparqlSelectResult select => new { form = "select", bindings = select.Bindings },
                RdfSparqlAskResult ask => (object)new { form = "ask", value = ask.Value },
                RdfSparqlGraphResult answer => new { form = answer.Form.ToString().ToLowerInvariant(), quads = answer.Quads },
                _ => throw new InvalidDataException("Unknown query result.")
            };
            token.ThrowIfCancellationRequested();
        }
        else if (_native.Database is SemanticDatabaseEngine semantic)
        {
            using var snapshot = await semantic.AcquireDistributedStateAsync(version, token);
            var compiled = new PortableEntityQueryRequestCompiler().Compile(request.EntityQuery!);
            var answer = await new PortableEntityQueryExecutor().ExecuteAsync(snapshot.State, snapshot.RequireReadView(semantic).EntityAccessPaths,
                compiled.Query, compiled.ProjectionOptions, snapshot.State.Version, token);
            if (answer.NextContinuation is not null)
            {
                throw new NotSupportedException("Native continuation cannot escape the distributed query.");
            }

            result = PortableEntityQueryResponseMapper.Map(answer, 0);
        }
        else
        {
            throw new NotSupportedException("Unknown query profile.");
        }
        // A large result retains the bounded exchange/streaming fallback. Failure
        // to fit is never truncation and never changes the query's LIMIT semantics.
        try
        {
            var value = result is GraphPatternQueryResponse or PortableEntityQueryResponse
                ? PartitionPublicQueryProjection.WithoutNativeSnapshot(result) : new PartitionProjectionValue(result);
            var page = new PartitionQueryExchangePage(new(), null, value);
            PartitionProjectionValue.Measure(page, PartitionQueryLimits.ExchangePageBytes, PartitionQueryJson.Options);
            return new(page, PartitionQueryJson.Options);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
