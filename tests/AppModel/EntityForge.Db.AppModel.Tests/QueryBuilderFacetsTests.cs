using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Client;
using EntityForge.Db.Transport.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.AppModel.Tests;

[TestClass]
public sealed class QueryBuilderFacetsTests
{
    private static readonly SemanticQueryConcept Entity = new("urn:entity", "Entity");
    private static readonly SemanticQueryProperty Number = new("urn:amount", "Amount", QueryValueEditor.Number,
        Enum.GetValues<QueryComparisonOperator>().ToHashSet(), LiteralKind: "integer");

    private static readonly SemanticQueryRelationship Related = new("urn:related", "Related", ["urn:entity"]);
    private static readonly SemanticQueryMetadata Metadata = new([Entity],
        new Dictionary<string, IReadOnlyList<SemanticQueryProperty>> { [Entity.Identifier] = [Number] },
        new Dictionary<string, IReadOnlyList<SemanticQueryRelationship>> { [Entity.Identifier] = [Related] });

    [TestMethod]
    public async Task Sorting_groups_and_negative_relationships_preserve_their_scope_on_the_wire()
    {
        var handler = new Capture();
        using var http = new HttpClient(handler) { BaseAddress = new("https://example.test/") };
        var list = new SemanticPropertyConditionBlock("list", Number, QueryComparisonOperator.In, JsonSerializer.SerializeToElement(new[] { 2, 10 }));
        var missing = new SemanticPropertyConditionBlock("missing", Number, QueryComparisonOperator.IsMissing, JsonSerializer.SerializeToElement((string?)null));
        var group = new SemanticLogicalQueryBlock("group", QueryLogicalOperator.Any, [list, missing]);
        var absent = new SemanticRelationshipConditionBlock("absent", Related, Entity, QueryLogicalOperator.Any, [list, missing], Negated: true);
        var query = new SemanticQueryDocument(Entity, [group, absent], [], OrderBy: [new(Number, QuerySortDirection.Descending,
            QuerySortValueSelection.Maximum, QueryMissingValuePlacement.First)]);
        Assert.IsTrue(SemanticQueryBuilderSupport.CanEdit(Metadata, query));
        await new SemanticEntityQueryExecutor(new HttpPortableEntityQueryClient(http)).ExecutePageAsync(query, "cursor", 42);
        var request = handler.Request!;
        Assert.AreEqual("cursor", request.After);
        Assert.AreEqual(42L, request.SnapshotVersion);
        Assert.AreEqual("Descending", request.OrderBy!.Single().Direction);
        Assert.AreEqual("Maximum", request.OrderBy!.Single().ValueSelection);
        Assert.AreEqual("First", request.OrderBy!.Single().MissingValues);
        Assert.AreEqual("and", request.Filter!.Kind);
        Assert.AreEqual("or", request.Filter.Operands![0].Kind);
        var negative = request.Filter.Operands[1];
        Assert.AreEqual("not", negative.Kind);
        Assert.AreEqual("relationshipExists", negative.Operand!.Kind);
        Assert.AreEqual("or", negative.Operand.TargetFilter!.Kind);
        Assert.AreEqual("integer", negative.Operand.TargetFilter.Operands![0].Operands![0].Value!.Kind);
        Assert.AreEqual("propertyExists", negative.Operand.TargetFilter.Operands[1].Operand!.Kind);
    }

    [TestMethod]
    [DataRow(QueryValueEditor.Date, "date", "2024-02-29")]
    [DataRow(QueryValueEditor.DateTime, "dateTime", "2024-02-29T12:30:00+01:00")]
    [DataRow(QueryValueEditor.Identifier, "guid", "b2b620dc-f84c-4a02-b156-9ac669d6dbf0")]
    public async Task Typed_values_keep_the_catalogue_literal_kind(QueryValueEditor editor, string kind, string text)
    {
        var property = Number with
        {
            Editor = editor,
            LiteralKind = kind
        };
        var handler = new Capture();
        using var http = new HttpClient(handler) { BaseAddress = new("https://example.test/") };
        await new SemanticEntityQueryExecutor(new HttpPortableEntityQueryClient(http)).ExecuteAsync(new(Entity,
            [new SemanticPropertyConditionBlock("value", property, QueryComparisonOperator.Equals, JsonSerializer.SerializeToElement(text))], []));
        Assert.AreEqual(kind, handler.Request!.Filter!.Value!.Kind);
    }

    [TestMethod]
    public void Invalid_values_and_unknown_orderings_are_not_silently_rewritten()
    {
        Assert.IsFalse(SemanticQueryBuilderSupport.SupportsValue(Number, QueryComparisonOperator.Equals, JsonSerializer.SerializeToElement(1.5)));
        Assert.IsFalse(SemanticQueryBuilderSupport.SupportsValue(Number, QueryComparisonOperator.In, JsonSerializer.SerializeToElement(new[] { "not a number" })));
        Assert.IsFalse(SemanticQueryBuilderSupport.SupportsValue(Number, QueryComparisonOperator.In, JsonSerializer.SerializeToElement(Array.Empty<int>())));
        var date = Number with
        {
            Editor = QueryValueEditor.Date,
            LiteralKind = "date"
        };
        Assert.IsFalse(SemanticQueryBuilderSupport.SupportsValue(date, QueryComparisonOperator.Equals, JsonSerializer.SerializeToElement("2023-02-29")));
        Assert.IsFalse(SemanticQueryBuilderSupport.CanEdit(Metadata, new(Entity, [], [], OrderBy: [new(Number with { Identifier = "urn:unknown" })])));
    }

    private sealed class Capture : HttpMessageHandler
    {
        public PortableEntityQueryRequest? Request;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            Request = await message.Content!.ReadFromJsonAsync<PortableEntityQueryRequest>(cancellationToken: token);
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new EntityProjectionTransportResponse(Guid.NewGuid(), 42, [], []))
            };
        }
    }
}
