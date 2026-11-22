using System.Globalization;
using System.Text.Json;
using EntityForge.Db.Semantic;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;
using HotChocolate;
using HotChocolate.Language;
using HotChocolate.Resolvers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting;

/// <summary>Maps one GraphQL mutation to one existing atomic incremental Semantic session.</summary>
internal static class SemanticGraphQlMutations
{
    internal const int MaximumChanges = 128;
    internal const int MaximumMembers = 4096;
    internal const int MaximumInputCharacters = 1024 * 1024;

    internal static async ValueTask<object?> ApplyAsync(IResolverContext context)
    {
        GraphQlWriteAuthorization.Require(context);
        if (context.Operation.RootSelectionSet.Selections.Count(s => !s.Field.Name.StartsWith("__", StringComparison.Ordinal)) != 1)
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage("Use one applySemanticChanges root with an explicit changes batch per operation.")
                .SetCode("GRAPHQL_CAPABILITY_UNAVAILABLE").Build());
        }

        context.RequestAborted.ThrowIfCancellationRequested();
        var literal = context.ArgumentLiteral<IValueNode>("input");
        if (literal.ToString().Length > MaximumInputCharacters)
        {
            throw Invalid("input", "The batch exceeds the 1 MiB input text limit.");
        }

        SemanticSessionEnvelope session = Compile(JsonSerializer.SerializeToElement(GraphQlInput.Literal(literal)));
        return await context.Service<IGraphQlApplicationServices>().Semantic.Operations().ApplyAsync(session, context.RequestAborted)
            .ConfigureAwait(false);
    }

    internal static SemanticSessionEnvelope Compile(JsonElement input)
    {
        return At("input", () =>
        {
            Guid id = Guid.Parse(Text(input, "sessionId"));
            var sourceInput = input.GetProperty("source");
            var source = new SemanticChangeSessionSource(Uri(sourceInput, "sourceSystem"), Uri(sourceInput, "sourcePartition"),
                Text(sourceInput, "sourceVersion"), Guid.Parse(Text(sourceInput, "generationId")), Text(sourceInput, "sourcePosition"));
            var changes = Items(input, "changes").ToArray();
            if (changes.Length is < 1 or > MaximumChanges)
            {
                throw Invalid("input.changes", $"A batch requires 1..{MaximumChanges} changes.");
            }

            if (changes.Sum(c => Items(c, "fields").Count() + Items(c, "relationships").Count()) > MaximumMembers)
            {
                throw Invalid("input.changes", $"A batch permits at most {MaximumMembers} field/relationship changes.");
            }

            var mapped = changes.Select((c, i) => At($"input.changes[{i}]", () => Change(c, source.SourceSystem, $"input.changes[{i}]"))).ToArray();
            SemanticSourceProgressPrecondition? precondition = null;
            if (Optional(input, "progressPrecondition") is { } condition)
            {
                var predecessor = Optional(condition, "predecessor");
                precondition = new(Enum.Parse<SemanticSourceProgressExpectation>(Text(condition, "expectation"), true),
                    predecessor is { } p ? new(Guid.Parse(Text(p, "generationId")), Text(p, "sourceVersion"), Text(p, "sourcePosition")) : null);
            }
            var start = new SemanticChangeSessionStart(id, SemanticChangeSessionKind.Incremental, source,
                Guid.Parse(Text(input, "producerActivityId")), Date(input, "startedAt")!.Value, String(input, "correlationId"), precondition);
            SemanticChangeSessionPage[] pages = [new(id, 0, mapped)];
            var close = new SemanticChangeSessionClose(id, 1, mapped.Length,
                new SemanticChangeSessionTransportCodec().ComputeContentHash(pages), Date(input, "completedAt")!.Value);
            return new SemanticSessionEnvelope(start, pages, close);
        });
    }

    private static SemanticEntityChange Change(JsonElement input, Uri source, string path) => new(
        Guid.Parse(Text(input, "changeId")), Enum.Parse<SemanticEntityChangeKind>(Text(input, "kind"), true),
        Identity(input.GetProperty("subject")), Provenance(input.GetProperty("provenance"), source),
        Items(input, "fields").Select((f, i) => At($"{path}.fields[{i}]", () => new SemanticFieldChange(
            new(Uri(f, "property"), SemanticTermRole.Property), Enum.Parse<SemanticFieldChangeKind>(Text(f, "kind"), true),
            Optional(f, "value") is { } v ? Value(v) : null))),
        Items(input, "relationships").Select((r, i) => At($"{path}.relationships[{i}]", () => new SemanticRelationshipChange(
            new(Uri(r, "relationship"), SemanticTermRole.Relationship), Enum.Parse<SemanticRelationshipChangeKind>(Text(r, "kind"), true),
            Optional(r, "target") is { } t ? Identity(t) : null))));

    private static SemanticEntityIdentity Identity(JsonElement input)
    {
        var value = Value(input.GetProperty("value"));
        return new(new(Uri(input, "concept"), SemanticTermRole.Concept),
            new(Uri(input, "scheme"), String(input, "schemeName") ?? Text(input, "scheme"), value.Kind), value);
    }

    private static SemanticProvenance Provenance(JsonElement p, Uri source) => new(source, Text(p, "sourceRecord"),
        Date(p, "observedAt")!.Value, Model(p.GetProperty("targetVocabulary"), SemanticModelKind.Vocabulary),
        Items(p, "mappings").Select(m => Model(m, SemanticModelKind.MappingSet)), Date(p, "validFrom"), Date(p, "validTo"),
        String(p, "correlationId"),
        Optional(p, "sourceEntity") is { } e ? new(Uri(e, "identifier"), String(e, "description")) : null,
        Optional(p, "transformActivity") is { } a ? new(Uri(a, "identifier"), Date(a, "startedAt"), Date(a, "endedAt")) : null,
        Optional(p, "responsibleAgent") is { } agent ? new(Uri(agent, "identifier"), String(agent, "name")) : null,
        Artifact(p, "transformTemplate"), Date(p, "generatedAt"),
        Optional(p, "sourceVocabulary") is { } v ? Model(v, SemanticModelKind.Vocabulary) : null, Artifact(p, "targetBinding"));

    private static SemanticModelReference Model(JsonElement m, SemanticModelKind kind) => new(Uri(m, "identifier"), Text(m, "version"), kind);

    private static SemanticArtifactReference? Artifact(JsonElement p, string name) => Optional(p, name) is { } a
        ? new(Uri(a, "identifier"), Text(a, "version"), String(a, "contentHash")) : null;

    private static SemanticValue Value(JsonElement input)
    {
        string kind = Text(input, "kind");
        string? lexical = String(input, "lexical");
        var json = Optional(input, "json");
        string? language = String(input, "language"), datatype = String(input, "datatype");
        if ((kind == "json" ? json is null || lexical is not null : json is not null) ||
            (kind == "nullValue" ? lexical is not null : kind != "json" && lexical is null) ||
            (kind != "languageString" && language is not null) || (kind != "typedLiteral" && datatype is not null))
        {
            throw new ArgumentException("Value members do not match the selected kind.");
        }

        return kind switch
        {
            "nullValue" => new NullSemanticValue(),
            "boolean" => new BooleanSemanticValue(bool.Parse(lexical!)),
            "integer" => new IntegerSemanticValue(long.Parse(lexical!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)),
            "floatingPoint" => Floating(lexical!),
            "decimal" => new DecimalSemanticValue(decimal.Parse(lexical!, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)),
            "string" => new StringSemanticValue(lexical!),
            "languageString" => new LanguageStringSemanticValue(lexical!, language!),
            "date" => new DateSemanticValue(DateOnly.ParseExact(lexical!, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
            "time" => new TimeSemanticValue(TimeOnly.Parse(lexical!, CultureInfo.InvariantCulture)),
            "dateTime" => new DateTimeSemanticValue(DateTimeOffset.Parse(lexical!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
            "duration" => new DurationSemanticValue(System.Xml.XmlConvert.ToTimeSpan(lexical!)),
            "binary" => new BinarySemanticValue(Convert.FromBase64String(lexical!)),
            "absoluteUri" => new AbsoluteUriSemanticValue(new Uri(lexical!, UriKind.Absolute)),
            "guid" => new GuidSemanticValue(Guid.Parse(lexical!)),
            "json" => new JsonSemanticValue(json!.Value.GetRawText()),
            "typedLiteral" => new TypedLiteralSemanticValue(lexical!, new Uri(datatype!, UriKind.Absolute)),
            _ => throw new ArgumentException("Unsupported value kind.")
        };
    }

    private static SemanticValue Floating(string lexical)
    {
        double number = double.Parse(lexical, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(number))
        {
            throw new ArgumentException("A floating point value must be finite.");
        }

        return new FloatingPointSemanticValue(number);
    }

    private static JsonElement? Optional(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    private static IEnumerable<JsonElement> Items(JsonElement p, string name) => Optional(p, name) is { } a ? a.EnumerateArray() : [];

    private static string Text(JsonElement p, string name) => String(p, name) ?? throw new ArgumentException($"'{name}' is required.");

    private static string? String(JsonElement p, string name) => Optional(p, name)?.GetString();

    private static Uri Uri(JsonElement p, string name) => new(Text(p, name), UriKind.Absolute);

    private static DateTimeOffset? Date(JsonElement p, string name) => String(p, name) is { } value
        ? DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null;

    private static T At<T>(string path, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException or JsonException)
        {
            throw Invalid(path, e.Message);
        }
    }

    private static GraphQLException Invalid(string path, string message) => new(ErrorBuilder.New().SetMessage(message)
        .SetCode("GRAPHQL_VALIDATION_FAILED").SetExtension("inputPath", path).Build());

    internal static void Bind(HotChocolate.Execution.Configuration.IRequestExecutorBuilder b)
    {
        b.AddResolver("SemanticCommitReceipt", "sessionId", c => c.Parent<SessionCommitResult>().SessionId.ToString("D"))
            .AddResolver("SemanticCommitReceipt", "databaseIncarnation", c => c.Parent<SessionCommitResult>().DatabaseIncarnation.ToString("D"))
            .AddResolver("SemanticCommitReceipt", "transactionId", c => c.Parent<SessionCommitResult>().TransactionId.ToString("D"))
            .AddResolver("SemanticCommitReceipt", "version", c => c.Parent<SessionCommitResult>().Version.Value)
            .AddResolver("SemanticCommitReceipt", "outcome", c => c.Parent<SessionCommitResult>().Receipt.Outcome.ToString())
            .AddResolver("SemanticCommitReceipt", "changeCursor", c => c.Parent<SessionCommitResult>().ChangeCursor?.Value)
            .AddResolver("SemanticCommitReceipt", "effectCount", c => c.Parent<SessionCommitResult>().EffectCount)
            .AddResolver("SemanticCommitReceipt", "sourceProgress", c => c.Parent<SessionCommitResult>().SourceProgress)
            .AddResolver("SemanticSourceProgress", "sourceSystem", c => c.Parent<SemanticSourceCommitProgress>().SourceSystem.AbsoluteUri)
            .AddResolver("SemanticSourceProgress", "sourcePartition", c => c.Parent<SemanticSourceCommitProgress>().SourcePartition.AbsoluteUri)
            .AddResolver("SemanticSourceProgress", "generationId", c => c.Parent<SemanticSourceCommitProgress>().Position.GenerationId.ToString("D"))
            .AddResolver("SemanticSourceProgress", "sourceVersion", c => c.Parent<SemanticSourceCommitProgress>().Position.SourceVersion)
            .AddResolver("SemanticSourceProgress", "sourcePosition", c => c.Parent<SemanticSourceCommitProgress>().Position.SourcePosition)
            .AddResolver("SemanticSourceProgress", "databaseVersion", c => c.Parent<SemanticSourceCommitProgress>().DatabaseVersion.Value);
    }
}
