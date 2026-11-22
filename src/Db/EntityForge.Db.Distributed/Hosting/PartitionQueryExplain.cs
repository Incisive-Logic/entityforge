using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using System.Text.Json;
using EntityForge.Db.Rdf;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionApplicationClient
{
    internal PartitionQueryPlan ExplainQuery(PartitionQueryRequest request)
    {
        if (request.Language == "gql" && request.Statement is { } statement && GqlProcedures is { } procedures)
        {
            request = request with
            {
                Statement = procedures.Expand(statement)
            };
        }
        if (request.Generation != Generation || request.Profile != Profile)
        {
            throw new InvalidDataException("Query destination differs.");
        }

        bool frontier = false, fragments = false, completeGraph = false;
        if (request.GraphQl is { } graphql && Profile == DatabaseProfileId.Semantic && request.Language == "graphql")
        {
            if (graphql.Query.Length > PartitionQueryLimits.MaximumStatementCharacters)
            {
                throw new ArgumentException("Query exceeds the statement bound.");
            }

            var compiled = new GraphQlEntityQueryCompiler(graphql.Variables).Compile(graphql.Query, graphql.OperationName);
            return ExplainQuery(request with
            {
                Language = "entity",
                GraphQl = null,
                EntityQuery = compiled.Request
            }) with
            {
                Language = request.Language
            };
        }
        else if (Profile == DatabaseProfileId.PropertyGraph && request.Language == "gql")
        {
            GraphGqlStandardSyntax.Validate(request.Statement ?? throw new ArgumentException("Missing GQL."));
            var compiled = PartitionQueryCompilation.CompileGraph(request.Statement ?? throw new ArgumentException("Missing GQL."));
            completeGraph = compiled.RequiresCompleteGraph;
            var graph = compiled.Query;
            frontier = !completeGraph && _routing.StorageLayout == DatabaseStorageLayout.Current && EntityForge.Db.Graph.BoundedGraphPatternExecutor.SupportsFrontierExchange(graph);
            fragments = !completeGraph && EntityForge.Db.Graph.BoundedGraphPatternExecutor.SupportsFragments(graph);
        }
        else if (Profile == DatabaseProfileId.Rdf && request.Language == "sparql")
        {
            if (request.Statement is not { Length: > 0 and <= PartitionQueryLimits.MaximumStatementCharacters })
            {
                throw new ArgumentException("Missing bounded SPARQL.");
            }
            // Parse and validate using the actual evaluator without contacting owners.
            _ = RdfSparql.ExecuteRelation([], request.Statement, default);
        }
        else if (Profile == DatabaseProfileId.Semantic && request.Language == "entity" && request.EntityQuery is { } entity)
        {
            if (entity.SnapshotVersion is not null || entity.SnapshotLeaseId is not null)
            {
                throw new NotSupportedException("Native snapshot identifiers are not distributed capabilities.");
            }

            _ = new PortableEntityQueryRequestCompiler().Compile(entity with
            {
                After = null
            });
        }
        else
        {
            throw new NotSupportedException("Query language does not match the profile.");
        }

        bool profileFragments = CompactProfileOperators && PartitionProfileFragments.Supports(request.Language,
            request.Statement, request.EntityQuery is { } selected ? selected with
            {
                First = null,
                After = null
            } : null);
        bool semanticFrontier = CompactProfileOperators && Profile == DatabaseProfileId.Semantic &&
            PartitionSemanticFrontier.Supports(request.EntityQuery is { } semantic ? semantic with
            {
                First = null,
                After = null
            } : null);
        return new(request.Language, _routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties ? fragments ? "graph-topology-property-fragments" : "graph-topology-property-relation" : _owners.Count == 1 ? "complete-owner-query-with-bounded-exchange-fallback" : profileFragments
            ? Profile == DatabaseProfileId.Rdf ? "rdf-fragment-bindings" : semanticFrontier ? "semantic-projection-fragments-with-identity-frontier-fallback" : "semantic-projection-fragments-with-relationship-fallback"
            : semanticFrontier ? "semantic-identity-frontier" : fragments ? "graph-fragment-bindings" : frontier ? "graph-frontier-exchange" : completeGraph ? "complete-paged-relation-exchange" : "selective-paged-relation-exchange", ExecutionCapabilities.ReadProtocol,
            _owners.Count, ["Borrow or acquire one certified complete-owner snapshot",
                "Validate and execute each complete owner query under one authority interval",
                _routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties
                ? fragments ? "Select property candidates, fetch bounded topology and bags, and join bindings at the protected cut" : "Exchange complete topology and property-bag roles and join them at the protected cut" : _owners.Count == 1
                ? "Evaluate complete native operators locally; fall back to bounded exchange for large results" : fragments
                ? "Execute native selection and expansion; join compact bindings with authoritative edge checks without rebuilding a graph" : frontier
                ? "Select roots, then fetch bounded vertex frontiers and verify authoritative edges at the same snapshot"
                : semanticFrontier ? "Select subject-owned scalar roots and expand bounded identity layers across all owners at the same snapshot; scalar fragments fall back when relationships require global support"
                : profileFragments ? "Execute native RDF bindings or direct scalar Semantic projections; relevant Semantic relationship evidence restarts complete relation evaluation"
                : "Push safe Graph predicates, RDF patterns or direct scalar Semantic concepts to owners; otherwise exchange complete relations in bounded pages",
                completeGraph ? "Evaluate row stages, calls and unions against the complete protected graph" : "Preserve global joins, filters, ordering and aggregates across owners",
                "Materialize bounded result without relation re-encoding",
                "Join every owner query completion and cleanup"],
            PartitionQueryLimits.MaximumFacts, PartitionQueryLimits.MaximumExchangeBytes);
    }
}
