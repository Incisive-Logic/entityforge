using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using EntityForge.Db.AppModel.Avalonia.Results;
using EntityForge.Db.Client;
using EntityForge.Query.Projection;
using EntityForge.Query.Values;
using EntityForge.UI.Avalonia.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.AppModel.Avalonia.Tests;

[TestClass]
public sealed class PanelThemeTests
{
    [TestMethod]
    public void Selected_tabs_are_unfilled_and_active_progress_has_visible_geometry()
    {
        HeadlessTestHost.Run(() =>
        {
            Application.Current!.Styles.Add(new FluentTheme());
            Application.Current.Styles.Add(new StyleInclude(new Uri("avares://EntityForge.UI.Avalonia/"))
            {
                Source = new Uri("avares://EntityForge.UI.Avalonia/Themes/EntityForgeTheme.axaml")
            });
            EntityForge.UI.Avalonia.EntityForgeThemeManager.Apply(Application.Current, EntityForge.UI.EntityForgeTheme.Dark);
            var tabs = new PanelTabs { ItemsSource = new[] { "Results", "JSON" }, SelectedIndex = 0 };
            var ring = new ProgressRing { IsActive = true };
            var window = new Window { Width = 400, Height = 150, Content = new StackPanel { Children = { tabs, ring } } };
            window.Show();
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var selected = (ListBoxItem)tabs.ContainerFromIndex(0)!;
            var border = selected.GetVisualDescendants().OfType<Border>().SingleOrDefault(b => b.Name == "TabUnderline");
            Assert.IsNotNull(border, "The panel tab template must replace the Fluent item template.");
            var presenter = selected.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "TabContent");
            Assert.AreEqual(Colors.Transparent, ((ISolidColorBrush)presenter.Background!).Color);
            var path = ring.GetVisualDescendants().OfType<global::Avalonia.Controls.Shapes.Path>().Single();
            Assert.IsNotNull(path.Stroke, "The spinner needs a resolved theme stroke.");
            Assert.IsTrue(path.Bounds.Width > 0 && path.Bounds.Height > 0, $"Spinner bounds: {path.Bounds}");
            Assert.IsTrue(ring.Classes.Contains(":active"));
            EntityProjectionQueryResult Page(int count) => new(new EntityProjection(Guid.NewGuid(),
                Enumerable.Range(0, count).Select(i => new EntityProjectionNode(new IdentifierValue($"urn:entity:{i}"), [], [], [])).ToArray()), new(1));
            var results = new EntityProjectionResultsControl();
            results.SetResult(Page(12));
            Assert.AreEqual(0, results.GetLogicalDescendants().OfType<Border>().Count(b => b.Classes.Contains("ef-list-row")));
            window.Content = results;
            Assert.IsTrue(results.IsRendering);
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.IsFalse(results.IsRendering);
            Assert.AreEqual(12, results.GetLogicalDescendants().OfType<Border>().Count(b => b.Classes.Contains("ef-list-row")));
            var diagnostics = results.GetLogicalDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Advanced query diagnostics"));
            Assert.IsNull(diagnostics.Content, "Hidden diagnostics must not serialize the page eagerly.");
            diagnostics.IsExpanded = true;
            Assert.IsNotNull(diagnostics.Content);
            results.SetResult(Page(30));
            results.SetResult(Page(1));
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(1, results.GetLogicalDescendants().OfType<Border>().Count(b => b.Classes.Contains("ef-list-row")), "An obsolete queued page must not append cards.");
            results.SetResult(Page(8));
            window.Content = null;
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.IsFalse(results.IsRendering);
            window.Content = results;
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(8, results.GetLogicalDescendants().OfType<Border>().Count(b => b.Classes.Contains("ef-list-row")), "Reattaching resumes the pending page.");
            window.Close();
        });
    }
}
