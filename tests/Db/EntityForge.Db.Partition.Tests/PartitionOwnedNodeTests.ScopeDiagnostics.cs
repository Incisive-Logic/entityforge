using EntityForge.Db.Rdf;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Kernel;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Prepared_scope_diagnostic_matches_the_profile_resource_identity(DatabaseProfileId profile)
    {
        Guid operation = EntityOperationIds.New(), subject = Guid.NewGuid();
        Activity? observed = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DatabaseKernelTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = trace =>
            {
                if (trace.OperationName == "db.partition.prepared_footprint" &&
                    Equals(trace.GetTagItem("entityforge.operation"), operation.ToString("N")))
                {
                    observed = trace;
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        var opened = await OpenReservationStore(profile, Path.Combine(_root, "scope-diagnostic"));
        await using var database = opened.Database;
        Guid seed = EntityOperationIds.New();
        await opened.Kernel.CommitAsync(seed, ReservationPayload(profile, seed, [subject], null, create: true));
        await opened.Kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, opened.Kernel.CurrentVersion,
            ReservationPayload(profile, operation, [subject], opened.Kernel.CurrentVersion));
        Assert.IsNotNull(observed);
        Assert.AreEqual(true, observed.GetTagItem("entityforge.scope_complete"));
        Assert.AreEqual(opened.Kernel.DatabaseIncarnation.ToString("N"), observed.GetTagItem("entityforge.incarnation"));
        string resource = profile == DatabaseProfileId.PropertyGraph ? subject.ToString("N") :
            RdfPhysicalCodec.ResourceIdentity(new RdfIri("urn:reservation:" + subject.ToString("N")));
        string changedPredicate = profile == DatabaseProfileId.PropertyGraph ? "query:property:name" : "query:predicate:urn:name";
        string[] expected = new[] { resource, changedPredicate }
            .Select(value => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(expected, JsonSerializer.Deserialize<string[]>((string)observed.GetTagItem("entityforge.scope_resources")!)!);
        await opened.Kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, false);
    }
}
