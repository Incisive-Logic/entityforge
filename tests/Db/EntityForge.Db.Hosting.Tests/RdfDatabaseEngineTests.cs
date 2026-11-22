using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using System.Diagnostics;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
[TestCategory("ProfileQualification")]
public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public async Task Integer_fragment_ranges_follow_native_updates_and_preserve_pinned_snapshots()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var predicate = new RdfIri("urn:number");
        var quads = Enumerable.Range(0, 1000).Select(i => new RdfQuad(new RdfIri("urn:item:" + i), predicate,
            new RdfLiteral(i.ToString(), "http://www.w3.org/2001/XMLSchema#integer"))).ToArray();
        var version = await database.CommitAsync(new(Guid.NewGuid(), quads.Select(q => (RdfMutation)new AssertRdfQuad(q)).ToArray()));
        using var old = await database.AcquireSnapshotAsync(version);
        const string Query = "SELECT ?s ?n WHERE { ?s <urn:number> ?n . FILTER(?n >= 40 && ?n < 43) }";
        string[] Read(RdfSnapshotLease snapshot) => RdfSparql.CaptureBindingFragments(database, snapshot, Query, 100, CancellationToken.None)
            .Select(f => ((RdfLiteral)f.Bindings["n"]).LexicalForm).ToArray();
        CollectionAssert.AreEquivalent(new[] { "40", "41", "42" }, Read(old));
        var changed = await database.CommitAsync(new(Guid.NewGuid(), [new RetractRdfQuad(quads[41]),
            new AssertRdfQuad(new(quads[41].Subject, predicate, new RdfLiteral("99", "http://www.w3.org/2001/XMLSchema#integer")))]));
        using var current = await database.AcquireSnapshotAsync(changed);
        CollectionAssert.AreEquivalent(new[] { "40", "42" }, Read(current));
        CollectionAssert.AreEquivalent(new[] { "40", "41", "42" }, Read(old));
    }

    [TestMethod]
    public void Rdf_public_values_reject_invalid_states_at_construction()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new RdfTripleTerm(
            null!, new RdfIri("https://example.test/p"), new RdfLiteral("value")));
        Assert.ThrowsExactly<ArgumentException>(() => new RdfTripleTerm(
            new RdfLiteral("invalid subject"), new RdfIri("https://example.test/p"), new RdfLiteral("value")));
        Assert.ThrowsExactly<ArgumentNullException>(() => new AssertRdfQuad(null!));
        Assert.ThrowsExactly<ArgumentException>(() => new RdfQuadPattern(Graph: new RdfLiteral("invalid")));
        Assert.ThrowsExactly<ArgumentException>(() => new RdfQuadPattern(
            Graph: new RdfIri("https://example.test/graph"), MatchDefaultGraphOnly: true));
        Assert.ThrowsExactly<ArgumentException>(() => new RdfSubjectStateRead(
            new RdfIri("https://example.test/s"), null, true,
            "rdf-subject-state:v1:" + new string('z', 64)));
    }

    [TestMethod]
    public async Task Rdf_initial_snapshot_is_a_valid_explicit_query_snapshot()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);

        RdfSparqlQueryResult result = await database.QuerySparqlAsync(new EntityForge.Db.Rdf.Contracts.RdfSparqlQueryRequest(
            "SELECT ?s WHERE { ?s <https://example.test/p> ?o }",
            snapshotVersion: DatabaseVersion.Initial));

        Assert.AreEqual(DatabaseVersion.Initial, result.SnapshotVersion);
        Assert.AreEqual(0, ((RdfSparqlSelectResult)result).Bindings.Count);
    }

    private const int ParserResilienceSamples = 128;
    private static readonly TimeSpan ParserResilienceBudget = TimeSpan.FromSeconds(5);
    private string? _directory;
    private readonly TemporaryTestDirectories _testDirectories = new("entityforge-rdf");

    [TestMethod]
    public async Task SPARQL_requests_parameterise_RDF_terms_and_own_their_bindings()
    {
        await using RdfDatabaseEngine database = await OpenAsync(CreateDirectory());
        var subject = new RdfIri("https://example.test/company");
        var predicate = new RdfIri("https://example.test/name");
        var dangerousLiteral = new RdfLiteral("Acuity\" . ?s ?p ?o . #");
        var oldLiteral = new RdfLiteral("old");
        await database.CommitAsync(new(Guid.NewGuid(),
            [new AssertRdfQuad(new(subject, predicate, oldLiteral))]));
        var bindings = new Dictionary<string, RdfTerm>
        {
            ["subject"] = subject,
            ["value"] = dangerousLiteral
        };
        var update = new EntityForge.Db.Rdf.Contracts.RdfSparqlUpdateRequest(Guid.NewGuid(),
            "DELETE { @subject <https://example.test/name> \"old\" } " +
            "INSERT { @subject <https://example.test/name> @value } " +
            "WHERE { @subject <https://example.test/name> \"old\" }", bindings);
        bindings["value"] = new RdfLiteral("mutated after construction");

        await database.CommitSparqlUpdateAsync(update);
        RdfSparqlQueryResult result = await database.QuerySparqlAsync(
            new EntityForge.Db.Rdf.Contracts.RdfSparqlQueryRequest(
            "SELECT ?value WHERE { @subject <https://example.test/name> ?value . }",
            new Dictionary<string, RdfTerm> { ["subject"] = subject }));

        Assert.AreEqual(dangerousLiteral, ((RdfSparqlSelectResult)result).Bindings.Single()["value"]);
        Assert.AreEqual(1, database.Find(new(Subject: subject, Predicate: predicate)).Count);
    }

    [TestMethod]
    public void SPARQL_parameter_scanner_respects_lexical_regions_and_comparison_operators()
    {
        var bindings = new Dictionary<string, RdfTerm>
        {
            ["limit"] = new RdfLiteral("10", "http://www.w3.org/2001/XMLSchema#integer"),
            ["subject"] = new RdfIri("urn:bound:subject")
        };
        string command = """"
            SELECT ?s WHERE {
              ?s <urn:value> ?v .
              FILTER (?v < @limit && ?v <= @limit && "@subject" = "@subject" && """@subject\"still literal""" = """@subject\"still literal""")
              # @subject must remain a comment
              <urn:@subject> <urn:value> ?v .
              ?s <urn:label> "name"@subject .
            }
            """";

        string rendered = RdfSparqlParameterization.Render(command, bindings);

        StringAssert.Contains(rendered, "?v < \"10\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        StringAssert.Contains(rendered, "?v <= \"10\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        StringAssert.Contains(rendered, "\"@subject\"");
        StringAssert.Contains(rendered, "# @subject must remain a comment");
        StringAssert.Contains(rendered, "<urn:@subject>");
        StringAssert.Contains(rendered, "\"name\"@subject");
    }

    [TestMethod]
    public void RDF_physical_codec_round_trips_every_term_shape()
    {
        var predicate = new RdfIri("https://example.test/predicate");
        var quoted = new RdfTripleTerm(
            new RdfBlankNode("quoted-subject"),
            predicate,
            new RdfTripleTerm(
                new RdfIri("https://example.test/nested-subject"),
                predicate,
                new RdfLiteral("\u03bb-value", language: "EN")));
        var quad = new RdfQuad(
            quoted,
            predicate,
            new RdfLiteral("42", "http://www.w3.org/2001/XMLSchema#integer"),
            new RdfBlankNode("named-graph"));

        Assert.AreEqual(quad, RdfPhysicalCodec.DecodeQuad(RdfPhysicalCodec.EncodeQuad(quad)));
        CollectionAssert.AreEqual(
            RdfPhysicalCodec.EncodeTerm(quoted),
            RdfPhysicalCodec.EncodeTerm(quoted),
            "Physical term keys must be deterministic.");
        byte[] unsupported = RdfPhysicalCodec.EncodeQuad(quad);
        unsupported[0]++;
        Assert.ThrowsExactly<InvalidDataException>(() => RdfPhysicalCodec.DecodeQuad(unsupported));
    }

    [TestMethod]
    public async Task RDF_adaptive_predicate_partitions_are_scoped_to_their_snapshot()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var predicate = new RdfIri("urn:partition:predicate");
        var subject = new RdfIri("urn:partition:subject");
        RdfMutation[] initial = Enumerable.Range(0, 20).Select(index =>
            (RdfMutation)new AssertRdfQuad(new(
                index == 0 ? subject : new RdfIri($"urn:partition:subject:{index}"),
                predicate, new RdfLiteral(index.ToString())))).ToArray();
        await database.CommitAsync(new(Guid.NewGuid(), initial));
        using RdfSnapshotLease historical = database.AcquireSnapshot();
        var pattern = new RdfQuadPattern(Subject: subject, Predicate: predicate);
        for (int index = 0; index < 10; index++)
        {
            Assert.AreEqual(1, database.Find(pattern).Count);
        }

        await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(new(
            subject, predicate, new RdfLiteral("new")))]));

        Assert.AreEqual(2, database.Find(pattern).Count);
        Assert.AreEqual(1, database.Find(pattern, historical).Count);
    }

    [TestMethod]
    public async Task RDF_quads_preserve_terms_named_graphs_retractions_and_restart()
    {
        string directory = CreateDirectory();
        var alice = new RdfIri("https://example.test/people/alice");
        var name = new RdfIri("https://schema.org/name");
        var knows = new RdfIri("https://schema.org/knows");
        var graph = new RdfIri("https://example.test/graphs/hr");
        var bob = new RdfBlankNode("bob-import-1");
        var label = new RdfLiteral("Alice", language: "EN-gb");
        var nameQuad = new RdfQuad(alice, name, label, graph);
        var knowsQuad = new RdfQuad(alice, knows, bob);

        await using (RdfDatabaseEngine database = await OpenAsync(directory))
        {
            await database.CommitAsync(new(Guid.NewGuid(),
                [new AssertRdfQuad(nameQuad), new AssertRdfQuad(knowsQuad)]));
            Assert.AreEqual(2, database.Find(new()).Count);
            Assert.AreEqual(nameQuad, database.Find(new(Subject: alice, Graph: graph)).Single());
            Assert.AreEqual("en-gb", ((RdfLiteral)nameQuad.Object).Language);
            await database.CreateCheckpointAsync();
        }

        await using (RdfDatabaseEngine reopened = await OpenAsync(directory))
        {
            CollectionAssert.AreEquivalent(new[] { nameQuad, knowsQuad }, reopened.Find(new()).ToArray());
            await reopened.CommitAsync(new(Guid.NewGuid(), [new RetractRdfQuad(knowsQuad)]));
            Assert.AreEqual(0, reopened.Find(new(Predicate: knows)).Count);
            Assert.AreEqual(nameQuad, reopened.Find(new(MatchDefaultGraphOnly: false, Graph: graph)).Single());
        }
    }

    [TestMethod]
    public void RDF_contracts_reject_literal_subjects_and_relative_IRIs()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new RdfIri("relative"));
        Assert.ThrowsExactly<ArgumentException>(() => new RdfQuad(
            new RdfLiteral("subject"), new RdfIri("https://example.test/predicate"),
            new RdfLiteral("object")).Validate());
    }

    [TestMethod]
    public async Task RDF_profile_is_selectable_through_the_host_boundary()
    {
        string directory = CreateDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(directory)
            .UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"))
            .Build();
        await host.StartAsync();
        EntityForgeDbRuntime runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        var subject = new RdfIri("https://example.test/subject");
        await runtime.RdfDatabase.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(new(subject,
            new RdfIri("https://example.test/predicate"), new RdfLiteral("value")))]));

        Assert.AreEqual(DatabaseProfileId.Rdf, runtime.Profile);
        Assert.AreEqual(1, runtime.RdfDatabase.Find(new(Subject: subject)).Count);
        Assert.AreEqual(1, await runtime.CreateCheckpointAsync());
    }

    [TestMethod]
    public async Task RDF_snapshot_and_routed_change_feed_remain_exact_across_later_commits()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var subject = new RdfIri("https://example.test/subject");
        var predicate = new RdfIri("https://example.test/predicate");
        var first = new RdfQuad(subject, predicate, new RdfLiteral("first"));
        await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(first)]));
        using RdfSnapshotLease snapshot = database.AcquireSnapshot();
        await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(
            new(subject, predicate, new RdfLiteral("second")))]));

        Assert.AreEqual(1, database.Find(new(Subject: subject), snapshot).Count);
        Assert.AreEqual(2, database.Find(new(Subject: subject)).Count);
        DatabaseChangePage changes = await database.ReadChangePageAfterAsync(null,
            new(DatabaseProfileId.Rdf, [new("rdf.predicate", predicate.Value)]), 10);
        Assert.AreEqual(2, changes.Changes.Count);
        Assert.IsTrue(changes.Changes.All(change => change.Payload.CodecId == "entityforge.rdf.transaction"));
    }

    [TestMethod]
    public async Task SPARQL_request_reads_one_retained_snapshot_and_reports_its_version()
    {
        await using RdfDatabaseEngine database = await OpenAsync(CreateDirectory());
        var subject = new RdfIri("https://example.test/subject");
        var predicate = new RdfIri("https://example.test/value");
        DatabaseVersion firstVersion = await database.CommitAsync(new(Guid.NewGuid(),
            [new AssertRdfQuad(new(subject, predicate, new RdfLiteral("first")))]));
        using RdfSnapshotLease retained = database.AcquireSnapshot(firstVersion);
        await database.CommitAsync(new(Guid.NewGuid(),
            [new AssertRdfQuad(new(subject, predicate, new RdfLiteral("second")))]));

        RdfSparqlQueryResult result = await database.QuerySparqlAsync(
            new EntityForge.Db.Rdf.Contracts.RdfSparqlQueryRequest(
            "SELECT ?value WHERE { <https://example.test/subject> <https://example.test/value> ?value . }",
            snapshotVersion: firstVersion));

        Assert.AreEqual(firstVersion, result.SnapshotVersion);
        Assert.AreEqual(new RdfLiteral("first"), ((RdfSparqlSelectResult)result).Bindings.Single()["value"]);
    }

    [TestMethod]
    public async Task NQuads_round_trip_preserves_datatypes_languages_named_graphs_and_scopes_blank_nodes()
    {
        const string text = "<https://example.test/s> <https://example.test/p> \"hello\"@en <https://example.test/g> .\n" +
            "_:b0 <https://example.test/value> \"42\"^^<http://www.w3.org/2001/XMLSchema#integer> _:graph .\n";
        await using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        IReadOnlyList<RdfQuad> first = RdfSyntax.ParseNQuads(input, "document-a");
        Assert.IsTrue(first.OfType<RdfQuad>().Any(quad => quad.Subject is RdfBlankNode blank &&
            blank.Identifier.StartsWith("document-a:", StringComparison.Ordinal)));
        await using var output = new MemoryStream();
        await RdfSyntax.WriteNQuadsAsync(first, output);
        output.Position = 0;
        IReadOnlyList<RdfQuad> reparsed = RdfSyntax.ParseNQuads(output, "roundtrip");

        Assert.AreEqual(2, reparsed.Count);
        Assert.IsTrue(reparsed.Any(quad => quad.Graph is RdfIri { Value: "https://example.test/g" }));
        Assert.IsTrue(reparsed.Any(quad => quad.Object is RdfLiteral
        { Datatype: "http://www.w3.org/2001/XMLSchema#integer", LexicalForm: "42" }));
    }

    [TestMethod]
    public async Task Turtle_round_trip_expands_prefixes_and_preserves_typed_terms()
    {
        const string text = "@prefix schema: <https://schema.org/> .\n" +
            "<https://example.test/alice> schema:name \"Alice\"@en ; schema:age \"42\"^^<http://www.w3.org/2001/XMLSchema#integer> .";
        await using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        IReadOnlyList<RdfQuad> parsed = RdfSyntax.ParseTurtle(input, "turtle-a");
        Assert.AreEqual(2, parsed.Count);
        Assert.IsTrue(parsed.Any(quad => quad.Predicate.Value == "https://schema.org/name" &&
            quad.Object is RdfLiteral { LexicalForm: "Alice", Language: "en" }));
        await using var output = new MemoryStream();
        await RdfSyntax.WriteTurtleAsync(parsed, output);
        output.Position = 0;
        IReadOnlyList<RdfQuad> reparsed = RdfSyntax.ParseTurtle(output, "turtle-b");
        CollectionAssert.AreEquivalent(parsed.ToArray(), reparsed.ToArray());
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await RdfSyntax.WriteTurtleAsync(
            [parsed[0] with { Graph = new RdfIri("https://example.test/graph") }], new MemoryStream()).AsTask());
    }

    [TestMethod]
    public async Task JSON_LD_round_trip_preserves_named_graphs_and_disables_remote_contexts()
    {
        const string json = """
            {
              "@context": { "name": "https://schema.org/name" },
              "@id": "https://example.test/alice",
              "name": { "@value": "Alice", "@language": "en" }
            }
            """;
        await using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        IReadOnlyList<RdfQuad> parsed = RdfSyntax.ParseJsonLd(input, "json-a");
        Assert.AreEqual(1, parsed.Count);
        Assert.AreEqual(new RdfLiteral("Alice", null, "en"), parsed.Single().Object);

        RdfQuad named = parsed.Single() with
        {
            Graph = new RdfIri("https://example.test/graph")
        };
        await using var output = new MemoryStream();
        await RdfSyntax.WriteJsonLdAsync([named], output);
        output.Position = 0;
        IReadOnlyList<RdfQuad> reparsed = RdfSyntax.ParseJsonLd(output, "json-b");
        CollectionAssert.AreEquivalent(new[] { named }, reparsed.ToArray());

        await using var remote = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "{\"@context\":\"https://example.test/context\",\"@id\":\"https://example.test/alice\"}"));
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSyntax.ParseJsonLd(remote, "json-remote"));
    }

    [TestMethod]
    public async Task SHACL_validates_exact_snapshots_reports_violations_and_never_mutates_data()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var alice = new RdfIri("https://example.test/alice");
        var name = new RdfIri("https://schema.org/name");
        RdfQuad assertion = new(alice, name, new RdfLiteral("Alice"));
        DatabaseVersion asserted = await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(assertion)]));
        using RdfSnapshotLease retained = database.AcquireSnapshot(asserted);
        const string shapesText = """
            @prefix sh: <http://www.w3.org/ns/shacl#> .
            @prefix ex: <https://example.test/> .
            @prefix schema: <https://schema.org/> .
            ex:PersonShape a sh:NodeShape ;
              sh:targetNode ex:alice ;
              sh:property [ sh:path schema:name ; sh:minCount 1 ] .
            """;
        await using var shapeStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(shapesText));
        IReadOnlyList<RdfQuad> shapes = RdfSyntax.ParseTurtle(shapeStream, "shapes");

        RdfShaclValidationReport conforming = database.ValidateShacl(shapes, version: asserted);
        Assert.IsTrue(conforming.Conforms);
        Assert.AreEqual(asserted.Value, conforming.SnapshotVersion);

        await database.CommitAsync(new(Guid.NewGuid(), [new RetractRdfQuad(assertion)]));
        DatabaseVersion beforeValidation = database.CurrentVersion;
        RdfShaclValidationReport historic = database.ValidateShacl(shapes, version: asserted);
        RdfShaclValidationReport current = database.ValidateShacl(shapes);
        Assert.IsTrue(historic.Conforms);
        Assert.IsFalse(current.Conforms);
        Assert.AreEqual(1, current.Results.Count);
        Assert.AreEqual(alice, current.Results.Single().FocusNode);
        Assert.AreEqual(beforeValidation, database.CurrentVersion);
        Assert.AreEqual(0, database.Find(new()).Count);
        Assert.IsFalse(RdfShacl.Capabilities.MutatesData);
        Assert.IsFalse(RdfShacl.Capabilities.SupportsSparqlConstraints);
    }

    [TestMethod]
    public void SHACL_rejects_unbounded_named_graph_and_executable_constraint_inputs()
    {
        var shape = new RdfIri("https://example.test/shape");
        var type = new RdfIri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type");
        var nodeShape = new RdfIri("http://www.w3.org/ns/shacl#NodeShape");
        RdfQuad validShape = new(shape, type, nodeShape);

        Assert.ThrowsExactly<RdfShaclValidationLimitException>(() => RdfShacl.Validate(0,
            Enumerable.Repeat(new RdfQuad(shape, type, nodeShape), RdfShacl.MaximumDataQuads + 1).ToArray(),
            [validShape]));
        Assert.ThrowsExactly<RdfShaclValidationLimitException>(() => RdfShacl.Validate(0, [],
            Enumerable.Repeat(validShape, RdfShacl.MaximumShapeQuads + 1).ToArray()));
        Assert.ThrowsExactly<NotSupportedException>(() => RdfShacl.Validate(0, [],
            [validShape with { Graph = new RdfIri("https://example.test/shapes") }]));
        Assert.ThrowsExactly<NotSupportedException>(() => RdfShacl.Validate(0, [],
            [validShape,
                new(shape, new RdfIri("http://www.w3.org/ns/shacl#select"),
                new RdfLiteral("SELECT * WHERE {}"))]));
    }

    [TestMethod]
    public void SHACL_bounds_the_materialized_validation_report()
    {
        const string shapesText = """
            @prefix sh: <http://www.w3.org/ns/shacl#> .
            @prefix ex: <https://example.test/> .
            ex:Shape a sh:NodeShape ; sh:targetNode ex:one, ex:two ;
                sh:property [ sh:path ex:required ; sh:minCount 1 ] .
            """;
        using var shapeStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(shapesText));

        RdfShaclValidationReport report = RdfShacl.Validate(12, [],
            RdfSyntax.ParseTurtle(shapeStream, "bounded-shapes"), maximumResults: 1);

        Assert.IsFalse(report.Conforms);
        Assert.AreEqual(1, report.Results.Count);
        Assert.IsTrue(report.ResultsTruncated);
        Assert.AreEqual(12L, report.SnapshotVersion);
    }

    [TestMethod]
    public async Task RDFS_entailment_is_bounded_snapshot_exact_separate_and_non_mutating()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var alice = new RdfIri("https://example.test/alice");
        var employee = new RdfIri("https://example.test/Employee");
        var person = new RdfIri("https://example.test/Person");
        var rdfType = new RdfIri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type");
        var subClass = new RdfIri("http://www.w3.org/2000/01/rdf-schema#subClassOf");
        RdfQuad schema = new(employee, subClass, person);
        RdfQuad assertion = new(alice, rdfType, employee);
        RdfQuad consequence = new(alice, rdfType, person);
        var anonymous = new RdfBlankNode("anonymous-employee");
        RdfQuad anonymousAssertion = new(anonymous, rdfType, employee);
        RdfQuad anonymousConsequence = new(anonymous, rdfType, person);
        DatabaseVersion asserted = await database.CommitAsync(new(Guid.NewGuid(),
            [new AssertRdfQuad(schema), new AssertRdfQuad(assertion), new AssertRdfQuad(anonymousAssertion)]));
        using RdfSnapshotLease retained = database.AcquireSnapshot(asserted);

        DatabaseVersion beforeInference = database.CurrentVersion;
        RdfEntailmentResult inferred = database.InferRdfs(version: asserted);
        Assert.IsTrue(inferred.DerivedQuads.Contains(consequence));
        Assert.IsTrue(inferred.DerivedQuads.Contains(anonymousConsequence));
        Assert.IsFalse(inferred.DerivedQuads.Contains(assertion));
        Assert.AreEqual(beforeInference, database.CurrentVersion);
        Assert.AreEqual(0, database.Find(new(Subject: alice, Predicate: rdfType, Object: person)).Count,
            "Derived quads must not enter asserted storage.");

        await database.CommitAsync(new(Guid.NewGuid(), [new RetractRdfQuad(assertion)]));
        Assert.IsTrue(database.InferRdfs(version: asserted).DerivedQuads.Contains(consequence));
        Assert.IsFalse(database.InferRdfs().DerivedQuads.Contains(consequence));
        Assert.IsTrue(RdfEntailment.Capabilities.UsesExactSnapshots);
        Assert.IsFalse(RdfEntailment.Capabilities.MutatesAssertedData);
    }

    [TestMethod]
    public void RDFS_entailment_rejects_unbounded_named_graph_inputs_and_bounds_results()
    {
        var employee = new RdfIri("https://example.test/Employee");
        var person = new RdfIri("https://example.test/Person");
        var subClass = new RdfIri("http://www.w3.org/2000/01/rdf-schema#subClassOf");
        RdfQuad schema = new(employee, subClass, person);

        Assert.ThrowsExactly<RdfEntailmentLimitException>(() => RdfEntailment.Infer(0,
            Enumerable.Repeat(schema, RdfEntailment.MaximumAssertedQuads + 1).ToArray()));
        Assert.ThrowsExactly<NotSupportedException>(() => RdfEntailment.Infer(0,
            [schema with { Graph = new RdfIri("https://example.test/graph") }]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RdfEntailment.Infer(0,
            [schema], maximumResults: RdfEntailment.MaximumDerivedQuads + 1));
    }

    [TestMethod]
    public void RDFS_entailment_covers_subproperty_domain_range_and_truncated_reports()
    {
        const string source = """
            @prefix ex: <https://example.test/> .
            @prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .
            ex:worksFor rdfs:subPropertyOf ex:memberOf ;
                rdfs:domain ex:Employee ; rdfs:range ex:Company .
            ex:alice ex:worksFor ex:acme .
            """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(source));
        IReadOnlyList<RdfQuad> asserted = RdfSyntax.ParseTurtle(stream, "rdfs-rules");
        RdfEntailmentResult result = RdfEntailment.Infer(3, asserted);
        var rdfType = new RdfIri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type");

        Assert.IsTrue(result.DerivedQuads.Contains(new(new RdfIri("https://example.test/alice"),
            new RdfIri("https://example.test/memberOf"), new RdfIri("https://example.test/acme"))));
        Assert.IsTrue(result.DerivedQuads.Contains(new(new RdfIri("https://example.test/alice"),
            rdfType, new RdfIri("https://example.test/Employee"))));
        Assert.IsTrue(result.DerivedQuads.Contains(new(new RdfIri("https://example.test/acme"),
            rdfType, new RdfIri("https://example.test/Company"))));
        Assert.IsTrue(RdfEntailment.Infer(3, asserted, maximumResults: 1).ResultsTruncated);
    }

    [TestMethod]
    public async Task RDF_star_quoted_triples_round_trip_through_syntax_storage_query_and_recovery()
    {
        string directory = CreateDirectory();
        const string turtleStar = """
            @prefix ex: <https://example.test/> .
            << ex:alice ex:worksFor ex:company >> ex:source ex:hrSystem .
            """;
        await using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(turtleStar));
        RdfQuad quad = RdfSyntax.ParseTurtle(input, "star").Single();
        var quoted = (RdfTripleTerm)quad.Subject;
        Assert.AreEqual(new RdfIri("https://example.test/alice"), quoted.Subject);

        await using (RdfDatabaseEngine database = await OpenAsync(directory))
        {
            await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(quad)]));
            Assert.AreEqual(quad, database.Find(new(Subject: quoted)).Single());
        }
        await using (RdfDatabaseEngine reopened = await OpenAsync(directory))
        {
            Assert.AreEqual(quad, reopened.Find(new(Subject: quoted)).Single());
        }

        await using var nquads = new MemoryStream();
        await RdfSyntax.WriteNQuadsAsync([quad], nquads);
        nquads.Position = 0;
        Assert.AreEqual(quad, RdfSyntax.ParseNQuads(nquads, "star").Single());
        await using var turtle = new MemoryStream();
        await RdfSyntax.WriteTurtleAsync([quad], turtle);
        turtle.Position = 0;
        Assert.AreEqual(quad, RdfSyntax.ParseTurtle(turtle, "star").Single());
        await Assert.ThrowsExactlyAsync<NotSupportedException>(async () =>
        {
            await using var json = new MemoryStream();
            await RdfSyntax.WriteJsonLdAsync([quad], json);
        });
    }

    [TestMethod]
    public void RDF_star_quoted_triple_nesting_is_bounded()
    {
        RdfTerm term = new RdfIri("https://example.test/subject");
        var predicate = new RdfIri("https://example.test/predicate");
        for (int depth = 0; depth <= RdfTripleTerm.MaximumNestingDepth; depth++)
        {
            term = new RdfTripleTerm(term, predicate, new RdfLiteral("value"));
        }

        Assert.ThrowsExactly<ArgumentException>(() =>
            new RdfQuad(term, predicate, new RdfLiteral("annotation")).Validate());
    }

    [TestMethod]
    public async Task Bounded_SPARQL_basic_graph_patterns_join_on_shared_variables()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var employedBy = new RdfIri("https://example.test/employedBy");
        var name = new RdfIri("https://schema.org/name");
        var alice = new RdfIri("https://example.test/alice");
        var company = new RdfIri("https://example.test/company");
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new AssertRdfQuad(new(alice, employedBy, company)),
            new AssertRdfQuad(new(company, name, new RdfLiteral("Acuity")))
        ]));

        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows = RdfSparql.Execute(database, """
            SELECT ?person ?companyName WHERE {
              ?person <https://example.test/employedBy> ?company .
              ?company <https://schema.org/name> ?companyName .
            } LIMIT 10
            """);

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(alice, rows[0]["person"]);
        Assert.AreEqual(new RdfLiteral("Acuity"), rows[0]["companyName"]);
        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> distinct = RdfSparql.Execute(database,
            "SELECT DISTINCT ?person WHERE { ?person <https://example.test/employedBy> ?company } LIMIT 1 OFFSET 0");
        Assert.AreEqual(alice, distinct.Single()["person"]);
        CollectionAssert.Contains(RdfSparql.Capabilities.Operators.ToArray(), "DISTINCT");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RdfSparql.Execute(database,
            "SELECT ?person WHERE { ?person <https://example.test/employedBy> ?company } LIMIT 10000 OFFSET 1"));
    }

    [TestMethod]
    public async Task Bounded_SPARQL_expands_prefixes_default_prefixes_and_rdf_type_shorthand()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var alice = new RdfIri("https://example.test/alice");
        var person = new RdfIri("https://example.test/Person");
        var type = new RdfIri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type");
        var name = new RdfIri("https://schema.org/name");
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new AssertRdfQuad(new(alice, type, person)),
            new AssertRdfQuad(new(alice, name, new RdfLiteral("Alice")))
        ]));

        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows = RdfSparql.Execute(database, """
            PREFIX : <https://example.test/>
            PREFIX schema: <https://schema.org/>
            SELECT ?name WHERE {
              :alice a :Person .
              :alice schema:name ?name .
            }
            """);

        Assert.AreEqual(new RdfLiteral("Alice"), rows.Single()["name"]);
        CollectionAssert.Contains(RdfSparql.Capabilities.Operators.ToArray(), "PREFIX");
        Assert.AreEqual(RdfSparql.MaximumPrefixes, RdfSparql.Capabilities.MaximumPrefixes);
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database,
            "SELECT ?name WHERE { missing:alice <https://schema.org/name> ?name }"));
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database,
            "PREFIX ex: <https://example.test/> PREFIX ex: <https://other.test/> " +
            "SELECT ?name WHERE { ex:alice <https://schema.org/name> ?name }"));
    }

    [TestMethod]
    public async Task Bounded_SPARQL_supports_tabular_values_undef_and_term_bindings()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var name = new RdfIri("https://schema.org/name");
        var alice = new RdfIri("https://example.test/alice");
        var bob = new RdfIri("https://example.test/bob");
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new AssertRdfQuad(new(alice, name, new RdfLiteral("Alice"))),
            new AssertRdfQuad(new(bob, name, new RdfLiteral("Bob")))
        ]));

        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows = RdfSparql.Execute(database, """
            PREFIX ex: <https://example.test/>
            PREFIX schema: <https://schema.org/>
            SELECT ?person ?expected ?copy WHERE {
              VALUES (?person ?expected) { (ex:alice "Alice") (ex:bob UNDEF) }
              ?person schema:name ?name .
              BIND (?name AS ?copy)
            } ORDER BY ?person
            """);

        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(new RdfLiteral("Alice"), rows[0]["expected"]);
        Assert.IsFalse(rows[1].ContainsKey("expected"));
        Assert.AreEqual(rows[0]["copy"], new RdfLiteral("Alice"));
        Assert.AreEqual(rows[1]["copy"], new RdfLiteral("Bob"));
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database,
            "SELECT ?x WHERE { VALUES (?x ?y) { (1) } ?s ?p ?x }"));
    }

    [TestMethod]
    public async Task Bounded_SPARQL_constructs_graphs_and_describes_direct_subject_statements()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var alice = new RdfIri("https://example.test/alice");
        var name = new RdfIri("https://schema.org/name");
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new AssertRdfQuad(new(alice, name, new RdfLiteral("Alice")))
        ]));

        IReadOnlyList<RdfQuad> constructed = RdfSparql.ExecuteConstruct(database, """
            PREFIX schema: <https://schema.org/>
            PREFIX view: <https://view.test/>
            CONSTRUCT { ?person view:displayName ?name }
            WHERE { ?person schema:name ?name }
            """);
        Assert.AreEqual(1, constructed.Count);
        Assert.AreEqual(new RdfIri("https://view.test/displayName"), constructed.Single().Predicate);

        IReadOnlyList<RdfQuad> described = RdfSparql.ExecuteDescribe(database, """
            PREFIX schema: <https://schema.org/>
            DESCRIBE ?person WHERE { ?person schema:name "Alice" }
            """);
        Assert.AreEqual(1, described.Count);
        Assert.AreEqual(alice, described.Single().Subject);
        Assert.AreEqual(RdfSparqlQueryForm.Ask, RdfSparql.QueryForm(
            "PREFIX schema: <https://schema.org/> ASK { ?s schema:name ?name }"));
        CollectionAssert.Contains(RdfSparql.Capabilities.QueryForms.ToArray(), "CONSTRUCT");
    }

    [TestMethod]
    public void Bounded_SPARQL_Update_compiles_data_commands_and_named_graphs_to_native_mutations()
    {
        IReadOnlyList<RdfMutation> mutations = RdfSparqlUpdate.Parse("""
            PREFIX ex: <https://example.test/>
            INSERT DATA {
              ex:alice ex:name "Alice" .
              GRAPH ex:people { ex:bob ex:name "Bob" }
            };
            DELETE DATA { ex:retired ex:name "Retired" }
            """, "update-scope");

        Assert.AreEqual(3, mutations.Count);
        Assert.AreEqual(2, mutations.OfType<AssertRdfQuad>().Count());
        Assert.AreEqual(1, mutations.OfType<RetractRdfQuad>().Count());
        Assert.AreEqual(new RdfIri("https://example.test/people"),
            mutations.OfType<AssertRdfQuad>().Single(item => item.Quad.Graph is not null).Quad.Graph);
        CollectionAssert.Contains(RdfSparql.Capabilities.UpdateForms.ToArray(), "INSERT DATA");
        Assert.ThrowsExactly<NotSupportedException>(() => RdfSparqlUpdate.Parse(
            "CLEAR ALL", "update-scope"));
    }

    [TestMethod]
    public async Task Bounded_SPARQL_modify_uses_one_native_transaction_and_replays_after_checkpoint_restart()
    {
        string directory = CreateDirectory();
        var subject = new RdfIri("https://example.test/person/1");
        var status = new RdfIri("https://example.test/status");
        var oldValue = new RdfLiteral("old");
        var activeValue = new RdfLiteral("active");
        Guid transactionId = Guid.NewGuid();
        const string update = """
            PREFIX ex: <https://example.test/>
            DELETE { ?person ex:status "old" }
            INSERT { ?person ex:status "active" }
            WHERE  { ?person ex:status "old" }
            """;

        await using (RdfDatabaseEngine database = await OpenAsync(directory))
        {
            await database.CommitAsync(new(Guid.NewGuid(),
                [new AssertRdfQuad(new(subject, status, oldValue))]));
            RdfSparqlUpdateCommit applied = await database.CommitSparqlUpdateAsync(transactionId, update);
            RdfSparqlUpdateCommit replay = await database.CommitSparqlUpdateAsync(transactionId, update);

            Assert.AreEqual(2, applied.MutationCount);
            Assert.AreEqual(applied, replay);
            CollectionAssert.AreEquivalent(new[] { activeValue },
                database.Find(new(Subject: subject, Predicate: status)).Select(quad => quad.Object).ToArray());
            await Assert.ThrowsExactlyAsync<EntityForgeDbIdempotencyException>(async () =>
                await database.CommitSparqlUpdateAsync(transactionId, update.Replace("active", "inactive")));
            await database.CreateCheckpointAsync();
        }

        await using (RdfDatabaseEngine reopened = await OpenAsync(directory))
        {
            RdfSparqlUpdateCommit replay = await reopened.CommitSparqlUpdateAsync(transactionId, update);
            Assert.AreEqual(2, replay.MutationCount);
            CollectionAssert.AreEquivalent(new[] { activeValue },
                reopened.Find(new(Subject: subject, Predicate: status)).Select(quad => quad.Object).ToArray());
        }
    }

    [TestMethod]
    public async Task Bounded_SPARQL_property_paths_support_sequence_alternative_inverse_and_repetition()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var alice = new RdfIri("https://example.test/alice");
        var bob = new RdfIri("https://example.test/bob");
        var carol = new RdfIri("https://example.test/carol");
        var dora = new RdfIri("https://example.test/dora");
        var knows = new RdfIri("https://example.test/knows");
        var manages = new RdfIri("https://example.test/manages");
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new AssertRdfQuad(new(alice, knows, bob)),
            new AssertRdfQuad(new(bob, knows, carol)),
            new AssertRdfQuad(new(alice, manages, dora))
        ]));

        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> sequence = RdfSparql.Execute(database, """
            PREFIX ex: <https://example.test/>
            SELECT ?person WHERE { ex:alice ex:knows/ex:knows ?person }
            """);
        Assert.AreEqual(carol, sequence.Single()["person"]);

        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> zeroOrOne = RdfSparql.Execute(database, """
            PREFIX ex: <https://example.test/>
            SELECT ?person WHERE { ex:alice ex:knows? ?person }
            """);
        CollectionAssert.AreEquivalent(new RdfTerm[] { alice, bob },
            zeroOrOne.Select(row => row["person"]).ToArray());
        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> alternative = RdfSparql.Execute(database, """
            PREFIX ex: <https://example.test/>
            SELECT ?person WHERE { ex:alice (ex:knows|ex:manages) ?person }
            """);
        CollectionAssert.AreEquivalent(new RdfTerm[] { bob, dora },
            alternative.Select(row => row["person"]).ToArray());

        Assert.AreEqual(bob, RdfSparql.Execute(database, """
            PREFIX ex: <https://example.test/>
            SELECT ?person WHERE { ex:carol ^ex:knows ?person }
            """).Single()["person"]);
        Assert.AreEqual(alice, RdfSparql.Execute(database, """
            PREFIX ex: <https://example.test/>
            SELECT ?person WHERE { ?person ex:knows/ex:knows ex:carol }
            """).Single()["person"]);
        Assert.AreEqual(RdfSparql.MaximumPathSteps, 16);
        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() => RdfSparql.Execute(database,
            "SELECT ?person WHERE { <urn:a> " + String.Join("/", Enumerable.Repeat("<urn:p>", 17)) + " ?person }"));
        Assert.AreEqual(3, RdfSparql.Execute(database, """
            PREFIX ex: <https://example.test/>
            SELECT ?person WHERE { ?person ex:knows+ ?other }
            """).Count);
    }

    [TestMethod]
    public async Task SPARQL_reachability_returns_complete_paths_beyond_four_hops()
    {
        await using RdfDatabaseEngine database = await OpenAsync(CreateDirectory());
        var predicate = new RdfIri("urn:next");
        var nodes = Enumerable.Range(0, 7).Select(i => new RdfIri($"urn:n{i}")).ToArray();
        await database.CommitAsync(new(Guid.NewGuid(), Enumerable.Range(0, 6)
            .Select(i => (RdfMutation)new AssertRdfQuad(new(nodes[i], predicate, nodes[i + 1]))).ToArray()));

        foreach (string path in new[] { "<urn:next>+", "(<urn:next>|<urn:other>)+" })
        {
            CollectionAssert.AreEquivalent(nodes.Skip(1).Cast<RdfTerm>().ToArray(), RdfSparql.Execute(database,
                $"SELECT ?o WHERE {{ <urn:n0> {path} ?o }}").Select(row => row["o"]).ToArray());
        }
        Assert.AreEqual(7, RdfSparql.Execute(database, "SELECT ?o WHERE { <urn:n0> <urn:next>* ?o }").Count);
        Assert.AreEqual(nodes[0], RdfSparql.Execute(database, "SELECT ?o WHERE { <urn:n0> ^<urn:next>* ?o }").Single()["o"]);
        var finite = RdfSparql.Execute(database,
            "SELECT ?o WHERE { <urn:n0> <urn:next>/<urn:next>/<urn:next>/<urn:next> ?o }");
        Assert.AreEqual(nodes[4], finite.Single()["o"]);
        Assert.AreEqual(nodes[5], RdfSparql.Execute(database,
            "SELECT ?o WHERE { <urn:n0> <urn:next>/<urn:next>/<urn:next>/<urn:next>/<urn:next> ?o }").Single()["o"]);
    }

    [TestMethod]
    public async Task Bounded_SPARQL_GRAPH_patterns_select_named_graphs_without_leaking_default_graph()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var name = new RdfIri("https://schema.org/name");
        var alice = new RdfIri("https://example.test/alice");
        var graphA = new RdfIri("https://example.test/graph/a");
        var graphB = new RdfIri("https://example.test/graph/b");
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new AssertRdfQuad(new(alice, name, new RdfLiteral("default"))),
            new AssertRdfQuad(new(alice, name, new RdfLiteral("A"), graphA)),
            new AssertRdfQuad(new(alice, name, new RdfLiteral("B"), graphB))
        ]));

        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> allNamed = RdfSparql.Execute(database, """
            SELECT ?graph ?value WHERE {
              GRAPH ?graph { <https://example.test/alice> <https://schema.org/name> ?value . }
            }
            """);
        Assert.AreEqual(2, allNamed.Count);
        CollectionAssert.AreEquivalent(new RdfTerm[] { graphA, graphB },
            allNamed.Select(row => row["graph"]).ToArray());
        Assert.IsFalse(allNamed.Any(row => Equals(row["value"], new RdfLiteral("default"))));

        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> oneGraph = RdfSparql.Execute(database, """
            SELECT ?value WHERE {
              GRAPH <https://example.test/graph/a> {
                <https://example.test/alice> <https://schema.org/name> ?value .
              }
            }
            """);
        Assert.AreEqual(new RdfLiteral("A"), oneGraph.Single()["value"]);
        CollectionAssert.Contains(RdfSparql.Capabilities.Operators.ToArray(), "GRAPH");
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database,
            "SELECT ?value WHERE { GRAPH \"not-a-graph\" { ?s <https://schema.org/name> ?value } }"));
    }

    [TestMethod]
    public async Task Bounded_SPARQL_supports_select_all_filters_and_deterministic_ordering()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var name = new RdfIri("https://schema.org/name");
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new AssertRdfQuad(new(new RdfIri("https://example.test/alice"), name, new RdfLiteral("Alice"))),
            new AssertRdfQuad(new(new RdfIri("https://example.test/bob"), name, new RdfLiteral("Bob"))),
            new AssertRdfQuad(new(new RdfIri("https://example.test/charlie"), name, new RdfLiteral("Charlie")))
        ]));

        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows = RdfSparql.Execute(database, """
            SELECT * WHERE {
              ?person <https://schema.org/name> ?name .
              FILTER (?name != "Bob")
            } ORDER BY DESC(?name) LIMIT 1
            """);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(new RdfLiteral("Charlie"), rows.Single()["name"]);
        Assert.IsTrue(rows.Single().ContainsKey("person"));

        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> reversedModifiers = RdfSparql.Execute(database,
            "SELECT ?name WHERE { ?person <https://schema.org/name> ?name FILTER (?name = ?name) } " +
            "ORDER BY ?name OFFSET 1 LIMIT 1");
        Assert.AreEqual(new RdfLiteral("Bob"), reversedModifiers.Single()["name"]);
        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> regexRows = RdfSparql.Execute(database,
            "SELECT ?name WHERE { ?person <https://schema.org/name> ?name FILTER regex(?name, \"^A\") }");
        Assert.AreEqual(new RdfLiteral("Alice"), regexRows.Single()["name"]);
    }

    [TestMethod]
    public async Task Bounded_SPARQL_orders_the_exact_single_pattern_before_applying_offset_and_limit()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var type = new RdfIri("https://www.w3.org/1999/02/22-rdf-syntax-ns#type");
        var person = new RdfIri("https://example.test/Person");
        var padding = new RdfIri("https://example.test/Padding");
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new AssertRdfQuad(new(new RdfIri("https://example.test/zulu"), type, person)),
            new AssertRdfQuad(new(new RdfIri("https://example.test/alpha"), type, person)),
            new AssertRdfQuad(new(new RdfIri("https://example.test/bravo"), type, person)),
            new AssertRdfQuad(new(new RdfIri("https://example.test/000-padding"), type, padding))
        ]));

        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> ascending = RdfSparql.Execute(database,
            "SELECT ?person WHERE { ?person <https://www.w3.org/1999/02/22-rdf-syntax-ns#type> " +
            "<https://example.test/Person> } ORDER BY ?person OFFSET 1 LIMIT 1");
        Assert.AreEqual(new RdfIri("https://example.test/bravo"), ascending.Single()["person"]);

        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> descending = RdfSparql.Execute(database,
            "SELECT ?person WHERE { ?person <https://www.w3.org/1999/02/22-rdf-syntax-ns#type> " +
            "<https://example.test/Person> } ORDER BY DESC(?person) LIMIT 1");
        Assert.AreEqual(new RdfIri("https://example.test/zulu"), descending.Single()["person"]);
    }

    [TestMethod]
    public async Task Bounded_SPARQL_fails_explicitly_instead_of_truncating_intermediate_joins()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var left = new RdfIri("https://example.test/left");
        var right = new RdfIri("https://example.test/right");
        RdfMutation[] mutations = Enumerable.Range(0, 101).SelectMany(index => new RdfMutation[]
        {
            new AssertRdfQuad(new(new RdfIri($"https://example.test/l{index}"), left,
                new RdfLiteral(index.ToString()))),
            new AssertRdfQuad(new(new RdfIri($"https://example.test/r{index}"), right,
                new RdfLiteral(index.ToString())))
        }).ToArray();
        await database.CommitAsync(new(Guid.NewGuid(), mutations));

        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() => RdfSparql.Execute(database,
            "SELECT ?l ?r WHERE { ?l <https://example.test/left> ?lv . " +
            "?r <https://example.test/right> ?rv } LIMIT 1"));
    }

    [TestMethod]
    public async Task Bounded_SPARQL_binds_variables_inside_RDF_star_triple_terms()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var alice = new RdfIri("https://example.test/alice");
        var worksFor = new RdfIri("https://example.test/worksFor");
        var company = new RdfIri("https://example.test/company");
        var source = new RdfIri("https://example.test/source");
        var hr = new RdfIri("https://example.test/hr");
        await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(new(
            new RdfTripleTerm(alice, worksFor, company), source, hr))]));

        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows = RdfSparql.Execute(database, """
            SELECT ?person ?employer ?source WHERE {
              << ?person <https://example.test/worksFor> ?employer >>
                <https://example.test/source> ?source .
            }
            """);

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(alice, rows.Single()["person"]);
        Assert.AreEqual(company, rows.Single()["employer"]);
        Assert.AreEqual(hr, rows.Single()["source"]);
        CollectionAssert.Contains(RdfSparql.Capabilities.Operators.ToArray(), "triple-term-pattern");

        string nested = "<https://example.test/alice>";
        for (int depth = 0; depth <= RdfTripleTerm.MaximumNestingDepth; depth++)
        {
            nested = $"<< {nested} <https://example.test/worksFor> <https://example.test/company> >>";
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RdfSparql.Execute(database,
            $"SELECT ?source WHERE {{ {nested} <https://example.test/source> ?source }}"));
    }

    [TestMethod]
    public async Task Bounded_SPARQL_supports_OPTIONAL_and_UNION_with_named_graphs()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var type = new RdfIri("https://example.test/type");
        var employee = new RdfIri("https://example.test/Employee");
        var contractor = new RdfIri("https://example.test/Contractor");
        var email = new RdfIri("https://example.test/email");
        var alice = new RdfIri("https://example.test/alice");
        var bob = new RdfIri("https://example.test/bob");
        var graph = new RdfIri("https://example.test/directory");
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new AssertRdfQuad(new(alice, type, employee, graph)),
            new AssertRdfQuad(new(alice, email, new RdfLiteral("alice@example.test"), graph)),
            new AssertRdfQuad(new(bob, type, contractor, graph))
        ]));

        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows = RdfSparql.Execute(database, """
            SELECT ?person ?email WHERE {
              GRAPH <https://example.test/directory> {
                { ?person <https://example.test/type> <https://example.test/Employee> }
                UNION
                { ?person <https://example.test/type> <https://example.test/Contractor> }
                OPTIONAL { ?person <https://example.test/email> ?email }
              }
            } ORDER BY ?person
            """);

        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(alice, rows[0]["person"]);
        Assert.AreEqual(new RdfLiteral("alice@example.test"), rows[0]["email"]);
        Assert.AreEqual(bob, rows[1]["person"]);
        Assert.IsFalse(rows[1].ContainsKey("email"));
        CollectionAssert.Contains(RdfSparql.Capabilities.Operators.ToArray(), "OPTIONAL");
        CollectionAssert.Contains(RdfSparql.Capabilities.Operators.ToArray(), "UNION");
        Assert.AreEqual(1, RdfSparql.Execute(database,
            "SELECT ?person WHERE { OPTIONAL { } }").Count);
    }

    [TestMethod]
    public async Task Bounded_SPARQL_UNION_fails_when_combined_work_exceeds_the_published_limit()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var type = new RdfIri("https://example.test/type");
        RdfMutation[] mutations = Enumerable.Range(0, (RdfSparql.MaximumResults / 2) + 1)
            .Select(index => (RdfMutation)new AssertRdfQuad(new(
                new RdfIri($"https://example.test/person/{index}"), type, new RdfLiteral("person"))))
            .ToArray();
        await database.CommitAsync(new(Guid.NewGuid(), mutations));

        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() => RdfSparql.Execute(database, """
            SELECT ?person WHERE {
              { ?person <https://example.test/type> "person" }
              UNION
              { ?person <https://example.test/type> "person" }
            }
            """));
    }

    [TestMethod]
    public async Task Release_SPARQL_supports_ASK_VALUES_and_practical_filter_expressions()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var name = new RdfIri("https://schema.org/name");
        var age = new RdfIri("https://schema.org/age");
        var alice = new RdfIri("https://example.test/alice");
        var bob = new RdfIri("https://example.test/bob");
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new AssertRdfQuad(new(alice, name, new RdfLiteral("Alice"))),
            new AssertRdfQuad(new(alice, age, new RdfLiteral("34",
                "http://www.w3.org/2001/XMLSchema#integer"))),
            new AssertRdfQuad(new(bob, name, new RdfLiteral("Bob"))),
            new AssertRdfQuad(new(bob, age, new RdfLiteral("17",
                "http://www.w3.org/2001/XMLSchema#integer")))
        ]));

        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows = RdfSparql.Execute(database, """
            SELECT ?person ?name WHERE {
              VALUES ?person { <https://example.test/alice> <https://example.test/bob> }
              ?person <https://schema.org/name> ?name .
              ?person <https://schema.org/age> ?age .
              OPTIONAL { ?person <https://schema.org/email> ?email }
              FILTER (isIRI(?person) && isLiteral(?name) && ?age >= 18 &&
                STRSTARTS(?name, "A") && !CONTAINS(?name, "z") && !BOUND(?email))
            }
            """);

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(alice, rows.Single()["person"]);
        Assert.IsTrue(RdfSparql.ExecuteAsk(database, """
            ASK WHERE { <https://example.test/alice> <https://schema.org/age> ?age FILTER (?age > 21) }
            """));
        Assert.IsFalse(RdfSparql.ExecuteAsk(database, """
            ASK { <https://example.test/bob> <https://schema.org/age> ?age FILTER (?age >= 18) }
            """));
        CollectionAssert.Contains(RdfSparql.Capabilities.QueryForms.ToArray(), "ASK");
        CollectionAssert.Contains(RdfSparql.Capabilities.Operators.ToArray(), "VALUES");
        Assert.IsTrue(RdfSparql.ExecuteAsk(database,
            "ASK { ?s <https://schema.org/name> ?name } LIMIT 1"));

        string deepExpression = String.Concat(Enumerable.Repeat("!(", RdfSparql.MaximumExpressionDepth + 1)) +
            "BOUND(?name)" + String.Concat(Enumerable.Repeat(")", RdfSparql.MaximumExpressionDepth + 1));
        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() => RdfSparql.Execute(database,
            $"SELECT ?name WHERE {{ ?person <https://schema.org/name> ?name FILTER ({deepExpression}) }}"));
        string tooManyValues = String.Join(' ', Enumerable.Range(0, RdfSparql.MaximumValues + 1)
            .Select(index => $"<https://example.test/value/{index}>"));
        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() => RdfSparql.Execute(database,
            $"SELECT ?value WHERE {{ VALUES ?value {{ {tooManyValues} }} }}"));
        string nestedGroups = "?person <https://schema.org/name> ?name";
        for (int depth = 0; depth <= RdfSparql.MaximumGroupDepth; depth++)
        {
            nestedGroups = $"OPTIONAL {{ {nestedGroups} }}";
        }

        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() => RdfSparql.Execute(database,
            $"SELECT ?name WHERE {{ {nestedGroups} }}"));
    }

    [TestMethod]
    public async Task Release_SPARQL_aggregates_and_groups_bounded_solution_sequences()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var department = new RdfIri("https://example.test/department");
        var salary = new RdfIri("https://example.test/salary");
        var engineering = new RdfIri("https://example.test/engineering");
        var research = new RdfIri("https://example.test/research");
        RdfIri[] people = Enumerable.Range(1, 4)
            .Select(index => new RdfIri($"https://example.test/person/{index}")).ToArray();
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new AssertRdfQuad(new(people[0], department, engineering)),
            new AssertRdfQuad(new(people[0], salary, Integer(10))),
            new AssertRdfQuad(new(people[1], department, engineering)),
            new AssertRdfQuad(new(people[1], salary, Integer(20))),
            new AssertRdfQuad(new(people[2], department, research)),
            new AssertRdfQuad(new(people[2], salary, Integer(30))),
            new AssertRdfQuad(new(people[3], department, research)),
            new AssertRdfQuad(new(people[3], salary, Integer(40)))
        ]));

        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows = RdfSparql.Execute(database, """
            SELECT ?department (COUNT(?person) AS ?people) (SUM(?salary) AS ?total)
              (MIN(?salary) AS ?minimum) (MAX(?salary) AS ?maximum) (AVG(?salary) AS ?average)
            WHERE {
              ?person <https://example.test/department> ?department .
              ?person <https://example.test/salary> ?salary
            }
            GROUP BY ?department ORDER BY DESC(?total)
            """);

        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(research, rows[0]["department"]);
        Assert.AreEqual("2", ((RdfLiteral)rows[0]["people"]).LexicalForm);
        Assert.AreEqual("70", ((RdfLiteral)rows[0]["total"]).LexicalForm);
        Assert.AreEqual("30", ((RdfLiteral)rows[0]["minimum"]).LexicalForm);
        Assert.AreEqual("40", ((RdfLiteral)rows[0]["maximum"]).LexicalForm);
        Assert.AreEqual("35", ((RdfLiteral)rows[0]["average"]).LexicalForm);
        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> havingRows = RdfSparql.Execute(database, """
            SELECT ?department (SUM(?salary) AS ?total)
            WHERE {
              ?person <https://example.test/department> ?department .
              ?person <https://example.test/salary> ?salary
            }
            GROUP BY ?department HAVING (SUM(?salary) > 50)
            """);
        Assert.AreEqual(research, havingRows.Single()["department"]);
        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> subqueryRows = RdfSparql.Execute(database, """
            SELECT ?department ?total WHERE {
              { SELECT ?department (SUM(?salary) AS ?total) WHERE {
                  ?person <https://example.test/department> ?department .
                  ?person <https://example.test/salary> ?salary
                } GROUP BY ?department HAVING (SUM(?salary) > 50) }
            }
            """);
        Assert.AreEqual(research, subqueryRows.Single()["department"]);
        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> countRows = RdfSparql.Execute(database, """
            SELECT ?department (COUNT(?person) AS ?people)
            WHERE {
              ?person <https://example.test/department> ?department .
              ?person <https://example.test/salary> ?salary
            }
            GROUP BY ?department ORDER BY DESC(?people)
            """);
        Assert.AreEqual(2, countRows.Count);
        Assert.IsTrue(countRows.All(row => ((RdfLiteral)row["people"]).LexicalForm == "2"));
        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> orderedRows = RdfSparql.Execute(database, """
            SELECT ?salary WHERE {
              ?person <https://example.test/department> <https://example.test/engineering> .
              ?person <https://example.test/salary> ?salary
            }
            ORDER BY DESC(?salary) LIMIT 1
            """);
        Assert.AreEqual("20", ((RdfLiteral)orderedRows.Single()["salary"]).LexicalForm);
        CollectionAssert.Contains(RdfSparql.Capabilities.Operators.ToArray(), "GROUP BY");
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database, """
            SELECT ?person (COUNT(*) AS ?count)
            WHERE { ?person <https://example.test/department> ?department }
            """));

        const string directCount = """
            SELECT ?department (COUNT(?person) AS ?people)
            WHERE { ?person <https://example.test/department> ?department }
            GROUP BY ?department
            """;
        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> directCounts =
            RdfSparql.Execute(database, directCount);
        Assert.AreEqual("2", ((RdfLiteral)directCounts.Single(row =>
            Equals(row["department"], engineering))["people"]).LexicalForm);
        var fifthPerson = new RdfIri("https://example.test/person/5");
        await database.CommitAsync(new(Guid.NewGuid(),
            [new AssertRdfQuad(new(fifthPerson, department, engineering))]));
        directCounts = RdfSparql.Execute(database, directCount);
        Assert.AreEqual("3", ((RdfLiteral)directCounts.Single(row =>
            Equals(row["department"], engineering))["people"]).LexicalForm);

        static RdfLiteral Integer(int value) => new(value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "http://www.w3.org/2001/XMLSchema#integer");
    }

    [TestMethod]
    public async Task Release_SPARQL_filters_correlated_exists_and_not_exists_through_native_matching()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var personType = new RdfIri("https://example.test/Person");
        var rdfType = new RdfIri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type");
        var email = new RdfIri("https://example.test/email");
        var alice = new RdfIri("https://example.test/alice");
        var bob = new RdfIri("https://example.test/bob");
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new AssertRdfQuad(new(alice, rdfType, personType)),
            new AssertRdfQuad(new(bob, rdfType, personType)),
            new AssertRdfQuad(new(alice, email, new RdfLiteral("alice@example.test")))
        ]));

        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> withEmail = RdfSparql.Execute(database, """
            SELECT ?person WHERE {
              ?person a <https://example.test/Person>
              FILTER EXISTS { ?person <https://example.test/email> ?email }
            }
            """);
        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> withoutEmail = RdfSparql.Execute(database, """
            SELECT ?person WHERE {
              ?person a <https://example.test/Person>
              FILTER NOT EXISTS { ?person <https://example.test/email> ?email }
            }
            """);

        Assert.AreEqual(alice, withEmail.Single()["person"]);
        Assert.AreEqual(bob, withoutEmail.Single()["person"]);
        CollectionAssert.Contains(RdfSparql.Capabilities.Operators.ToArray(), "NOT EXISTS");
    }

    [TestMethod]
    public async Task Release_SPARQL_supports_common_value_functions_membership_regex_and_minus()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var name = new RdfIri("https://schema.org/name");
        var inactive = new RdfIri("https://example.test/inactive");
        var alice = new RdfIri("https://example.test/alice");
        var bob = new RdfIri("https://example.test/bob");
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new AssertRdfQuad(new(alice, name, new RdfLiteral("Alice", language: "en-GB"))),
            new AssertRdfQuad(new(bob, name, new RdfLiteral("Bob", language: "en"))),
            new AssertRdfQuad(new(bob, inactive, new RdfLiteral("true")))
        ]));

        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows = RdfSparql.Execute(database, """
            SELECT ?person WHERE {
              ?person <https://schema.org/name> ?name .
              FILTER (REGEX(STR(?name), "^ali", "i") && LANGMATCHES(LANG(?name), "en") &&
                ?person IN (<https://example.test/alice>, <https://example.test/bob>))
              MINUS { ?person <https://example.test/inactive> "true" }
            }
            """);

        Assert.AreEqual(alice, rows.Single()["person"]);
        CollectionAssert.Contains(RdfSparql.Capabilities.Operators.ToArray(), "MINUS");
    }

    [TestMethod]
    public async Task SPARQL_numeric_comparison_preserves_large_integer_identity_and_function_errors()
    {
        await using RdfDatabaseEngine database = await OpenAsync(CreateDirectory());
        var value = new RdfIri("urn:numeric:value");
        var otherValue = new RdfIri("urn:numeric:other-value");
        var label = new RdfIri("urn:numeric:label");
        var first = new RdfIri("urn:numeric:first");
        var second = new RdfIri("urn:numeric:second");
        const string integer = "http://www.w3.org/2001/XMLSchema#integer";
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new AssertRdfQuad(new(first, value, new RdfLiteral("9007199254740992", integer))),
            new AssertRdfQuad(new(first, otherValue, new RdfLiteral("9007199254740993", integer))),
            new AssertRdfQuad(new(second, value, new RdfLiteral("9007199254740993", integer))),
            new AssertRdfQuad(new(first, label, new RdfLiteral("first"))),
            new AssertRdfQuad(new(second, label, new RdfLiteral("second")))
        ]));

        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> ordered = RdfSparql.Execute(database, """
            SELECT ?s WHERE { ?s <urn:numeric:value> ?value } ORDER BY DESC(?value) LIMIT 1
            """);
        Assert.AreEqual(second, ordered.Single()["s"]);
        Assert.IsFalse(RdfSparql.ExecuteAsk(database, """
            ASK WHERE { <urn:numeric:first> <urn:numeric:value> ?value .
              <urn:numeric:first> <urn:numeric:other-value> ?other . FILTER (?value = ?other) }
            """));
        Assert.IsFalse(RdfSparql.ExecuteAsk(database, """
            ASK WHERE { <urn:numeric:first> <urn:numeric:label> ?label
              OPTIONAL { <urn:missing> <urn:flags> ?flags }
              FILTER (REGEX(?label, "first", ?flags)) }
            """));
        Assert.IsFalse(RdfSparql.ExecuteAsk(database, """
            ASK WHERE { <urn:numeric:first> <urn:numeric:value> ?value
              FILTER (STRSTARTS(?value, "9")) }
            """));
    }

    [TestMethod]
    public async Task Alpha_SPARQL_parser_rejects_malformed_queries_without_semantic_fallback()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        string[] malformed =
        [
            "SELECT WHERE { ?s ?p ?o }",
            "SELECT ?s WHERE { ?s ?p }",
            "SELECT ?s WHERE { OPTIONAL { }",
            "SELECT ?s WHERE { { ?s ?p ?o }",
            "SELECT ?s WHERE { ?s ?p ?o SERVICE <https://example.test/sparql> { ?s ?p ?o } }",
            "ASK { ?s ?p ?o } LIMIT -1",
            "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }",
            "DELETE WHERE { ?s ?p ?o }"
        ];
        foreach (string query in malformed)
        {
            try
            {
                if (query.TrimStart().StartsWith("ASK", StringComparison.OrdinalIgnoreCase))
                {
                    _ = RdfSparql.ExecuteAsk(database, query);
                }
                else
                {
                    _ = RdfSparql.Execute(database, query);
                }

                Assert.Fail($"Malformed query was accepted: {query}");
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or
                                              NotSupportedException or RdfSparqlWorkLimitException)
            {
            }
        }
    }

    [TestMethod]
    public async Task Alpha_SPARQL_parser_rejects_a_bounded_adversarial_corpus_without_pathological_runtime()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var stopwatch = Stopwatch.StartNew();
        for (int index = 0; index < ParserResilienceSamples; index++)
        {
            string padding = new(' ', index % 29);
            string query = (index % 4) switch
            {
                0 => $"SELECT ?s WHERE {{{padding}?s ?p ?o FILTER (((BOUND(?s))) }}",
                1 => $"SELECT ?s WHERE {{{padding}?s ?p ?o SERVICE <https://example.test/{index}> {{ ?s ?p ?o }} }}",
                2 => $"SELECT ?s WHERE {{{padding}?s ?p ?o }} GROUP BY ?missing",
                _ => $"SELECT ?s WHERE {{{padding}?s (<https://example.test/p>/) ?o }}"
            };

            try
            {
                _ = RdfSparql.Execute(database, query);
                Assert.Fail($"Adversarial SPARQL sample {index} was accepted.");
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or
                                              NotSupportedException or RdfSparqlWorkLimitException)
            {
            }
        }
        stopwatch.Stop();

        Assert.IsTrue(stopwatch.Elapsed < ParserResilienceBudget,
            $"The bounded SPARQL rejection corpus took {stopwatch.Elapsed}; budget is {ParserResilienceBudget}.");
    }

    [TestMethod]
    public async Task RDF_document_pages_resume_idempotently_after_restart_and_reject_changed_replays()
    {
        string directory = CreateDirectory();
        var documentId = new RdfDocumentIngestId(Guid.NewGuid());
        var predicate = new RdfIri("https://example.test/value");
        RdfQuad first = new(new RdfIri("https://example.test/1"), predicate, new RdfLiteral("one"));
        RdfQuad second = new(new RdfIri("https://example.test/2"), predicate, new RdfLiteral("two"));

        await using (RdfDatabaseEngine database = await OpenAsync(directory))
        {
            RdfDocumentPageCommit applied = await database.CommitDocumentPageAsync(
                documentId, new(0, [first]));
            RdfDocumentPageCommit replay = await database.CommitDocumentPageAsync(
                documentId, new(0, [first]));
            Assert.AreEqual(DatabaseCommitOutcome.AppliedWithChanges, applied.Outcome);
            Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Outcome);
            Assert.AreEqual(applied.Version, replay.Version);
        }

        await using (RdfDatabaseEngine reopened = await OpenAsync(directory))
        {
            RdfDocumentPageCommit replay = await reopened.CommitDocumentPageAsync(
                documentId, new(0, [first]));
            Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Outcome);
            await reopened.CommitDocumentPageAsync(documentId, new(1, [second]));
            Assert.AreEqual(2, reopened.Find(new(Predicate: predicate)).Count);
            await Assert.ThrowsExactlyAsync<EntityForgeDbIdempotencyException>(async () =>
                await reopened.CommitDocumentPageAsync(documentId, new(0, [second])));
        }
    }

    [TestMethod]
    public void RDF_document_pages_enforce_the_atomic_work_bound()
    {
        var predicate = new RdfIri("https://example.test/value");
        RdfQuad quad = new(new RdfIri("https://example.test/1"), predicate, new RdfLiteral("one"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RdfDocumentPage(0, []));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RdfDocumentPage(0,
            Enumerable.Repeat(quad, RdfDocumentPage.MaximumQuadCount + 1).ToArray()));
    }

    [TestMethod]
    public async Task RDF_reindex_preserves_queries_snapshots_and_restart()
    {
        string directory = CreateDirectory();
        var predicate = new RdfIri("https://example.test/value");
        var subject = new RdfIri("https://example.test/subject");
        var quad = new RdfQuad(subject, predicate, new RdfLiteral("value"));
        await using (RdfDatabaseEngine database = await OpenAsync(directory))
        {
            await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(quad)]));
            using RdfSnapshotLease snapshot = database.AcquireSnapshot();
            DatabaseVersion reindexed = await database.ReindexAsync();
            Assert.IsTrue(reindexed.Value > snapshot.Version.Value);
            Assert.AreEqual(quad, database.Find(new(Subject: subject)).Single());
            Assert.AreEqual(quad, database.Find(new(Subject: subject), snapshot).Single());
            Assert.AreEqual(DatabaseMaintenancePhase.Completed, database.MaintenanceStatus.Phase);
        }
        await using RdfDatabaseEngine reopened = await OpenAsync(directory);
        Assert.AreEqual(quad, reopened.Find(new(Subject: subject)).Single());
    }

    [TestMethod]
    public async Task RDF_predicate_object_queries_use_the_selective_object_access_path_across_reindex_and_restart()
    {
        string directory = CreateDirectory();
        var employedBy = new RdfIri("https://example.test/employedBy");
        var target = new RdfIri("https://example.test/company/17");
        RdfQuad expected = new(new RdfIri("https://example.test/person/17"), employedBy, target);
        RdfQuad[] quads = Enumerable.Range(0, 100).Select(index => new RdfQuad(
            new RdfIri($"https://example.test/person/{index}"), employedBy,
            index == 17 ? target : new RdfIri($"https://example.test/company/{index}"))).ToArray();
        var pattern = new RdfQuadPattern(Predicate: employedBy, Object: target);

        Assert.AreEqual(RdfPhysicalAccessFamily.Object, RdfPhysicalAccessPath.SelectedFamily(pattern));
        await using (RdfDatabaseEngine database = await OpenAsync(directory))
        {
            await database.CommitAsync(new(Guid.NewGuid(), quads.Select(quad =>
                (RdfMutation)new AssertRdfQuad(quad)).ToArray()));
            Assert.AreEqual(expected, database.Find(pattern).Single());
            await database.ReindexAsync();
            Assert.AreEqual(expected, database.Find(pattern).Single());
        }

        await using RdfDatabaseEngine reopened = await OpenAsync(directory);
        Assert.AreEqual(expected, reopened.Find(pattern).Single());
    }

    [TestMethod]
    public async Task RDF_bulk_load_directly_builds_an_empty_database_and_remains_durable_and_observable()
    {
        string directory = CreateDirectory();
        var predicate = new RdfIri("https://example.test/bulk/predicate");
        RdfQuad[] quads = Enumerable.Range(0, 100).Select(index => new RdfQuad(
            new RdfIri($"https://example.test/bulk/{index}"), predicate,
            new RdfLiteral($"value-{index}"))).ToArray();
        Guid transactionId = Guid.NewGuid();

        await using (RdfDatabaseEngine database = await OpenAsync(directory))
        {
            DatabaseVersion version = await database.BulkLoadAsync(transactionId, quads);
            Assert.AreEqual(1L, version.Value);
            Assert.AreEqual(version, await database.BulkLoadAsync(transactionId, quads));
            Assert.AreEqual(quads.Length, database.Find(new(Predicate: predicate)).Count);
            DatabaseChangePage changes = await database.ReadChangePageAfterAsync(null,
                new(DatabaseProfileId.Rdf, [new("rdf.predicate", predicate.Value)]), 2);
            Assert.AreEqual(1, changes.Changes.Count);
            CommittedChangePayload payload = changes.Changes.Single().Payload;
            Assert.AreEqual(quads.Length, payload.EffectCount);
            Assert.AreEqual(RdfProfileRuntime.DatasetResetCodecId, payload.CodecId);
            Assert.AreEqual(CommittedChangeKind.DatasetReset, payload.Kind);
            Assert.IsTrue(payload.RequiresSnapshotRefresh);
            Assert.IsTrue(payload.Content.Length < 1_024);
            Assert.IsTrue(payload.Routing.MayContain(new("rdf.predicate", predicate.Value)));
            Assert.IsTrue(payload.Routing.MayContain(new("rdf.subject", "https://example.test/unknown")));
            await Assert.ThrowsExactlyAsync<EntityForgeDbCommitException>(async () =>
                await database.BulkLoadAsync(Guid.NewGuid(), quads));
            await database.CreateCheckpointAsync();
        }

        await using RdfDatabaseEngine reopened = await OpenAsync(directory);
        Assert.AreEqual(new DatabaseVersion(1), await reopened.BulkLoadAsync(transactionId, quads));
        CollectionAssert.AreEquivalent(quads, reopened.Find(new(Predicate: predicate)).ToArray());
    }

    [TestMethod]
    public async Task RDF_query_pages_remain_on_one_snapshot_while_writes_and_reindex_continue()
    {
        string directory = CreateDirectory();
        await using RdfDatabaseEngine database = await OpenAsync(directory);
        var predicate = new RdfIri("https://example.test/value");
        RdfQuad[] original = Enumerable.Range(0, 5).Select(index => new RdfQuad(
            new RdfIri($"https://example.test/{index}"), predicate, new RdfLiteral(index.ToString()))).ToArray();
        await database.CommitAsync(new(Guid.NewGuid(), original.Select(quad =>
            (RdfMutation)new AssertRdfQuad(quad)).ToArray()));

        RdfQueryPage first = await database.QueryPageAsync(new(Predicate: predicate), 2);
        Assert.AreEqual(2, first.Quads.Count);
        Assert.IsNotNull(first.NextContinuation);
        RdfQuad later = new(new RdfIri("https://example.test/later"), predicate, new RdfLiteral("later"));
        await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(later)]));
        await database.ReindexAsync();
        var retained = new List<RdfQuad>(first.Quads);
        string? cursor = first.NextContinuation;
        while (cursor is not null)
        {
            RdfQueryPage page = await database.QueryPageAsync(new(Predicate: predicate), 2, cursor);
            retained.AddRange(page.Quads);
            cursor = page.NextContinuation;
        }
        CollectionAssert.AreEquivalent(original, retained.ToArray());
        Assert.IsFalse(retained.Contains(later));
        await Assert.ThrowsExactlyAsync<RdfQueryContinuationException>(async () =>
            await database.QueryPageAsync(new(Subject: later.Subject), 2, first.NextContinuation));
    }

    [TestMethod]
    [DataRow("DELETE { ?s <https://example.test/p> ?o } WHERE { ?s <https://example.test/p> ?o }")]
    [DataRow("DELETE WHERE { ?s <https://example.test/p> ?o }")]
    [DataRow("CLEAR ALL")]
    [DataRow("CLEAR SILENT ALL")]
    public async Task SPARQL_modify_rejects_overflow_without_partial_commit(string update)
    {
        await using RdfDatabaseEngine database = await OpenAsync(CreateDirectory());
        var predicate = new RdfIri("https://example.test/p");
        RdfMutation[] assertions = Enumerable.Range(0, RdfSparqlUpdate.MaximumMutations + 1)
            .Select(index => (RdfMutation)new AssertRdfQuad(new(
                new RdfIri($"https://example.test/s/{index}"), predicate, new RdfLiteral(index.ToString()))))
            .ToArray();
        await database.CommitAsync(new(Guid.NewGuid(), assertions));
        DatabaseVersion before = database.CurrentVersion;

        await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(async () =>
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(), update));

        Assert.AreEqual(before, database.CurrentVersion);
        Assert.AreEqual(assertions.Length, database.Find(new(Predicate: predicate)).Count);
    }

    [TestMethod]
    public async Task SPARQL_modify_does_not_truncate_many_to_one_solution_mappings()
    {
        await using RdfDatabaseEngine database = await OpenAsync(CreateDirectory());
        var predicate = new RdfIri("urn:p");
        var first = new RdfIri("urn:a");
        var last = new RdfIri("urn:z");
        RdfMutation[] assertions = Enumerable.Range(0, RdfSparqlUpdate.MaximumMutations + 1)
            .Select(index => (RdfMutation)new AssertRdfQuad(new(first, predicate,
                new RdfLiteral(index.ToString()))))
            .Append(new AssertRdfQuad(new(last, predicate, new RdfLiteral("last"))))
            .ToArray();
        await database.CommitAsync(new(Guid.NewGuid(), assertions));

        await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "INSERT { ?s <urn:flag> \"yes\" } WHERE { ?s <urn:p> ?o }");

        var flag = new RdfIri("urn:flag");
        CollectionAssert.AreEquivalent(new RdfTerm[] { first, last },
            database.Find(new(Predicate: flag)).Select(quad => quad.Subject).ToArray());
    }

    [TestMethod]
    public async Task SPARQL_optimized_paths_preserve_named_graph_counts_and_numeric_ordering()
    {
        await using RdfDatabaseEngine database = await OpenAsync(CreateDirectory());
        var predicate = new RdfIri("https://example.test/p");
        var integer = "http://www.w3.org/2001/XMLSchema#integer";
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new AssertRdfQuad(new(new RdfIri("https://example.test/default"), predicate,
                new RdfLiteral("1", integer))),
            new AssertRdfQuad(new(new RdfIri("https://example.test/named"), predicate,
                new RdfLiteral("10", integer), new RdfIri("https://example.test/g"))),
            new AssertRdfQuad(new(new RdfIri("https://example.test/two"), predicate,
                new RdfLiteral("2", integer), new RdfIri("https://example.test/g")))
        ]));

        var orderedGraph = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
            "SELECT ?s WHERE { GRAPH ?g { ?s <https://example.test/p> ?o } } ORDER BY ?s LIMIT 1");
        Assert.AreEqual(new RdfIri("https://example.test/named"), orderedGraph.Bindings.Single()["s"]);
        var numeric = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
            "SELECT ?o WHERE { GRAPH ?g { ?s <https://example.test/p> ?o } } ORDER BY ?o LIMIT 1");
        Assert.AreEqual(new RdfLiteral("2", integer), numeric.Bindings.Single()["o"]);
        var repeated = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
            "SELECT ?x (COUNT(?x) AS ?count) WHERE { ?x <https://example.test/p> ?x } GROUP BY ?x");
        Assert.IsEmpty(repeated.Bindings);
        var distinctRows = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
            "SELECT (COUNT(DISTINCT *) AS ?count) WHERE { GRAPH ?g { ?s <https://example.test/p> ?o } }");
        Assert.AreEqual(new RdfLiteral("2", integer), distinctRows.Bindings.Single()["count"]);
        const string xsdDouble = "http://www.w3.org/2001/XMLSchema#double";
        var exponentPredicate = new RdfIri("https://example.test/exponent");
        await database.CommitAsync(new(Guid.NewGuid(),
        [
            new AssertRdfQuad(new(new RdfIri("urn:exponent:two"), exponentPredicate,
                new RdfLiteral("2E0", xsdDouble))),
            new AssertRdfQuad(new(new RdfIri("urn:exponent:ten"), exponentPredicate,
                new RdfLiteral("10E0", xsdDouble)))
        ]));
        var exponentOnly = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
            "SELECT ?o WHERE { ?s <https://example.test/exponent> ?o } ORDER BY ?o LIMIT 1");
        Assert.AreEqual(new RdfLiteral("2E0", xsdDouble), exponentOnly.Bindings.Single()["o"]);
    }

    [TestMethod]
    public async Task SPARQL_filter_errors_survive_negation_and_boolean_truth_tables()
    {
        await using RdfDatabaseEngine database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(new(
            new RdfIri("https://example.test/s"), new RdfIri("https://example.test/p"),
            new RdfLiteral("value")))]));

        var negated = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
            "SELECT ?s WHERE { ?s <https://example.test/p> ?o FILTER (!(?missing = 1)) }");
        Assert.IsEmpty(negated.Bindings);
        var notIn = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
            "SELECT ?s WHERE { ?s <https://example.test/p> ?o FILTER (?missing NOT IN (1, 2)) }");
        Assert.IsEmpty(notIn.Bindings);
        var negatedFunction = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
            "SELECT ?s WHERE { ?s <https://example.test/p> ?o FILTER (!isIRI(?missing)) }");
        Assert.IsEmpty(negatedFunction.Bindings);
        var negatedBound = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
            "SELECT ?s WHERE { ?s <https://example.test/p> ?o FILTER (!BOUND(?missing)) }");
        Assert.HasCount(1, negatedBound.Bindings);
    }

    [TestMethod]
    public async Task Failed_parameter_rendering_does_not_retain_a_snapshot()
    {
        await using RdfDatabaseEngine database = await OpenAsync(CreateDirectory());
        var triple = new RdfTripleTerm(new RdfIri("https://example.test/s"),
            new RdfIri("https://example.test/p"), new RdfLiteral("o"));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(async () => await database.QuerySparqlAsync(
            new EntityForge.Db.Rdf.Contracts.RdfSparqlQueryRequest(
                "SELECT ?s WHERE { @term <https://example.test/p> ?s }", bindings:
                new Dictionary<string, RdfTerm> { ["term"] = triple })));

        await database.CreateCheckpointAsync();
    }

    [TestMethod]
    public void RDF_syntax_preserves_lexical_IRI_identity()
    {
        const string text = "<http://EXAMPLE.test/s> <http://example.test/p> <http://example.test/o> .\n" +
                            "<http://example.test/s> <http://example.test/p> <http://example.test/o> .";
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        IReadOnlyList<RdfQuad> quads = RdfSyntax.ParseNTriples(input, "identity");

        Assert.HasCount(2, quads);
        CollectionAssert.AreEquivalent(new RdfTerm[]
        {
            new RdfIri("http://EXAMPLE.test/s"), new RdfIri("http://example.test/s")
        }, quads.Select(quad => quad.Subject).ToArray());
    }

    [TestMethod]
    public async Task RDF_exports_and_parameters_preserve_lexical_IRI_identity()
    {
        const string stringDatatypeUpper = "http://www.w3.org/2001/XMLSchema#String";
        RdfQuad[] quads =
        [
            new(new RdfIri("http://EXAMPLE.test/s"), new RdfIri("http://example.test/p"),
                new RdfLiteral("upper")),
            new(new RdfIri("http://example.test/s"), new RdfIri("http://example.test/p"),
                new RdfLiteral("lower")),
            new(new RdfIri("http://example.test:80/s"), new RdfIri("http://example.test/p"),
                new RdfLiteral("default-port")),
            new(new RdfIri("urn:datatype"), new RdfIri("urn:value"),
                new RdfLiteral("typed", stringDatatypeUpper))
        ];
        using var turtle = new MemoryStream();
        await RdfSyntax.WriteTurtleAsync(quads, turtle);
        turtle.Position = 0;
        CollectionAssert.AreEquivalent(quads.Select(ExactQuadIdentity).ToArray(),
            RdfSyntax.ParseNTriples(turtle, "turtle").Select(ExactQuadIdentity).ToArray());
        RdfQuad[] jsonQuads =
        [
            .. quads,
            new(new RdfIri("urn:graph-subject"), new RdfIri("urn:value"), new RdfLiteral("graph"),
                new RdfIri("http://EXAMPLE.test:80/graph"))
        ];
        using var json = new MemoryStream();
        await RdfSyntax.WriteJsonLdAsync(jsonQuads, json);
        json.Position = 0;
        CollectionAssert.AreEquivalent(jsonQuads.Select(ExactQuadIdentity).ToArray(),
            RdfSyntax.ParseJsonLd(json, "json").Select(ExactQuadIdentity).ToArray());

        await using RdfDatabaseEngine database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), quads.Select(quad =>
            (RdfMutation)new AssertRdfQuad(quad)).ToArray()));
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
            new EntityForge.Db.Rdf.Contracts.RdfSparqlQueryRequest(
                "SELECT ?o WHERE { @subject <http://example.test/p> ?o }", bindings:
                new Dictionary<string, RdfTerm> { ["subject"] = new RdfIri("http://EXAMPLE.test/s") }));
        Assert.AreEqual(new RdfLiteral("upper"), result.Bindings.Single()["o"]);
    }

    [TestMethod]
    public async Task RDF_runtime_keeps_logical_state_and_command_replay_private_until_publication()
    {
        string directory = CreateDirectory();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(Path.Combine(directory, "staging.pages"));
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        RdfPhysicalAccessPath physical = await RdfPhysicalAccessPath.OpenAsync(pages, pageSpace);
        var runtime = new RdfProfileRuntime(physical);
        Guid transactionId = Guid.NewGuid();
        var quad = new RdfQuad(new RdfIri("https://example.test/staged"),
            new RdfIri("https://example.test/p"), new RdfLiteral("value"));
        const string commandHash = "HASH";
        var transaction = new RdfTransaction(transactionId, [new AssertRdfQuad(quad)],
            SourceCommandHash: commandHash);
        byte[] payload = RdfProfileRuntime.Encode(transaction);

        await runtime.ValidateAsync(transactionId, payload);
        await runtime.ApplyAsync(new DatabaseVersion(1), transactionId, payload);

        Assert.AreEqual(DatabaseVersion.Initial, runtime.CurrentVersion);
        Assert.IsEmpty(runtime.Find(new(Subject: quad.Subject)));
        Assert.IsFalse(runtime.TryGetCommandReplay(transactionId, commandHash, out _));

        await runtime.PublishStagedStateAsync(transactionId);

        Assert.AreEqual(new DatabaseVersion(1), runtime.CurrentVersion);
        Assert.AreEqual(quad, runtime.Find(new(Subject: quad.Subject)).Single());
        Assert.IsTrue(runtime.TryGetCommandReplay(transactionId, commandHash, out RdfSparqlUpdateCommit? replay));
        Assert.AreEqual(new DatabaseVersion(1), replay.Version);
    }

    [TestMethod]
    public async Task RDF_physical_publication_failure_fails_reads_closed_and_recovers_once()
    {
        string directory = CreateDirectory();
        const string wal = "entityforge.rdf.wal";
        const string checkpoint = "entityforge.rdf.checkpoint";
        await new LocalDatabaseFormatManager(directory, wal, checkpoint,
            profile: DatabaseProfileId.Rdf).EnsureCurrentAsync();
        var pages = new FailOncePageStore(await PhysicalPageFile.OpenAsync(
            Path.Combine(directory, "entityforge.rdf.pages")));
        RdfDatabaseEngine database = await RdfDatabaseEngine.OpenAsync(
            await GroupedRedoDatabaseCommitLog.OpenAsync(Path.Combine(directory, wal)),
            new SegmentedDatabaseCommittedChangeStore(Path.Combine(directory, "entityforge.rdf.changes")),
            new FileDatabaseCheckpointStore(Path.Combine(directory, checkpoint)), pages);
        Guid transactionId = Guid.NewGuid();
        var quad = new RdfQuad(new RdfIri("urn:publication:s"), new RdfIri("urn:publication:p"),
            new RdfLiteral("value"));
        pages.Arm();

        var failure = await Assert.ThrowsExactlyAsync<EntityForge.Db.Mapping.EntityOperationCommittedException<DatabaseCommitReceipt>>(async () =>
            await database.CommitAsync(new(transactionId, [new AssertRdfQuad(quad)])));
        Assert.AreEqual(transactionId, failure.Receipt.TransactionId);
        Assert.AreEqual(new DatabaseVersion(1), failure.Receipt.Version);
        Assert.ThrowsExactly<EntityForgeDbRecoveryRequiredException>(() => database.Find(new(Subject: quad.Subject)));
        await database.DisposeAsync();

        await using (RdfDatabaseEngine recovered = await OpenAsync(directory))
        {
            Assert.AreEqual(quad, recovered.Find(new(Subject: quad.Subject)).Single());
            DatabaseVersion recoveredVersion = recovered.CurrentVersion;
            DatabaseCommitReceipt replay = await recovered.CommitWithReceiptAsync(
                new(transactionId, [new AssertRdfQuad(quad)]));
            Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Outcome);
            Assert.AreEqual(recoveredVersion, replay.Version);
            Assert.AreEqual(recoveredVersion, recovered.CurrentVersion);
            DatabaseChangePage changes = await recovered.ReadChangePageAfterAsync(null,
                new(DatabaseProfileId.Rdf, [new("rdf.predicate", "urn:publication:p")]), 10);
            Assert.HasCount(1, changes.Changes);
            await recovered.CreateCheckpointAsync();
        }

        await using RdfDatabaseEngine reopened = await OpenAsync(directory);
        Assert.AreEqual(new DatabaseVersion(1), reopened.CurrentVersion);
        Assert.AreEqual(quad, reopened.Find(new(Subject: quad.Subject)).Single());
        DatabaseChangePage reopenedChanges = await reopened.ReadChangePageAfterAsync(null,
            new(DatabaseProfileId.Rdf, [new("rdf.predicate", "urn:publication:p")]), 10);
        Assert.HasCount(1, reopenedChanges.Changes);
    }

    [TestCleanup]
    public void Cleanup() => _testDirectories.Cleanup();

    private string CreateDirectory()
    {
        _directory = _testDirectories.Create();
        return _directory;
    }

    private static string ExactQuadIdentity(RdfQuad quad) => System.Text.Json.JsonSerializer.Serialize(quad);

    private static async ValueTask<RdfDatabaseEngine> OpenAsync(string directory, RdfQueryPagingOptions? queryPaging = null,
        RdfBulkLoadOptions? bulkLoad = null)
    {
        const string wal = "entityforge.rdf.wal";
        const string checkpoint = "entityforge.rdf.checkpoint";
        await new LocalDatabaseFormatManager(directory, wal, checkpoint,
            profile: DatabaseProfileId.Rdf).EnsureCurrentAsync();
        return await RdfDatabaseEngine.OpenAsync(
            await GroupedRedoDatabaseCommitLog.OpenAsync(Path.Combine(directory, wal)),
            new SegmentedDatabaseCommittedChangeStore(Path.Combine(directory, "entityforge.rdf.changes")),
            new FileDatabaseCheckpointStore(Path.Combine(directory, checkpoint)),
            await PhysicalPageFile.OpenAsync(Path.Combine(directory, "entityforge.rdf.pages")), queryPaging: queryPaging, bulkLoad: bulkLoad);
    }

    private sealed class FailOncePageStore(IResizablePhysicalPageStore inner) : IResizablePhysicalPageStore
    {
        private int _armed;

        public int PageSize => inner.PageSize;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(PhysicalPageId pageId,
            CancellationToken cancellationToken = default) => inner.ReadAsync(pageId, cancellationToken);

        public ValueTask WriteAsync(PhysicalPageId pageId, ReadOnlyMemory<byte> page,
            CancellationToken cancellationToken = default) => Interlocked.Exchange(ref _armed, 0) == 1
                ? ValueTask.FromException(new IOException("Injected RDF physical publication failure."))
                : inner.WriteAsync(pageId, page, cancellationToken);

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
            inner.FlushAsync(cancellationToken);

        public ValueTask<long> GetPageCountAsync(CancellationToken cancellationToken = default) =>
            inner.GetPageCountAsync(cancellationToken);

        public ValueTask EnsureCapacityAsync(long pageCount, CancellationToken cancellationToken = default) =>
            inner.EnsureCapacityAsync(pageCount, cancellationToken);

        public ValueTask TruncateAsync(long pageCount, CancellationToken cancellationToken = default) =>
            inner.TruncateAsync(pageCount, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
