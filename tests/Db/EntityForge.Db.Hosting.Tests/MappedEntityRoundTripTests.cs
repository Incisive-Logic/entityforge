using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;
using EntityForge.Query.Projection;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class MappedEntityRoundTripTests
{
    private sealed record UuidEntity(Guid Id, Guid Token);

    private sealed record UuidOwner(string Id, IReadOnlyList<Guid> Targets, Guid[] Tokens);

    private sealed record ArrayEntity<TValue>(string Id, TValue[] Values);

    [TestMethod]
    public async Task Graph_mapped_scalar_arrays_preserve_duplicates_order_and_empty_values()
    {
        await using var database = await OpenGraphAsync();
        async Task RoundTrip<TValue>(TValue first, TValue second)
        {
            var map = GraphEntityMap.For<ArrayEntity<TValue>>().VertexLabel("Array" + typeof(TValue).Name)
                .Identity(entity => entity.Id, "id").Property(entity => entity.Values, "values")
                .Materialize(context => new(context.IdentityAs<string>(), context.Required<TValue[]>(nameof(ArrayEntity<TValue>.Values))));
            var store = database.EntityStore(map);
            var entity = new ArrayEntity<TValue>("array", [first, second, first]);
            await store.SaveAsync(entity);
            CollectionAssert.AreEqual(entity.Values, (await store.FindAsync(entity.Id))!.Values);
            await store.SaveAsync(entity with
            {
                Values = []
            });
            Assert.AreEqual(0, (await store.FindAsync(entity.Id))!.Values.Length);
        }
        await RoundTrip("b", "a");
        await RoundTrip(2, 1);
        await RoundTrip(Int64.MinValue, Int64.MaxValue);
        await RoundTrip(1.5f, -2.25f);
        await RoundTrip(1.5, -2.25);
        await RoundTrip(0.1234567890123456789012345678m, Decimal.MinValue);
        await RoundTrip(true, false);
        await RoundTrip(Guid.Empty, Guid.NewGuid());
        await RoundTrip(new EntityForge.Values.CalendarDate(2020, 2, 29), new EntityForge.Values.CalendarDate(-1, 1, 1));
        await RoundTrip(new EntityForge.Values.CalendarDuration(1, 2, 3, 4), new EntityForge.Values.CalendarDuration(0, 0, 0, 0));
        await RoundTrip(new EntityForge.Values.LocalTime(1), new EntityForge.Values.LocalTime(2));
        await RoundTrip(new EntityForge.Values.ZonedTime(new(1), 3600), new EntityForge.Values.ZonedTime(new(2), 0));
        await RoundTrip(new EntityForge.Values.LocalDateTime(new(2020, 1, 1), new(1)), new EntityForge.Values.LocalDateTime(new(2021, 1, 1), new(2)));
        await RoundTrip(new EntityForge.Values.ZonedDateTime(new(new(2020, 1, 1), new(1)), 3600), new EntityForge.Values.ZonedDateTime(new(new(2021, 1, 1), new(2)), 0));
        await RoundTrip(new EntityForge.Values.SpatialPoint(7203, 1, 2), new EntityForge.Values.SpatialPoint(7203, 3, 4));
    }

    [TestMethod]
    public async Task Graph_map_preserves_UUID_array_properties_and_target_map_references()
    {
        var targetMap = GraphEntityMap.For<UuidEntity>().VertexLabel("UuidTarget")
            .Identity(entity => entity.Id, "id").Property(entity => entity.Token, "token")
            .Materialize(context => new(context.IdentityAs<Guid>(), context.Required<Guid>(nameof(UuidEntity.Token))));
        var ownerMap = GraphEntityMap.For<UuidOwner>().VertexLabel("UuidOwner")
            .Identity(entity => entity.Id, "id").Edge(entity => entity.Targets, "targets", targetMap)
            .Property(entity => entity.Tokens, "tokens")
            .Materialize(context => new(context.IdentityAs<string>(), context.Required<IReadOnlyList<Guid>>(nameof(UuidOwner.Targets)),
                context.Required<Guid[]>(nameof(UuidOwner.Tokens))));
        await using var database = await OpenGraphAsync();
        Guid targetId = Guid.NewGuid();
        await database.EntityStore(targetMap).SaveAsync(new(targetId, Guid.Empty));
        var owner = new UuidOwner("owner", [targetId], [Guid.Empty, Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), Guid.Empty]);
        await database.EntityStore(ownerMap).SaveAsync(owner);
        var restored = (await database.EntityStore(ownerMap).FindAsync(owner.Id))!;
        CollectionAssert.AreEqual(owner.Targets.ToArray(), restored.Targets.ToArray());
        CollectionAssert.AreEqual(owner.Tokens, restored.Tokens);
        Assert.AreEqual(EntityCollectionKind.OrderedSequence, database.EntityStore(ownerMap).Mapping.Manifest.Members.Single(member => member.ClrName == nameof(UuidOwner.Tokens)).CollectionKind);
        await database.EntityStore(ownerMap).SaveAsync(owner with
        {
            Tokens = []
        });
        Assert.AreEqual(0, (await database.EntityStore(ownerMap).FindAsync(owner.Id))!.Tokens.Length);
    }

    [TestMethod]
    public async Task Graph_map_preserves_native_UUID_identity_and_property()
    {
        var map = GraphEntityMap.For<UuidEntity>().VertexLabel("UuidEntity")
            .Identity(entity => entity.Id, "id").Property(entity => entity.Token, "token")
            .Materialize(context => new(context.IdentityAs<Guid>(), context.Required<Guid>(nameof(UuidEntity.Token))));
        await using var database = await OpenGraphAsync();
        var store = database.EntityStore(map);
        var entity = new UuidEntity(Guid.NewGuid(), Guid.Empty);
        await store.SaveAsync(entity);
        Assert.AreEqual(entity, await store.FindAsync(entity.Id));
        Assert.AreEqual(new DatabaseValue.Uuid(Guid.Empty), database.GetVertex(new GraphVertexId(entity.Id))!.Properties["token"]);
    }

    private sealed record ClockEntity(string Id, EntityForge.Values.LocalTime Time, EntityForge.Values.LocalDateTime DateTime, EntityForge.Values.ZonedTime Zoned, EntityForge.Values.ZonedDateTime Instant);

    [TestMethod]
    public async Task Graph_map_captures_and_materialises_nanosecond_local_times()
    {
        var map = GraphEntityMap.For<ClockEntity>().VertexLabel("Clock")
            .Identity(entity => entity.Id, "id").Property(entity => entity.Time, "time")
            .Property(entity => entity.DateTime, "datetime").Property(entity => entity.Zoned, "zoned").Property(entity => entity.Instant, "instant")
            .Materialize(context => new(context.IdentityAs<string>(), context.Required<EntityForge.Values.LocalTime>(nameof(ClockEntity.Time)),
                context.Required<EntityForge.Values.LocalDateTime>(nameof(ClockEntity.DateTime)), context.Required<EntityForge.Values.ZonedTime>(nameof(ClockEntity.Zoned)), context.Required<EntityForge.Values.ZonedDateTime>(nameof(ClockEntity.Instant))));
        await using var database = await OpenGraphAsync();
        var store = database.EntityStore(map);
        var entity = new ClockEntity("alarm", new(86_399_999_999_999), new(new(-10000, 1, 1), new(1)), new(new(86_399_999_999_999), -3723), new(new(new(-10000, 1, 1), new(86_399_999_999_999)), 561, "Europe/Paris"));
        await store.SaveAsync(entity);
        Assert.AreEqual(entity, await store.FindAsync(entity.Id));
    }

    private sealed record SpatialEntity(string Id, EntityForge.Values.SpatialPoint Location);

    [TestMethod]
    public async Task Graph_map_captures_and_materialises_spatial_points()
    {
        var map = GraphEntityMap.For<SpatialEntity>().VertexLabel("Place")
            .Identity(entity => entity.Id, "id").Property(entity => entity.Location, "location")
            .Materialize(context => new(context.IdentityAs<string>(), context.Required<EntityForge.Values.SpatialPoint>(nameof(SpatialEntity.Location))));
        await using var database = await OpenGraphAsync();
        var store = database.EntityStore(map);
        var entity = new SpatialEntity("site", new(4979, -1.25, 52.5, 123));
        await store.SaveAsync(entity);
        Assert.AreEqual(entity, await store.FindAsync(entity.Id));
    }

    private sealed record CalendarEntity(string Id, EntityForge.Values.CalendarDate Day, EntityForge.Values.CalendarDuration Duration);

    [TestMethod]
    public async Task Graph_map_captures_and_materialises_extended_calendar_dates()
    {
        var map = GraphEntityMap.For<CalendarEntity>()
            .VertexLabel("Calendar")
            .Identity(entity => entity.Id, "id")
            .Property(entity => entity.Day, "day")
            .Property(entity => entity.Duration, "duration")
            .Materialize(context => new(context.IdentityAs<string>(),
                context.Required<EntityForge.Values.CalendarDate>(nameof(CalendarEntity.Day)),
                context.Required<EntityForge.Values.CalendarDuration>(nameof(CalendarEntity.Duration))));
        await using var database = await OpenGraphAsync();
        var store = database.EntityStore(map);
        var entity = new CalendarEntity("extended", new(-50000, 2, 29), new(14, -2, -1, 500_000_000));
        await store.SaveAsync(entity);
        Assert.AreEqual(entity, await store.FindAsync(entity.Id));
    }

    [TestMethod]
    public void Neutral_mapping_treats_binary_as_one_scalar_value()
    {
        EntityMap<BinaryEntity> map = EntityMap.For<BinaryEntity>()
            .Identity(entity => entity.Id)
            .Property(entity => entity.Payload)
            .Build();

        Assert.IsFalse(map.Members.Single(member => member.Name == nameof(BinaryEntity.Payload)).IsCollection);
    }

    private string? _directory;

    [TestCleanup]
    public void Cleanup()
    {
        if (_directory is not null && Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task Graph_map_writes_updates_and_materialises_a_poco()
    {
        GraphEntityMap<Company> map = GraphEntityMap.For<Company>()
            .VertexLabel("Company")
            .Identity(company => company.Number, "number")
            .Property(company => company.Name, "name")
            .Property(company => company.EmployeeCount, "employeeCount")
            .Materialize(context => new(context.IdentityAs<string>(),
                context.Required<string>(nameof(Company.Name)),
                context.Required<int>(nameof(Company.EmployeeCount))));
        await using GraphDatabaseEngine database = await OpenGraphAsync();
        GraphEntityStore<Company> store = database.EntityStore(map);

        await store.SaveAsync(new Company("COMPANY000001", "Acuity", 12));
        await store.SaveAsync(new Company("COMPANY000001", "Acuity Consulting", 14));

        EntityProjection graphDocument = database.ProjectEntity(map, "COMPANY000001")!;
        EntityProjectionNode graphNode = graphDocument.Roots.Single();
        Assert.AreEqual("Company", graphNode.Types.Single().Identifier.AbsoluteUri.Split(':').Last());
        Assert.AreEqual("Acuity Consulting", ((LiteralValue)graphNode.Fields
            .Single(field => field.Name == nameof(Company.Name)).Values.Single().Value).LexicalValue);
        Assert.AreEqual(new Company("COMPANY000001", "Acuity Consulting", 14),
            await store.FindAsync("COMPANY000001"));
        EntityReadResult<Company>? graphRead = await store.ReadAsync("COMPANY000001");
        Assert.IsNotNull(graphRead);
        Assert.AreEqual(database.CurrentVersion, graphRead.SnapshotVersion);
        await store.SaveAsync(new Company("COMPANY000001", "Acuity Current", 15));
        await Assert.ThrowsExactlyAsync<GraphWriteConflictException>(() => store.SaveAsync(
            new Company("COMPANY000001", "Acuity Stale", 16),
            GraphEntityWriteOptions.Create(graphRead.SnapshotVersion)).AsTask());
        DatabaseCommitReceipt deleted = await store.DeleteAsync("COMPANY000001");
        Assert.AreEqual(DatabaseCommitOutcome.AppliedWithChanges, deleted.Outcome);
        Assert.IsNull(await store.FindAsync("COMPANY000001"));
    }

    [TestMethod]
    public async Task Graph_mapped_batch_commits_multiple_entities_at_one_version_and_replays()
    {
        GraphEntityMap<Company> map = GraphEntityMap.For<Company>()
            .VertexLabel("Company")
            .Identity(company => company.Number, "number")
            .Property(company => company.Name, "name")
            .Property(company => company.EmployeeCount, "employeeCount")
            .Materialize(context => new(context.IdentityAs<string>(),
                context.Required<string>(nameof(Company.Name)),
                context.Required<int>(nameof(Company.EmployeeCount))));
        await using GraphDatabaseEngine database = await OpenGraphAsync();
        GraphEntityStore<Company> store = database.EntityStore(map);
        Guid operationId = Guid.NewGuid();

        GraphEntityWriteBatch<Company> batch = store.CreateWriteBatch(operationId)
            .Save(new("COMPANY000001", "Acuity", 12))
            .Save(new("COMPANY000002", "Beacon", 8));
        DatabaseCommitReceipt registration = await database.Mappings().RegisterAsync(store.Mapping.Manifest);
        DatabaseCommitReceipt receipt = await batch.CommitAsync();

        Assert.AreEqual(new DatabaseVersion(registration.Version.Value + 2), receipt.Version);
        Assert.AreEqual("Acuity", (await store.FindAsync("COMPANY000001"))!.Name);
        Assert.AreEqual("Beacon", (await store.FindAsync("COMPANY000002"))!.Name);
        DatabaseCommitReceipt replay = await batch.CommitAsync();
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Outcome);
    }

    [TestMethod]
    public async Task Graph_save_reconciles_nullable_properties_and_relationships()
    {
        GraphEntityMap<GraphCompany> map = GraphEntityMap.For<GraphCompany>()
            .VertexLabel("Company")
            .Identity(company => company.Number, "number")
            .Property(company => company.Name, "name")
            .Edge(company => company.Employees, "employs")
            .Build();
        await using GraphDatabaseEngine database = await OpenGraphAsync();
        GraphVertexId employee = GraphVertexId.New();
        await database.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(employee, ["Employee"])]));

        await database.EntityStore(map).SaveAsync(new GraphCompany("COMPANY000001", "Acuity", [employee.Value]));
        GraphEdge edge = database.Incoming(employee, "employs").Single();
        GraphVertex company = database.GetVertex(edge.From)!;
        Assert.AreEqual("Acuity", ((DatabaseValue.String)company.Properties["name"]).Value);
        Assert.AreEqual(edge, database.GetEdge(edge.Id));

        await database.EntityStore(map).SaveAsync(new GraphCompany("COMPANY000001", null, []));
        Assert.IsFalse(database.GetVertex(company.Id)!.Properties.ContainsKey("name"));
        Assert.AreEqual(0, database.Outgoing(company.Id, "employs").Count);
    }

    [TestMethod]
    public async Task Graph_map_round_trips_collection_relationships_through_a_target_map()
    {
        GraphEntityMap<GraphEmployee> employeeMap = GraphEntityMap.For<GraphEmployee>()
            .VertexLabel("Employee")
            .Identity(employee => employee.Id, "employeeId")
            .Property(employee => employee.Name, "name")
            .Materialize(context => new(context.IdentityAs<string>(),
                context.Required<string>(nameof(GraphEmployee.Name))));
        GraphEntityMap<GraphEmployer> employerMap = GraphEntityMap.For<GraphEmployer>()
            .VertexLabel("Company")
            .Identity(company => company.Id, "companyId")
            .Edge(company => company.EmployeeIds, "employs", employeeMap)
            .Materialize(context => new(context.IdentityAs<string>(),
                context.Required<IReadOnlyList<string>>(nameof(GraphEmployer.EmployeeIds))));
        await using GraphDatabaseEngine database = await OpenGraphAsync();

        await database.EntityStore(employeeMap).SaveAsync(new("EMP-1", "Alice"));
        await database.EntityStore(employeeMap).SaveAsync(new("EMP-2", "Bob"));
        GraphEntityStore<GraphEmployer> employers = database.EntityStore(employerMap);
        await employers.SaveAsync(new("CO-1", ["EMP-1", "EMP-2"]));

        GraphEmployer restored = (await employers.FindAsync("CO-1"))!;
        CollectionAssert.AreEquivalent(new[] { "EMP-1", "EMP-2" }, restored.EmployeeIds.ToArray());
    }

    [TestMethod]
    public async Task Graph_map_round_trips_related_mapped_pocos()
    {
        GraphEntityMap<GraphEmployee> employeeMap = GraphEntityMap.For<GraphEmployee>()
            .VertexLabel("Employee")
            .Identity(employee => employee.Id, "employeeId")
            .Property(employee => employee.Name, "name")
            .Materialize(context => new(context.IdentityAs<string>(),
                context.Required<string>(nameof(GraphEmployee.Name))));
        GraphEntityMap<GraphEmployerObjects> employerMap = GraphEntityMap.For<GraphEmployerObjects>()
            .VertexLabel("Company")
            .Identity(company => company.Id, "companyId")
            .Edge(company => company.Employees, "employs", employeeMap)
            .Materialize(context => new(context.IdentityAs<string>(),
                context.Required<IReadOnlyList<GraphEmployee>>(nameof(GraphEmployerObjects.Employees))));
        await using GraphDatabaseEngine database = await OpenGraphAsync();
        GraphEmployee alice = new("EMP-1", "Alice");
        GraphEmployee bob = new("EMP-2", "Bob");
        await database.EntityStore(employeeMap).SaveAsync(alice);
        await database.EntityStore(employeeMap).SaveAsync(bob);
        GraphEntityStore<GraphEmployerObjects> employers = database.EntityStore(employerMap);

        await employers.SaveAsync(new("CO-1", [alice, bob]));

        GraphEmployerObjects restored = (await employers.FindAsync("CO-1"))!;
        CollectionAssert.AreEquivalent(new[] { alice, bob }, restored.Employees.ToArray());
    }

    [TestMethod]
    public async Task Graph_relationship_save_uses_current_leased_observation_after_unrelated_commit()
    {
        GraphEntityMap<GraphCompany> map = GraphEntityMap.For<GraphCompany>()
            .VertexLabel("Company")
            .Identity(company => company.Number, "number")
            .Property(company => company.Name, "name")
            .Edge(company => company.Employees, "employs")
            .Build();
        await using GraphDatabaseEngine database = await OpenGraphAsync();
        GraphVertexId employee = GraphVertexId.New();
        await database.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(employee, ["Employee"])]));
        await database.EntityStore(map).SaveAsync(new GraphCompany("COMPANY000001", "Acuity", [employee.Value]));
        DatabaseVersion expectedVersion = database.CurrentVersion;
        await database.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(GraphVertexId.New(), ["Unrelated"])]));

        DatabaseCommitReceipt receipt = await database.EntityStore(map).SaveAsync(
            new GraphCompany("COMPANY000001", "Acuity Group", [employee.Value]),
            GraphEntityWriteOptions.Create(expectedVersion));

        Assert.AreEqual(DatabaseCommitOutcome.AppliedWithChanges, receipt.Outcome);
        GraphVertexId company = database.Incoming(employee, "employs").Single().From;
        Assert.AreEqual("Acuity Group",
            ((DatabaseValue.String)database.GetVertex(company)!.Properties["name"]).Value);
    }

    [TestMethod]
    public async Task Graph_save_adds_the_mapped_label_to_an_existing_vertex()
    {
        GraphEntityMap<Company> map = GraphEntityMap.For<Company>()
            .VertexLabel("Company")
            .Identity(company => company.Number, "number")
            .Property(company => company.Name, "name")
            .Property(company => company.EmployeeCount, "employeeCount")
            .Build();
        await using GraphDatabaseEngine database = await OpenGraphAsync();
        GraphVertexId vertex = (await database.EntityStore(map).PrepareSaveAsync(
            new Company("COMPANY000001", "Acuity", 12), Guid.NewGuid()))
            .Transaction.Mutations.OfType<CreateVertex>().Single().Vertex;
        await database.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(vertex, ["Organisation"])]));

        DatabaseCommitReceipt receipt = await database.EntityStore(map).SaveAsync(
            new Company("COMPANY000001", "Acuity", 12));

        Assert.AreEqual(DatabaseCommitOutcome.AppliedWithChanges, receipt.Outcome);
        CollectionAssert.Contains(database.GetVertex(vertex)!.Labels.ToArray(), "Company");
        Assert.IsNotNull(database.ProjectEntity(map, "COMPANY000001"));
    }

    [TestMethod]
    public async Task Rdf_map_replaces_subject_state_and_materialises_a_poco()
    {
        RdfEntityMap<Company> map = RdfEntityMap.For<Company>()
            .Class(new Uri("https://mapping.entityforge.test/Company"))
            .Subject(company => company.Number, new Uri("https://mapping.entityforge.test/company/"))
            .Predicate(company => company.Name, new Uri("https://mapping.entityforge.test/name"))
            .Predicate(company => company.EmployeeCount, new Uri("https://mapping.entityforge.test/employeeCount"))
            .Materialize(context => new(context.IdentityAs<string>(),
                context.Required<string>(nameof(Company.Name)),
                context.Required<int>(nameof(Company.EmployeeCount))));
        await using IRdfDatabase database = await OpenRdfAsync();
        RdfEntityStore<Company> store = database.EntityStore(map);
        var subject = new RdfIri("https://mapping.entityforge.test/company/COMPANY000001");
        var unrelated = new RdfQuad(subject, new RdfIri("https://mapping.entityforge.test/unrelated"),
            new RdfLiteral("preserve"));
        await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(unrelated)]));

        await store.SaveAsync(new Company("COMPANY000001", "Acuity", 12));
        await store.SaveAsync(new Company("COMPANY000001", "Acuity Consulting", 14));

        EntityProjection rdfDocument = database.ProjectEntity(map, "COMPANY000001")!;
        EntityProjectionNode rdfNode = rdfDocument.Roots.Single();
        Assert.AreEqual("https://mapping.entityforge.test/Company", rdfNode.Types.Single().Identifier.AbsoluteUri);
        EntityProjectionValue employeeCount = rdfNode.Fields
            .Single(field => field.Name == nameof(Company.EmployeeCount)).Values.Single();
        Assert.AreEqual(QueryScalarKind.Integer, ((LiteralValue)employeeCount.Value).ScalarKind);
        Assert.AreEqual("https://mapping.entityforge.test/employeeCount",
            employeeCount.SourceTerm.Identifier.AbsoluteUri);
        Assert.AreEqual(new Company("COMPANY000001", "Acuity Consulting", 14),
            await store.FindAsync("COMPANY000001"));
        EntityReadResult<Company>? rdfRead = await store.ReadAsync("COMPANY000001");
        Assert.IsNotNull(rdfRead);
        Assert.AreEqual(database.CurrentVersion, rdfRead.SnapshotVersion);
        StringAssert.StartsWith(rdfRead.ConcurrencyToken!, "rdf-subject-state:v1:");
        Assert.ThrowsExactly<ArgumentException>(() => new RdfEntityWriteOptions(
            Guid.NewGuid(), rdfRead.SnapshotVersion));
        await store.SaveAsync(new Company("COMPANY000001", "Acuity Current", 15));
        await Assert.ThrowsExactlyAsync<RdfWriteConflictException>(() => store.SaveAsync(
            new Company("COMPANY000001", "Acuity Stale", 16),
            RdfEntityWriteOptions.FromRead(rdfRead)).AsTask());
        Assert.IsTrue(database.Find(new(Subject: subject)).Contains(unrelated));

        await store.DeleteAsync("COMPANY000001");
        Assert.IsNull(await store.FindAsync("COMPANY000001"));
        CollectionAssert.AreEqual(new[] { unrelated }, database.Find(new(Subject: subject)).ToArray());

        var replay = new RdfTransaction(Guid.NewGuid(), [new AssertRdfQuad(new RdfQuad(
            new RdfIri("https://mapping.entityforge.test/replay"),
            new RdfIri("https://mapping.entityforge.test/value"), new RdfLiteral("verified")))]);
        DatabaseCommitReceipt applied = await database.CommitWithReceiptAsync(replay);
        DatabaseCommitReceipt repeated = await database.CommitWithReceiptAsync(replay);
        Assert.AreEqual(DatabaseCommitOutcome.AppliedWithChanges, applied.Outcome);
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, repeated.Outcome);
        Assert.AreEqual(applied.ChangeCursor, repeated.ChangeCursor);
    }

    [TestMethod]
    public async Task Rdf_mapped_batch_commits_multiple_entities_at_one_version_and_replays()
    {
        RdfEntityMap<Company> map = RdfEntityMap.For<Company>()
            .Class(new Uri("https://mapping.entityforge.test/Company"))
            .Subject(company => company.Number, new Uri("https://mapping.entityforge.test/company/"))
            .Predicate(company => company.Name, new Uri("https://mapping.entityforge.test/name"))
            .Predicate(company => company.EmployeeCount, new Uri("https://mapping.entityforge.test/employeeCount"))
            .Materialize(context => new(context.IdentityAs<string>(),
                context.Required<string>(nameof(Company.Name)),
                context.Required<int>(nameof(Company.EmployeeCount))));
        await using IRdfDatabase database = await OpenRdfAsync();
        RdfEntityStore<Company> store = database.EntityStore(map);
        RdfEntityWriteBatch<Company> batch = store.CreateWriteBatch(Guid.NewGuid())
            .Save(new("COMPANY000001", "Acuity", 12))
            .Save(new("COMPANY000002", "Beacon", 8));

        DatabaseCommitReceipt registration = await database.Mappings().RegisterAsync(store.Mapping.Manifest);
        DatabaseCommitReceipt receipt = await batch.CommitAsync();

        Assert.AreEqual(new DatabaseVersion(registration.Version.Value + 2), receipt.Version);
        Assert.AreEqual("Acuity", (await store.FindAsync("COMPANY000001"))!.Name);
        Assert.AreEqual("Beacon", (await store.FindAsync("COMPANY000002"))!.Name);
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, (await batch.CommitAsync()).Outcome);
    }

    [TestMethod]
    public async Task Rdf_map_round_trips_property_and_relationship_collections()
    {
        RdfEntityMap<RdfProfile> map = RdfEntityMap.For<RdfProfile>()
            .Class(new Uri("https://mapping.entityforge.test/Profile"))
            .Subject(profile => profile.Id, new Uri("https://mapping.entityforge.test/profile/"))
            .Predicate(profile => profile.Tags, new Uri("https://mapping.entityforge.test/tag"))
            .Relationship(profile => profile.Contacts, new Uri("https://mapping.entityforge.test/contact"))
            .Materialize(context => new(context.IdentityAs<string>(),
                context.Required<IReadOnlyList<string>>(nameof(RdfProfile.Tags)),
                context.Required<Uri[]>(nameof(RdfProfile.Contacts))));
        await using IRdfDatabase database = await OpenRdfAsync();
        RdfEntityStore<RdfProfile> store = database.EntityStore(map);
        Uri[] contacts = [new("https://mapping.entityforge.test/contact/alice"),
            new("https://mapping.entityforge.test/contact/bob")];

        await store.SaveAsync(new("PROFILE-1", ["database", "semantic"], contacts));
        RdfProfile restored = (await store.FindAsync("PROFILE-1"))!;

        CollectionAssert.AreEquivalent(new[] { "database", "semantic" }, restored.Tags.ToArray());
        CollectionAssert.AreEquivalent(contacts, restored.Contacts);
    }

    [TestMethod]
    public async Task Rdf_subject_replacement_rejects_a_stale_delta()
    {
        await using IRdfDatabase database = await OpenRdfAsync();
        var subject = new RdfIri("https://mapping.entityforge.test/company/COMPANY000001");
        var predicate = new RdfIri("https://mapping.entityforge.test/name");
        var original = new RdfQuad(subject, predicate, new RdfLiteral("Original"));
        await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(original)]));
        DatabaseVersion readVersion = database.CurrentVersion;
        RdfSubjectStateRead read = RdfSubjectStateRead.Capture(subject, null, true, [original]);
        var first = new RdfTransaction(Guid.NewGuid(),
            [new RetractRdfQuad(original), new AssertRdfQuad(new(subject, predicate, new RdfLiteral("First")))],
            ReadVersion: readVersion, ReadPredicates: [read]);
        var second = new RdfTransaction(Guid.NewGuid(),
            [new RetractRdfQuad(original), new AssertRdfQuad(new(subject, predicate, new RdfLiteral("Second")))],
            ReadVersion: readVersion, ReadPredicates: [read]);

        await database.CommitAsync(first);
        RdfWriteConflictException conflict = await Assert.ThrowsExceptionAsync<RdfWriteConflictException>(
            async () => await database.CommitAsync(second));

        Assert.AreEqual(readVersion, conflict.ReadVersion);
        CollectionAssert.AreEqual(new[] { "First" }, database.Find(new(Subject: subject))
            .Select(quad => ((RdfLiteral)quad.Object).LexicalForm).ToArray());
    }

    private sealed record GraphCompany(string Number, string? Name, IReadOnlyList<Guid> Employees);

    private sealed record GraphEmployee(string Id, string Name);

    private sealed record GraphEmployer(string Id, IReadOnlyList<string> EmployeeIds);

    private sealed record GraphEmployerObjects(string Id, IReadOnlyList<GraphEmployee> Employees);

    private sealed record RdfProfile(string Id, IReadOnlyList<string> Tags, Uri[] Contacts);

    private async ValueTask<GraphDatabaseEngine> OpenGraphAsync()
    {
        string directory = CreateDirectory();
        await new LocalDatabaseFormatManager(directory, "graph.wal", "graph.checkpoint",
            profile: DatabaseProfileId.PropertyGraph).EnsureCurrentAsync();
        return await GraphDatabaseEngine.OpenAsync(
            await GroupedRedoDatabaseCommitLog.OpenAsync(Path.Combine(directory, "graph.wal")),
            new SegmentedDatabaseCommittedChangeStore(Path.Combine(directory, "graph.changes")),
            new FileDatabaseCheckpointStore(Path.Combine(directory, "graph.checkpoint")),
            await PhysicalPageFile.OpenAsync(Path.Combine(directory, "graph.pages")));
    }

    private async ValueTask<RdfDatabaseEngine> OpenRdfAsync()
    {
        string directory = CreateDirectory();
        await new LocalDatabaseFormatManager(directory, "rdf.wal", "rdf.checkpoint",
            profile: DatabaseProfileId.Rdf).EnsureCurrentAsync();
        return await RdfDatabaseEngine.OpenAsync(
            await GroupedRedoDatabaseCommitLog.OpenAsync(Path.Combine(directory, "rdf.wal")),
            new SegmentedDatabaseCommittedChangeStore(Path.Combine(directory, "rdf.changes")),
            new FileDatabaseCheckpointStore(Path.Combine(directory, "rdf.checkpoint")),
            await PhysicalPageFile.OpenAsync(Path.Combine(directory, "rdf.pages")));
    }

    private string CreateDirectory()
    {
        if (_directory is not null)
        {
            throw new InvalidOperationException("Only one database is used per test.");
        }

        _directory = Path.Combine(Path.GetTempPath(), $"entityforge-mapped-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        return _directory;
    }

    private sealed record Company(string Number, string Name, int EmployeeCount);

    private sealed record BinaryEntity(Guid Id, byte[] Payload);
}
