using System.Text.Json;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    // One execution owns the request budget and remote blank-node scopes. Reusing
    // a clause's relation for duplicate endpoint bindings preserves input bags.
    private sealed class ServiceExecution(RdfSparqlHttpResources? resources, string? baseIri)
    {
        private readonly Dictionary<(Guid Clause, RdfTerm? Endpoint, string Body), ValuesElement> _results = [];
        private int _calls;

        internal async ValueTask<ValuesElement> FetchAsync(ServiceElement service, RdfTerm? endpoint,
            IReadOnlyDictionary<string, RdfTerm>? correlation, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            string body = correlation is null || correlation.Count == 0 ? service.RemoteBody : new ServiceSubstitution(correlation, service.RemoteBody).Body(service.Group);
            var key = (service.Identity, endpoint, body);
            if (_results.TryGetValue(key, out var cached))
            {
                return cached;
            }
            if (++_calls > 16)
            {
                throw new RdfSparqlWorkLimitException("A query exceeds sixteen SERVICE calls.");
            }
            ValuesElement result;
            try
            {
                if (resources is null || endpoint is not RdfIri iri)
                {
                    throw new InvalidDataException("SERVICE requires a bound endpoint IRI and configured HTTP endpoints.");
                }
                string remote = (baseIri is null ? "" : "BASE <" + baseIri + "> ") + "SELECT * WHERE " + body;
                var response = await resources.QueryAsync(new Uri(iri.Value), remote, cancellation).ConfigureAwait(false);
                result = response.IsXml ? ParseServiceXmlResult(response.Body, response.Charset) : ParseServiceResult(response.Body);
            }
            catch (Exception failure) when (!cancellation.IsCancellationRequested &&
                failure is HttpRequestException or IOException or InvalidDataException or JsonException or System.Xml.XmlException or ArgumentException or OperationCanceledException or KeyNotFoundException or InvalidOperationException &&
                failure is not RdfSparqlWorkLimitException)
            {
                if (!service.Silent)
                {
                    throw new RdfSparqlRemoteExecutionException("SERVICE", failure);
                }
                result = new([], [Array.Empty<RdfTerm?>()]);
            }
            _results.Add(key, result);
            return result;
        }
    }

    private static async ValueTask<List<Dictionary<string, RdfTerm>>> EvaluateServiceGroupAsync(
        RdfSparqlReadView database, Group group, List<Dictionary<string, RdfTerm>> rows,
        IReadOnlyDictionary<string, RdfTerm>? correlation, ServiceExecution execution)
    {
        database.CheckCancellation();
        if (!Services(group).Any())
        {
            return EvaluateGroup(database, group, rows, correlation);
        }
        foreach (var element in group.Elements)
        {
            database.CheckCancellation();
            if (rows.Count == 0)
            {
                break;
            }
            switch (element)
            {
                case ServiceElement service:
                    var fetched = new List<Dictionary<string, RdfTerm>>();
                    foreach (var row in rows)
                    {
                        var values = await execution.FetchAsync(service, Resolve(service.Endpoint, row), correlation, database.Cancellation).ConfigureAwait(false);
                        foreach (var match in EvaluateValues(values, [row], database.Cancellation))
                        {
                            AddBounded(fetched, match);
                        }
                    }
                    rows = fetched;
                    break;
                case NamedGraphElement named:
                    rows = await EvaluateServiceNamedGraphAsync(database, named, rows, correlation, execution).ConfigureAwait(false);
                    break;
                case NestedGroupElement nested:
                    rows = JoinSolutions(rows, await EvaluateServiceGroupAsync(database, nested.Group,
                        [CorrelationSeed(correlation)], correlation, execution).ConfigureAwait(false), database.Cancellation);
                    break;
                case OptionalElement optional:
                    var candidates = await EvaluateServiceGroupAsync(database, optional.Group with
                    {
                        Filters = []
                    },
                        [CorrelationSeed(correlation)], correlation, execution).ConfigureAwait(false);
                    var optionalRows = new List<Dictionary<string, RdfTerm>>();
                    foreach (var row in rows)
                    {
                        var matches = await FilterServiceRowsAsync(database, JoinSolutions([row], candidates, database.Cancellation),
                            optional.Group.Filters, execution).ConfigureAwait(false);
                        if (matches.Count == 0)
                        {
                            AddBounded(optionalRows, row);
                        }
                        foreach (var match in matches)
                        {
                            AddBounded(optionalRows, match);
                        }
                    }
                    rows = optionalRows;
                    break;
                case UnionElement union:
                    var unionRows = new List<Dictionary<string, RdfTerm>>();
                    foreach (var branch in union.Groups)
                    {
                        var selected = await EvaluateServiceGroupAsync(database, branch,
                            [CorrelationSeed(correlation)], correlation, execution).ConfigureAwait(false);
                        foreach (var match in JoinSolutions(rows, selected, database.Cancellation))
                        {
                            AddBounded(unionRows, match);
                        }
                    }
                    rows = unionRows;
                    break;
                case MinusElement minus:
                    var excluded = await EvaluateServiceGroupAsync(database, minus.Group,
                        [CorrelationSeed(correlation)], correlation, execution).ConfigureAwait(false);
                    rows = MinusSolutions(rows, excluded, correlation, database.Cancellation);
                    break;
                case SubqueryElement subquery:
                    var inner = await EvaluateServiceGroupAsync(database, subquery.Query.Body,
                        [CorrelationSeed(correlation)], correlation, execution).ConfigureAwait(false);
                    rows = JoinSolutions(rows, await CompleteServiceSelectAsync(database, inner, subquery.Query, execution).ConfigureAwait(false), database.Cancellation);
                    break;
                case BindElement bind:
                    rows = await BindServiceRowsAsync(database, bind, rows, execution).ConfigureAwait(false);
                    break;
                default:
                    rows = EvaluateGroup(database, new([element], []), rows, correlation);
                    break;
            }
        }
        return await FilterServiceRowsAsync(database, rows, group.Filters, execution).ConfigureAwait(false);
    }

    private static async ValueTask<List<Dictionary<string, RdfTerm>>> EvaluateServiceNamedGraphAsync(
        RdfSparqlReadView database, NamedGraphElement named, IReadOnlyList<Dictionary<string, RdfTerm>> rows,
        IReadOnlyDictionary<string, RdfTerm>? correlation, ServiceExecution execution)
    {
        var selected = new List<Dictionary<string, RdfTerm>>();
        foreach (var graph in database.NamedGraphs(named.Graph))
        {
            database.CheckCancellation();
            var seed = CorrelationSeed(correlation);
            if (!Bind(named.Graph, graph, seed))
            {
                continue;
            }
            using var active = database.WithActiveGraph(graph);
            var matches = await EvaluateServiceGroupAsync(active, named.Group,
                [CorrelationSeed(correlation)], correlation, execution).ConfigureAwait(false);
            foreach (var row in JoinSolutions([seed], matches, database.Cancellation))
            {
                AddBounded(selected, row);
            }
        }
        return JoinSolutions(rows, selected, database.Cancellation);
    }
}
