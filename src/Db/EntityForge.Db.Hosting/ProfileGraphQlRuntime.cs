using EntityForge.Db.Client;
using System.Globalization;
using System.Text.Json;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Query.Projection;
using EntityForge.Query.Values;
using HotChocolate;
using HotChocolate.Execution.Internal;
using HotChocolate.Execution.Processing;
using HotChocolate.Resolvers;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting;

/// <summary>Schema-backed Graph/RDF projections; children only shape materialized results.</summary>
internal static class ProfileGraphQlRuntime
{
    internal static string SchemaName(DatabaseProfileId profile) => "EntityForge" + profile;

    internal static void Register(IServiceCollection services)
    {
        foreach (var profile in new[] { DatabaseProfileId.PropertyGraph, DatabaseProfileId.Rdf })
        {
            var schema = QuerySchema + ProfileGraphQlMutations.Schema(profile);
            var b = services.AddGraphQL(SchemaName(profile)).AddMaxExecutionDepthRule(32)
                .ConfigureSchema(s => s.AddDocumentFromString(schema))
                .AddType(new LongType()).AddType(new AnyType("JSON", null, BindingBehavior.Explicit))
                .AddType(new GraphQlTextScalar("UUID", t => Guid.TryParse(t, out _)))
                .AddErrorFilter<GraphQlErrors>()
                .AddResolver("Query", "entities", EntitiesAsync)
                .AddResolver("Mutation", profile == DatabaseProfileId.PropertyGraph ? "applyGraphChanges" : "applyRdfChanges",
                    ProfileGraphQlMutations.ApplyAsync);
            b.AddResolver("CommitResult", "transactionId", c => c.Parent<ProfileGraphQlMutations.CommitResult>().TransactionId)
                .AddResolver("CommitResult", "databaseIncarnation", c => c.Parent<ProfileGraphQlMutations.CommitResult>().DatabaseIncarnation)
                .AddResolver("CommitResult", "outcome", c => c.Parent<ProfileGraphQlMutations.CommitResult>().Outcome)
                .AddResolver("CommitResult", "effectCount", c => c.Parent<ProfileGraphQlMutations.CommitResult>().EffectCount)
                .AddResolver("CommitResult", "changeCursor", c => c.Parent<ProfileGraphQlMutations.CommitResult>().ChangeCursor)
                .AddResolver("CommitResult", "version", c => c.Parent<ProfileGraphQlMutations.CommitResult>().Version)
                .AddResolver("CommitResult", "mutationCount", c => c.Parent<ProfileGraphQlMutations.CommitResult>().MutationCount)
                .AddResolver("EntityPage", "nodes", c => c.Parent<EntityProjectionQueryResult>().Document.Roots)
                .AddResolver("EntityPage", "pageInfo", c => c.Parent<EntityProjectionQueryResult>())
                .AddResolver("EntityPage", "evidence", c => c.Parent<EntityProjectionQueryResult>().Document.Evidence)
                .AddResolver("PageInfo", "endCursor", c => c.Parent<EntityProjectionQueryResult>().NextContinuation)
                .AddResolver("PageInfo", "hasNextPage", c => c.Parent<EntityProjectionQueryResult>().NextContinuation is not null)
                .AddResolver("PageInfo", "snapshotVersion", c => c.Parent<EntityProjectionQueryResult>().SnapshotVersion.Value)
                .AddResolver("Entity", "identity", c => Identity(c.Parent<EntityProjectionNode>().Identity))
                .AddResolver("Entity", "types", c => c.Parent<EntityProjectionNode>().Types.Select(t => t.Identifier.AbsoluteUri))
                .AddResolver("Entity", "field", c => c.Parent<EntityProjectionNode>().Fields.SingleOrDefault(f => f.Name == Key(c.Selection))?.Values.Select(v => Value(v.Value)).ToArray() ?? [])
                .AddResolver("FieldValues", "values", c => c.Parent<object[]>())
                .AddResolver("Entity", "relationship", c => c.Parent<EntityProjectionNode>().Relationships.SingleOrDefault(r => r.Name == Key(c.Selection))?.Items ?? [])
                .AddResolver("RelatedEntities", "nodes", c => c.Parent<IEnumerable<EntityProjectionItem>>().Select(i => i is EntityProjectionNode node ? node :
                    new EntityProjectionNode(((EntityProjectionReference)i).Identity, [], [], [])));
        }
    }

    internal static void RequireSingleRoot(IResolverContext c)
    {
        if (c.Operation.RootSelectionSet.Selections.Count(s => !s.Field.Name.StartsWith("__", StringComparison.Ordinal)) != 1)
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage("Use one data root per operation; an explicit mutation batch is one native transaction.")
                .SetCode("GRAPHQL_CAPABILITY_UNAVAILABLE").Build());
        }
    }

    private static async ValueTask<object?> EntitiesAsync(IResolverContext c)
    {
        RequireSingleRoot(c);
        var args = Args(c, c.Selection);
        var names = new Dictionary<string, string>();
        var projection = Projection(c, c.GetSelections(c.Schema.GetType<ObjectType>("EntityPage"), c.Selection), 0, names);
        var request = new EntityProjectionQueryRequest(Required(args, "type", "concept"))
        {
            Identities = c.ArgumentValue<string[]?>("identities"),
            First = c.ArgumentValue<int?>("first"),
            SnapshotVersion = c.ArgumentValue<long?>("snapshotVersion"),
            After = c.ArgumentValue<string?>("after"),
            IncludeEvidence = c.ArgumentValue<bool>("includeEvidence"),
            Fields = projection.Fields,
            Relationships = projection.Relationships
        };
        var result = await c.Service<IGraphQlApplicationServices>().QueryAsync(request, c.RequestAborted).ConfigureAwait(false);
        if (c.ContextData.TryGetValue("entityforge.document", out var capture) && capture is GraphQlExecution.DocumentCapture document)
        {
            document.Document = EntityProjectionTransport.Encode(new EntityProjectionQueryResult(
                new EntityProjection(result.Document.SourceQueryId, result.Document.Roots.Select(n => GraphQlExecution.Rename(n, names)),
                    result.Document.Evidence, result.Document.SemanticModels), result.SnapshotVersion, result.NextContinuation));
        }

        return result;
    }

    private static (IReadOnlyList<EntityProjectionFieldSelection> Fields, IReadOnlyList<EntityProjectionRelationshipSelection> Relationships)
        Projection(IResolverContext c, IEnumerable<ISelection> container, int depth, Dictionary<string, string> names)
    {
        var fields = new List<EntityProjectionFieldSelection>();
        var relationships = new List<EntityProjectionRelationshipSelection>();
        foreach (var s in container.Where(s => s.Field.Name == "nodes")
            .SelectMany(s => c.GetSelections(c.Schema.GetType<ObjectType>("Entity"), s)).DistinctBy(s => s.Id))
        {
            if (s.Field.Name is not ("field" or "relationship"))
            {
                continue;
            }

            names[Key(s)] = s.ResponseName;
            var args = Args(c, s);
            string source = Required(args, "source", "property");
            if (s.Field.Name == "field")
            {
                fields.Add(new(source, Key(s)));
            }
            else
            {
                if (depth >= 4)
                {
                    throw new ArgumentException("Relationship projection is bounded to four hops.");
                }

                var nested = Projection(c, c.GetSelections(c.Schema.GetType<ObjectType>("RelatedEntities"), s), depth + 1, names);
                relationships.Add(new(source, Key(s), Required(args, "targetType", "targetConcept"), nested.Fields, nested.Relationships,
                    Convert.ToString(Argument(args, "direction"), CultureInfo.InvariantCulture)!));
            }
        }
        return (fields, relationships);
    }

    private static IReadOnlyDictionary<string, ArgumentValue> Args(IResolverContext c, ISelection s) =>
        ArgumentCoercionHelper.TryCoerceArguments(s.Arguments, c, out var args) ? args : throw new ArgumentException("Invalid arguments.");

    private static string Required(IReadOnlyDictionary<string, ArgumentValue> args, string name, string alias) =>
        Convert.ToString(Argument(args, name) ?? Argument(args, alias), CultureInfo.InvariantCulture) is { Length: > 0 } value ? value :
            throw new ArgumentException($"'{name}' is required.");

    private static object? Argument(IReadOnlyDictionary<string, ArgumentValue> args, string name)
    {
        var value = args[name];
        return value.IsFullyCoerced ? value.Value : new InputParser().ParseLiteral(value.ValueLiteral!, value.Type, HotChocolate.Path.Root.Append(name));
    }

    private static string Key(ISelection s) => "selection_" + s.Id;

    private static string Identity(GraphElementIdentity identity) => identity switch
    {
        IdentifierValue i => i.Identifier,
        BlankNodeValue b => b.Identifier,
        _ => throw new ArgumentException("Unsupported identity.")
    };

    private static object? Value(QueryValue value) => value switch
    {
        LiteralValue { ScalarKind: QueryScalarKind.Boolean } v => bool.Parse(v.LexicalValue),
        LiteralValue { ScalarKind: QueryScalarKind.Integer } v => long.Parse(v.LexicalValue, CultureInfo.InvariantCulture),
        LiteralValue { ScalarKind: QueryScalarKind.Decimal } v => decimal.Parse(v.LexicalValue, CultureInfo.InvariantCulture),
        LiteralValue v => v.LexicalValue,
        IdentifierValue v => v.Identifier,
        JsonValue v => v.Value.ToJsonElement(),
        _ => value.ToString()
    };

    private const string QuerySchema = """
        schema { query: Query mutation: Mutation }
        scalar Long
        scalar JSON
        scalar UUID
        type Query { entities(type: String, concept: String, identities: [String!], first: Int, snapshotVersion: Long, after: String, includeEvidence: Boolean! = false): EntityPage! }
        type EntityPage { nodes: [Entity!]! pageInfo: PageInfo! evidence: JSON }
        type PageInfo { endCursor: String hasNextPage: Boolean! snapshotVersion: Long! }
        type Entity {
          identity: String! types: [String!]!
          field(source: String, property: String): FieldValues!
          relationship(source: String, property: String, targetType: String, targetConcept: String, direction: String! = "outgoing"): RelatedEntities!
        }
        type FieldValues { values: [JSON]! }
        type RelatedEntities { nodes: [Entity!]! }
        type CommitResult { transactionId: UUID! version: Long! mutationCount: Int! databaseIncarnation: UUID! outcome: String! effectCount: Int changeCursor: String }
        """;
}
