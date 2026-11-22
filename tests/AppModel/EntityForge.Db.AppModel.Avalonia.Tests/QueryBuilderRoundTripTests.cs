using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using EntityForge.Db.AppModel.Avalonia.Queries;
using EntityForge.Db.AppModel.Queries;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.AppModel.Avalonia.Tests;

[TestClass]
public sealed class QueryBuilderRoundTripTests
{
    [TestMethod]
    public void Concept_selector_tracks_available_width_when_shrinking_and_growing()
    {
        HeadlessTestHost.Run(() =>
        {
            var concept = new SemanticQueryConcept("urn:item", new string('W', 80));
            var metadata = new SemanticQueryMetadata([concept],
                new Dictionary<string, IReadOnlyList<SemanticQueryProperty>>(),
                new Dictionary<string, IReadOnlyList<SemanticQueryRelationship>>());
            var builder = new SemanticQueryBuilderControl();
            builder.SetQuery(metadata, new SemanticQueryDocument(concept, [], []));
            var selector = builder.GetLogicalDescendants().OfType<ComboBox>().First();
            var row = (WrapPanel)selector.Parent!;
            var window = new Window { Content = builder, Width = 900, Height = 600 };
            try
            {
                window.Show();
                foreach (double width in new[] { 700d, 240d, 600d })
                {
                    row.Measure(new Size(width, double.PositiveInfinity));
                    row.Arrange(new Rect(0, 0, width, row.DesiredSize.Height));
                    Assert.AreEqual(width, selector.MaxWidth);
                    selector.Measure(new Size(width, double.PositiveInfinity));
                    Assert.IsTrue(selector.DesiredSize.Width <= width);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void Builder_preserves_nested_logic_sorting_and_aliases_and_validates_edits()
    {
        HeadlessTestHost.Run(() =>
        {
            var concept = new SemanticQueryConcept("urn:item", "Item");
            var property = new SemanticQueryProperty("urn:amount", "Amount", QueryValueEditor.Number,
                new HashSet<QueryComparisonOperator> { QueryComparisonOperator.Equals, QueryComparisonOperator.In, QueryComparisonOperator.IsMissing }, LiteralKind: "integer");
            var relationship = new SemanticQueryRelationship("urn:related", "Related", [concept.Identifier]);
            var metadata = new SemanticQueryMetadata([concept],
                new Dictionary<string, IReadOnlyList<SemanticQueryProperty>> { [concept.Identifier] = [property] },
                new Dictionary<string, IReadOnlyList<SemanticQueryRelationship>> { [concept.Identifier] = [relationship] });
            var condition = new SemanticPropertyConditionBlock("value", property, QueryComparisonOperator.In, JsonSerializer.SerializeToElement(new[] { 2, 10 }));
            var query = new SemanticQueryDocument(concept,
                [new SemanticLogicalQueryBlock("outer", QueryLogicalOperator.None,
                    [new SemanticRelationshipConditionBlock("relation", relationship, concept, QueryLogicalOperator.Any, [condition], true)])],
                [new(property, "customAmount")], OrderBy: [new(property, QuerySortDirection.Descending, QuerySortValueSelection.Maximum, QueryMissingValuePlacement.First)]);
            var builder = new SemanticQueryBuilderControl();
            builder.SetQuery(metadata, query);
            var roundTrip = builder.CurrentQuery!;
            Assert.AreEqual("customAmount", roundTrip.Projection.Single().ResultName);
            Assert.AreEqual(query.OrderBy!.Single(), roundTrip.OrderBy!.Single());
            var group = (SemanticLogicalQueryBlock)roundTrip.Conditions.Single();
            Assert.AreEqual(QueryLogicalOperator.None, group.Operator);
            var related = (SemanticRelationshipConditionBlock)group.Children.Single();
            Assert.IsTrue(related.Negated);
            Assert.AreEqual(QueryLogicalOperator.Any, related.Operator);
            CollectionAssert.AreEqual(new[] { 2, 10 }, ((SemanticPropertyConditionBlock)related.Conditions.Single()).Value.EnumerateArray().Select(v => v.GetInt32()).ToArray());
            var controls = builder.GetLogicalDescendants().OfType<Control>().ToArray();
            var list = controls.OfType<TextBox>().Single(t => t.AcceptsReturn);
            var run = controls.OfType<Button>().Single(b => Equals(b.Content, "Run Query"));
            list.Text = "not a number";
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.IsFalse(run.IsEnabled);
            list.Text = "3\n12";
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(run.IsEnabled);
            SemanticQueryDocument? executed = null;
            builder.QueryRequested += (_, args) => executed = args.Query;
            run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsNotNull(executed);
            var secondary = new SemanticQueryOrdering(property);
            builder.SetQuery(metadata, query with
            {
                OrderBy = [query.OrderBy![0], secondary]
            });
            Button[] SortButtons(string name) => builder.GetLogicalDescendants().OfType<Button>()
                .Where(b => global::Avalonia.Automation.AutomationProperties.GetName(b) == name).ToArray();
            Assert.IsFalse(SortButtons("Move Sort Field Up")[0].IsEnabled);
            Assert.IsFalse(SortButtons("Move Sort Field Down")[1].IsEnabled);
            SortButtons("Move Sort Field Down")[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            CollectionAssert.AreEqual(new[] { secondary, query.OrderBy[0] }, builder.CurrentQuery!.OrderBy!.ToArray());
            SortButtons("Move Sort Field Up")[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            CollectionAssert.AreEqual(new[] { query.OrderBy[0], secondary }, builder.CurrentQuery!.OrderBy!.ToArray());
            var advanced = builder.GetLogicalDescendants().OfType<Button>().First(b => Equals(b.Content, "Advanced"));
            var advancedContent = (Control)((Flyout)advanced.Flyout!).Content!;
            var facets = advancedContent.GetLogicalDescendants().OfType<ComboBox>().ToArray();
            facets[0].SelectedIndex = 1; // Missing last.
            facets[1].SelectedIndex = 0; // Lowest value.
            Assert.AreEqual(QueryMissingValuePlacement.Last, builder.CurrentQuery!.OrderBy![0].MissingValues);
            Assert.AreEqual(QuerySortValueSelection.Minimum, builder.CurrentQuery.OrderBy[0].ValueSelection);
            using var session = new EntityQuerySession();
            var submitted = builder.CurrentQuery;
            session.ExecuteAsync(new EmptyExecutor(), submitted!).AsTask().GetAwaiter().GetResult();
            var recreated = new SemanticQueryBuilderControl();
            recreated.DraftChanged += (_, args) => session.EditDraft(args.Query);
            recreated.SetQuery(metadata, submitted);
            var window = new Window { Content = recreated };
            window.Show();
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.IsFalse(session.HasDraftChanges, "Recreating and attaching the editor must not mark its results stale.");
            recreated.GetLogicalDescendants().OfType<TextBox>().Single(t => t.AcceptsReturn).Text = "4\n13";
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(session.HasDraftChanges, "A real nested condition edit must mark the results stale.");
            window.Close();
            var unsupported = query with
            {
                OrderBy = [new(property with { Identifier = "urn:unknown" })]
            };
            builder.SetQuery(metadata, unsupported);
            Assert.AreSame(unsupported, builder.CurrentQuery);
        });
    }

    private sealed class EmptyExecutor : IEntityQueryExecutor
    {
        public ValueTask<EntityForge.Db.Client.EntityProjectionQueryResult> ExecuteAsync(
            SemanticQueryDocument query, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EntityForge.Db.Client.EntityProjectionQueryResult(
                new EntityForge.Query.Projection.EntityProjection(Guid.NewGuid(), []), new(1)));
    }
}
