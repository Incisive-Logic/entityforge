using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Studio.Workspace;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Studio.Core.Tests;

[TestClass]
public sealed class QueryExecutionFeedbackTests
{
    [TestMethod]
    public void Duration_is_per_run_survives_paging_and_clears_when_unbound()
    {
        var clock = new Clock();
        var feedback = new QueryExecutionFeedback(clock);
        feedback.Update(EntityQueryExecutionState.Running, false);
        clock.Ticks = 125;
        feedback.Update(EntityQueryExecutionState.Succeeded, false);
        Assert.AreEqual(TimeSpan.FromMilliseconds(125), feedback.Duration);
        clock.Ticks = 900;
        feedback.Update(EntityQueryExecutionState.Running, true);
        feedback.Update(EntityQueryExecutionState.Succeeded, false);
        Assert.AreEqual(TimeSpan.FromMilliseconds(125), feedback.Duration);
        feedback.Update(EntityQueryExecutionState.Running, false);
        clock.Ticks = 950;
        feedback.Update(EntityQueryExecutionState.Cancelled, false);
        Assert.AreEqual(TimeSpan.FromMilliseconds(50), feedback.Duration);
        feedback.Update(EntityQueryExecutionState.Idle, false);
        Assert.IsNull(feedback.Duration);
    }

    private sealed class Clock : TimeProvider
    {
        public long Ticks
        {
            get; set;
        }

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Ticks;
    }
}
