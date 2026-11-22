using EntityForge.Db.Semantic.Query;
using EntityForge.Db.Query.Adapters;
using System.Globalization;
using System.Text.Json;
using EntityForge.Db.Semantic;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Query.Projection;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Execution.Internal;
using HotChocolate.Execution.Processing;
using HotChocolate.Resolvers;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using EntityForge.Db.Client;
using System.Text.Json.Nodes;

namespace EntityForge.Db.Hosting;

/// <summary>Schema execution shapes an already materialized Semantic result; child resolvers do no I/O.</summary>
internal static class SemanticGraphQlRuntime
{
    internal const string SchemaName = "EntityForgeSemantic";

    internal static void Register(IServiceCollection services)
    {
        using var stream = typeof(SemanticGraphQlRuntime).Assembly.GetManifestResourceStream("SemanticGraphQl.schema.graphql")!;
        using var reader = new StreamReader(stream);
        string schema = reader.ReadToEnd();
        var builder = services.AddGraphQL(SchemaName)
            .AddMaxExecutionDepthRule(32)
            .ConfigureSchema(b => b.AddDocumentFromString(schema))
            .AddType(new StringType())
            .AddType(new GraphQlTextScalar("URI", text => Uri.TryCreate(text, UriKind.Absolute, out _)))
            .AddType(new GraphQlTextScalar("UUID", text => Guid.TryParse(text, out _)))
            .AddType(new GraphQlTextScalar("DateTime", text => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out _)))
            .AddType(new LongType())
            .AddType(new AnyType("JSON", null, BindingBehavior.Explicit))
            .AddType(new SemanticLiteralScalar())
            .AddErrorFilter<GraphQlErrors>()
            .AddResolver("Query", "entities", EntitiesAsync)
            .AddResolver("Query", "aggregateEntities", AggregateAsync)
            .AddResolver("Mutation", "applySemanticChanges", SemanticGraphQlMutations.ApplyAsync);
        SemanticGraphQlMutations.Bind(builder);

        // Explicit bindings preserve Semantic values and let GraphQL own aliases,
        // fragments, directives and exact response selection at every level.
        builder.AddResolver("EntityPage", "nodes", c => c.Parent<PortableEntityQueryResponse>().Entities)
            .AddResolver("EntityPage", "pageInfo", c => c.Parent<PortableEntityQueryResponse>())
            .AddResolver("EntityPage", "evidence", c => c.Parent<PortableEntityQueryResponse>().Evidence)
            .AddResolver("PageInfo", "endCursor", c => c.Parent<PortableEntityQueryResponse>().NextContinuation)
            .AddResolver("PageInfo", "hasNextPage", c => c.Parent<PortableEntityQueryResponse>().NextContinuation is not null)
            .AddResolver("PageInfo", "snapshotVersion", c => c.Parent<PortableEntityQueryResponse>().SnapshotVersion)
            .AddResolver("Entity", "identity", c => c.Parent<PortableEntityNodeResponse>().Identity)
            .AddResolver("Entity", "types", c => c.Parent<PortableEntityNodeResponse>().Types)
            .AddResolver("Entity", "field", c => c.Parent<PortableEntityNodeResponse>().Fields.GetValueOrDefault(Key(c.Selection)) ?? [])
            .AddResolver("FieldValues", "values", c => c.Parent<IReadOnlyList<PortableEntityValueResponse>>())
            .AddResolver("Entity", "relationship", c => c.Parent<PortableEntityNodeResponse>().Relationships.GetValueOrDefault(Key(c.Selection)) ?? [])
            .AddResolver("RelatedEntities", "nodes", c => c.Parent<IReadOnlyList<PortableEntityItemResponse>>()
                .Select(i => i.Entity ?? new PortableEntityNodeResponse(i.Identity, [],
                    new Dictionary<string, IReadOnlyList<PortableEntityValueResponse>>(),
                    new Dictionary<string, IReadOnlyList<PortableEntityItemResponse>>())).ToArray());

        BindProperties<PortableEntityValueResponse>(builder, "SemanticValue");
        BindProperties<PortableEvidenceResponse>(builder, "Evidence");
        BindProperties<PortableSemanticAggregationResponse>(builder, "AggregateResult", "QueryId");
        BindProperties<PortableSemanticAggregationRowResponse>(builder, "AggregateRow");
    }

    private static void BindProperties<T>(HotChocolate.Execution.Configuration.IRequestExecutorBuilder builder,
        string type, params string[] excluded)
    {
        foreach (var property in typeof(T).GetProperties().Where(p => !excluded.Contains(p.Name)))
        {
            string field = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            builder.AddResolver(type, field, c => property.GetValue(c.Parent<T>()));
        }
    }

    private static async ValueTask<object?> EntitiesAsync(IResolverContext context)
    {
        RequireSingleDataRoot(context);
        string arguments = Arguments(context, context.Selection);
        var compiled = new GraphQlEntityQueryCompiler(null).Compile(
            "{ entities(" + arguments + ") { nodes { identity } } }", null);
        var names = new Dictionary<string, string>();
        var request = compiled.Request with
        {
            Projection = Projection(context, compiled.Request.RootConcept,
            context.GetSelections(context.Schema.GetType<ObjectType>("EntityPage"), context.Selection), 0, names)
        };
        var service = new SemanticEntityQueryApplicationService(context.Service<IGraphQlApplicationServices>().Semantic);
        var query = await service.CompileAsync(request, context.RequestAborted).ConfigureAwait(false);
        var result = await service.ExecuteAsync(query, context.RequestAborted).ConfigureAwait(false);
        if (context.ContextData.TryGetValue("entityforge.document", out var value) && value is GraphQlExecution.DocumentCapture capture)
        {
            var document = result.Projection.Document;
            var renamed = new EntityProjection(document.SourceQueryId, document.Roots.Select(n => GraphQlExecution.Rename(n, names)),
                document.Evidence, document.SemanticModels);
            capture.Document = EntityProjectionTransport.Encode(new EntityProjectionQueryResult(renamed,
                result.SnapshotVersion, result.Projection.NextContinuation));
        }
        return result.ToPortableResponse();
    }

    private static async ValueTask<object?> AggregateAsync(IResolverContext context)
    {
        RequireSingleDataRoot(context);
        var compiled = new GraphQlSemanticAggregationCompiler().Compile(
            "{ aggregateEntities(" + Arguments(context, context.Selection) + ") { inputEntityCount } }", null);
        var runtime = context.Service<IGraphQlApplicationServices>();
        var source = await new SemanticEntityQueryApplicationService(runtime.Semantic)
            .CompileAsync(compiled.Request.Source, context.RequestAborted).ConfigureAwait(false);
        var query = PortableSemanticAggregationMapper.Compile(compiled.Request, source);
        return PortableSemanticAggregationMapper.Map(await runtime.Semantic.AggregateEntitiesAsync(
            query, source.RequestedSnapshot, context.RequestAborted).ConfigureAwait(false));
    }

    private static PortableEntityProjectionRequest Projection(IResolverContext context, string concept,
        IEnumerable<ISelection> container, int depth, Dictionary<string, string> names)
    {
        if (depth > EntityTraversalLimits.MaximumRelationshipDepth)
        {
            throw new GraphQLException("Relationship projection exceeds the Semantic depth bound.");
        }

        var fields = new List<PortableEntityFieldRequest>();
        var relationships = new List<PortableEntityRelationshipRequest>();
        foreach (var selected in container.Where(s => s.Field.Name == "nodes")
            .SelectMany(s => context.GetSelections(context.Schema.GetType<ObjectType>("Entity"), s))
            .DistinctBy(s => s.Id))
        {
            if (selected.Field.Name is not ("field" or "relationship"))
            {
                continue;
            }

            names[Key(selected)] = selected.ResponseName;
            var args = Coerced(context, selected);
            string Required(string name)
            {
                var argument = args[name];
                object? value = argument.IsFullyCoerced ? argument.Value : new InputParser().ParseLiteral(
                    argument.ValueLiteral!, argument.Type, context.Path.Append(name));
                return Convert.ToString(value, CultureInfo.InvariantCulture)!;
            }
            if (selected.Field.Name == "field")
            {
                var providers = args.TryGetValue("preferredProviders", out var value) && value.ValueLiteral is { } literal && literal is not HotChocolate.Language.NullValueNode
                    ? JsonSerializer.Deserialize<string[]>(JsonSerializer.Serialize(value.IsFullyCoerced ? value.Value :
                        new InputParser().ParseLiteral(literal, value.Type, context.Path.Append("preferredProviders")))) : null;
                fields.Add(new(Required("property"), Key(selected), Required("valueSelection"), providers));
            }
            else
            {
                string target = Required("targetConcept");
                relationships.Add(new(Required("property"), Key(selected), Projection(context, target,
                    context.GetSelections(context.Schema.GetType<ObjectType>("RelatedEntities"), selected), depth + 1, names),
                    Required("direction")));
            }
        }
        return new(concept, fields, relationships);
    }

    private static string Key(ISelection selection) => "selection_" + selection.Id.ToString(CultureInfo.InvariantCulture);

    // Version-pinned HC coercion bridge: never interpret variables or fragments independently.
    private static IReadOnlyDictionary<string, ArgumentValue> Coerced(IResolverContext context, ISelection selection) =>
        ArgumentCoercionHelper.TryCoerceArguments(selection.Arguments, context, out var arguments)
            ? arguments : throw new GraphQLException("Invalid field arguments.");

    private static string Arguments(IResolverContext context, ISelection selection) => string.Join(",",
        selection.Field.Arguments.Select(a => (a.Name, Value: context.ArgumentLiteral<HotChocolate.Language.IValueNode>(a.Name)))
            .Where(p => p.Value is not HotChocolate.Language.NullValueNode)
            .Select(p => p.Name + ":" + p.Value.ToString()));

    private static void RequireSingleDataRoot(IResolverContext context)
    {
        if (context.Operation.RootSelectionSet.Selections.Count(s => !s.Field.Name.StartsWith("__", StringComparison.Ordinal)) > 1)
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage("Use one Semantic data root per operation; separate roots do not share an implicit snapshot.")
                .SetCode("GRAPHQL_CAPABILITY_UNAVAILABLE").Build());
        }
    }
}
