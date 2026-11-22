using EntityForge.Db.Semantic;
using EntityForge.Semantics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SemanticChangeFeedResponseTests
{
    [TestMethod]
    public void Conformance_events_preserve_issue_details_without_breaking_the_change_page()
    {
        Guid session = Guid.NewGuid();
        var issue = new SemanticValidationIssue("fields.age", "Expected an integer",
            SemanticValidationCodes.ValueTypeMismatch, SemanticValidationSeverity.Warning,
            rejectedValue: new StringSemanticValue("unknown"), constraint: new Uri("https://example.test/age"));
        var page = new SemanticCommittedChangePage(
            [new(Guid.NewGuid(), new(1), Guid.NewGuid(), new("cursor"),
                [new SemanticConformanceIssueRecorded(session, issue)])], new("cursor"), false);
        SemanticChangeEffectResponse effect = SemanticChangeFeedResponseMapper.Map(page).Changes.Single().Effects.Single();
        Assert.AreEqual("conformanceIssueRecorded", effect.Kind);
        var details = effect.Value!.Value;
        Assert.AreEqual(session, details.GetProperty("sessionId").GetGuid());
        Assert.AreEqual(issue.Code, details.GetProperty("code").GetString());
        Assert.AreEqual("Warning", details.GetProperty("severity").GetString());
        Assert.AreEqual("unknown", details.GetProperty("rejectedValue").GetString());
        Assert.AreEqual(issue.Constraint!.AbsoluteUri, details.GetProperty("constraint").GetString());
    }
}
