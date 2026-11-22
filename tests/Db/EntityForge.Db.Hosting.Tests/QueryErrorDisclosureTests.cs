using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class QueryErrorDisclosureTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Query_database_failures_hide_storage_details_and_do_not_suggest_safe_retry(bool format)
    {
        const string sensitive = "storage /private/customer-data.db token=secret-value";
        Exception failure = format ? new EntityForgeDbFormatException(sensitive) :
            new EntityForgeDbStorageException(sensitive, new IOException("inner private path"));
        var result = (ProblemHttpResult)await EntityForgeDbHttpErrors.MapAsync(() => Task.FromException<IResult>(failure));
        Assert.AreEqual(500, result.StatusCode);
        Assert.IsNull(result.ProblemDetails.Detail);
        Assert.AreEqual("database_failure", result.ProblemDetails.Extensions["code"]);
        Assert.AreEqual(false, result.ProblemDetails.Extensions["retryable"]);
        string json = System.Text.Json.JsonSerializer.Serialize(result.ProblemDetails);
        Assert.IsFalse(json.Contains("customer-data", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("secret-value", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("inner private path", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Query_client_errors_retain_actionable_message_and_language_condition()
    {
        var failure = new ArgumentException("Invalid numeric cast.");
        failure.Data["EntityForge.GqlStatus"] = "22003";
        var result = (ProblemHttpResult)await EntityForgeDbHttpErrors.MapAsync(() => Task.FromException<IResult>(failure));
        Assert.AreEqual(400, result.StatusCode);
        Assert.AreEqual("Invalid numeric cast.", result.ProblemDetails.Detail);
        Assert.AreEqual("22003", result.ProblemDetails.Extensions["gqlStatus"]);
    }
}
