using System.Net.Http.Json;
using EntityForge.Db.Hosting;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;
using EntityForge.Semantics.Protobuf;

namespace EntityForge.Db.Server;

internal static class SemanticDemoData
{
    internal const string Prefix = "urn:entityforge:demo:";
    private static readonly DateTimeOffset Observed = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static SemanticTerm Term(string name, SemanticTermRole role) => new(new Uri(Prefix + name), role);

    internal static async Task LoadAsync(HttpClient http)
    {
        var vocabulary = new SemanticVocabularyPublicationRequest(Prefix + "vocabulary", "1",
        [
            new(Prefix + "Person", SemanticTermRole.Concept, "Person", "People described by the staff directory and project office."),
            new(Prefix + "Organisation", SemanticTermRole.Concept, "Organisation", "An organisation has its own identity, even when an identifier resembles a person's."),
            new(Prefix + "name", SemanticTermRole.Property, "Name", Field: new(SemanticValueKind.String, Maximum: null,
                ApplicableConcepts: [Prefix + "Person", Prefix + "Organisation"])),
            new(Prefix + "title", SemanticTermRole.Property, "Job title", "Each source retains its own assertion.",
                Field: new(SemanticValueKind.String, Maximum: null, ApplicableConcepts: [Prefix + "Person"])),
            new(Prefix + "joined", SemanticTermRole.Property, "Year joined", Field: new(SemanticValueKind.Integer,
                ApplicableConcepts: [Prefix + "Person"])),
            new(Prefix + "worksFor", SemanticTermRole.Relationship, "Works for", Relationship: new([Prefix + "Person"], [Prefix + "Organisation"]))
        ], [new(Prefix + "directoryId", "Directory ID", SemanticValueKind.String), new(Prefix + "staffNumber", "Staff number", SemanticValueKind.Integer)]);
        using var published = await http.PostAsJsonAsync("v1/semantic/vocabularies", vocabulary);
        await RequireSuccessAsync(published);

        SemanticEntityIdentity Identity(string concept, SemanticValue value, bool numeric = false) => new(Term(concept, SemanticTermRole.Concept),
            new(new Uri(Prefix + (numeric ? "staffNumber" : "directoryId")), numeric ? "Staff number" : "Directory ID", numeric ? SemanticValueKind.Integer : SemanticValueKind.String), value);
        var ada = Identity("Person", new StringSemanticValue("ada"));
        var sam = Identity("Person", new StringSemanticValue("sam"));
        var organisation = Identity("Organisation", new StringSemanticValue("ada"));
        var textSeven = Identity("Person", new StringSemanticValue("7"));
        var numberSeven = Identity("Person", new IntegerSemanticValue(7), true);
        SemanticFieldChange Field(string name, SemanticValue value) => new(Term(name, SemanticTermRole.Property), SemanticFieldChangeKind.Set, value);
        SemanticEntityChange Change(string source, SemanticEntityIdentity identity, SemanticFieldChange[] fields, bool worksFor = false) =>
            new(Guid.NewGuid(), SemanticEntityChangeKind.ReplaceSourceState, identity,
                new(new Uri(Prefix + source), identity.Value switch
                {
                    StringSemanticValue text => text.Value,
                    IntegerSemanticValue number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => throw new InvalidOperationException("Unexpected sample identity kind.")
                }, Observed,
                    new(new Uri(Prefix + "vocabulary"), "1", SemanticModelKind.Vocabulary)), fields,
                worksFor ? [new(Term("worksFor", SemanticTermRole.Relationship), SemanticRelationshipChangeKind.Add, organisation)] : []);
        await ApplyAsync("directory",
        [
            Change("directory", ada, [Field("name", new StringSemanticValue("Ada")), Field("title", new StringSemanticValue("Research engineer")), Field("joined", new IntegerSemanticValue(2021))], true),
            Change("directory", sam, [Field("name", new StringSemanticValue("Sam")), Field("title", new StringSemanticValue("Data engineer"))], true),
            Change("directory", organisation, [Field("name", new StringSemanticValue("Northstar Lab"))]),
            Change("directory", textSeven, [Field("name", new StringSemanticValue("Morgan (directory ID)"))]),
            Change("directory", numberSeven, [Field("name", new StringSemanticValue("Taylor (staff number)"))])
        ]);
        await ApplyAsync("project-office", [Change("project-office", ada, [Field("title", new StringSemanticValue("Project lead"))])]);

        async Task ApplyAsync(string source, SemanticEntityChange[] changes)
        {
            var id = Guid.NewGuid();
            var codec = new SemanticChangeSessionTransportCodec();
            var start = new SemanticChangeSessionStart(id, SemanticChangeSessionKind.Incremental,
                new(new Uri(Prefix + source), new Uri(Prefix + source + ":partition"), "1", Guid.NewGuid(), "1"), Guid.NewGuid(), Observed);
            SemanticChangeSessionPage[] pages = [new(id, 0, changes)];
            var close = new SemanticChangeSessionClose(id, 1, changes.Length, codec.ComputeContentHash(pages), Observed);
            using var response = await http.PostAsJsonAsync("v1/semantic/sessions", new
            {
                start = Convert.ToBase64String(codec.Serialize(start)),
                pages = pages.Select(page => Convert.ToBase64String(codec.Serialize(page))).ToArray(),
                close = Convert.ToBase64String(codec.Serialize(close))
            });
            await RequireSuccessAsync(response);
        }
    }

    private static async Task RequireSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Preparing sample data failed: " + await response.Content.ReadAsStringAsync());
        }
    }
}
