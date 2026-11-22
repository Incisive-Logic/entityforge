using EntityForge.Db.Prototypes.Pages;

namespace EntityForge.Db.Prototypes.Pages.Tests;

[TestClass]
public sealed class StatisticsPlanningExperimentTests
{
    [TestMethod]
    public void Bounded_statistics_capture_skew_and_drive_visible_explain_actuals()
    {
        StatisticsPlanningExperimentResult result = new StatisticsPlanningExperiment().Run();

        Assert.AreEqual(10_000, result.Statistics.RecordCount);
        Assert.AreEqual(500, result.Statistics.NullCount);
        Assert.IsTrue(result.Statistics.MostCommonValues.Count <= 4);
        Assert.IsTrue(result.Statistics.Histogram.Count <= 10);
        Assert.AreEqual(3_000, result.Statistics.MostCommonValues.Single(item => item.Value == 0).Count);
        Assert.AreEqual(6, result.ExplainAnalyze.Count);
        foreach (ExplainAnalyzeResult explain in result.ExplainAnalyze)
        {
            Assert.AreEqual(11, explain.CatalogVersion);
            Assert.AreEqual(7, explain.StatisticsVersion);
            Assert.IsTrue(explain.EstimatedCandidates >= 0);
            Assert.IsTrue(explain.ActualCandidates >= 0);
            Assert.AreEqual(explain.ActualCandidates, explain.ActualResults);
            Assert.IsTrue(explain.PageReads >= explain.CacheHits);
        }
        ExplainAnalyzeResult common = result.ExplainAnalyze.First(item => item.Operation == "Equal(0)");
        Assert.AreEqual(common.ActualCandidates, common.EstimatedCandidates);
        Assert.AreEqual("most-common-value", common.EstimateSource);
        ExplainAnalyzeResult uncommon = result.ExplainAnalyze.First(item => item.Operation == "Equal(42)");
        Assert.IsTrue(Math.Abs(uncommon.EstimatedCandidates - uncommon.ActualCandidates) <= 1);
        ExplainAnalyzeResult range = result.ExplainAnalyze.First(item => item.Operation == "Range(10,29)");
        Assert.IsTrue(Math.Abs(range.EstimatedCandidates - range.ActualCandidates) <= 10);
    }

    [TestMethod]
    public void Advisor_keeps_all_three_placements_as_distinct_choices()
    {
        StatisticsPlanningExperimentResult result = new StatisticsPlanningExperiment().Run();

        Assert.AreEqual("ColdJson", result.Recommendations.Single(item => item.Property == "notes").RecommendedBinding);
        Assert.AreEqual("IndexedJson", result.Recommendations.Single(item => item.Property == "country").RecommendedBinding);
        Assert.AreEqual("HotScalar", result.Recommendations.Single(item => item.Property == "score").RecommendedBinding);
        Assert.IsTrue(result.Recommendations.All(item => item.AlternativeCosts.Count == 3));
    }
}
