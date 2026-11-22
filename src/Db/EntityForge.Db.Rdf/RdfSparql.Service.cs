using System.Text.Json;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private sealed record PreparedSparqlQuery(Group Body, Modifiers Modifiers, SelectProjection? Projection,
        Func<RdfSparqlQueryResult> Run);

    internal static bool UsesService(string query) => Token().Matches(NormalizeQueryLexicalForm(query))
        .Any(token => token.Value.Equals("SERVICE", StringComparison.OrdinalIgnoreCase));

    private static void RequireSynchronousQuery(string query)
    {
        if (UsesService(query))
        {
            throw new NotSupportedException("SERVICE requires asynchronous query execution with configured HTTP endpoints.");
        }
    }

    internal static async ValueTask<RdfSparqlQueryResult> ExecuteAtSnapshotAsync(RdfDatabaseEngine database, string query,
        RdfSnapshotLease snapshot, RdfSparqlHttpResources? resources, CancellationToken cancellation)
    {
        using var view = new RdfSparqlReadView(database, snapshot, cancellation: cancellation);
        return await ExecuteViewAsync(view, query, resources).ConfigureAwait(false);
    }

    internal static async ValueTask<RdfSparqlQueryResult> ExecuteRelationAsync(IReadOnlyList<RdfQuad> relation, string query,
        RdfSparqlHttpResources? resources, CancellationToken cancellation)
    {
        using var view = new RdfSparqlReadView(null, null, relation, cancellation);
        return await ExecuteViewAsync(view, query, resources).ConfigureAwait(false);
    }

    private static async ValueTask<RdfSparqlQueryResult> ExecuteViewAsync(RdfSparqlReadView view, string query,
        RdfSparqlHttpResources? resources)
    {
        view.CheckCancellation();
        if (!UsesService(query))
        {
            return ExecuteView(view, query);
        }
        if (ParseDataset(query) is { } dataset)
        {
            using var selected = DatasetView(view, dataset);
            return await ExecuteViewAsync(selected, dataset.Query, resources).ConfigureAwait(false);
        }
        PreparedSparqlQuery prepared;
        ParsedSelectQuery? select = null;
        switch (QueryForm(query))
        {
            case RdfSparqlQueryForm.Select:
                var plan = GetSelectPlan(query);
                if (UsesQueryClock(query))
                {
                    plan = new QueryFunctionBindings().Plan(plan);
                }
                prepared = new(plan.Body, plan.Modifiers, plan.Projection, () => new RdfSparqlSelectResult(view.Version, ExecuteSelect(view, plan)));
                select = plan;
                break;
            case RdfSparqlQueryForm.Ask:
                prepared = PrepareAsk(view, query);
                break;
            case RdfSparqlQueryForm.Construct:
                prepared = PrepareConstruct(view, query);
                break;
            case RdfSparqlQueryForm.Describe:
                prepared = PrepareDescribe(view, query);
                break;
            default:
                throw new NotSupportedException("Unsupported SERVICE query form.");
        }
        var services = Services(prepared.Body).Concat(Services(prepared.Modifiers))
            .Concat(prepared.Projection is { } projection ? Services(projection) : []).DistinctBy(service => service.Identity).ToArray();
        if (services.Length > 16)
        {
            throw new RdfSparqlWorkLimitException("A query exceeds sixteen SERVICE calls.");
        }
        var execution = new ServiceExecution(resources, ParseProlog(query).Prefixes.GetValueOrDefault("$base"));
        Group body = prepared.Projection is null ? ApplyGroupingExpressions(prepared.Body, prepared.Modifiers) : prepared.Body;
        view.RootRows = await EvaluateServiceGroupAsync(view, body,
            [new(StringComparer.Ordinal)], null, execution).ConfigureAwait(false);
        view.CheckCancellation();
        if (select is not null)
        {
            return new RdfSparqlSelectResult(view.Version,
                await CompleteServiceSelectAsync(view, view.RootRows, select, execution).ConfigureAwait(false));
        }
        var (graphProjection, graphModifiers) = PrepareExpressions(new(false, [], [], []), prepared.Modifiers);
        var completed = await CompleteServiceRowsAsync(view, view.RootRows, graphProjection, graphModifiers, execution).ConfigureAwait(false);
        view.CompletedGraphRows = completed.Skip(graphModifiers.Offset).Take(graphModifiers.Limit).ToList();
        return prepared.Run();
    }

    private static ValuesElement ParseServiceResult(byte[] bytes)
    {
        // Classify depth as a resource limit before DOM parsing. A generic
        // JsonException is a remote format failure and can be silenced, whereas
        // the query's nesting budget must remain effective under SERVICE SILENT.
        var depthCheck = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = Int32.MaxValue });
        while (depthCheck.Read())
        {
            if (depthCheck.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && depthCheck.CurrentDepth >= 16)
            {
                throw new RdfSparqlWorkLimitException("SERVICE JSON exceeds sixteen nesting levels.");
            }
        }
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        RequireObject(root);
        if (root.TryGetProperty("boolean", out _))
        {
            throw new InvalidDataException("SERVICE SELECT results cannot contain an ASK boolean result.");
        }
        RequireObject(root.GetProperty("head"));
        RequireObject(root.GetProperty("results"));
        string[] variables = root.GetProperty("head").GetProperty("vars").EnumerateArray().Select(value => value.GetString()!).ToArray();
        if (variables.Length > 256)
        {
            throw new RdfSparqlWorkLimitException("SERVICE exceeds 256 result columns.");
        }
        if (variables.Any(variable => variable is null ||
                !System.Text.RegularExpressions.Regex.IsMatch(variable, "\\A" + VariableName + "\\z")) || variables.Distinct(StringComparer.Ordinal).Count() != variables.Length)
        {
            throw new InvalidDataException("Invalid SERVICE result variables.");
        }
        var rows = new List<IReadOnlyList<RdfTerm?>>();
        var blanks = new Dictionary<string, RdfBlankNode>(StringComparer.Ordinal);
        foreach (var row in root.GetProperty("results").GetProperty("bindings").EnumerateArray())
        {
            RequireObject(row);
            if (rows.Count >= MaximumResults)
            {
                throw new RdfSparqlWorkLimitException("SERVICE exceeds 10,000 result rows.");
            }
            var properties = row.EnumerateObject().ToArray();
            if (properties.Any(property => !variables.Contains(property.Name, StringComparer.Ordinal)) ||
                properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
            {
                throw new InvalidDataException("Invalid SERVICE result bindings.");
            }
            rows.Add(variables.Select(variable => row.TryGetProperty(variable, out var value) ? Term(value) : null).ToArray());
        }
        return new(variables, rows);

        RdfTerm Term(JsonElement value)
        {
            RequireObject(value);
            string lexical = value.GetProperty("value").GetString() ?? throw new InvalidDataException("Missing SERVICE term value.");
            string? type = value.GetProperty("type").GetString();
            bool hasDatatype = value.TryGetProperty("datatype", out var datatype);
            bool hasLanguage = value.TryGetProperty("xml:lang", out var language);
            if (type is "uri" or "bnode" && (hasDatatype || hasLanguage))
            {
                throw new InvalidDataException("A SERVICE IRI or blank node cannot carry literal metadata.");
            }
            if (type == "literal" && (hasDatatype && hasLanguage ||
                hasDatatype && datatype.ValueKind != JsonValueKind.String ||
                hasLanguage && language.ValueKind != JsonValueKind.String))
            {
                throw new InvalidDataException("A SERVICE literal requires either a string datatype or a string language tag, not both.");
            }
            if (hasLanguage && language.ValueKind == JsonValueKind.String && !RdfTermValidation.IsLanguageTag(language.GetString()!))
            {
                throw new InvalidDataException("A SERVICE literal contains an invalid language tag.");
            }
            return type switch
            {
                "uri" => new RdfIri(lexical),
                "bnode" when !String.IsNullOrWhiteSpace(lexical) => blanks.TryGetValue(lexical, out var blank) ? blank : blanks[lexical] = FreshBlankNode(),
                "literal" => new RdfLiteral(lexical,
                    hasDatatype ? datatype.GetString() : null,
                    hasLanguage ? language.GetString() : null),
                // Older endpoints (including dotNetRDF) use the pre-1.1
                // typed-literal discriminator with the same datatype payload.
                "typed-literal" when value.TryGetProperty("datatype", out var legacyDatatype) &&
                    legacyDatatype.ValueKind == JsonValueKind.String && !value.TryGetProperty("xml:lang", out _) =>
                    new RdfLiteral(lexical, legacyDatatype.GetString()),
                _ => throw new InvalidDataException("Unsupported SERVICE result term.")
            };
        }

        static void RequireObject(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Select(property => property.Name)
                .Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count())
            {
                throw new InvalidDataException("A SERVICE result contains an invalid or ambiguous object.");
            }
        }
    }

    private static IEnumerable<ServiceElement> Services(Group group) => group.Elements.SelectMany(element => element switch
    {
        ServiceElement service => new[] { service },
        NestedGroupElement nested => Services(nested.Group),
        OptionalElement optional => Services(optional.Group),
        MinusElement minus => Services(minus.Group),
        UnionElement union => union.Groups.SelectMany(Services),
        SubqueryElement subquery => Services(subquery.Query.Body).Concat(Services(subquery.Query.Projection)).Concat(Services(subquery.Query.Modifiers)),
        BindElement bind => Services(bind.Value),
        _ => []
    }).Concat(group.Filters.SelectMany(Services));

    private static IEnumerable<ServiceElement> Services(SelectProjection projection) =>
        projection.Computed.SelectMany(bind => Services(bind.Value)).Concat(projection.Aggregates.SelectMany(aggregate => Services(aggregate.Value)));

    private static IEnumerable<ServiceElement> Services(Modifiers modifiers) => Services(modifiers.Having)
        .Concat(modifiers.Order.SelectMany(order => Services(order.Value))).Concat(modifiers.GroupExpressions.SelectMany(bind => Services(bind.Value)));

    private static IEnumerable<ServiceElement> Services(ValueExpression? expression) => expression switch
    {
        ArithmeticExpression arithmetic => Services(arithmetic.Left).Concat(Services(arithmetic.Right)),
        ValueFunctionExpression function => Services(function.Argument),
        CallExpression call => call.Arguments.SelectMany(Services),
        AggregateValueExpression aggregate => Services(aggregate.Aggregate.Value),
        BooleanValueExpression boolean => Services(boolean.Filter),
        ConditionalExpression conditional => Services(conditional.Condition).Concat(Services(conditional.True)).Concat(Services(conditional.False)),
        _ => []
    };

    private static IEnumerable<ServiceElement> Services(FilterExpression? expression) => expression switch
    {
        ExistsExpression exists => Services(exists.Group),
        EffectiveBooleanExpression boolean => Services(boolean.Value),
        ComparisonExpression comparison => Services(comparison.Left).Concat(Services(comparison.Right)),
        BinaryBooleanExpression binary => Services(binary.Left).Concat(Services(binary.Right)),
        NotExpression not => Services(not.Operand),
        FunctionExpression function => Services(function.First).Concat(Services(function.Second)).Concat(Services(function.Third)),
        InExpression values => Services(values.Operand).Concat(values.Values.SelectMany(Services)),
        _ => []
    };

}
