using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [EntityType("loading-path-audit", "item")]
    public sealed record LoadingAuditItem(string Id, string Name, int Number);

    [TestMethod]
    [DoNotParallelize]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Loading_path_audit_measures_real_transaction_work(DatabaseProfileId profile)
    {
        const int BatchSize = 64, Batches = 8, PayloadCharacters = 128;
        string? traceDirectory = Environment.GetEnvironmentVariable("ENTITYFORGE_LOADING_AUDIT_TRACE_DIRECTORY");
        string? endpoint = Environment.GetEnvironmentVariable("ENTITYFORGE_LOADING_AUDIT_ETCD_ENDPOINT");
        using var auditEtcd = string.IsNullOrWhiteSpace(endpoint) ? null : new dotnet_etcd.EtcdClient(endpoint,
            configureChannelOptions: options => options.Credentials = Grpc.Core.ChannelCredentials.Insecure);
        string prefix = "/entityforge/loading-causal-audit/" + Guid.NewGuid().ToString("N") + "/";
        try
        {
            await WithOrdinaryTransactions(profile, async (remote, client, nodes, authority) =>
            {
                await remote.CreateTransaction().Save(new LoadingAuditItem("warmup", "warmup", -1)).CommitAsync();
                var stages = new ConcurrentDictionary<string, (long Count, long Ticks)>();
                var work = new ConcurrentDictionary<string, long>();
                var activities = new ConcurrentDictionary<string, (long Count, double Ms)>();
                var native = new ConcurrentDictionary<string, (long Count, double Sum)>();
                var intervals = new ConcurrentQueue<object>();
                var transactions = new List<object>();
                var activityStarts = new ConcurrentDictionary<ActivitySpanId, long>();
                using var meters = new MeterListener();
                if (Environment.GetEnvironmentVariable("ENTITYFORGE_LOADING_AUDIT_DETAILS") == "1")
                {
                    meters.InstrumentPublished = (instrument, owner) =>
                    {
                        if (instrument.Meter.Name is "EntityForge.Db" or "EntityForge.Db.Kernel" or
                            "EntityForge.Db.Semantic.Projections" or "EntityForge.Db.Storage.Pages")
                        {
                            owner.EnableMeasurementEvents(instrument);
                        }
                    };
                    void Record(Instrument instrument, double amount, ReadOnlySpan<KeyValuePair<string, object?>> tags)
                    {
                        string key = instrument.Name + ":" + instrument.Unit;
                        foreach (var tag in tags)
                        {
                            key += "/" + tag.Key + "=" + tag.Value;
                        }

                        native.AddOrUpdate(key, (1, amount), (_, old) => (old.Count + 1, old.Sum + amount));
                    }
                    meters.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
                    meters.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
                    meters.Start();
                }

                using var listener = new ActivityListener
                {
                    ShouldListenTo = source => source.Name is DistributedReadTelemetry.SourceName or "EntityForge.Db.Kernel" or
                        "System.Net.Http" or "Microsoft.AspNetCore",
                    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                    ActivityStarted = activity =>
                    {
                        if (traceDirectory is not null)
                        {
                            activityStarts[activity.SpanId] = Stopwatch.GetTimestamp();
                        }
                    },
                    ActivityStopped = activity =>
                    {
                        if (traceDirectory is not null && activityStarts.TryRemove(activity.SpanId, out long start))
                        {
                            intervals.Enqueue(new
                            {
                                Kind = "Activity",
                                Stage = activity.OperationName,
                                Trace = activity.TraceId.ToString(),
                                Start = start,
                                End = Stopwatch.GetTimestamp(),
                                Span = activity.SpanId.ToString(),
                                Parent = activity.ParentSpanId.ToString()
                            });
                        }

                        string stage = activity.OperationName + ":" + activity.GetTagItem("entityforge.phase");
                        activities.AddOrUpdate(stage, (1, activity.Duration.TotalMilliseconds),
                            (_, old) => (old.Count + 1, old.Ms + activity.Duration.TotalMilliseconds));
                        foreach (string tag in new[] { "entityforge.request_bytes", "entityforge.response_bytes", "entityforge.entries" })
                        {
                            if (activity.GetTagItem(tag) is { } amount)
                            {
                                work.AddOrUpdate(stage + ":" + tag, Convert.ToInt64(amount), (_, old) => old + Convert.ToInt64(amount));
                            }
                        }
                    }
                };
                if (traceDirectory is not null || Environment.GetEnvironmentVariable("ENTITYFORGE_LOADING_AUDIT_DETAILS") == "1")
                {
                    ActivitySource.AddActivityListener(listener);
                }

                var previous = DistributedReadTelemetry.ObserveElapsedTicks;
                var previousWork = DistributedReadTelemetry.ObserveWork;
                var previousInterval = DistributedReadTelemetry.ObserveInterval;
                if (traceDirectory is not null)
                {
                    DistributedReadTelemetry.ObserveInterval = (stage, start, end, trace) =>
                    {
                        previousInterval?.Invoke(stage, start, end, trace);
                        intervals.Enqueue(new
                        {
                            Kind = "Measurement",
                            Stage = stage,
                            Trace = trace.ToString(),
                            Start = start,
                            End = end
                        });
                    };
                }

                long captureBytes = 0, evidenceBytes = 0, ownerCount = 0;
                var bindings = new List<PartitionApplicationBinding>();
                double commitMilliseconds = 0;
                long allocated = GC.GetTotalAllocatedBytes(true);
                DistributedReadTelemetry.ObserveElapsedTicks = (stage, ticks) =>
                {
                    previous?.Invoke(stage, ticks);
                    stages.AddOrUpdate(stage, (1, ticks), (_, prior) => (prior.Count + 1, prior.Ticks + ticks));
                };
                DistributedReadTelemetry.ObserveWork = (stage, amount) =>
                {
                    previousWork?.Invoke(stage, amount);
                    work.AddOrUpdate(stage, amount, (_, prior) => prior + amount);
                };
                try
                {
                    for (int batch = 0; batch < Batches; batch++)
                    {
                        var transaction = remote.CreateTransaction();
                        for (int i = 0; i < BatchSize; i++)
                        {
                            int key = batch * BatchSize + i;
                            transaction.Save(new LoadingAuditItem(key.ToString(), new string('x', PayloadCharacters), key));
                        }
                        captureBytes += transaction.Capture().Sum(c => (long)c.Length);
                        using var trace = new Activity("loading-audit.transaction").SetIdFormat(ActivityIdFormat.W3C).Start();
                        long started = Stopwatch.GetTimestamp();
                        var receipt = await transaction.CommitAsync();
                        long ended = Stopwatch.GetTimestamp();
                        commitMilliseconds += (ended - started) * 1000d / Stopwatch.Frequency;
                        transactions.Add(new
                        {
                            Batch = batch,
                            Operation = transaction.TransactionId,
                            Trace = trace.TraceId.ToString(),
                            Start = started,
                            End = ended
                        });
                        Assert.AreEqual(transaction.TransactionId, receipt.OperationId);
                        var binding = await authority.ReadApplicationAsync(transaction.TransactionId);
                        Assert.IsNotNull(binding);
                        bindings.Add(binding);
                        evidenceBytes += binding.PlanningEvidence.Length;
                        ownerCount += binding.Owners.Count;
                    }
                    allocated = GC.GetTotalAllocatedBytes(true) - allocated;
                }
                finally
                {
                    DistributedReadTelemetry.ObserveElapsedTicks = previous;
                    DistributedReadTelemetry.ObserveWork = previousWork;
                    DistributedReadTelemetry.ObserveInterval = previousInterval;
                }
                if (traceDirectory is not null)
                {
                    Directory.CreateDirectory(traceDirectory);
                    await File.WriteAllTextAsync(Path.Combine(traceDirectory, profile + ".json"),
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            Profile = profile.ToString(),
                            Authority = authority.GetType().Name,
                            Stopwatch.Frequency,
                            Transactions = transactions,
                            Intervals = intervals.ToArray()
                        }));
                }
                Console.WriteLine($"AUDIT profile={profile}; entities={BatchSize * Batches}; transactions={Batches}; commitMs={commitMilliseconds:F3}; processAllocated={allocated}; captureBytes={captureBytes}; evidenceBytes={evidenceBytes}; ownerCount={ownerCount}");
                foreach (var pair in stages.OrderBy(p => p.Key))
                {
                    Console.WriteLine($"STAGE {pair.Key}; calls={pair.Value.Count}; inclusiveMs={1000d * pair.Value.Ticks / Stopwatch.Frequency:F3}");
                }

                foreach (var pair in activities.OrderBy(p => p.Key))
                {
                    Console.WriteLine($"ACTIVITY {pair.Key}; calls={pair.Value.Count}; inclusiveMs={pair.Value.Ms:F3}");
                }

                foreach (var pair in work.OrderBy(p => p.Key))
                {
                    Console.WriteLine($"WORK {pair.Key}; amount={pair.Value}");
                }

                foreach (var pair in native.OrderBy(p => p.Key))
                {
                    Console.WriteLine($"NATIVE {pair.Key}; calls={pair.Value.Count}; sum={pair.Value.Sum:F3}");
                }

                if (profile == DatabaseProfileId.Semantic)
                {
                    var expansions = bindings.SelectMany(b => PartitionSemanticPlanning.ReadValidated(b, nodes[0].Routing).Expansions).ToArray();
                    var contributions = expansions.SelectMany(e => e.Contributions).ToArray();
                    var changes = contributions.SelectMany(c => c.Session.Pages.SelectMany(p => p.Changes)).ToArray();
                    Console.WriteLine($"SHAPE contributions={contributions.Length}; empty={contributions.Count(c => !c.Session.Pages.Any(p => p.Changes.Count != 0))}; changes={changes.Length}; fields={changes.Sum(c => c.Fields.Count)}; nativeBytes={expansions.Sum(e => (long)e.Envelope.Expanded.Length)}");
                    int emptyNonHome = 0, emptyHome = 0;
                    foreach (var expansion in expansions)
                    {
                        for (int index = 0; index < expansion.Contributions.Count; index++)
                        {
                            var contribution = expansion.Contributions[index];
                            var source = expansion.Envelope.Sources[expansion.Envelope.ContributionIndices![index]];
                            if (contribution.Session.Pages.Any(p => p.Changes.Count != 0))
                            {
                                continue;
                            }

                            if (contribution.Read!.OwnerCoverage is not null)
                            {
                                emptyHome++;
                            }
                            else if (!source.HadMembership && source.LocalPredecessor is null)
                            {
                                emptyNonHome++;
                            }
                        }
                    }

                    Console.WriteLine($"EMPTY homeCoverage={emptyHome}; nonHomeWithoutPriorMembershipOrProgress={emptyNonHome}");
                    Assert.AreEqual(BatchSize * Batches, contributions.Length);
                    Assert.AreEqual(0, contributions.Count(c => !c.Session.Pages.Any(p => p.Changes.Count != 0)));
                    foreach (var binding in bindings)
                    {
                        using var interpretation = new PartitionSemanticPlanning.InterpretationScope();
                        var plan = PartitionSemanticPlanning.Plan(binding, nodes[0].Routing, nodes.Select(n => n.Domain).ToArray());
                        int applicationBytes = PartitionSelectionCodec.EncodeApplication(binding).Length;
                        byte[] selectedPlan = PartitionSelectionCodec.EncodeSelectedPlan(plan, binding);
                        int planBytes = selectedPlan.Length;
                        Assert.IsTrue(applicationBytes + planBytes < GrpcEtcdPartitionTransactionAuthority.MaximumEncodedPlanBytes / 2);
                        var restoredBinding = PartitionSelectionCodec.DecodeApplication(PartitionSelectionCodec.EncodeApplication(binding));
                        var restoredPlan = PartitionSelectionCodec.DecodePlan(selectedPlan, restoredBinding);
                        Assert.AreEqual(plan.Descriptor, restoredPlan.Descriptor);
                        for (int index = 0; index < plan.Members.Count; index++)
                        {
                            CollectionAssert.AreEqual(plan.Members[index].Payload.ToArray(), restoredPlan.Members[index].Payload.ToArray());
                        }

                        Assert.ThrowsExactly<InvalidDataException>(() => PartitionSelectionCodec.DecodePlan(selectedPlan));
                        Assert.ThrowsExactly<InvalidDataException>(() => PartitionSelectionCodec.DecodePlan(selectedPlan, bindings.First(b => b.Operation != binding.Operation)));
                        var corrupt = selectedPlan.ToArray();
                        corrupt[^1] ^= 1;
                        Assert.ThrowsExactly<InvalidDataException>(() => PartitionSelectionCodec.DecodePlan(corrupt, restoredBinding));
                        corrupt = selectedPlan.ToArray();
                        corrupt[^33] ^= 1;
                        System.Security.Cryptography.SHA256.HashData(corrupt.AsSpan(0, corrupt.Length - 32)).CopyTo(corrupt, corrupt.Length - 32);
                        Assert.ThrowsExactly<InvalidDataException>(() => PartitionSelectionCodec.DecodePlan(corrupt, restoredBinding));
                        Console.WriteLine($"COMBINED applicationBytes={applicationBytes}; planBytes={planBytes}; total={applicationBytes + planBytes}; currentEtcdBodyBudget={GrpcEtcdPartitionTransactionAuthority.MaximumEncodedPlanBytes / 2}");
                    }
                }
                // Read verification is deliberately outside the measured interval.
                for (int i = 0; i < BatchSize * Batches; i++)
                {
                    LoadingAuditItem? value = profile switch
                    {
                        DatabaseProfileId.PropertyGraph => (await remote.ReadGraphAsync<LoadingAuditItem>(i.ToString())).Value,
                        DatabaseProfileId.Rdf => (await remote.ReadRdfAsync<LoadingAuditItem>(i.ToString())).Value,
                        _ => (await remote.ReadSemanticAsync<LoadingAuditItem>(i.ToString())).Value
                    };
                    Assert.IsNotNull(value);
                    Assert.AreEqual(i, value.Number);
                    Assert.AreEqual(new string('x', PayloadCharacters), value.Name);
                }
                await client.CloseReadEpochAsync();
                Assert.IsTrue(nodes.All(n => n.UnreleasedReadCutCount == 0));
            }, authorityFactory: auditEtcd is null ? null : async (generation, routing) =>
                await GrpcEtcdPartitionTransactionAuthority.OpenAsync(auditEtcd, prefix, generation, profile, routing));
        }
        finally
        {
            if (auditEtcd is not null)
            {
                await auditEtcd.DeleteAsync(new Etcdserverpb.DeleteRangeRequest
                {
                    Key = Google.Protobuf.ByteString.CopyFromUtf8(prefix),
                    RangeEnd = Google.Protobuf.ByteString.CopyFromUtf8(prefix[..^1] + "0")
                });
            }
        }
    }
}
