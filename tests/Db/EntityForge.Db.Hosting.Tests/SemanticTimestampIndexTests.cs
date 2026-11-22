using EntityForge.Db.Query.Adapters;
using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;
using EntityForge.Db.Query;
using EntityForge.Db.Transport.Contracts;
using System.Text.Json;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SemanticTimestampIndexTests
{
    public TestContext TestContext { get; set; } = null!;

    private static readonly SemanticTerm Concept = new(new Uri("https://timestamp-index.test/Item"), SemanticTermRole.Concept);
    private static readonly SemanticTerm Number = new(new Uri("https://timestamp-index.test/number"), SemanticTermRole.Property);
    private static readonly SemanticTerm Peer = new(new Uri("https://timestamp-index.test/peer"), SemanticTermRole.Relationship);
    private static readonly SemanticIdentityScheme Scheme = new(new Uri("https://timestamp-index.test/id"), "ID", SemanticValueKind.String);
    private static readonly SemanticModelReference Model = new(new Uri("https://timestamp-index.test/model"), "1", SemanticModelKind.Vocabulary);

    private static SemanticEntityIdentity Identity(int key) => new(Concept, Scheme, new StringSemanticValue(key.ToString()));

    private static SemanticFieldEvidence Field(int key) => new(Guid.NewGuid(), Identity(key), Number, new IntegerSemanticValue(key), new(new Uri("https://timestamp-index.test/source"), key.ToString(), DateTimeOffset.UnixEpoch, Model), new(1), null);

    [TestMethod]
    public async Task Source_only_timestamp_changes_preserve_scalar_evidence()
    {
        const int population = 100_000;
        var fields = Enumerable.Range(0, population).Select(Field).ToArray();
        var before = SemanticDatabaseState.Empty with
        {
            Version = new(1),
            Fields = fields,
            Catalog = new([Model], [Concept, Number], [Scheme])
        };
        var index = SemanticTimestampIndex.For(before);
        var source = fields[42].Provenance.SourceSystem;
        var partition = new Uri("urn:metadata:partition");
        var owner = new SourceOwner(Identity(42), source, "42");
        var metadata = before with
        {
            Version = new(2),
            SourceMemberships = before.SourceMemberships.SetItem(owner, partition, fields[42].Provenance),
            SourceProgress = new Dictionary<SourcePartitionKey, SourcePartitionProgress>
            {
                [new(source, partition)] = new(Guid.NewGuid(), "v2", "p2", new(2))
            }
        };
        var after = new SemanticTimestampEffects(before, metadata, new([])).Apply(before);
        Assert.AreEqual(1, after.SourceProgress.Count);
        Assert.IsTrue(after.SourceMemberships.Entries.Any(pair => pair.Key == owner));
        var revisedMetadata = after with
        {
            Version = new(3),
            SourceMemberships = after.SourceMemberships.SetItem(owner, new Uri("urn:metadata:partition2"), fields[42].Provenance),
            SourceProgress = new Dictionary<SourcePartitionKey, SourcePartitionProgress>
            {
                [new(source, partition)] = new(Guid.NewGuid(), "v3", "p3", new(3))
            }
        };
        var revisedSource = new SemanticTimestampEffects(after, revisedMetadata, new([])).Apply(after);
        var removedMetadata = revisedSource with
        {
            Version = new(4),
            SourceMemberships = before.SourceMemberships,
            SourceProgress = before.SourceProgress
        };
        var removedSource = new SemanticTimestampEffects(revisedSource, removedMetadata, new([])).Apply(revisedSource);
        Assert.AreEqual(0, removedSource.SourceProgress.Count);
        Assert.IsFalse(removedSource.SourceMemberships.Entries.Any(pair => pair.Key == owner));
        string? document = null;
        foreach (var state in new[]
        {
            before,
            after,
            revisedSource,
            removedSource
        }

        )
        {
            var paths = DatabaseReadView.Create(state, SemanticTimestampIndex.For(state)).EntityAccessPaths;
            var request = new PortableEntityQueryRequest(Concept.Identifier.AbsoluteUri, new(Concept.Identifier.AbsoluteUri, Fields: [new(Number.Identifier.AbsoluteUri, "number")]), Filter: new("propertyComparison", Number.Identifier.AbsoluteUri, "equal", new(JsonSerializer.SerializeToElement(42), "integer")), ConceptMatchMode: "direct");
            var compiled = new PortableEntityQueryRequestCompiler().Compile(request);
            var result = await new PortableEntityQueryExecutor().ExecuteAsync(state, paths, compiled.Query, compiled.ProjectionOptions, state.Version);
            var content = JsonSerializer.Serialize(new
            {
                result.Document.Roots,
                result.Document.Evidence,
                result.Document.SemanticModels
            });
            if (document is not null)
            {
                Assert.AreEqual(document, content);
            }

            document = content;
            CollectionAssert.AreEquivalent(new[] { fields[42] }, (await paths.ReadEntityEvidenceAsync([Identity(42)])).Fields.ToArray());
        }
    }

    [TestMethod]
    public void Initial_build_matches_incremental_oracle_with_less_original_population_allocation()
    {
        const int Population = 100_000;
        var warm = SemanticDatabaseState.Empty with
        {
            Fields = [Field(0)]
        };
        _ = BuildIncrementally(warm);
        _ = SemanticTimestampIndex.For(warm);
        var state = SemanticDatabaseState.Empty with
        {
            Fields = Enumerable.Range(0, Population).Select(Field).ToArray()
        };
        long start = GC.GetAllocatedBytesForCurrentThread();
        var expected = BuildIncrementally(state);
        long previous = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        var actual = SemanticTimestampIndex.For(state);
        long current = GC.GetAllocatedBytesForCurrentThread() - start;
        TestContext.WriteLine($"Initial index allocation for {Population} assertions: previous={previous}, current={current} bytes.");
        AssertEquivalentIndexes(expected, actual);
        Assert.IsTrue(current < previous / 2, "Private construction should eliminate most discarded immutable paths.");
    }

    [TestMethod]
    public void Initial_build_preserves_replacements_mixed_values_ended_evidence_and_relationship_indexes()
    {
        var first = Field(1);
        var moved = first with
        {
            Subject = Identity(2),
            Value = new StringSemanticValue("text"),
            EndVersion = new(3)
        };
        var second = Field(2);
        var edge = new SemanticRelationshipEvidence(Guid.NewGuid(), Identity(1), Peer, Identity(3), first.Provenance, new(1), null);
        var replacement = edge with
        {
            Subject = Identity(2),
            Target = Identity(2)
        };
        foreach (var state in new[]
        {
            SemanticDatabaseState.Empty,
            SemanticDatabaseState.Empty with
            {
                Fields = [first, second, moved, Field(4)],
                Relationships = [edge, replacement]
            }
        }

        )
        {
            AssertEquivalentIndexes(BuildIncrementally(state), SemanticTimestampIndex.For(state));
        }
    }

    private static SemanticTimestampIndex BuildIncrementally(SemanticDatabaseState state)
    {
        // Preserve the original construction algorithm as an oracle. The unchanged
        // incremental setters also exercise replacement/removal semantics.
        var index = new SemanticTimestampIndex();
        const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var field = typeof(SemanticTimestampIndex).GetMethod("Set", Flags, [typeof(SemanticFieldEvidence)])!.CreateDelegate<Action<SemanticFieldEvidence>>(index);
        var edge = typeof(SemanticTimestampIndex).GetMethod("Set", Flags, [typeof(SemanticRelationshipEvidence)])!.CreateDelegate<Action<SemanticRelationshipEvidence>>(index);
        foreach (var item in state.Fields)
        {
            field(item);
        }

        foreach (var item in state.Relationships)
        {
            edge(item);
        }

        return index;
    }

    private static void AssertEquivalentIndexes(SemanticTimestampIndex expected, SemanticTimestampIndex actual)
    {
        foreach (var field in typeof(SemanticTimestampIndex).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
        {
            var left = (IDictionary)field.GetValue(expected)!;
            var right = (IDictionary)field.GetValue(actual)!;
            Assert.AreEqual(left.Count, right.Count, field.Name);
            foreach (DictionaryEntry entry in left)
            {
                Assert.IsTrue(right.Contains(entry.Key), field.Name);
                if (entry.Value is IEnumerable values)
                {
                    CollectionAssert.AreEquivalent(values.Cast<object>().ToArray(), ((IEnumerable)right[entry.Key]!).Cast<object>().ToArray(), field.Name);
                }
                else
                {
                    Assert.AreEqual(entry.Value, right[entry.Key], field.Name);
                }
            }
        }
    }

    [TestMethod]
    public void Native_planning_evidence_updates_only_owned_subjects_and_keeps_predecessor_immutable()
    {
        const int Count = 100_000;
        var fields = Enumerable.Range(0, Count).Select(Field).ToArray();
        var before = SemanticDatabaseState.Empty with
        {
            Version = new(1),
            Fields = fields
        };
        var old = SemanticPlanningEvidence.Create(before);
        var replacement = fields[42] with
        {
            Value = new IntegerSemanticValue(-42),
            BeginVersion = new(2)
        };
        var after = before with
        {
            Version = new(2),
            Fields = new ForbiddenEnumeration<SemanticFieldEvidence>(Count)
        };
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var changed = old.Apply(new(before, after, new([new SemanticFieldAssertionRetracted(fields[42]), new SemanticFieldAssertionAdded(replacement)])));
        Assert.IsTrue(GC.GetAllocatedBytesForCurrentThread() - allocated < 1024 * 1024);
        Assert.AreSame(old.Fields[Identity(43)], changed.Fields[Identity(43)]);
        Assert.AreEqual(fields[42], old.Read([Identity(42)], default).Fields.Single());
        Assert.AreEqual(replacement, changed.Read([Identity(42), Identity(42)], default).Fields.Single());
        var removed = changed.Apply(new(after, after with
        {
            Version = new(3)
        }, new([new SemanticFieldAssertionRetracted(replacement)])));
        Assert.AreEqual(0, removed.Read([Identity(42)], default).Fields.Count);
        Assert.ThrowsExactly<InvalidOperationException>(() => old.Apply(new(after, after, new([]))));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => changed.Read([], canceled.Token));
        Assert.AreEqual(replacement, changed.Read([Identity(42)], default).Fields.Single());
    }

    [TestMethod]
    public void Native_planning_evidence_excludes_incoming_edges_and_patches_relationships()
    {
        var field = Field(1);
        var edge = new SemanticRelationshipEvidence(Guid.NewGuid(), Identity(1), Peer, Identity(2), field.Provenance, new(1), null);
        var self = edge with
        {
            AssertionId = Guid.NewGuid(),
            Target = Identity(1)
        };
        var before = SemanticDatabaseState.Empty with
        {
            Version = new(1),
            Fields = [field],
            Relationships = [edge, self]
        };
        var old = SemanticPlanningEvidence.Create(before);
        Assert.AreEqual(0, old.Read([Identity(2)], default).Relationships.Count);
        Assert.AreEqual(2, old.Read([Identity(1), Identity(1)], default).Relationships.Count);
        var moved = edge with
        {
            Target = Identity(3),
            BeginVersion = new(2)
        };
        var after = before with
        {
            Version = new(2),
            Relationships = new ForbiddenEnumeration<SemanticRelationshipEvidence>(1)
        };
        var next = old.Apply(new(before, after, new([new SemanticRelationshipAssertionRetracted(edge), new SemanticRelationshipAssertionRetracted(self), new SemanticRelationshipAssertionAdded(moved)])));
        Assert.AreEqual(moved, next.Read([Identity(1)], default).Relationships.Single());
        Assert.AreEqual(0, next.Read([Identity(3)], default).Relationships.Count);
        CollectionAssert.AreEquivalent(new[] { edge, self }, old.Read([Identity(1)], default).Relationships.ToArray());
    }

    [TestMethod]
    public async Task Integer_range_seeks_original_population_and_carries_only_changed_paths()
    {
        const int Population = 100_000;
        const int First = 40_000;
        const int Width = 128;
        var fields = Enumerable.Range(0, Population).Select(Field).ToImmutableArray();
        var before = SemanticDatabaseState.Empty with
        {
            Version = new(1),
            Fields = fields
        };
        var old = SemanticTimestampIndex.For(before);
        int comparisons = 0;
        Func<SemanticValue, bool>[] predicates = [value =>
        {
            comparisons++;
            return ((IntegerSemanticValue)value).Value >= First;
        },
            value =>
        {
            comparisons++;
            return ((IntegerSemanticValue)value).Value < First + Width;
        }
        ];
        var range = new SemanticIntegerRange(First, First + Width - 1, false);
        var result = await old.FindMatchingSubjectsAsync(Number, predicates, range, before.Version, CancellationToken.None);
        CollectionAssert.AreEquivalent(Enumerable.Range(First, Width).Select(Identity).ToArray(), result.ToArray());
        Assert.AreEqual(Width * predicates.Length, comparisons, "Range selection must not evaluate the rest of the property.");
        var replacement = fields[First] with
        {
            Value = new IntegerSemanticValue(-1),
            BeginVersion = new(2)
        };
        var after = before with
        {
            Version = new(2),
            Fields = new ForbiddenEnumeration<SemanticFieldEvidence>(Population)
        };
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        SemanticTimestampIndex.Carry(before, after, new([new SemanticFieldAssertionAdded(replacement)]));
        var next = SemanticTimestampIndex.For(after);
        Assert.IsTrue(GC.GetAllocatedBytesForCurrentThread() - allocated < 1024 * 1024, "A write rebuilt the complete sorted index.");
        Assert.AreEqual(Width - 1, (await next.FindMatchingSubjectsAsync(Number, predicates, range, after.Version, CancellationToken.None)).Count);
        Assert.AreEqual(Width, (await old.FindMatchingSubjectsAsync(Number, predicates, range, before.Version, CancellationToken.None)).Count);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await next.FindMatchingSubjectsAsync(Number, predicates, range, after.Version, canceled.Token));
        Assert.AreEqual(Width - 1, (await next.FindMatchingSubjectsAsync(Number, predicates, range, after.Version, CancellationToken.None)).Count);
        TestContext.WriteLine($"100000 values: {Width * predicates.Length} predicate evaluations for a {Width}-value window; immutable update below 1 MiB.");
    }

    [TestMethod]
    public async Task Original_size_updated_view_uses_only_changed_evidence_and_preserves_old_snapshot()
    {
        const int Count = 100_000;
        var fields = Enumerable.Range(0, Count).Select(Field).ToArray();
        var before = SemanticDatabaseState.Empty with
        {
            Version = new(1),
            Fields = fields,
            Catalog = new([Model], [Concept, Number], [Scheme])
        };
        var baselineWatch = Stopwatch.StartNew();
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        var baseline = DatabaseReadView.Create(before, null);
        _ = await baseline.EntityAccessPaths.ReadEntityEvidenceAsync([Identity(42)]);
        long baselineBytes = GC.GetAllocatedBytesForCurrentThread() - allocation;
        baselineWatch.Stop();
        var old = SemanticTimestampIndex.For(before);
        var replacement = fields[42] with
        {
            Value = new IntegerSemanticValue(-42),
            BeginVersion = new(2)
        };
        // Any accidental rebuild of the complete state fails, independent of timing.
        var after = before with
        {
            Version = new(2),
            Fields = new ForbiddenEnumeration<SemanticFieldEvidence>(Count)
        };
        allocation = GC.GetAllocatedBytesForCurrentThread();
        var changedWatch = Stopwatch.StartNew();
        SemanticTimestampIndex.Carry(before, after, new([new SemanticFieldAssertionAdded(replacement)]));
        var current = SemanticTimestampIndex.For(after);
        var view = DatabaseReadView.Create(after, current);
        var actual = await view.EntityAccessPaths.ReadEntityEvidenceAsync([Identity(42)]);
        long changedBytes = GC.GetAllocatedBytesForCurrentThread() - allocation;
        Assert.AreEqual(replacement, actual.Fields.Single());
        Assert.AreEqual(fields[42], (await old.ReadEntityEvidenceAsync([Identity(42)])).Fields.Single());
        Assert.AreEqual(0, (await current.FindFieldAssertionsAsync(Number, new IntegerSemanticValue(42))).Count);
        Assert.AreEqual(1, (await current.FindFieldAssertionsAsync(Number, new IntegerSemanticValue(-42))).Count);
        Assert.AreEqual((long)Count, await current.CountConceptMembersAsync(Concept));
        Assert.IsTrue(changedBytes < baselineBytes / 10, $"Update allocated {changedBytes} versus full rebuild {baselineBytes}.");
        TestContext.WriteLine($"100k full rebuild + point: {baselineWatch.Elapsed.TotalMilliseconds:F3} ms/{baselineBytes} bytes; changed view + point: {changedWatch.Elapsed.TotalMilliseconds:F3} ms/{changedBytes} bytes.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await current.ReadEntityEvidenceAsync([Identity(42)], cancelled.Token));
        var reads = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ => await current.ReadEntityEvidenceAsync([Identity(42)])));
        Assert.IsTrue(reads.All(read => read.Fields.Single() == replacement));
        Assert.AreNotSame(reads[0].Fields, reads[1].Fields);
    }

    [TestMethod]
    public async Task Mixed_original_size_windows_after_publication_match_full_evaluation()
    {
        const int Count = 100_000, First = 40_000, Width = 128;
        var fields = Enumerable.Range(0, Count).Select(Field).ToArray();
        var state = SemanticDatabaseState.Empty with
        {
            Version = new(1),
            Fields = new SemanticAssertionCollection<SemanticFieldEvidence>(fields.ToImmutableDictionary(e => e.AssertionId)),
            Catalog = new([Model], [Concept, Number], [Scheme]),
            AssertionStatistics = new(Count, 0),
            QueryStatistics = SemanticQueryStatistics.Empty.Apply(new(fields, []), new(1))
        };
        var expected = Enumerable.Range(First, Width).Select(i => i.ToString()).ToArray();
        PortableQueryLiteralRequest Integer(int value) => new(JsonSerializer.SerializeToElement(value), "integer");
        var request = new PortableEntityQueryRequest(Concept.Identifier.AbsoluteUri, new(Concept.Identifier.AbsoluteUri, Fields: [new(Number.Identifier.AbsoluteUri, "number")]), Filter: new("and", Operands: [new("propertyComparison", Number.Identifier.AbsoluteUri, "greaterThanOrEqual", Integer(First)), new("propertyComparison", Number.Identifier.AbsoluteUri, "lessThan", Integer(First + Width))]), ConceptMatchMode: "direct");
        var compiled = new PortableEntityQueryRequestCompiler().Compile(request);
        var watch = Stopwatch.StartNew();
        var baseline = DatabaseReadView.Create(state, null);
        var oldResult = await new PortableEntityQueryExecutor().ExecuteAsync(state, baseline.EntityAccessPaths, compiled.Query, compiled.ProjectionOptions, state.Version);
        double baselineMs = watch.Elapsed.TotalMilliseconds;
        _ = SemanticTimestampIndex.For(state);
        var revised = fields[0] with
        {
            Value = new IntegerSemanticValue(-1),
            BeginVersion = new(2)
        };
        var next = new SemanticTimestampEffects(state, state with
        {
            Version = new(2)
        }, new([new SemanticFieldAssertionAdded(revised)])).Apply(state);
        watch.Restart();
        var view = DatabaseReadView.Create(next, SemanticTimestampIndex.For(next));
        var result = await new PortableEntityQueryExecutor().ExecuteAsync(next, view.EntityAccessPaths, compiled.Query, compiled.ProjectionOptions, next.Version);
        double indexedMs = watch.Elapsed.TotalMilliseconds;
        foreach (var answer in new[]
        {
            oldResult,
            result
        }

        )
        {
            CollectionAssert.AreEquivalent(expected, PortableEntityQueryResponseMapper.Map(answer, 0).Entities.Select(e => e.Fields["number"].Single().Value.ToString()).ToArray());
        }

        TestContext.WriteLine($"100k mixed window after write: old view+query {baselineMs:F3} ms; indexed view+query {indexedMs:F3} ms.");
        var owner = new PortableEntityQueryRequestCompiler().Compile(request with
        {
            Filter = new("propertyComparison", Number.Identifier.AbsoluteUri, "equal", Integer(First))
        });
        Assert.AreEqual(SemanticPropertyStorageKind.ColdJson, next.PropertyStorage.Resolve(Number).StorageKind);
        long pointAllocation = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        var fragment = await new PortableEntityQueryExecutor().ExecuteFragmentAsync(next, view.EntityAccessPaths, owner.Query, owner.ProjectionOptions, next.Version, CancellationToken.None);
        long pointBytes = GC.GetAllocatedBytesForCurrentThread() - pointAllocation;
        CollectionAssert.AreEquivalent(new[] { First.ToString() }, PortableEntityQueryResponseMapper.Map(fragment.Projection, 0).Entities.Select(e => e.Fields["number"].Single().Value.ToString()).ToArray());
        Assert.IsTrue(pointBytes < 1024 * 1024, $"Point fragment must not hydrate the complete ColdJson concept: {pointBytes} bytes.");
        TestContext.WriteLine($"100k ColdJson point through actual fragment evaluator: {watch.Elapsed.TotalMilliseconds:F3} ms; {pointBytes} bytes.");
    }

    [TestMethod]
    public async Task Integer_bounds_keep_extremes_mixed_values_and_multivalue_transitions()
    {
        var first = Field(1) with
        {
            Value = new IntegerSemanticValue(long.MinValue)
        };
        var second = Field(2) with
        {
            Value = new IntegerSemanticValue(long.MaxValue)
        };
        var duplicate = first with
        {
            AssertionId = Guid.NewGuid(),
            Value = new IntegerSemanticValue(long.MaxValue)
        };
        var fields = new[]
        {
            first,
            second,
            Field(3)with
            {
                Value = new StringSemanticValue("50")
            },
            Field(4)with
            {
                Value = new DecimalSemanticValue(50m)
            },
            Field(5)with
            {
                EndVersion = new(2)
            },
            Field(6)with
            {
                BeginVersion = new(3)
            }
        };
        var before = SemanticDatabaseState.Empty with
        {
            Version = new(2),
            Fields = fields
        };
        var old = SemanticTimestampIndex.For(before);
        var multiple = before with
        {
            Fields = fields.Append(duplicate).ToArray()
        };
        SemanticTimestampIndex.Carry(before, multiple, new([new SemanticFieldAssertionAdded(duplicate)]));
        var removed = multiple with
        {
            Fields = fields
        };
        SemanticTimestampIndex.Carry(multiple, removed, new([new SemanticFieldAssertionRetracted(duplicate)]));
        foreach (var state in new[]
        {
            before,
            multiple,
            removed
        }

        )
        {
            foreach (var bounds in new[]
            {
                (long.MinValue, long.MaxValue),
                (long.MaxValue, long.MaxValue),
                (long.MinValue, long.MinValue),
                (40L, 60L),
                (60L, 40L)
            }

            )
            {
                foreach (bool inclusive in new[]
                {
                    false,
                    true
                }

                )
                {
                    PortableQueryLiteralRequest Integer(long value) => new(JsonSerializer.SerializeToElement(value), "integer");
                    var compiled = new PortableEntityQueryRequestCompiler().Compile(new(Concept.Identifier.AbsoluteUri, new(Concept.Identifier.AbsoluteUri, Fields: [new(Number.Identifier.AbsoluteUri, "number")]), Filter: new("and", Operands: [new("propertyComparison", Number.Identifier.AbsoluteUri, inclusive ? "greaterThanOrEqual" : "greaterThan", Integer(bounds.Item1)), new("propertyComparison", Number.Identifier.AbsoluteUri, inclusive ? "lessThanOrEqual" : "lessThan", Integer(bounds.Item2))]), ConceptMatchMode: "direct"));
                    var executor = new PortableEntityQueryExecutor();
                    var expected = await executor.ExecuteAsync(state, new SemanticEntityAccessPaths(state), compiled.Query, compiled.ProjectionOptions, state.Version);
                    var view = DatabaseReadView.Create(state, SemanticTimestampIndex.For(state));
                    var actual = await executor.ExecuteAsync(state, view.EntityAccessPaths, compiled.Query, compiled.ProjectionOptions, state.Version);
                    CollectionAssert.AreEquivalent(PortableEntityQueryResponseMapper.Map(expected, 0).Entities.Select(e => e.Identity).ToArray(), PortableEntityQueryResponseMapper.Map(actual, 0).Entities.Select(e => e.Identity).ToArray());
                }
            }
        }
    }

    [TestMethod]
    public async Task Numeric_conjuncts_preserve_multivalue_and_visibility_semantics()
    {
        var low = Field(1) with
        {
            Value = new IntegerSemanticValue(0)
        };
        var high = low with
        {
            AssertionId = Guid.NewGuid(),
            Value = new IntegerSemanticValue(100)
        };
        var ended = Field(2) with
        {
            Value = new IntegerSemanticValue(50),
            EndVersion = new(2)
        };
        var live = Field(3) with
        {
            Value = new IntegerSemanticValue(50)
        };
        var state = SemanticDatabaseState.Empty with
        {
            Version = new(2),
            Fields = [low, high, ended, live]
        };
        PortableQueryLiteralRequest Integer(int value) => new(JsonSerializer.SerializeToElement(value), "integer");
        var compiled = new PortableEntityQueryRequestCompiler().Compile(new(Concept.Identifier.AbsoluteUri, new(Concept.Identifier.AbsoluteUri, Fields: [new(Number.Identifier.AbsoluteUri, "number")]), Filter: new("and", Operands: [new("propertyComparison", Number.Identifier.AbsoluteUri, "greaterThan", Integer(40)), new("propertyComparison", Number.Identifier.AbsoluteUri, "lessThan", Integer(60))]), ConceptMatchMode: "direct"));
        var executor = new PortableEntityQueryExecutor();
        var old = await executor.ExecuteAsync(state, new SemanticEntityAccessPaths(state), compiled.Query, compiled.ProjectionOptions, state.Version);
        var indexed = DatabaseReadView.Create(state, SemanticTimestampIndex.For(state));
        var actual = await new PortableEntityQueryExecutor().ExecuteAsync(state, indexed.EntityAccessPaths, compiled.Query, compiled.ProjectionOptions, state.Version);
        CollectionAssert.AreEquivalent(PortableEntityQueryResponseMapper.Map(old, 0).Entities.Select(e => e.Identity).ToArray(), PortableEntityQueryResponseMapper.Map(actual, 0).Entities.Select(e => e.Identity).ToArray());
        Assert.AreEqual(2, actual.Document.Roots.Count, "Separate assertions can satisfy the two existential conjuncts; expired evidence cannot.");
    }

    [TestMethod]
    public async Task Retractions_replacements_incoming_edges_and_self_edges_match_full_scan()
    {
        var field = Field(1);
        var edge = new SemanticRelationshipEvidence(Guid.NewGuid(), Identity(1), Peer, Identity(2), field.Provenance, new(1), null);
        var self = edge with
        {
            AssertionId = Guid.NewGuid(),
            Target = Identity(1)
        };
        var before = SemanticDatabaseState.Empty with
        {
            Version = new(1),
            Fields = [field],
            Relationships = [edge, self]
        };
        var old = SemanticTimestampIndex.For(before);
        var moved = edge with
        {
            Target = Identity(3),
            BeginVersion = new(2)
        };
        var after = before with
        {
            Version = new(2),
            Fields = [],
            Relationships = [moved]
        };
        SemanticTimestampIndex.Carry(before, after, new([new SemanticFieldAssertionRetracted(field), new SemanticRelationshipAssertionRetracted(self), new SemanticRelationshipAssertionAdded(moved)]));
        var index = SemanticTimestampIndex.For(after);
        var oracle = new SemanticEntityAccessPaths(after);
        foreach (var identities in new[]
        {
            new[]
            {
                Identity(1)
            },
            new[]
            {
                Identity(2)
            },
            new[]
            {
                Identity(3)
            },
            new[]
            {
                Identity(1),
                Identity(3),
                Identity(1)
            }
        }

        )
        {
            var expected = await oracle.ReadEntityEvidenceAsync(identities);
            var actual = await index.ReadEntityEvidenceAsync(identities);
            CollectionAssert.AreEquivalent(expected.Fields.ToArray(), actual.Fields.ToArray());
            CollectionAssert.AreEquivalent(expected.Relationships.ToArray(), actual.Relationships.ToArray());
        }

        Assert.IsFalse(await index.ContainsIdentityAsync(Identity(2)));
        Assert.IsTrue(await old.ContainsIdentityAsync(Identity(2)));
        Assert.AreEqual(2, (await old.ReadEntityEvidenceAsync([Identity(1), Identity(2)])).Relationships.Count);
        Assert.AreEqual(1, (await index.ReadEntitySupportAsync([Identity(1)])).Single().Assertions.Count);
    }

    private sealed class ForbiddenEnumeration<T>(int count) : IReadOnlyList<T>
    {
        public int Count => count;

        public T this[int index] => throw new AssertFailedException("Rebuilt an immutable timestamp index.");

        public IEnumerator<T> GetEnumerator() => throw new AssertFailedException("Scanned unchanged timestamp evidence.");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
