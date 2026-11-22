using ShardMapVersion = EntityForge.Db.Routing.ShardMapVersion;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Pages;
using EntityForge.Db.Query.Planning;
using EntityForge.Db.Kernel;
using EntityForge.Db.Durability;
using EntityForge.Db.Routing;
using System.Security.Cryptography;
using System.Text;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;
using EntityForge.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
[TestCategory("ProfileQualification")]
public sealed partial class GraphDatabaseEngineTests
{
    private const int OrderedAdjacencyQualificationCount = 150;
    private const string WalFileName = "entityforge.kernel.wal";
    private const string CheckpointFileName = "entityforge.kernel.checkpoint";

    [DataTestMethod]
    [DataRow("property")]
    [DataRow("delete")]
    [DataRow("other")]
    [DataRow("absent")]
    public async Task Edge_property_predicate_tracks_native_lifecycle_and_selected_property(string change)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var from = GraphVertexId.New();
        var to = GraphVertexId.New();
        var edge = GraphEdgeId.New();
        await database.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(from, []), new CreateVertex(to, [])]));
        if (change != "absent")
        {
            await database.CommitAsync(new(Guid.NewGuid(), [new CreateEdge(edge, from, to, "knows")]));
        }

        var version = database.CurrentVersion;
        using var snapshot = database.AcquireSnapshot(version);
        GraphMutation mutation = change switch
        {
            "delete" => new DeleteEdge(edge),
            "absent" => new CreateEdge(edge, from, to, "knows"),
            _ => new SetEdgeProperty(edge, change == "other" ? "other" : "status", new DatabaseValue.String("new"))
        };
        await database.CommitAsync(new(Guid.NewGuid(), [mutation]));
        var transaction = new GraphTransaction(Guid.NewGuid(), [new SetVertexProperty(from, "derived", new DatabaseValue.String("value"))],
            version, [new EdgePropertyRead(edge, "status")]);
        byte[] encoded = GraphTransactionCodec.Encode(transaction);
        CollectionAssert.AreEqual(transaction.ReadPredicates!.ToArray(), GraphTransactionCodec.DecodeRetained(transaction.TransactionId, encoded).ReadPredicates!.ToArray());
        var decoded = GraphTransactionCodec.Decode(transaction.TransactionId, encoded);
        if (change == "other")
        {
            await database.CommitAsync(decoded);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<GraphWriteConflictException>(async () => await database.CommitAsync(decoded));
        }
    }

    [TestMethod]
    public async Task Distributed_incoming_fragment_survives_checkpoint_without_a_fake_source_vertex()
    {
        string path = CreateDirectory();
        GraphVertexId remote = GraphVertexId.New(), local = GraphVertexId.New();
        GraphEdgeId edge = GraphEdgeId.New();
        await using (GraphDatabaseEngine database = await OpenAsync(path))
        {
            DatabaseVersion fragmentVersion = await database.CommitAsync(new(Guid.NewGuid(),
            [
                new CreateVertex(local, ["Company"]),
                new CreateEdgeReplica(edge, remote, local, "worksFor", GraphEdgeStorageRole.IncomingAdjacency)
            ]));
            Assert.HasCount(1, database.Incoming(local));
            Assert.IsEmpty(database.Outgoing(remote));
            using (GraphSnapshotLease retained = database.AcquireSnapshot(fragmentVersion))
            {
                await database.CommitAsync(new(Guid.NewGuid(),
                    [new SetVertexProperty(local, "name", new DatabaseValue.String("Local"))]));
                Assert.HasCount(1, database.Incoming(local, fragmentVersion));
                Assert.IsEmpty(database.Outgoing(remote, fragmentVersion));
            }
            await database.ReindexAsync();
            Assert.HasCount(1, database.Incoming(local));
            Assert.IsEmpty(database.Outgoing(remote));
            await database.CreateCheckpointAsync();
        }

        await using GraphDatabaseEngine reopened = await OpenAsync(path);
        Assert.HasCount(1, reopened.Incoming(local));
        Assert.IsEmpty(reopened.Outgoing(remote));
        Assert.IsNull(reopened.GetVertex(remote));
    }

    [TestMethod]
    public async Task Distributed_edge_role_combinations_survive_historical_reads_reindex_and_reopen()
    {
        string path = CreateDirectory();
        GraphVertexId localFrom = GraphVertexId.New(), localTo = GraphVertexId.New();
        GraphVertexId remoteFrom = GraphVertexId.New(), remoteTo = GraphVertexId.New();
        GraphEdgeId ownerOnly = GraphEdgeId.New(), outgoingOnly = GraphEdgeId.New();
        GraphEdgeId incomingOnly = GraphEdgeId.New(), bothDirections = GraphEdgeId.New();
        await using (GraphDatabaseEngine database = await OpenAsync(path))
        {
            DatabaseVersion version = await database.CommitAsync(new(Guid.NewGuid(),
            [
                new CreateVertex(localFrom, ["Person"]),
                new CreateVertex(localTo, ["Company"]),
                new CreateEdgeReplica(ownerOnly, remoteFrom, remoteTo, "owns", GraphEdgeStorageRole.Authoritative),
                new CreateEdgeReplica(outgoingOnly, localFrom, remoteTo, "out",
                    GraphEdgeStorageRole.OutgoingAdjacency),
                new CreateEdgeReplica(incomingOnly, remoteFrom, localTo, "in",
                    GraphEdgeStorageRole.IncomingAdjacency),
                new CreateEdgeReplica(bothDirections, localFrom, localTo, "both",
                    GraphEdgeStorageRole.OutgoingAdjacency | GraphEdgeStorageRole.IncomingAdjacency)
            ]));
            using (GraphSnapshotLease lease = database.AcquireSnapshot(version))
            {
                await database.CommitAsync(new(Guid.NewGuid(),
                    [new SetVertexProperty(localFrom, "name", new DatabaseValue.String("Ada"))]));
                Assert.IsEmpty(database.Outgoing(remoteFrom, version));
                Assert.IsEmpty(database.Incoming(remoteTo, version));
                Assert.HasCount(2, database.Outgoing(localFrom, version));
                Assert.HasCount(2, database.Incoming(localTo, version));
            }
            await database.ReindexAsync();
            Assert.IsEmpty(database.Outgoing(remoteFrom));
            Assert.IsEmpty(database.Incoming(remoteTo));
            Assert.HasCount(2, database.Outgoing(localFrom));
            Assert.HasCount(2, database.Incoming(localTo));
            await database.CreateCheckpointAsync();
        }

        await using GraphDatabaseEngine reopened = await OpenAsync(path);
        Assert.IsNotNull(reopened.GetEdge(ownerOnly));
        Assert.HasCount(2, reopened.Outgoing(localFrom));
        Assert.HasCount(2, reopened.Incoming(localTo));
        Assert.IsEmpty(reopened.Outgoing(remoteFrom));
        Assert.IsEmpty(reopened.Incoming(remoteTo));
    }

    private string? _directory;
    private readonly TemporaryTestDirectories _testDirectories = new("entityforge-native-graph");

    [TestMethod]
    public async Task Graph_mutations_close_property_and_label_lifecycle_and_survive_restart()
    {
        string path = CreateDirectory();
        GraphVertexId vertex = GraphVertexId.New();
        GraphEdgeId edge = GraphEdgeId.New();
        GraphVertexId target = GraphVertexId.New();
        await using (GraphDatabaseEngine database = await OpenAsync(path))
        {
            await database.CommitAsync(new(Guid.NewGuid(),
            [
                new CreateVertex(vertex, ["Person"]),
                new CreateVertex(target, ["Company"]),
                new CreateEdge(edge, vertex, target, "worksFor"),
                new SetVertexProperty(vertex, "name", new DatabaseValue.String("Ada")),
                new SetEdgeProperty(edge, "since", new DatabaseValue.Int64(2020))
            ]));
            await database.CommitAsync(new(Guid.NewGuid(),
            [
                new RemoveVertexProperty(vertex, "name"),
                new RemoveEdgeProperty(edge, "since"),
                new AddVertexLabel(vertex, "Manager"),
                new RemoveVertexLabel(vertex, "Person")
            ]));
        }

        await using GraphDatabaseEngine reopened = await OpenAsync(path);
        GraphVertex stored = reopened.GetVertex(vertex)!;
        Assert.IsFalse(stored.Properties.ContainsKey("name"));
        Assert.IsTrue(stored.Labels.SetEquals(["Manager"]));
        Assert.IsFalse(reopened.Outgoing(vertex).Single().Properties.ContainsKey("since"));
        Assert.AreEqual(edge, reopened.GetEdge(edge)!.Id);
        Assert.AreNotEqual(Guid.Empty, GraphVertexId.New().Value);
        Assert.AreEqual(Guid.Empty, default(GraphVertexId).Value);
    }

    [TestMethod]
    public void Graph_transaction_defensively_captures_caller_owned_collections()
    {
        var mutations = new List<GraphMutation> { new CreateVertex(GraphVertexId.New(), ["Person"]) };
        var predicates = new List<GraphReadPredicate> { new OutgoingAdjacencyRead(GraphVertexId.New()) };
        var transaction = new GraphTransaction(Guid.NewGuid(), mutations, DatabaseVersion.Initial, predicates);
        mutations.Clear();
        predicates.Clear();
        Assert.AreEqual(1, transaction.Mutations.Count);
        Assert.AreEqual(1, transaction.ReadPredicates!.Count);
        Assert.ThrowsExactly<ArgumentException>(() => new GraphTransaction(Guid.Empty, []));
    }

    [TestMethod]
    public void Graph_values_do_not_expose_mutable_collection_implementations()
    {
        var labels = new HashSet<string>(StringComparer.Ordinal) { "Company" };
        var properties = new Dictionary<string, DatabaseValue>(StringComparer.Ordinal)
        {
            ["name"] = new DatabaseValue.String("Acuity")
        };
        var vertex = new GraphVertex(GraphVertexId.New(), labels, properties);
        var edge = new GraphEdge(GraphEdgeId.New(), GraphVertexId.New(), GraphVertexId.New(),
            "owns", properties);
        labels.Add("Changed");
        properties["changed"] = new DatabaseValue.Boolean(true);

        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((ISet<string>)vertex.Labels).Add("Injected"));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IDictionary<string, DatabaseValue>)vertex.Properties).Add(
                "injected", new DatabaseValue.Boolean(true)));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IDictionary<string, DatabaseValue>)edge.Properties).Add(
                "injected", new DatabaseValue.Boolean(true)));
        Assert.IsFalse(vertex.Labels.Contains("Changed"));
        Assert.IsFalse(vertex.Properties.ContainsKey("changed"));
        Assert.IsFalse(edge.Properties.ContainsKey("changed"));
    }

    [TestMethod]
    public void Graph_checkpoint_encoder_rejects_counts_its_decoder_cannot_accept()
    {
        GraphVertex vertex = new(GraphVertexId.New(), new HashSet<string> { "Company" },
            new Dictionary<string, DatabaseValue>());
        GraphReadView view = GraphReadView.Build(
            new Dictionary<GraphVertexId, GraphVertex> { [vertex.Id] = vertex },
            new Dictionary<GraphEdgeId, GraphEdge>());
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            GraphCheckpointCodec.Encode(view, GraphQueryStatistics.Empty, maximumEntryCount: 0));
    }

    [TestMethod]
    public async Task Staged_graph_state_is_unreachable_until_final_publication()
    {
        string path = CreateDirectory();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(
            System.IO.Path.Combine(path, "staging.pages"));
        GraphPhysicalAccessPath access = await GraphPhysicalAccessPath.OpenAsync(pages);
        var runtime = new GraphProfileRuntime(JsonValueAdmissionPolicy.Default, access);
        Guid transactionId = Guid.NewGuid();
        GraphVertexId vertex = GraphVertexId.New();
        var transaction = new GraphTransaction(transactionId, [new CreateVertex(vertex, ["Company"])]);

        await runtime.ApplyAsync(new(1), transactionId, GraphTransactionCodec.Encode(transaction));

        Assert.AreEqual(DatabaseVersion.Initial, runtime.CurrentVersion);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            runtime.FindCandidates(new GraphAllCandidatePredicate(), new(1)));

        await runtime.PublishStagedStateAsync(transactionId);
        Assert.AreEqual(new DatabaseVersion(1), runtime.CurrentVersion);
        CollectionAssert.AreEqual(new[] { vertex },
            runtime.FindCandidates(new GraphAllCandidatePredicate(), new(1)).ToArray());
    }

    [TestMethod]
    public async Task Query_candidates_and_values_are_bound_to_one_immutable_snapshot()
    {
        string path = CreateDirectory();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(
            System.IO.Path.Combine(path, "query-snapshot.pages"));
        GraphPhysicalAccessPath access = await GraphPhysicalAccessPath.OpenAsync(pages);
        var runtime = new GraphProfileRuntime(JsonValueAdmissionPolicy.Default, access);
        GraphVertexId vertex = GraphVertexId.New();
        Guid firstId = Guid.NewGuid();
        var first = new GraphTransaction(firstId,
        [
            new CreateVertex(vertex, ["Company"]),
            new SetVertexProperty(vertex, "name", new DatabaseValue.String("Acuity"))
        ]);
        await runtime.ApplyAsync(new(1), firstId, GraphTransactionCodec.Encode(first));
        await runtime.PublishStagedStateAsync(firstId);
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new BoundedGraphPatternExecutor(runtime, async () =>
        {
            captured.TrySetResult();
            await resume.Task;
        });
        var query = new BoundedGraphPatternQuery(
            new("company", ["Company"], [new("name", new LiteralValue("Acuity"))]));

        Task<QueryResultEnvelope> executing = executor.ExecuteAsync(query, new(1), CancellationToken.None).AsTask();
        await captured.Task;
        Guid secondId = Guid.NewGuid();
        var second = new GraphTransaction(secondId,
            [new SetVertexProperty(vertex, "name", new DatabaseValue.String("Renamed"))]);
        await runtime.ApplyAsync(new(2), secondId, GraphTransactionCodec.Encode(second));
        await runtime.PublishStagedStateAsync(secondId);
        resume.TrySetResult();

        QueryResultEnvelope result = await executing;
        Assert.AreEqual(1, ((GraphPatternResult)result.Result).Matches.Count);
    }

    [TestMethod]
    public async Task Graph_commit_routes_all_logical_dependencies_before_storage_mutation()
    {
        string path = CreateDirectory();
        var router = new TwoParticipantRouter();
        await using GraphDatabaseEngine database = await OpenAsync(path, router);
        var edge = new GraphEdgeId(Guid.NewGuid());
        var from = new GraphVertexId(Guid.NewGuid());
        var to = new GraphVertexId(Guid.NewGuid());

        EntityForgeDatabaseCapabilityException exception = await Assert.ThrowsExactlyAsync<
            EntityForgeDatabaseCapabilityException>(async () => await database.CommitAsync(
                new(Guid.NewGuid(), [new CreateEdge(edge, from, to, "employs")])));

        StringAssert.Contains(exception.Message, "distributed coordinator");
        Assert.AreEqual(3, router.DependencyCount);
        Assert.AreEqual(typeof(GraphWriteDependency), router.DependencyType);
        Assert.AreEqual(new DatabaseVersion(0), database.CurrentVersion);
    }

    [TestMethod]
    public async Task Graph_commit_rejects_a_single_nonlocal_participant_before_storage_mutation()
    {
        string path = CreateDirectory();
        await using GraphDatabaseEngine database = await OpenAsync(path, new NonLocalParticipantRouter());
        var vertex = new GraphVertexId(Guid.NewGuid());

        await Assert.ThrowsExactlyAsync<EntityForgeDatabaseCapabilityException>(async () =>
            await database.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(vertex, ["Company"])])));

        Assert.AreEqual(new DatabaseVersion(0), database.CurrentVersion);
        Assert.IsNull(database.GetVertex(vertex));
    }

    [TestMethod]
    public async Task Graph_commit_rejects_a_changed_shard_map_epoch_before_storage_mutation()
    {
        string path = CreateDirectory();
        await using GraphDatabaseEngine database = await OpenAsync(path, new ChangedEpochRouter());
        var vertex = new GraphVertexId(Guid.NewGuid());

        ShardMapChangedException exception = await Assert.ThrowsExactlyAsync<ShardMapChangedException>(async () =>
            await database.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(vertex, ["Company"])])));

        Assert.AreEqual(new ShardMapVersion(7), exception.Captured);
        Assert.AreEqual(new ShardMapVersion(8), exception.Current);
        Assert.AreEqual(new DatabaseVersion(0), database.CurrentVersion);
    }

    [TestCleanup]
    public void Cleanup() => _testDirectories.Cleanup();

    [TestMethod]
    public async Task Vertices_edges_and_properties_survive_restart_and_are_traversable()
    {
        string path = CreateDirectory();
        var company = new GraphVertexId(Guid.NewGuid());
        var person = new GraphVertexId(Guid.NewGuid());
        var employment = new GraphEdgeId(Guid.NewGuid());
        var transaction = new GraphTransaction(Guid.NewGuid(),
        [
            new CreateVertex(company, ["Company"]),
            new SetVertexProperty(company, "name", new DatabaseValue.String("Acuity")),
            new CreateVertex(person, ["Person"]),
            new SetVertexProperty(person, "name", new DatabaseValue.String("Ada")),
            new CreateEdge(employment, person, company, "worksAt"),
            new SetEdgeProperty(employment, "since", new DatabaseValue.Int64(2024))
        ]);

        await using (var database = await OpenAsync(path))
        {
            await database.CommitAsync(transaction);
        }

        await using var reopened = await OpenAsync(path);
        GraphVertex? recoveredCompany = reopened.GetVertex(company);
        GraphEdge edge = reopened.Outgoing(person, "worksAt").Single();

        Assert.IsNotNull(recoveredCompany);
        Assert.AreEqual(new DatabaseValue.String("Acuity"), recoveredCompany.Properties["name"]);
        Assert.AreEqual(company, edge.To);
        Assert.AreEqual(new DatabaseValue.Int64(2024), edge.Properties["since"]);
    }

    [TestMethod]
    public async Task Neutral_fixed_pattern_matches_intermediate_nodes_at_a_retained_snapshot()
    {
        string path = CreateDirectory();
        var company = new GraphVertexId(Guid.NewGuid());
        var department = new GraphVertexId(Guid.NewGuid());
        var person = new GraphVertexId(Guid.NewGuid());
        var hasDepartment = new GraphEdgeId(Guid.NewGuid());
        var employs = new GraphEdgeId(Guid.NewGuid());
        await using GraphDatabaseEngine database = await OpenAsync(path);
        DatabaseVersion retained = await database.CommitAsync(new(Guid.NewGuid(),
        [
            new CreateVertex(company, ["Company"]),
            new SetVertexProperty(company, "name", new DatabaseValue.String("Acuity")),
            new CreateVertex(department, ["Department"]),
            new SetVertexProperty(department, "name", new DatabaseValue.String("Research")),
            new CreateVertex(person, ["Person"]),
            new SetVertexProperty(person, "name", new DatabaseValue.String("Ada")),
            new CreateEdge(hasDepartment, company, department, "hasDepartment"),
            new CreateEdge(employs, department, person, "employs")
        ]));
        using GraphSnapshotLease lease = database.AcquireSnapshot(retained);
        await database.CommitAsync(new(Guid.NewGuid(),
            [new SetVertexProperty(company, "name", new DatabaseValue.String("Renamed"))]));
        var query = new BoundedGraphPatternQuery(
            new("company", ["Company"], [new("name", new LiteralValue("Acuity"))]),
            [new("departmentEdge", "company", "hasDepartment", GraphPatternDirection.Outgoing,
                 new("department", ["Department"], [new("name", new LiteralValue("Research"))])),
                new("employment", "department", "employs", GraphPatternDirection.Outgoing,
                 new("person", ["Person"]))]);

        QueryResultEnvelope historical = await database.QueryAsync(query, lease.Version);
        QueryResultEnvelope current = await database.QueryAsync(query);

        var result = (GraphPatternResult)historical.Result;
        Assert.AreEqual(1, result.Matches.Count);
        Assert.AreEqual(3, result.Graph.Nodes.Count);
        Assert.AreEqual(2, result.Graph.Edges.Count);
        Assert.AreEqual(5, historical.Evidence.Count);
        CollectionAssert.AreEquivalent(new[]
        {
            "company", "departmentEdge", "department", "employment", "person"
        }, result.Matches.Single().Bindings.Keys.ToArray());
        Assert.AreEqual(2, result.Matches.Single().Paths.Count);
        Assert.IsTrue(result.Matches.Single().Paths.All(path => path.Elements.Count == 3));
        Assert.AreEqual(0, ((GraphPatternResult)current.Result).Matches.Count);
        GraphPatternQueryExplanation explanation = await database.ExplainAsync(query, analyze: true, asOf: lease.Version);
        Assert.AreEqual(1L, explanation.ActualMatches);
        Assert.AreEqual(1L, explanation.ActualCandidates);
        Assert.IsTrue(explanation.CandidateAcquisitionDuration >= TimeSpan.Zero);
        Assert.AreEqual(2, explanation.RelationshipAnalysis!.Count);
        Assert.AreEqual(1L, explanation.RelationshipAnalysis[0].InputMatches);
        Assert.AreEqual(1L, explanation.RelationshipAnalysis[0].InspectedEdges);
        Assert.AreEqual(1L, explanation.RelationshipAnalysis[1].OutputMatches);
        CollectionAssert.AreEqual(new[]
        {
            "snapshot.graph-outgoing-adjacency", "snapshot.graph-outgoing-adjacency"
        }, explanation.RelationshipAccessPaths.ToArray());
        await Assert.ThrowsExceptionAsync<GraphPatternExpansionLimitException>(() => database.QueryAsync(
            new BoundedGraphPatternQuery(new("any"), maximumExpansions: 1)).AsTask());
    }

    [TestMethod]
    public async Task Neutral_bounded_pattern_joins_sibling_branches_from_the_same_binding()
    {
        string path = CreateDirectory();
        var company = new GraphVertexId(Guid.NewGuid());
        var person = new GraphVertexId(Guid.NewGuid());
        var skillOne = new GraphVertexId(Guid.NewGuid());
        var skillTwo = new GraphVertexId(Guid.NewGuid());
        var office = new GraphVertexId(Guid.NewGuid());
        var otherCompany = new GraphVertexId(Guid.NewGuid());
        await using GraphDatabaseEngine database = await OpenAsync(path);
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new CreateVertex(company, ["Company"]),
            new SetVertexProperty(company, "name", new DatabaseValue.String("Acuity")),
            new CreateVertex(person, ["Person"]),
            new CreateVertex(skillOne, ["Skill"]),
            new SetVertexProperty(skillOne, "name", new DatabaseValue.String("Zulu")),
            new CreateVertex(skillTwo, ["Skill"]),
            new SetVertexProperty(skillTwo, "name", new DatabaseValue.String("Alpha")),
            new CreateVertex(office, ["Office"]),
            new CreateVertex(otherCompany, ["Company"]),
            new SetVertexProperty(otherCompany, "name", new DatabaseValue.String("Zenith")),
            new CreateEdge(new(Guid.NewGuid()), company, person, "employs"),
            new CreateEdge(new(Guid.NewGuid()), person, skillOne, "hasSkill"),
            new CreateEdge(new(Guid.NewGuid()), person, skillTwo, "hasSkill"),
            new CreateEdge(new(Guid.NewGuid()), person, office, "basedAt")
        ]));
        var query = new BoundedGraphPatternQuery(
            new("company", ["Company"], [new("name", new LiteralValue("Acuity"))]),
            [new("employment", "company", "employs", GraphPatternDirection.Outgoing,
                 new("person", ["Person"])),
                new("capability", "person", "hasSkill", GraphPatternDirection.Outgoing,
                 new("skill", ["Skill"])),
                new("location", "person", "basedAt", GraphPatternDirection.Outgoing,
                 new("office", ["Office"]))]);

        var result = (GraphPatternResult)(await database.QueryAsync(query)).Result;

        Assert.AreEqual(2, result.Matches.Count);
        Assert.IsTrue(result.Matches.All(match => match.Bindings.Keys.ToHashSet().SetEquals(
            ["company", "employment", "person", "capability", "skill", "location", "office"])));
        Assert.IsTrue(result.Matches.All(match => match.Paths.Count == 3));
        Assert.AreEqual(5, result.Graph.Nodes.Count);
        Assert.AreEqual(4, result.Graph.Edges.Count);

        var bagProjection = new BoundedGraphPatternQuery(query.Start, query.Relationships,
            returnedBindings: ["company"]);
        var bagResult = (GraphPatternResult)(await database.QueryAsync(bagProjection)).Result;
        Assert.AreEqual(2, bagResult.Matches.Count, "Projection retains duplicate binding rows.");
        Assert.AreEqual(1, bagResult.Graph.Nodes.Count);

        var distinctProjection = new BoundedGraphPatternQuery(query.Start, query.Relationships,
            maximumMatches: 1, returnedBindings: ["company"], distinct: true, offset: 0);
        var distinctResult = (GraphPatternResult)(await database.QueryAsync(distinctProjection)).Result;
        Assert.AreEqual(1, distinctResult.Matches.Count);
        Assert.AreEqual(1, distinctResult.Graph.Nodes.Count);

        var offsetProjection = new BoundedGraphPatternQuery(query.Start, query.Relationships,
            maximumMatches: 1, returnedBindings: ["skill"],
            orderBy: [new("skill", "name", GraphPatternSortDirection.Ascending)], offset: 1);
        var offsetResult = (GraphPatternResult)(await database.QueryAsync(offsetProjection)).Result;
        Assert.AreEqual("Zulu", ((LiteralValue)offsetResult.Graph.Nodes.Single().Properties.Single().Value).LexicalValue);

        var comparisonQuery = new BoundedGraphPatternQuery(
            new("skill", ["Skill"],
                [new("name", new LiteralValue("Alpha"), GraphPatternComparisonOperator.GreaterThan)]));
        var comparisonResult = (GraphPatternResult)(await database.QueryAsync(comparisonQuery)).Result;
        Assert.AreEqual(1, comparisonResult.Matches.Count);
        Assert.AreEqual("Zulu",
            ((LiteralValue)comparisonResult.Graph.Nodes.Single().Properties.Single().Value).LexicalValue);

        var booleanQuery = new BoundedGraphPatternQuery(new("skill", ["Skill"]), filter:
            new GraphPatternBooleanFilter(
                new GraphPatternPropertyComparison("skill", "name", new LiteralValue("Alpha")),
                GraphPatternBooleanOperator.Or,
                new GraphPatternNotFilter(new GraphPatternPropertyComparison(
                    "skill", "name", new LiteralValue("Zulu")))));
        var booleanResult = (GraphPatternResult)(await database.QueryAsync(booleanQuery)).Result;
        Assert.AreEqual(1, booleanResult.Matches.Count);
        Assert.AreEqual("Alpha",
            ((LiteralValue)booleanResult.Graph.Nodes.Single().Properties.Single().Value).LexicalValue);

        var projectedQuery = new BoundedGraphPatternQuery(query.Start, query.Relationships,
            maximumMatches: 1, returnedBindings: ["skill"],
            orderBy: [new("skill", "name", GraphPatternSortDirection.Ascending)]);
        var projected = (GraphPatternResult)(await database.QueryAsync(projectedQuery)).Result;
        GraphPatternQueryExplanation explanation = await database.ExplainAsync(projectedQuery, analyze: true);

        Assert.AreEqual(1, projected.Matches.Count);
        CollectionAssert.AreEqual(new[] { "skill" }, projected.Matches.Single().Bindings.Keys.ToArray());
        Assert.AreEqual(1, projected.Graph.Nodes.Count);
        Assert.AreEqual(0, projected.Graph.Edges.Count);
        Assert.AreEqual("Alpha", ((LiteralValue)projected.Graph.Nodes.Single().Properties.Single().Value).LexicalValue);
        Assert.AreEqual("in-memory.graph-binding-sort", explanation.SortAccessPath);
        Assert.AreEqual(2L, explanation.ActualRowsSorted);
        Assert.AreEqual(1L, explanation.ActualProjectedValues);
        CollectionAssert.AreEqual(new[] { 1, 3, 2 },
            explanation.RelationshipEstimates.Select(item => item.OriginalStep).ToArray(),
            "Independent required leaf branches should execute in estimated-cost order.");
        CollectionAssert.AreEqual(new[] { 1, 3, 2 },
            explanation.RelationshipAnalysis!.Select(item => item.OriginalStep).ToArray());
        Assert.IsTrue(explanation.RelationshipEstimates.Skip(1).All(item => item.Reorderable));

        var rootsOnly = new BoundedGraphPatternQuery(new("company", ["Company"]), maximumMatches: 1,
            returnedBindings: ["company"],
            orderBy: [new("company", "name", GraphPatternSortDirection.Descending)]);
        var orderedRoot = (GraphPatternResult)(await database.QueryAsync(rootsOnly)).Result;
        Assert.AreEqual("Zenith",
            ((LiteralValue)orderedRoot.Graph.Nodes.Single().Properties.Single().Value).LexicalValue);

        var optionalQuery = new BoundedGraphPatternQuery(query.Start,
            [.. query.Relationships,
                new("management", "person", "managedBy", GraphPatternDirection.Outgoing,
                 new("manager", ["Person"]), GraphPatternMatchKind.Optional)],
            returnedBindings: ["person", "manager"]);
        var optional = (GraphPatternResult)(await database.QueryAsync(optionalQuery)).Result;
        GraphPatternQueryExplanation optionalExplain = await database.ExplainAsync(optionalQuery, analyze: true);
        Assert.AreEqual(2, optional.Matches.Count);
        Assert.IsTrue(optional.Matches.All(match => match.Bindings["manager"] is UnboundValue));
        Assert.AreEqual(2L, optionalExplain.RelationshipAnalysis![3].PreservedRows);
    }

    [TestMethod]
    public async Task Fixed_one_hop_projection_preserves_parallel_edge_multiplicity_and_limits()
    {
        string path = CreateDirectory();
        await using GraphDatabaseEngine database = await OpenAsync(path);
        var company = new GraphVertexId(Guid.NewGuid());
        var person = new GraphVertexId(Guid.NewGuid());
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new CreateVertex(company, ["Company"]),
            new CreateVertex(person, ["Person"]),
            new CreateEdge(new(Guid.NewGuid()), company, person, "employs"),
            new CreateEdge(new(Guid.NewGuid()), company, person, "employs")
        ]));
        var query = new BoundedGraphPatternQuery(new("company", ["Company"]),
            [new("employment", "company", "employs", GraphPatternDirection.Outgoing,
                new("person", ["Person"]))], maximumMatches: 10,
            maximumExpansions: 10, returnedBindings: ["person"]);

        var result = (GraphPatternResult)(await database.QueryAsync(query)).Result;

        Assert.AreEqual(2, result.Matches.Count);
        Assert.AreEqual(1, result.Graph.Nodes.Count);
        Assert.IsTrue(result.Matches.All(match => match.Paths.Count == 0 &&
            match.Bindings.Keys.SequenceEqual(["person"])));
        var limited = new BoundedGraphPatternQuery(query.Start, query.Relationships,
            maximumMatches: 1, maximumExpansions: 10, returnedBindings: ["person"], offset: 1);
        Assert.AreEqual(1, ((GraphPatternResult)(await database.QueryAsync(limited)).Result).Matches.Count);
        var expansionLimited = new BoundedGraphPatternQuery(query.Start, query.Relationships,
            maximumMatches: 1, maximumExpansions: 2, returnedBindings: ["person"]);
        await Assert.ThrowsExactlyAsync<GraphPatternExpansionLimitException>(async () =>
            await database.QueryAsync(expansionLimited));
    }

    [TestMethod]
    public void Property_posting_index_is_scoped_to_its_immutable_read_view()
    {
        GraphVertex[] vertices = Enumerable.Range(0, 20).Select(index => new GraphVertex(
            new(Guid.Parse($"00000000-0000-0000-0000-{index + 1:D12}")), new HashSet<string> { "Person" },
            new Dictionary<string, DatabaseValue> { ["number"] = new DatabaseValue.Int64(index) }))
            .ToArray();
        GraphReadView original = GraphReadView.Build(vertices.ToDictionary(item => item.Id),
            new Dictionary<GraphEdgeId, GraphEdge>());
        for (int index = 0; index < 12; index++)
        {
            Assert.AreEqual(1, original.FindByProperty("number", new DatabaseValue.Int64(index)).Count);
        }

        Assert.AreEqual(0, original.FindByProperty("number", new DatabaseValue.Int64(100)).Count);

        GraphVertex inserted = new(new(Guid.NewGuid()), new HashSet<string> { "Person" },
            new Dictionary<string, DatabaseValue> { ["number"] = new DatabaseValue.Int64(100) });
        GraphReadView current = GraphReadView.Build(vertices.Append(inserted).ToDictionary(item => item.Id),
            new Dictionary<GraphEdgeId, GraphEdge>());

        Assert.AreEqual(0, original.FindByProperty("number", new DatabaseValue.Int64(100)).Count);
        Assert.AreEqual(inserted.Id,
            current.FindByProperty("number", new DatabaseValue.Int64(100)).Single());
    }

    [TestMethod]
    public async Task Concurrent_first_queries_keep_caches_bound_to_their_snapshot()
    {
        var id = new GraphVertexId(Guid.NewGuid());
        GraphReadView View(long number) => GraphReadView.Build(
            new Dictionary<GraphVertexId, GraphVertex>
            {
                [id] = new(id, new HashSet<string> { "Person" },
                    new Dictionary<string, DatabaseValue> { ["number"] = new DatabaseValue.Int64(number) })
            }, new Dictionary<GraphEdgeId, GraphEdge>());
        GraphReadView original = View(1), current = View(2);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
        {
            GraphReadView view = index % 2 == 0 ? original : current;
            long expected = index % 2 == 0 ? 1 : 2;
            for (int lookup = 0; lookup < 16; lookup++)
            {
                Assert.AreEqual(id, view.FindByProperty("number", new DatabaseValue.Int64(expected)).Single());
                Assert.AreEqual(0, view.FindByProperty("number", new DatabaseValue.Int64(3 - expected)).Count);
                Assert.AreEqual(id, view.OrderedByProperty("number", GraphPatternSortDirection.Ascending).Single());
                Assert.AreEqual(0, view.Outgoing(id, "knows").Count);
            }
        })));
    }

    [TestMethod]
    public async Task Neutral_quantified_relationship_returns_each_bounded_evidence_path()
    {
        string path = CreateDirectory();
        var employee = new GraphVertexId(Guid.NewGuid());
        var manager = new GraphVertexId(Guid.NewGuid());
        var director = new GraphVertexId(Guid.NewGuid());
        await using GraphDatabaseEngine database = await OpenAsync(path);
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new CreateVertex(employee, ["Employee"]),
            new CreateVertex(manager, ["Leader"]),
            new CreateVertex(director, ["Leader"]),
            new CreateEdge(new(Guid.NewGuid()), employee, manager, "reportsTo"),
            new CreateEdge(new(Guid.NewGuid()), manager, director, "reportsTo"),
            new CreateEdge(new(Guid.NewGuid()), director, employee, "reportsTo")
        ]));
        var query = new BoundedGraphPatternQuery(new("employee", ["Employee"]),
            [new("line", "employee", "reportsTo", GraphPatternDirection.Outgoing,
                new("leader", ["Leader"]), hops: new(1, 4))],
            returnedBindings: ["line", "leader"]);

        var result = (GraphPatternResult)(await database.QueryAsync(query)).Result;
        GraphPatternQueryExplanation explanation = await database.ExplainAsync(query, analyze: true);
        GraphPatternRelationshipAnalysis relationshipAnalysis = explanation.RelationshipAnalysis!.Single();

        Assert.AreEqual(2, result.Matches.Count);
        CollectionAssert.AreEqual(new[] { 3, 5 }, result.Matches
            .Select(match => ((PathValue)match.Bindings["line"]).Elements.Count).Order().ToArray());
        Assert.AreEqual(3, result.Graph.Nodes.Count);
        Assert.AreEqual(2, result.Graph.Edges.Count);
        Assert.AreEqual(1, relationshipAnalysis.MinimumHops);
        Assert.AreEqual(4, relationshipAnalysis.MaximumHops);
        Assert.AreEqual(2L, relationshipAnalysis.OutputMatches);
        Assert.AreEqual(4L, relationshipAnalysis.InspectedEdges,
            "Trail matching inspects but does not reuse an edge within one path.");
        Assert.IsFalse(explanation.RelationshipEstimates.Single().Reorderable);
    }

    [TestMethod]
    public async Task Any_shortest_selects_one_stable_minimum_trail_per_endpoint()
    {
        string path = CreateDirectory();
        var employee = new GraphVertexId(Guid.NewGuid());
        var intermediate = new GraphVertexId(Guid.NewGuid());
        var manager = new GraphVertexId(Guid.NewGuid());
        GraphEdgeId directOne = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        GraphEdgeId directTwo = new(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        await using GraphDatabaseEngine database = await OpenAsync(path);
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new CreateVertex(employee, ["Employee"]),
            new CreateVertex(intermediate, ["Intermediate"]),
            new CreateVertex(manager, ["Leader"]),
            new CreateEdge(directTwo, employee, manager, "reportsTo"),
            new CreateEdge(directOne, employee, manager, "reportsTo"),
            new CreateEdge(new(Guid.NewGuid()), employee, intermediate, "reportsTo"),
            new CreateEdge(new(Guid.NewGuid()), intermediate, manager, "reportsTo")
        ]));
        var query = new BoundedGraphPatternQuery(new("employee", ["Employee"]),
            [new("line", "employee", "reportsTo", GraphPatternDirection.Outgoing,
                new("leader", ["Leader"]), hops: new(1, 2),
                pathSelection: GraphPatternPathSelection.AnyShortest)],
            returnedBindings: ["line", "leader"]);

        var result = (GraphPatternResult)(await database.QueryAsync(query)).Result;
        GraphPatternRelationshipAnalysis analysis =
            (await database.ExplainAsync(query, analyze: true)).RelationshipAnalysis!.Single();

        Assert.AreEqual(1, result.Matches.Count);
        var selected = (PathValue)result.Matches.Single().Bindings["line"];
        Assert.AreEqual(3, selected.Elements.Count);
        StringAssert.Contains(((IdentifierValue)((EdgeValue)selected.Elements[1]).Identity!).Identifier,
            directOne.Value.ToString());
        Assert.AreEqual("any-shortest", analysis.PathSelection);
        Assert.AreEqual(3L, analysis.EnumeratedPaths);
        Assert.AreEqual(2L, analysis.DiscardedPaths);
    }

    [TestMethod]
    public async Task All_shortest_selects_every_minimum_trail_per_endpoint()
    {
        string path = CreateDirectory();
        var employee = new GraphVertexId(Guid.NewGuid());
        var intermediate = new GraphVertexId(Guid.NewGuid());
        var manager = new GraphVertexId(Guid.NewGuid());
        GraphEdgeId directOne = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        GraphEdgeId directTwo = new(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        await using GraphDatabaseEngine database = await OpenAsync(path);
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new CreateVertex(employee, ["Employee"]),
            new CreateVertex(intermediate, ["Intermediate"]),
            new CreateVertex(manager, ["Leader"]),
            new CreateEdge(directTwo, employee, manager, "reportsTo"),
            new CreateEdge(directOne, employee, manager, "reportsTo"),
            new CreateEdge(new(Guid.NewGuid()), employee, intermediate, "reportsTo"),
            new CreateEdge(new(Guid.NewGuid()), intermediate, manager, "reportsTo")
        ]));
        var query = new BoundedGraphPatternQuery(new("employee", ["Employee"]),
            [new("line", "employee", "reportsTo", GraphPatternDirection.Outgoing,
                new("leader", ["Leader"]), hops: new(1, 2),
                pathSelection: GraphPatternPathSelection.AllShortest)],
            returnedBindings: ["line", "leader"]);

        var result = (GraphPatternResult)(await database.QueryAsync(query)).Result;
        GraphPatternRelationshipAnalysis analysis =
            (await database.ExplainAsync(query, analyze: true)).RelationshipAnalysis!.Single();

        Assert.AreEqual(2, result.Matches.Count);
        CollectionAssert.AreEqual(new[] { directOne.Value, directTwo.Value }, result.Matches
            .Select(match => (PathValue)match.Bindings["line"])
            .Select(pathValue => ((IdentifierValue)((EdgeValue)pathValue.Elements[1]).Identity!).Identifier)
            .Select(value => Guid.Parse(value["urn:uuid:".Length..])).ToArray());
        Assert.AreEqual("all-shortest", analysis.PathSelection);
        Assert.AreEqual(3L, analysis.EnumeratedPaths);
        Assert.AreEqual(1L, analysis.DiscardedPaths);
    }

    [TestMethod]
    public async Task Converging_relationship_closes_a_cycle_onto_an_existing_binding()
    {
        string path = CreateDirectory();
        var company = new GraphVertexId(Guid.NewGuid());
        var employee = new GraphVertexId(Guid.NewGuid());
        var manager = new GraphVertexId(Guid.NewGuid());
        var other = new GraphVertexId(Guid.NewGuid());
        GraphEdgeId closure = new(Guid.NewGuid());
        await using GraphDatabaseEngine database = await OpenAsync(path);
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new CreateVertex(company, ["Company"]),
            new CreateVertex(employee, ["Person"]),
            new CreateVertex(manager, ["Person"]),
            new CreateVertex(other, ["Company"]),
            new CreateEdge(new(Guid.NewGuid()), company, employee, "employs"),
            new CreateEdge(new(Guid.NewGuid()), employee, manager, "reportsTo"),
            new CreateEdge(closure, manager, company, "advises"),
            new CreateEdge(new(Guid.NewGuid()), manager, other, "advises")
        ]));
        var query = new BoundedGraphPatternQuery(new("company", ["Company"]),
            [new("employment", "company", "employs", GraphPatternDirection.Outgoing, new("employee", ["Person"])),
                new("management", "employee", "reportsTo", GraphPatternDirection.Outgoing, new("manager", ["Person"])),
                new("advice", "manager", "advises", GraphPatternDirection.Outgoing, new("company", ["Company"]))]);

        var result = (GraphPatternResult)(await database.QueryAsync(query)).Result;
        GraphPatternRelationshipAnalysis analysis =
            (await database.ExplainAsync(query, analyze: true)).RelationshipAnalysis![2];

        Assert.AreEqual(1, result.Matches.Count);
        StringAssert.Contains(((IdentifierValue)((EdgeValue)result.Matches.Single().Bindings["advice"]).Identity!)
            .Identifier, closure.Value.ToString());
        Assert.IsTrue(analysis.Converges);
        Assert.AreEqual("company", analysis.TargetBinding);
        Assert.AreEqual(2L, analysis.InspectedEdges);
        Assert.IsFalse((await database.ExplainAsync(query)).RelationshipEstimates[2].Reorderable);
    }

    [TestMethod]
    public async Task Optional_convergence_preserves_the_existing_target_when_the_edge_is_absent()
    {
        string path = CreateDirectory();
        var company = new GraphVertexId(Guid.NewGuid());
        var employee = new GraphVertexId(Guid.NewGuid());
        await using GraphDatabaseEngine database = await OpenAsync(path);
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new CreateVertex(company, ["Company"]),
            new CreateVertex(employee, ["Person"]),
            new CreateEdge(new(Guid.NewGuid()), company, employee, "employs")
        ]));
        var query = new BoundedGraphPatternQuery(new("company", ["Company"]),
            [new("employment", "company", "employs", GraphPatternDirection.Outgoing, new("employee", ["Person"])),
                new("return", "employee", "worksFor", GraphPatternDirection.Outgoing, new("company", ["Company"]),
                GraphPatternMatchKind.Optional)]);

        var result = (GraphPatternResult)(await database.QueryAsync(query)).Result;
        GraphPatternMatch match = result.Matches.Single();

        Assert.IsInstanceOfType<NodeValue>(match.Bindings["company"]);
        Assert.IsInstanceOfType<UnboundValue>(match.Bindings["return"]);
    }

    [TestMethod]
    public async Task Two_branch_count_aggregation_groups_shared_root_matches_and_invalidates_with_the_snapshot()
    {
        string path = CreateDirectory();
        var company = new GraphVertexId(Guid.NewGuid());
        var sector = new GraphVertexId(Guid.NewGuid());
        var firstPerson = new GraphVertexId(Guid.NewGuid());
        var secondPerson = new GraphVertexId(Guid.NewGuid());
        await using GraphDatabaseEngine database = await OpenAsync(path);
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new CreateVertex(company, ["Company"]),
            new CreateVertex(sector, ["Sector"]),
            new CreateVertex(firstPerson, ["Person"]),
            new SetVertexProperty(firstPerson, "number", new DatabaseValue.Int64(2)),
            new CreateEdge(new(Guid.NewGuid()), company, sector, "IN_SECTOR"),
            new CreateEdge(new(Guid.NewGuid()), firstPerson, company, "EMPLOYED_BY")
        ]));
        var query = new BoundedGraphPatternQuery(new("company", ["Company"]),
        [
            new("classification", "company", "IN_SECTOR", GraphPatternDirection.Outgoing,
                new("sector", ["Sector"])),
            new("employment", "company", "EMPLOYED_BY", GraphPatternDirection.Incoming,
                new("person", ["Person"]))
        ], returnedBindings: ["sector"], aggregation: new([new(GraphPatternAggregateFunction.Count,
            "employees", "person")], [new("sector", "sector")], maximumInputRows: 10));
        var orderedQuery = new BoundedGraphPatternQuery(new("company", ["Company"]),
        [
            new("classification", "company", "IN_SECTOR", GraphPatternDirection.Outgoing,
                new("sector", ["Sector"])),
            new("employment", "company", "EMPLOYED_BY", GraphPatternDirection.Incoming,
                new("person", ["Person"]))
        ], maximumMatches: 10, returnedBindings: ["person"], orderBy: [new("person", "number")]);

        Assert.AreEqual("1", Count(await database.QueryAsync(query)));
        CollectionAssert.AreEqual(new[] { "2" }, Numbers(await database.QueryAsync(orderedQuery)));
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new CreateVertex(secondPerson, ["Person"]),
            new SetVertexProperty(secondPerson, "number", new DatabaseValue.Int64(1)),
            new CreateEdge(new(Guid.NewGuid()), secondPerson, company, "EMPLOYED_BY")
        ]));
        Assert.AreEqual("2", Count(await database.QueryAsync(query)));
        CollectionAssert.AreEqual(new[] { "1", "2" }, Numbers(await database.QueryAsync(orderedQuery)));

        static string Count(QueryResultEnvelope envelope) =>
            ((LiteralValue)((RecordResult)envelope.Result).Rows.Single().Values["employees"]).LexicalValue;
        static string[] Numbers(QueryResultEnvelope envelope) =>
            ((GraphPatternResult)envelope.Result).Matches.Select(match =>
                ((LiteralValue)((NodeValue)match.Bindings["person"]).Properties.Single().Value).LexicalValue)
            .ToArray();
    }

    [TestMethod]
    public async Task One_hop_count_aggregation_preserves_order_paging_evidence_and_historical_snapshot()
    {
        string path = CreateDirectory();
        var firstCompany = new GraphVertexId(new Guid("00000000-0000-0000-0000-000000000001"));
        var secondCompany = new GraphVertexId(new Guid("00000000-0000-0000-0000-000000000002"));
        var thirdCompany = new GraphVertexId(new Guid("00000000-0000-0000-0000-000000000003"));
        GraphVertexId[] people = Enumerable.Range(1, 5).Select(index => new GraphVertexId(
            new Guid($"10000000-0000-0000-0000-{index:D12}"))).ToArray();
        await using GraphDatabaseEngine database = await OpenAsync(path);
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new CreateVertex(firstCompany, ["Company"]),
            new CreateVertex(secondCompany, ["Company"]),
            new CreateVertex(thirdCompany, ["Company"]),
            new CreateVertex(people[0], ["Person"]),
            new CreateVertex(people[1], ["Person"]),
            new CreateVertex(people[2], ["Person"]),
            new CreateVertex(people[3], ["Person"]),
            new CreateEdge(new(Guid.NewGuid()), people[0], firstCompany, "EMPLOYED_BY"),
            new CreateEdge(new(Guid.NewGuid()), people[1], firstCompany, "EMPLOYED_BY"),
            new CreateEdge(new(Guid.NewGuid()), people[2], secondCompany, "EMPLOYED_BY"),
            new CreateEdge(new(Guid.NewGuid()), people[3], thirdCompany, "EMPLOYED_BY")
        ]));
        DatabaseVersion original = database.CurrentVersion;
        using GraphSnapshotLease retainedOriginal = database.AcquireSnapshot(original);

        BoundedGraphPatternQuery Query(GraphPatternSortDirection direction, int offset = 0, int limit = 10) =>
            new(new("person", ["Person"]),
                [new("employment", "person", "EMPLOYED_BY", GraphPatternDirection.Outgoing,
                    new("company", ["Company"]))], maximumExpansions: 20,
                returnedBindings: ["company"], aggregation: new([new(GraphPatternAggregateFunction.Count,
                    "employees", "person")], [new("company", "company")], [new("employees", direction)],
                    offset: offset, limit: limit, maximumInputRows: 10));

        QueryResultEnvelope ascending = await database.QueryAsync(
            Query(GraphPatternSortDirection.Ascending, offset: 1, limit: 2));
        CollectionAssert.AreEqual(new[]
        {
            $"urn:uuid:{thirdCompany.Value:D}", $"urn:uuid:{firstCompany.Value:D}"
        }, AggregateIdentities(ascending));
        CollectionAssert.AreEqual(new[] { "1", "2" }, AggregateCounts(ascending));
        Assert.AreEqual(2, ascending.Evidence.Count);

        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new CreateVertex(people[4], ["Person"]),
            new CreateEdge(new(Guid.NewGuid()), people[4], secondCompany, "EMPLOYED_BY")
        ]));
        CollectionAssert.AreEqual(new[] { "2", "2", "1" }, AggregateCounts(
            await database.QueryAsync(Query(GraphPatternSortDirection.Descending))));
        CollectionAssert.AreEqual(new[] { "2", "1", "1" }, AggregateCounts(
            await database.QueryAsync(Query(GraphPatternSortDirection.Descending), original)));

        static string[] AggregateCounts(QueryResultEnvelope envelope) =>
            ((RecordResult)envelope.Result).Rows.Select(row =>
                ((LiteralValue)row.Values["employees"]).LexicalValue).ToArray();
        static string[] AggregateIdentities(QueryResultEnvelope envelope) =>
            ((RecordResult)envelope.Result).Rows.Select(row =>
                ((IdentifierValue)((NodeValue)row.Values["company"]).Identity).Identifier).ToArray();
    }

    [TestMethod]
    public async Task Common_candidate_planner_intersects_graph_identity_and_property_access()
    {
        string path = CreateDirectory();
        var acuity = new GraphVertexId(Guid.NewGuid());
        var other = new GraphVertexId(Guid.NewGuid());
        await using (GraphDatabaseEngine database = await OpenAsync(path))
        {
            await database.CommitAsync(new(Guid.NewGuid(),
            [
                new CreateVertex(acuity, ["Company"]),
                new SetVertexProperty(acuity, "name", new DatabaseValue.String("Acuity")),
                new CreateVertex(other, ["Company"]),
                new SetVertexProperty(other, "name", new DatabaseValue.String("Other"))
            ]));
        }

        await using GraphDatabaseEngine reopened = await OpenAsync(path);
        GraphCandidatePlan plan = await reopened.PlanCandidatesAsync(new(
            [acuity, other], "name", new DatabaseValue.String("Acuity")));

        CollectionAssert.AreEqual(new[] { acuity }, plan.Candidates.ToArray());
        CollectionAssert.AreEqual(new[]
        {
            "snapshot.graph-vertex-property-index",
            "ordered.graph-vertex-identity"
        }, plan.AccessPaths.ToArray());
        CollectionAssert.AreEqual(new long[] { 1, 1 }, plan.AcquiredCandidateCounts.ToArray());
        CollectionAssert.AreEqual(new long?[] { 1, 2 }, plan.EstimatedCandidateCounts.ToArray());

        GraphVertexId[] ordered = new[] { acuity, other }.OrderBy(item => item.Value).ToArray();
        EntityCandidatePage<GraphVertexId> first = EntityCandidatePaginator.Page(
            ordered, 1, null, item => item.Value.ToString("D"));
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("graph-query")));
        string token = EntityQueryContinuationCodec.Encode(new(
            reopened.CurrentVersion.Value, fingerprint, first.NextPosition!));
        EntityQueryContinuation decoded = EntityQueryContinuationCodec.Decode(token);
        EntityCandidatePage<GraphVertexId> second = EntityCandidatePaginator.Page(
            ordered, 1, decoded.Position, item => item.Value.ToString("D"));
        Assert.AreEqual(ordered[1], second.Items.Single());
        Assert.IsNull(second.NextPosition);
        int tamperAt = token.Length / 2;
        string tampered = token[..tamperAt] + (token[tamperAt] == 'A' ? 'B' : 'A') + token[(tamperAt + 1)..];
        EntityQueryContinuationException invalid = Assert.ThrowsException<EntityQueryContinuationException>(
            () => EntityQueryContinuationCodec.Decode(tampered));
        Assert.AreEqual(EntityQueryContinuationFailure.Malformed, invalid.Failure);
    }

    [TestMethod]
    public async Task Graph_statistics_are_versioned_checkpointed_and_never_applied_to_historical_reads()
    {
        string path = CreateDirectory();
        var acuity = new GraphVertexId(Guid.NewGuid());
        var other = new GraphVertexId(Guid.NewGuid());
        DatabaseVersion retained;
        await using (var database = await OpenAsync(path))
        {
            retained = await database.CommitAsync(new(Guid.NewGuid(),
            [
                new CreateVertex(acuity, ["Company"]),
                new SetVertexProperty(acuity, "name", new DatabaseValue.String("Acuity")),
                new CreateVertex(other, ["Company"]),
                new SetVertexProperty(other, "name", new DatabaseValue.String("Other"))
            ]));
            await database.CreateCheckpointAsync();
        }

        await using GraphDatabaseEngine reopened = await OpenAsync(path);
        var query = new BoundedGraphPatternQuery(
            new("company", ["Company"], [new("name", new LiteralValue("Acuity"))]));
        GraphPatternQueryExplanation restored = await reopened.ExplainAsync(query, analyze: true);

        Assert.AreEqual(retained, restored.StatisticsVersion);
        Assert.AreEqual(1L, restored.EstimatedCandidates);
        Assert.AreEqual(QueryEstimateQuality.Exact, restored.CandidateEstimate.Quality);
        Assert.AreEqual("snapshot.graph-read-view", restored.CandidateEstimate.Source);
        Assert.AreEqual(1L, restored.ActualCandidates);

        using GraphSnapshotLease lease = reopened.AcquireSnapshot(retained);
        await reopened.CommitAsync(new(Guid.NewGuid(),
            [new SetVertexProperty(other, "name", new DatabaseValue.String("Acuity"))]));
        GraphPatternQueryExplanation current = await reopened.ExplainAsync(query, analyze: true);
        GraphPatternQueryExplanation historical = await reopened.ExplainAsync(query, asOf: lease.Version);

        Assert.AreEqual(reopened.CurrentVersion, current.StatisticsVersion);
        Assert.AreEqual(2L, current.EstimatedCandidates);
        Assert.AreEqual(retained, historical.StatisticsVersion);
        Assert.AreEqual(1L, historical.EstimatedCandidates);
        Assert.AreEqual(QueryEstimateQuality.Exact, historical.CandidateEstimate.Quality);
    }

    [TestMethod]
    public void Graph_statistics_bound_untrusted_vocabulary_and_report_overflow()
    {
        int total = GraphQueryStatistics.MaximumTrackedKeysPerFamily + 4;
        GraphCommittedEffect[] effects = Enumerable.Range(0, total)
            .Select(index => (GraphCommittedEffect)new GraphVertexCreated(new(
                new GraphVertexId(Guid.NewGuid()), new HashSet<string> { $"Label{index:D5}" },
                new Dictionary<string, DatabaseValue>())))
            .ToArray();

        GraphQueryStatistics statistics = GraphQueryStatistics.Empty.Apply(
            new DatabaseVersion(1), new GraphCommittedEffects(effects));

        Assert.AreEqual(total, statistics.VertexCount);
        Assert.AreEqual(GraphQueryStatistics.MaximumTrackedKeysPerFamily,
            statistics.VertexLabelCardinalities.Count);
        Assert.AreEqual(4L, statistics.OverflowVertexLabels);

        GraphCommittedEffect[] propertyEffects = Enumerable.Range(0,
                GraphQueryStatistics.MaximumTrackedValuesPerProperty + 4)
            .Select(index => (GraphCommittedEffect)new GraphVertexPropertyChanged(
                new GraphVertexId(Guid.NewGuid()), "name", null, new DatabaseValue.String($"Value{index:D3}")))
            .ToArray();
        statistics = statistics.Apply(new DatabaseVersion(2), new(propertyEffects));

        GraphValueFrequency values = statistics.VertexValueFrequencies["name"];
        Assert.AreEqual(GraphQueryStatistics.MaximumTrackedValuesPerProperty, values.TrackedValues.Count);
        Assert.AreEqual(4L, values.OverflowOccurrences);
        Assert.AreEqual(1L, statistics.EstimateVertexValue("name", new DatabaseValue.String("Value000")));
        Assert.IsNull(statistics.EstimateVertexValue("name", new DatabaseValue.String("Value035")));
    }

    [TestMethod]
    public async Task Relationship_degree_estimates_survive_restart_and_are_snapshot_compatible()
    {
        string path = CreateDirectory();
        GraphVertexId[] vertices = Enumerable.Range(0, 4).Select(_ => new GraphVertexId(Guid.NewGuid())).ToArray();
        DatabaseVersion retained;
        await using (var database = await OpenAsync(path))
        {
            retained = await database.CommitAsync(new(Guid.NewGuid(),
            [
                .. vertices.Select(vertex => (GraphMutation)new CreateVertex(vertex, ["Person"])),
                new CreateEdge(new(Guid.NewGuid()), vertices[0], vertices[1], "knows"),
                new CreateEdge(new(Guid.NewGuid()), vertices[0], vertices[2], "knows"),
                new CreateEdge(new(Guid.NewGuid()), vertices[1], vertices[3], "knows")
            ]));
            await database.CreateCheckpointAsync();
        }

        await using GraphDatabaseEngine reopened = await OpenAsync(path);
        var query = new BoundedGraphPatternQuery(new("person", ["Person"]),
            [new("connection", "person", "knows", GraphPatternDirection.Outgoing, new("known", ["Person"]))]);
        GraphPatternQueryExplanation current = await reopened.ExplainAsync(query, analyze: true);
        GraphPatternRelationshipEstimate estimate = current.RelationshipEstimates.Single();

        Assert.AreEqual(retained, current.StatisticsVersion);
        Assert.AreEqual(4D, estimate.EstimatedInputRows);
        Assert.AreEqual(0.75D, estimate.AverageDegree);
        Assert.AreEqual(3D, estimate.EstimatedInspectedEdges);
        Assert.AreEqual(QueryEstimateQuality.Modelled, estimate.EstimateQuality);
        Assert.AreEqual(3L, current.RelationshipAnalysis!.Single().InspectedEdges);

        using GraphSnapshotLease lease = reopened.AcquireSnapshot(retained);
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reopened.AfterExplainSnapshotCaptured = async () =>
        {
            captured.TrySetResult();
            await release.Task;
        };
        Task<GraphPatternQueryExplanation> explaining = reopened.ExplainAsync(
            query, asOf: lease.Version).AsTask();
        await captured.Task;
        var additional = new GraphVertexId(Guid.NewGuid());
        await reopened.CommitAsync(new(Guid.NewGuid(),
        [
            new CreateVertex(additional, ["Person"]),
            new CreateEdge(new(Guid.NewGuid()), vertices[0], additional, "knows"),
            new CreateEdge(new(Guid.NewGuid()), vertices[1], additional, "knows")
        ]));
        release.TrySetResult();
        GraphPatternQueryExplanation historicalExplanation = await explaining;
        GraphPatternRelationshipEstimate historical = historicalExplanation.RelationshipEstimates.Single();
        reopened.AfterExplainSnapshotCaptured = null;
        Assert.AreEqual(retained, historicalExplanation.SnapshotVersion);
        Assert.AreEqual(retained, historicalExplanation.StatisticsVersion);
        Assert.AreEqual(4L, historicalExplanation.EstimatedCandidates);
        Assert.AreEqual(0.75D, historical.AverageDegree);
        Assert.AreEqual(3D, historical.EstimatedInspectedEdges);
        Assert.AreEqual(QueryEstimateQuality.Modelled, historical.EstimateQuality);
    }

    [TestMethod]
    public async Task Ordered_directional_adjacency_splits_and_rebuilds_through_restart()
    {
        string path = CreateDirectory();
        var source = new GraphVertexId(Guid.NewGuid());
        GraphVertexId[] targets = Enumerable.Range(0, OrderedAdjacencyQualificationCount)
            .Select(_ => new GraphVertexId(Guid.NewGuid())).ToArray();
        GraphEdgeId[] edges = Enumerable.Range(0, OrderedAdjacencyQualificationCount)
            .Select(_ => new GraphEdgeId(Guid.NewGuid())).ToArray();
        var mutations = new List<GraphMutation> { new CreateVertex(source, ["Source"]) };
        for (int index = 0; index < targets.Length; index++)
        {
            mutations.Add(new CreateVertex(targets[index], ["Target"]));
            mutations.Add(new CreateEdge(edges[index], source, targets[index],
                index % 2 == 0 ? "even" : "odd"));
        }
        await using (GraphDatabaseEngine database = await OpenAsync(path))
        {
            await database.CommitAsync(new(Guid.NewGuid(), mutations));
        }

        await using GraphDatabaseEngine reopened = await OpenAsync(path);
        Assert.AreEqual(OrderedAdjacencyQualificationCount, reopened.Outgoing(source).Count);
        Assert.AreEqual(OrderedAdjacencyQualificationCount / 2, reopened.Outgoing(source, "even").Count);
        Assert.AreEqual(1, reopened.Incoming(targets[0]).Count);
        Assert.AreEqual(OrderedAdjacencyQualificationCount / 2,
            reopened.Outgoing([source], "even").Count);
        CollectionAssert.AreEquivalent(edges.Take(2).ToArray(),
            reopened.Incoming(targets.Take(2).ToArray()).Select(edge => edge.Id).ToArray());
    }

    [TestMethod]
    public async Task Atomic_json_bag_preserves_shape_and_canonical_equality_through_restart()
    {
        string path = CreateDirectory();
        var company = new GraphVertexId(Guid.NewGuid());
        var address = new DatabaseValue.Json(
            """{"line4":"EC1A 1AA","line2":"15 Market Street","line1":"Acuity House","line3":"London"}""");
        await using (GraphDatabaseEngine database = await OpenAsync(path))
        {
            await database.CommitAsync(new(Guid.NewGuid(),
            [
                new CreateVertex(company, ["Company"]),
                new SetVertexProperty(company, "address", address)
            ]));
        }

        await using GraphDatabaseEngine reopened = await OpenAsync(path);
        var recovered = (DatabaseValue.Json)reopened.GetVertex(company)!.Properties["address"];

        Assert.AreEqual(new DatabaseValue.Json(
            """{"line1":"Acuity House","line2":"15 Market Street","line3":"London","line4":"EC1A 1AA"}"""),
            recovered);
        Assert.AreEqual(
            """{"line1":"Acuity House","line2":"15 Market Street","line3":"London","line4":"EC1A 1AA"}""",
            recovered.Value.Value);
    }

    [TestMethod]
    public async Task Invalid_graph_transaction_is_rejected_before_it_reaches_the_log()
    {
        string path = CreateDirectory();
        var missingVertex = new GraphVertexId(Guid.NewGuid());
        await using (var database = await OpenAsync(path))
        {
            await Assert.ThrowsExceptionAsync<EntityForge.Db.EntityForgeDbCommitException>(async () =>
                await database.CommitAsync(new(Guid.NewGuid(),
                    [new SetVertexProperty(missingVertex, "name", new DatabaseValue.String("orphan"))])));
        }

        await using var log = await GroupedRedoDatabaseCommitLog.OpenAsync(Path.Combine(path, WalFileName));
        Assert.AreEqual(0L, log.GetRedoStatus().EncodedWalBytes);
        Assert.AreEqual(0, (await log.RecoverAsync()).Count);
    }

    [TestMethod]
    public async Task Graph_database_directory_cannot_be_reopened_as_semantic()
    {
        string directory = CreateDirectory();
        await using (await OpenAsync(directory))
        {
        }

        var semanticFormat = new EntityForge.Db.Storage.Local.LocalDatabaseFormatManager(
            directory, WalFileName, CheckpointFileName);
        await Assert.ThrowsExceptionAsync<EntityForge.Db.EntityForgeDbFormatException>(async () =>
            await semanticFormat.EnsureCurrentAsync());
    }

    [TestMethod]
    public async Task As_of_reads_and_incoming_traversal_use_committed_graph_views()
    {
        string directory = CreateDirectory();
        var person = new GraphVertexId(Guid.NewGuid());
        var company = new GraphVertexId(Guid.NewGuid());
        var edge = new GraphEdgeId(Guid.NewGuid());
        await using var database = await OpenAsync(directory);
        DatabaseVersion first = await database.CommitAsync(new(Guid.NewGuid(),
            [new CreateVertex(person, ["Person"]),
                new CreateVertex(company, ["Company"]),
                new CreateEdge(edge, person, company, "worksAt")]));
        using GraphSnapshotLease lease = database.AcquireSnapshot(first);
        await database.CommitAsync(new(Guid.NewGuid(),
            [new SetVertexProperty(company, "name", new DatabaseValue.String("Acuity"))]));

        Assert.IsFalse(database.GetVertex(company, lease.Version)!.Properties.ContainsKey("name"));
        Assert.AreEqual(edge, database.Incoming(company, "worksAt").Single().Id);
    }

    [TestMethod]
    public async Task Retried_transaction_is_idempotent_and_does_not_append_another_record()
    {
        string directory = CreateDirectory();
        var transaction = new GraphTransaction(Guid.NewGuid(),
            [new CreateVertex(new(Guid.NewGuid()), ["Company"])]);
        await using var database = await OpenAsync(directory);
        DatabaseVersion first = await database.CommitAsync(transaction);
        long firstLength = new FileInfo(Path.Combine(directory, WalFileName)).Length;
        DatabaseVersion retry = await database.CommitAsync(transaction);

        Assert.AreEqual(first, retry);
        Assert.AreEqual(firstLength,
            new FileInfo(Path.Combine(directory, WalFileName)).Length);
    }

    [TestMethod]
    public async Task Overlapping_stale_write_conflicts_but_independent_stale_write_commits()
    {
        string directory = CreateDirectory();
        var company = new GraphVertexId(Guid.NewGuid());
        var other = new GraphVertexId(Guid.NewGuid());
        await using var database = await OpenAsync(directory);
        DatabaseVersion readVersion = await database.CommitAsync(new(Guid.NewGuid(),
            [new CreateVertex(company, ["Company"]), new CreateVertex(other, ["Company"])]));
        await database.CommitAsync(new(Guid.NewGuid(),
            [new SetVertexProperty(company, "name", new DatabaseValue.String("Acuity"))], readVersion));

        await Assert.ThrowsExceptionAsync<GraphWriteConflictException>(async () =>
            await database.CommitAsync(new(Guid.NewGuid(),
                [new SetVertexProperty(company, "name", new DatabaseValue.String("Changed"))], readVersion)));
        DatabaseVersion independent = await database.CommitAsync(new(Guid.NewGuid(),
            [new SetVertexProperty(other, "name", new DatabaseValue.String("Independent"))], readVersion));
        Assert.AreEqual(3L, independent.Value);
    }

    [TestMethod]
    public async Task Database_wide_match_read_conflicts_with_any_intervening_graph_commit()
    {
        string directory = CreateDirectory();
        var first = GraphVertexId.New();
        var second = GraphVertexId.New();
        await using var database = await OpenAsync(directory);
        DatabaseVersion readVersion = await database.CommitAsync(new(Guid.NewGuid(),
            [new CreateVertex(first, ["Company"]), new CreateVertex(second, ["Company"])]));
        await database.CommitAsync(new(Guid.NewGuid(),
            [new SetVertexProperty(first, "name", new DatabaseValue.String("Acuity"))]));

        await Assert.ThrowsExactlyAsync<GraphWriteConflictException>(async () =>
            await database.CommitAsync(new(Guid.NewGuid(),
                [new SetVertexProperty(second, "name", new DatabaseValue.String("Other"))],
                readVersion, [new DatabaseRead()])));
    }

    [TestMethod]
    public async Task Initial_physical_build_cleanup_runs_after_build_failure_and_successful_replay()
    {
        int abandoned = 0;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await InitialPhysicalBuildCoordinator.ExecuteAsync(true, Guid.NewGuid(), ReadOnlyMemory<byte>.Empty,
                (_, _) => ValueTask.FromException(new InvalidOperationException("Injected build failure.")),
                (_, _) =>
{

}, (_, _, _) => ValueTask.FromResult(new DatabaseVersion(1)),
                (_, _) => abandoned++));
        Assert.AreEqual(1, abandoned);

        DatabaseVersion replay = await InitialPhysicalBuildCoordinator.ExecuteAsync(true, Guid.NewGuid(),
            ReadOnlyMemory<byte>.Empty, (_, _) => ValueTask.CompletedTask, (_, _) =>
{

},
            (_, _, _) => ValueTask.FromResult(new DatabaseVersion(7)), (_, _) => abandoned++);
        Assert.AreEqual(new DatabaseVersion(7), replay);
        Assert.AreEqual(2, abandoned);
    }

    [TestMethod]
    public async Task Stale_property_read_conflicts_when_its_vertex_is_deleted()
    {
        string directory = CreateDirectory();
        var company = new GraphVertexId(Guid.NewGuid());
        await using var database = await OpenAsync(directory);
        DatabaseVersion readVersion = await database.CommitAsync(new(Guid.NewGuid(),
            [new CreateVertex(company, ["Company"]),
                new SetVertexProperty(company, "name", new DatabaseValue.String("Acuity"))]));
        await database.CommitAsync(new(Guid.NewGuid(), [new DeleteVertex(company)]));

        await Assert.ThrowsExceptionAsync<GraphWriteConflictException>(async () =>
            await database.CommitAsync(new(Guid.NewGuid(), [], readVersion,
                [new VertexPropertyRead(company, "name")])));
    }

    [TestMethod]
    public async Task Edge_then_vertex_deletion_survives_restart()
    {
        string directory = CreateDirectory();
        var from = new GraphVertexId(Guid.NewGuid());
        var to = new GraphVertexId(Guid.NewGuid());
        var edge = new GraphEdgeId(Guid.NewGuid());
        await using (var database = await OpenAsync(directory))
        {
            await database.CommitAsync(new(Guid.NewGuid(),
                [new CreateVertex(from, ["Person"]),
                    new CreateVertex(to, ["Company"]),
                    new CreateEdge(edge, from, to, "worksAt")]));
            await database.CommitAsync(new(Guid.NewGuid(), [new DeleteEdge(edge), new DeleteVertex(to)]));
        }

        await using var reopened = await OpenAsync(directory);
        Assert.IsNull(reopened.GetVertex(to));
        Assert.AreEqual(0, reopened.Outgoing(from).Count);
    }

    [TestMethod]
    public async Task Graph_checkpoint_restores_state_idempotency_and_replays_later_wal_records()
    {
        string directory = CreateDirectory();
        var company = new GraphVertexId(Guid.NewGuid());
        var first = new GraphTransaction(Guid.NewGuid(), [new CreateVertex(company, ["Company"])]);
        await using (var database = await OpenAsync(directory))
        {
            await database.CommitAsync(first);
            await database.CreateCheckpointAsync();
            await database.CommitAsync(new(Guid.NewGuid(),
                [new SetVertexProperty(company, "name", new DatabaseValue.String("Acuity"))]));
        }

        await using var reopened = await OpenAsync(directory);
        Assert.AreEqual(new DatabaseValue.String("Acuity"), reopened.GetVertex(company)!.Properties["name"]);
        DatabaseVersion beforeRetry = reopened.CurrentVersion;
        DatabaseVersion retry = await reopened.CommitAsync(first);
        Assert.AreEqual(new DatabaseVersion(1), retry);
        Assert.AreEqual(beforeRetry, reopened.CurrentVersion);
    }

    [TestMethod]
    public async Task Snapshot_acquired_during_checkpoint_save_remains_valid_and_does_not_fail_publication()
    {
        string directory = CreateDirectory();
        GraphVertexId company = GraphVertexId.New();
        string checkpointPath = Path.Combine(directory, CheckpointFileName);
        var checkpointStore = new BlockingCheckpointStore(new FileDatabaseCheckpointStore(checkpointPath));
        await using (GraphDatabaseEngine database = await OpenAsync(directory,
                         checkpointStore: checkpointStore))
        {
            DatabaseVersion version = await database.CommitAsync(new(Guid.NewGuid(),
                [new CreateVertex(company, ["Company"])]));
            Task<DatabaseVersion> checkpoint = database.CreateCheckpointAsync().AsTask();
            await checkpointStore.Entered.Task;
            using GraphSnapshotLease lease = database.AcquireSnapshot(version);
            checkpointStore.Release.TrySetResult();

            Assert.AreEqual(version, await checkpoint);
            Assert.AreEqual(company, database.GetVertex(company, lease.Version)!.Id);
        }

        await using GraphDatabaseEngine reopened = await OpenAsync(directory);
        Assert.AreEqual(company, reopened.GetVertex(company)!.Id);
    }

    [TestMethod]
    public async Task Graph_checkpoint_rejects_read_versions_older_than_its_omitted_conflict_history()
    {
        string directory = CreateDirectory();
        var company = new GraphVertexId(Guid.NewGuid());
        DatabaseVersion stale;
        await using (var database = await OpenAsync(directory))
        {
            stale = await database.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(company, ["Company"])]));
            await database.CommitAsync(new(Guid.NewGuid(),
                [new SetVertexProperty(company, "name", new DatabaseValue.String("Acuity"))]));
            await database.CreateCheckpointAsync();
        }

        await using var reopened = await OpenAsync(directory);
        await Assert.ThrowsExceptionAsync<GraphSnapshotTooOldException>(async () =>
            await reopened.CommitAsync(new(Guid.NewGuid(),
                [new SetVertexProperty(company, "name", new DatabaseValue.String("Stale"))], stale)));
        Assert.AreEqual(new DatabaseValue.String("Acuity"), reopened.GetVertex(company)!.Properties["name"]);
    }

    [TestMethod]
    public async Task Graph_history_has_an_explicit_bounded_oldest_readable_version()
    {
        string directory = CreateDirectory();
        var vertex = new GraphVertexId(Guid.NewGuid());
        await using var database = await OpenAsync(directory);
        DatabaseVersion first = await database.CommitAsync(new(Guid.NewGuid(),
            [new CreateVertex(vertex, ["Company"])]));
        await database.CommitAsync(new(Guid.NewGuid(),
            [new SetVertexProperty(vertex, "current", new DatabaseValue.Int64(0))]));
        Assert.ThrowsException<GraphSnapshotTooOldException>(() => database.GetVertex(vertex, first));
        for (int index = 0; index < 130; index++)
        {
            await database.CommitAsync(new(Guid.NewGuid(),
                [new SetVertexProperty(vertex, $"value-{index}", new DatabaseValue.Int64(index))]));
        }

        Assert.IsTrue(database.OldestReadableVersion.Value > first.Value);
        Assert.ThrowsException<GraphSnapshotTooOldException>(() => database.GetVertex(vertex, first));
    }

    [TestMethod]
    public async Task Directional_adjacency_read_range_detects_a_phantom_edge()
    {
        string directory = CreateDirectory();
        var person = new GraphVertexId(Guid.NewGuid());
        var company = new GraphVertexId(Guid.NewGuid());
        await using var database = await OpenAsync(directory);
        DatabaseVersion readVersion = await database.CommitAsync(new(Guid.NewGuid(),
            [new CreateVertex(person, ["Person"]), new CreateVertex(company, ["Company"])]));
        await database.CommitAsync(new(Guid.NewGuid(),
            [new CreateEdge(GraphEdgeId.New(), person, company, "worksAt")]));

        await Assert.ThrowsExceptionAsync<GraphWriteConflictException>(async () =>
            await database.CommitAsync(new(Guid.NewGuid(),
                [new SetVertexProperty(person, "name", new DatabaseValue.String("Ada"))], readVersion,
                [new OutgoingAdjacencyRead(person, "worksAt")])));
    }

    [TestMethod]
    public async Task Snapshot_lease_preserves_its_version_until_release_and_blocks_checkpoint_floor_advance()
    {
        string directory = CreateDirectory();
        var vertex = new GraphVertexId(Guid.NewGuid());
        await using var database = await OpenAsync(directory);
        DatabaseVersion first = await database.CommitAsync(new(Guid.NewGuid(),
            [new CreateVertex(vertex, ["Company"])]));
        using (GraphSnapshotLease lease = database.AcquireSnapshot(first))
        {
            for (int index = 0; index < 130; index++)
            {
                await database.CommitAsync(new(Guid.NewGuid(),
                    [new SetVertexProperty(vertex, $"leased-{index}", new DatabaseValue.Int64(index))]));
            }

            Assert.IsNotNull(database.GetVertex(vertex, lease.Version));
            await Assert.ThrowsExceptionAsync<GraphSnapshotLeaseException>(async () =>
                await database.CreateCheckpointAsync());
        }

        Assert.ThrowsException<GraphSnapshotTooOldException>(() => database.GetVertex(vertex, first));
    }

    [TestMethod]
    public async Task Change_bootstrap_pages_a_bounded_snapshot_and_resumes_without_a_gap()
    {
        string directory = CreateDirectory();
        var company = new GraphVertexId(Guid.NewGuid());
        var person = new GraphVertexId(Guid.NewGuid());
        var employment = new GraphEdgeId(Guid.NewGuid());
        await using var database = await OpenAsync(directory);
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new CreateVertex(company, ["Company"]),
            new CreateVertex(person, ["Person"]),
            new CreateEdge(employment, person, company, "worksAt")
        ]));

        GraphChangeBootstrap bootstrap = await database.AcquireChangeBootstrapAsync();
        Guid laterTransaction = Guid.NewGuid();
        await database.CommitAsync(new(laterTransaction,
            [new SetVertexProperty(company, "name", new DatabaseValue.String("Acuity"))]));
        var effects = new List<GraphCommittedEffect>();
        GraphSnapshotCursor? cursor = null;
        do
        {
            GraphLogicalSnapshotPage page = await database.ReadSnapshotPageAsync(
                bootstrap.Lease, cursor, maximumCount: 1);
            effects.AddRange(page.Effects.Effects);
            cursor = page.NextCursor;
        } while (cursor is not null);
        DatabaseChangePage changes = await database.ReadChangePageAfterAsync(bootstrap.ChangeCursor,
            new(DatabaseProfileId.PropertyGraph), 10);

        Assert.AreEqual(2, effects.OfType<GraphVertexCreated>().Count());
        Assert.AreEqual(1, effects.OfType<GraphEdgeCreated>().Count());
        Assert.AreEqual(laterTransaction, changes.Changes.Single().TransactionId);
        bootstrap.Lease.Dispose();
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () =>
            await database.ReadSnapshotPageAsync(bootstrap.Lease));
    }

    [TestMethod]
    public async Task Empty_graph_snapshot_remains_readable_across_the_first_commit()
    {
        string directory = CreateDirectory();
        await using var database = await OpenAsync(directory);
        GraphChangeBootstrap bootstrap = await database.AcquireChangeBootstrapAsync();
        await database.CommitAsync(new(Guid.NewGuid(),
            [new CreateVertex(new(Guid.NewGuid()), ["Company"])]));

        GraphLogicalSnapshotPage page = await database.ReadSnapshotPageAsync(bootstrap.Lease);
        Assert.AreEqual(0, page.Effects.Effects.Count);
        Assert.IsNull(page.NextCursor);
        bootstrap.Lease.Dispose();
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Historical_adjacency_does_not_expose_mutable_snapshot_cache(bool incoming, bool labelled)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var from = GraphVertexId.New();
        var to = GraphVertexId.New();
        var edge = GraphEdgeId.New();
        await database.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(from, []),
            new CreateVertex(to, []),
            new CreateEdge(edge, from, to, "link")]));
        using GraphSnapshotLease snapshot = database.AcquireSnapshot();
        await database.CommitAsync(new(Guid.NewGuid(), [new DeleteEdge(edge)]));
        IReadOnlyList<GraphEdge> Read() => incoming
            ? database.Incoming(to, snapshot.Version, labelled ? "link" : null)
            : database.Outgoing(from, snapshot.Version, labelled ? "link" : null);
        IReadOnlyList<GraphEdge> original = Read();
        Assert.AreEqual(edge, original.Single().Id);
        // A read-only interface alone must not expose the array owned by the retained view.
        if (original is IList<GraphEdge> mutable)
        {
            Assert.ThrowsException<NotSupportedException>(() => mutable[0] = null!);
        }

        Assert.AreEqual(edge, Read().Single().Id);
        Assert.AreEqual(0, database.Outgoing(from).Count);
    }

    [TestMethod]
    public async Task Graph_snapshot_lease_cannot_be_used_by_another_database_at_the_same_version()
    {
        await using var first = await OpenAsync(CreateDirectory());
        await using var second = await OpenAsync(CreateDirectory());
        var firstVertex = GraphVertexId.New();
        var secondVertex = GraphVertexId.New();
        await first.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(firstVertex, ["First"])]));
        await second.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(secondVertex, ["Second"])]));
        using GraphSnapshotLease lease = first.AcquireSnapshot();
        using GraphSnapshotLease other = second.AcquireSnapshot();
        Assert.AreEqual(lease.Version, other.Version);

        await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await second.ReadSnapshotPageAsync(lease));
        GraphLogicalSnapshotPage page = await first.ReadSnapshotPageAsync(lease);
        Assert.AreEqual(firstVertex, ((GraphVertexCreated)page.Effects.Effects.Single()).Vertex.Id);
        GraphLogicalSnapshotPage otherPage = await second.ReadSnapshotPageAsync(other);
        Assert.AreEqual(secondVertex, ((GraphVertexCreated)otherPage.Effects.Effects.Single()).Vertex.Id);
    }

    [TestMethod]
    public async Task Graph_snapshot_cursor_is_bound_to_its_snapshot_version()
    {
        string directory = CreateDirectory();
        await using var database = await OpenAsync(directory);
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new CreateVertex(new(Guid.NewGuid()), ["Company"]),
            new CreateVertex(new(Guid.NewGuid()), ["Company"])
        ]));
        using GraphSnapshotLease first = database.AcquireSnapshot();
        GraphLogicalSnapshotPage firstPage = await database.ReadSnapshotPageAsync(first,
            maximumCount: 1);
        Assert.IsNotNull(firstPage.NextCursor);
        await database.CommitAsync(new(Guid.NewGuid(),
            [new CreateVertex(new(Guid.NewGuid()), ["Company"])]));
        using GraphSnapshotLease second = database.AcquireSnapshot();

        await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await database.ReadSnapshotPageAsync(second, firstPage.NextCursor, maximumCount: 1));
    }

    [TestMethod]
    public async Task Edge_deletion_invalidates_directional_adjacency_read_ranges()
    {
        string directory = CreateDirectory();
        var from = new GraphVertexId(Guid.NewGuid());
        var to = new GraphVertexId(Guid.NewGuid());
        var edge = new GraphEdgeId(Guid.NewGuid());
        await using var database = await OpenAsync(directory);
        DatabaseVersion readVersion = await database.CommitAsync(new(Guid.NewGuid(),
            [new CreateVertex(from, ["Person"]),
                new CreateVertex(to, ["Company"]),
                new CreateEdge(edge, from, to, "worksAt")]));
        await database.CommitAsync(new(Guid.NewGuid(), [new DeleteEdge(edge)]));

        await Assert.ThrowsExceptionAsync<GraphWriteConflictException>(async () =>
            await database.CommitAsync(new(Guid.NewGuid(),
                [new SetVertexProperty(from, "name", new DatabaseValue.String("Ada"))], readVersion,
                [new OutgoingAdjacencyRead(from, "worksAt")])));
        await Assert.ThrowsExceptionAsync<GraphWriteConflictException>(async () =>
            await database.CommitAsync(new(Guid.NewGuid(),
                [new SetVertexProperty(to, "name", new DatabaseValue.String("Acuity"))], readVersion,
                [new IncomingAdjacencyRead(to)])));
    }

    [TestMethod]
    public async Task Checkpoint_advances_the_unleased_as_of_retention_floor_across_restart()
    {
        string directory = CreateDirectory();
        var vertex = new GraphVertexId(Guid.NewGuid());
        DatabaseVersion retained;
        await using (var database = await OpenAsync(directory))
        {
            retained = await database.CommitAsync(new(Guid.NewGuid(),
                [new CreateVertex(vertex, ["Company"])]));
            await database.CommitAsync(new(Guid.NewGuid(),
                [new SetVertexProperty(vertex, "name", new DatabaseValue.String("Acuity"))]));
            await database.CreateCheckpointAsync();
        }

        await using var reopened = await OpenAsync(directory);
        Assert.ThrowsExactly<GraphSnapshotTooOldException>(() => reopened.GetVertex(vertex, retained));
        Assert.IsTrue(reopened.OldestReadableVersion.Value > retained.Value);
    }

    [TestMethod]
    public async Task Online_reindex_preserves_graph_results_and_restart_without_a_logical_change()
    {
        string path = CreateDirectory();
        var company = new GraphVertexId(Guid.NewGuid());
        var person = new GraphVertexId(Guid.NewGuid());
        var employment = new GraphEdgeId(Guid.NewGuid());
        DatabaseChangeFeedBounds before;
        DatabaseVersion reindexed;
        await using (GraphDatabaseEngine database = await OpenAsync(path))
        {
            await database.CommitAsync(new(Guid.NewGuid(),
            [
                new CreateVertex(company, ["Company"]),
                new CreateVertex(person, ["Person"]),
                new CreateEdge(employment, person, company, "worksAt")
            ]));
            before = await database.GetChangeFeedBoundsAsync();
            using (GraphSnapshotLease retained = database.AcquireSnapshot())
            {
                reindexed = await database.ReindexAsync();
                Assert.AreEqual(DatabaseMaintenancePhase.Completed, database.MaintenanceStatus.Phase);
                Assert.AreEqual(company, database.Outgoing(person, "worksAt").Single().To);
                Assert.AreEqual(company, database.Outgoing(person, retained.Version, "worksAt").Single().To);
            }
            DatabaseChangeFeedBounds after = await database.GetChangeFeedBoundsAsync();
            Assert.AreEqual(before.Current, after.Current,
                "A meaning-preserving physical replacement must not emit a logical change.");
            Assert.IsTrue(reindexed.Value > 1);
            await database.CreateCheckpointAsync();
        }

        await using GraphDatabaseEngine reopened = await OpenAsync(path);
        Assert.AreEqual(company, reopened.Outgoing(person, "worksAt").Single().To);
    }

    [TestMethod]
    public async Task Online_reindex_converges_with_a_foreground_commit()
    {
        string path = CreateDirectory();
        var foreground = new GraphVertexId(Guid.NewGuid());
        await using (GraphDatabaseEngine database = await OpenAsync(path))
        {
            GraphMutation[] baseline = Enumerable.Range(0, 3_000)
                .Select(_ => (GraphMutation)new CreateVertex(new(Guid.NewGuid()), ["Person"]))
                .ToArray();
            await database.CommitAsync(new(Guid.NewGuid(), baseline));

            Task<DatabaseVersion> reindex = database.ReindexAsync().AsTask();
            await database.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(foreground, ["Person"])]));
            await reindex;

            Assert.IsNotNull(database.GetVertex(foreground));
            Assert.AreEqual(DatabaseMaintenancePhase.Completed, database.MaintenanceStatus.Phase);
        }

        await using GraphDatabaseEngine reopened = await OpenAsync(path);
        Assert.IsNotNull(reopened.GetVertex(foreground));
    }

    [TestMethod]
    public async Task Property_only_commits_do_not_invalidate_graph_reindex()
    {
        string path = CreateDirectory();
        var vertex = new GraphVertexId(Guid.NewGuid());
        await using GraphDatabaseEngine database = await OpenAsync(path);
        GraphMutation[] baseline =
        [
            new CreateVertex(vertex, ["Person"]),
            .. Enumerable.Range(0, 3_000)
                .Select(_ => (GraphMutation)new CreateVertex(new(Guid.NewGuid()), ["Person"]))
        ];
        await database.CommitAsync(new(Guid.NewGuid(), baseline));

        Task<DatabaseVersion> reindex = database.ReindexAsync().AsTask();
        await database.CommitAsync(new(Guid.NewGuid(),
            [new SetVertexProperty(vertex, "name", new DatabaseValue.String("Updated"))]));
        await reindex;

        Assert.AreEqual(1, database.MaintenanceStatus.Attempt);
        Assert.AreEqual(new DatabaseValue.String("Updated"), database.GetVertex(vertex)!.Properties["name"]);
    }

    [TestMethod]
    public async Task Graph_bulk_load_builds_an_empty_database_and_remains_durable_and_observable()
    {
        string path = CreateDirectory();
        var companyId = new GraphVertexId(Guid.NewGuid());
        var personId = new GraphVertexId(Guid.NewGuid());
        var edgeId = new GraphEdgeId(Guid.NewGuid());
        GraphVertex[] vertices =
        [
            new(companyId, new HashSet<string> { "Company" },
                new Dictionary<string, DatabaseValue> { ["name"] = new DatabaseValue.String("Acme") }),
            new(personId, new HashSet<string> { "Person" },
                new Dictionary<string, DatabaseValue> { ["name"] = new DatabaseValue.String("Alice") })
        ];
        GraphEdge[] edges =
        [
            new(edgeId, personId, companyId, "EMPLOYED_BY",
                new Dictionary<string, DatabaseValue>())
        ];
        Guid transactionId = Guid.NewGuid();

        await using (GraphDatabaseEngine database = await OpenAsync(path))
        {
            DatabaseVersion version = await database.BulkLoadAsync(transactionId, vertices, edges);
            Assert.AreEqual(new DatabaseVersion(1), version);
            Assert.AreEqual(version, await database.BulkLoadAsync(transactionId, vertices, edges));
            Assert.AreEqual("Acme", ((DatabaseValue.String)database.GetVertex(companyId)!.Properties["name"]).Value);
            Assert.AreEqual(edgeId, database.Outgoing(personId).Single().Id);
            DatabaseChangePage changes = await database.ReadChangePageAfterAsync(null,
                new(DatabaseProfileId.PropertyGraph), 2);
            Assert.AreEqual(1, changes.Changes.Count);
            CommittedChangePayload payload = changes.Changes.Single().Payload;
            Assert.AreEqual(5, payload.EffectCount);
            Assert.AreEqual(GraphCommittedChangeCodecs.DatasetReset, payload.CodecId);
            Assert.AreEqual(CommittedChangeKind.DatasetReset, payload.Kind);
            Assert.IsTrue(payload.RequiresSnapshotRefresh);
            Assert.AreEqual(0, payload.Content.Length);
            CollectionAssert.AreEquivalent(new[]
            {
                GraphChangeRoutingKinds.Vertex,
                GraphChangeRoutingKinds.Edge,
                GraphChangeRoutingKinds.Label,
                GraphChangeRoutingKinds.Property,
                GraphChangeRoutingKinds.EffectKind
            }, payload.Routing.OverflowedKinds.ToArray());
            await Assert.ThrowsExactlyAsync<EntityForgeDbCommitException>(async () =>
                await database.BulkLoadAsync(Guid.NewGuid(), vertices, edges));
            await database.CreateCheckpointAsync();
        }

        await using GraphDatabaseEngine reopened = await OpenAsync(path);
        Assert.AreEqual(new DatabaseVersion(1),
            await reopened.BulkLoadAsync(transactionId, vertices, edges));
        Assert.AreEqual(edgeId, reopened.Incoming(companyId).Single().Id);
    }

    [TestMethod]
    public async Task Empty_graph_bulk_replay_cleans_unused_preparation_and_remains_repeatable()
    {
        string path = CreateDirectory();
        GraphVertexId vertex = GraphVertexId.New();
        GraphVertex[] vertices = [new(vertex, new HashSet<string> { "Company" },
            new Dictionary<string, DatabaseValue>())];
        Guid transactionId = Guid.NewGuid();
        await using GraphDatabaseEngine database = await OpenAsync(path);
        DatabaseVersion loaded = await database.BulkLoadAsync(transactionId, vertices, []);
        await database.CommitAsync(new(Guid.NewGuid(), [new DeleteVertex(vertex)]));

        Assert.AreEqual(loaded, await database.BulkLoadAsync(transactionId, vertices, []));
        Assert.AreEqual(loaded, await database.BulkLoadAsync(transactionId, vertices, []));
        Assert.IsNull(database.GetVertex(vertex));
        GraphVertexId subsequent = GraphVertexId.New();
        await database.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(subsequent, ["Company"])]));
        Assert.IsNotNull(database.GetVertex(subsequent));
    }

    private string CreateDirectory()
    {
        _directory = _testDirectories.Create();
        return _directory;
    }

    private static async ValueTask<GraphDatabaseEngine> OpenAsync(string directory,
        IShardRouter? shardRouter = null, IDatabaseCheckpointStore? checkpointStore = null)
    {
        await new LocalDatabaseFormatManager(directory, WalFileName, CheckpointFileName,
            profile: DatabaseProfileId.PropertyGraph).EnsureCurrentAsync();
        GroupedRedoDatabaseCommitLog redo = await GroupedRedoDatabaseCommitLog.OpenAsync(
            Path.Combine(directory, WalFileName));
        return await GraphDatabaseEngine.OpenAsync(
            redo,
            new SegmentedDatabaseCommittedChangeStore(Path.Combine(directory, "entityforge.changes")),
            checkpointStore ?? new FileDatabaseCheckpointStore(Path.Combine(directory, CheckpointFileName)),
            await PhysicalPageFile.OpenAsync(Path.Combine(directory, "entityforge.pages")),
            shardRouter: shardRouter);
    }

    private sealed class BlockingCheckpointStore(IDatabaseCheckpointStore inner) : IDatabaseCheckpointStore
    {
        private readonly IDatabaseCheckpointStore _inner = inner;

        public TaskCompletionSource Entered
        {
            get;
        }
        =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release
        {
            get;
        }
        =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask SaveAsync(DurableCheckpointEnvelope checkpoint,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await _inner.SaveAsync(checkpoint, cancellationToken);
        }

        public ValueTask<DurableCheckpointEnvelope?> LoadAsync(
            CancellationToken cancellationToken = default) => _inner.LoadAsync(cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class TwoParticipantRouter : IShardRouter
    {
        public int DependencyCount
        {
            get; private set;
        }

        public Type? DependencyType
        {
            get; private set;
        }

        public RoutedParticipantSet Route<TDependency>(IReadOnlyList<TDependency> dependencies)
        {
            DependencyCount = dependencies.Count;
            DependencyType = typeof(TDependency);
            return new(new(7), [new("shard-a"), new("shard-b")]);
        }

        public void Validate(RoutedParticipantSet routed)
        {
        }
    }

    private sealed class NonLocalParticipantRouter : IShardRouter
    {
        public RoutedParticipantSet Route<TDependency>(IReadOnlyList<TDependency> dependencies) =>
            new(new(7), [new("remote-shard")]);

        public void Validate(RoutedParticipantSet routed)
        {
        }
    }

    private sealed class ChangedEpochRouter : IShardRouter
    {
        public RoutedParticipantSet Route<TDependency>(IReadOnlyList<TDependency> dependencies) =>
            new(new(7), [LocalShardTopology.Shard]);

        public void Validate(RoutedParticipantSet routed) =>
            throw new ShardMapChangedException(routed.MapVersion, new(8));
    }
}
