using System.Collections.Immutable;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Durability;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SemanticTimestampEffectsTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Trimmed_timestamp_basis_does_not_accumulate_retired_replay_records()
    {
        const int Updates = 20000;
        var domain = Guid.NewGuid();
        var native = SemanticDatabaseState.Empty;
        var history = new DatabaseLogicalHistory<SemanticDatabaseState, SemanticTimestampEffects>(native,
            new(domain, 0, 0, 0, true), (prior, effect) => effect.Apply(prior));
        for (int i = 1; i <= Updates; i++)
        {
            var id = Guid.NewGuid();
            var after = native with
            {
                Version = new(i),
                AppliedSessions =
                ImmutableDictionary<Guid, AppliedSession>.Empty.Add(id, new("hash", new(i)))
            };
            var time = new DurableTimestampState(domain, i, 0, i, true);
            history.Publish(id, time, new(native, after, new([])));
            history.Trim(time);
            Assert.AreEqual(1, after.AppliedSessions.Count, "Read projection must not mutate native replay protection.");
            // Retention checkpoints retire native replay records without publishing a transaction.
            native = after with
            {
                AppliedSessions = ImmutableDictionary<Guid, AppliedSession>.Empty
            };
        }
        var basis = history.Read(new(domain, Updates, 0, Updates, true));
        Assert.AreEqual(0, history.Count);
        Assert.AreEqual(0L, history.RetainedBytes);
        Assert.AreEqual(0, basis.AppliedSessions.Count, "The trimmed read basis must not retain replay records.");
        Assert.AreEqual(new DatabaseVersion(Updates), basis.Version);
    }

    [TestMethod]
    public void Timestamp_publication_scaling_diagnostic()
    {
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_TIMESTAMP_SCALING") != "1")
        {
            Assert.Inconclusive("Explicit isolated publication diagnostic only.");
            return;
        }
        const int Publications = 32;
        var concept = new SemanticTerm(new Uri("https://scaling.test/Item"), SemanticTermRole.Concept);
        var term = new SemanticTerm(new Uri("https://scaling.test/name"), SemanticTermRole.Property);
        var scheme = new SemanticIdentityScheme(new Uri("https://scaling.test/id"), "ID", SemanticValueKind.String);
        var model = new SemanticModelReference(new Uri("https://scaling.test/model"), "1", SemanticModelKind.Vocabulary);
        var type = typeof(SemanticDatabaseState).Assembly.GetType("EntityForge.Db.Semantic.Engine.SemanticTimestampEffects") ??
            typeof(SemanticDatabaseEngine).Assembly.GetType("EntityForge.Db.Semantic.Engine.SemanticTimestampEffects") ??
            typeof(SemanticTimestampEffects).Assembly.GetType("EntityForge.Db.Semantic.Engine.SemanticTimestampEffects")!;
        var constructor = type.GetConstructors(System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length >= 3);
        var apply = type.GetMethod("Apply", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        foreach (int count in new[] { 1024, 16384 })
        {
            var fields = Enumerable.Range(0, count).Select(index => new SemanticFieldEvidence(Guid.NewGuid(),
                new(concept, scheme, new StringSemanticValue(index.ToString())), term, new StringSemanticValue("same"),
                new(new Uri("https://scaling.test/source"), index.ToString(), DateTimeOffset.UnixEpoch, model), new(1), null)).ToArray();
            var state = SemanticDatabaseState.Empty with
            {
                Version = new(1),
                Fields = fields,
                AssertionStatistics = new(count, 0),
                QueryStatistics = SemanticQueryStatistics.Empty.Apply(new(fields, []), new(1))
            };
            var effects = new SemanticCommittedEffects([new SemanticFieldAssertionAdded(fields[0] with { BeginVersion = new(2) })]);
            object[] arguments = constructor.GetParameters().Length == 3
                ? [state, state with { Version = new(2) }, effects]
                : [state, state with { Version = new(2) }, effects, null!];
            var change = constructor.Invoke(arguments);
            for (int warmup = 0; warmup < 4; warmup++)
            {
                state = (SemanticDatabaseState)apply.Invoke(change, [state])!;
            }

            long allocation = GC.GetAllocatedBytesForCurrentThread();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int publication = 0; publication < Publications; publication++)
            {
                state = (SemanticDatabaseState)apply.Invoke(change, [state])!;
            }

            double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocation;
            Assert.AreEqual(count, state.Fields.Count);
            Assert.AreEqual((long)count, state.QueryStatistics.PropertyCardinalities[term]);
            TestContext.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                Assertions = count,
                Publications,
                Seconds = seconds,
                AllocatedBytes = allocated
            }));
        }
    }

    [TestMethod]
    public void Indexed_dependencies_detect_new_contributors_but_ignore_unrelated_subjects()
    {
        var concept = new SemanticTerm(new Uri("https://scope.test/Item"), SemanticTermRole.Concept);
        var scheme = new SemanticIdentityScheme(new Uri("https://scope.test/id"), "ID", SemanticValueKind.String);
        var model = new SemanticModelReference(new Uri("https://scope.test/model"), "1", SemanticModelKind.Vocabulary);
        var source = new Uri("https://scope.test/source");
        var partition = new Uri("https://scope.test/partition");
        var other = new Uri("https://scope.test/other");
        var subject = new SemanticEntityIdentity(concept, scheme, new StringSemanticValue("selected"));
        var provenance = new SemanticProvenance(source, "selected", DateTimeOffset.UnixEpoch, model);
        var id = Guid.NewGuid();
        var start = new SemanticChangeSessionStart(id, SemanticChangeSessionKind.SourceSnapshot,
            new(source, partition, "1", Guid.NewGuid(), "1"), Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        SemanticChangeSessionPage[] pages = [new(id, 0, [new(Guid.NewGuid(),
            SemanticEntityChangeKind.ReplaceSourceState, subject, provenance)])];
        var session = new SemanticSessionEnvelope(start, pages, new(id, 1, 1,
            new EntityForge.Semantics.Protobuf.SemanticChangeSessionTransportCodec().ComputeContentHash(pages), DateTimeOffset.UnixEpoch));
        var before = SemanticDatabaseState.Empty with
        {
            Version = new(1),
            SourceMemberships = SourceMembershipIndex.Empty.SetItem(new(subject, source, "selected"), other, provenance)
        };
        var after = before with
        {
            Version = new(2),
            SourceMemberships = before.SourceMemberships.SetItem(new(subject, source, "selected"), partition, provenance),
            AppliedSessions = new Dictionary<Guid, AppliedSession> { [id] = new("hash", new(2)) }
        };
        var complete = new SemanticTimestampEffects(before, after, new([])).Apply(before);
        var scoped = new SemanticTimestampEffects(before, after, new([]), session).Apply(before);
        CollectionAssert.AreEquivalent(complete.SourceMemberships.Entries.ToArray(), scoped.SourceMemberships.Entries.ToArray());
        CollectionAssert.AreEquivalent(complete.AppliedSessions.ToArray(), scoped.AppliedSessions.ToArray());
        var read = SemanticPartitionDependencies.Capture(after, session, [], []);
        // Independent byte-framing oracle for this fixed fixture, not the production hash helper.
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, new System.Text.UTF8Encoding(false, true), leaveOpen: true))
        {
            void Bytes(byte[] value)
            {
                writer.Write((long)value.Length);
                writer.Write(value);
            }
            void Text(string value) => Bytes(System.Text.Encoding.UTF8.GetBytes(value));
            var codec = new EntityForge.Semantics.Protobuf.SemanticChangeSessionTransportCodec();
            Text("semantic-partition-dependencies/v3");
            writer.Write(1L);
            Bytes(codec.SerializeIdentity(subject));
            Text(source.AbsoluteUri);
            Text("selected");
            Text(partition.AbsoluteUri);
            Bytes(codec.SerializeProvenance(provenance));
            writer.Write(0L); // Membership contributor has no progress.
            writer.Write(0L); // Selected partition has no progress.
            writer.Write(after.PropertyStorage.Version.Value);
            writer.Write(after.Governance.Version.Value);
            writer.Write(after.Catalog.VocabularyVersion.Value);
        }
        Assert.AreEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(buffer.ToArray())), read.Fingerprint);
        var unrelated = after with
        {
            SourceMemberships = after.SourceMemberships.SetItem(
            new(new(concept, scheme, new StringSemanticValue("unrelated")), source, "other"), other, provenance)
        };
        Assert.AreEqual(read.Fingerprint, SemanticPartitionDependencies.Capture(unrelated, session, [], []).Fingerprint);
        var contributor = unrelated with
        {
            SourceMemberships = unrelated.SourceMemberships.SetItem(
            new(subject, source, "another"), other, provenance)
        };
        Assert.AreNotEqual(read.Fingerprint, SemanticPartitionDependencies.Capture(contributor, session, [], []).Fingerprint);
    }

    [TestMethod]
    public void Out_of_order_metadata_preserves_governance_revision_and_rule_removal()
    {
        var initial = SemanticDatabaseState.Empty;
        var rule = new SemanticGovernanceRule(new("https://timestamp.test/rule"), SemanticGovernanceRuleKind.Constraint,
            new("https://timestamp.test/definition"), "1", true, new(1));
        var published = initial with
        {
            Version = new(1),
            Governance = new(new(1),
            new Dictionary<Uri, SemanticGovernanceRule> { [rule.Identifier] = rule })
        };
        var unrelated = published with
        {
            Version = new(2)
        };
        var removed = unrelated with
        {
            Version = new(3),
            Governance = new(new(2), new Dictionary<Uri, SemanticGovernanceRule>())
        };
        Guid domain = Guid.NewGuid();
        DurableTimestampState Time(long value) => new(domain, value, 0, 0, true);
        var history = new DatabaseLogicalHistory<SemanticDatabaseState, SemanticTimestampEffects>(initial, Time(0), (state, effect) => effect.Apply(state));
        history.Publish(Guid.NewGuid(), Time(20), new(initial, published, new([])));
        history.Publish(Guid.NewGuid(), Time(10), new(published, unrelated, new([])));
        Assert.AreEqual(0, history.Read(Time(10)).Governance.Rules.Count);
        Assert.AreEqual(initial.Governance.Version, history.Read(Time(10)).Governance.Version);
        var pinned = history.Read(Time(20));
        history.Publish(Guid.NewGuid(), Time(30), new(unrelated, removed, new([])));
        Assert.AreEqual(rule, pinned.Governance.Rules[rule.Identifier]);
        Assert.AreEqual(0, history.Read(Time(30)).Governance.Rules.Count);
        Assert.AreEqual(new GovernanceCatalogVersion(2), history.Read(Time(30)).Governance.Version);
        history.Trim(Time(30));
        Assert.AreEqual(0, history.Read(Time(31)).Governance.Rules.Count);
    }

    [TestMethod]
    public async Task Logical_order_preserves_assertions_provenance_counts_and_catalogue_isolation()
    {
        var concept = new SemanticTerm(new Uri("https://timestamp.test/Item"), SemanticTermRole.Concept);
        var term = new SemanticTerm(new Uri("https://timestamp.test/name"), SemanticTermRole.Property);
        var scheme = new SemanticIdentityScheme(new Uri("https://timestamp.test/id"), "ID", SemanticValueKind.String);
        var model = new SemanticModelReference(new Uri("https://timestamp.test/model"), "1", SemanticModelKind.Vocabulary);
        SemanticFieldEvidence Field(string id, long version) => new(Guid.NewGuid(),
            new(concept, scheme, new StringSemanticValue(id)), term, new StringSemanticValue(id),
            new(new Uri("https://timestamp.test/source"), id, DateTimeOffset.UnixEpoch, model), new(version), null);
        var a = Field("a", 1);
        var b = Field("b", 2);
        var initial = SemanticDatabaseState.Empty;
        var afterA = initial with
        {
            Version = new(1),
            Catalog = initial.Catalog with
            {
                Terms = [term],
                VocabularyVersion = new(1)
            }
        };
        var afterB = afterA with
        {
            Version = new(2)
        };
        Guid domain = Guid.NewGuid();
        DurableTimestampState Time(long value) => new(domain, value, 0, 0, true);
        var history = new DatabaseLogicalHistory<SemanticDatabaseState, SemanticTimestampEffects>(initial, Time(0), (state, effect) => effect.Apply(state));
        history.Publish(Guid.NewGuid(), Time(30), new(initial, afterA, new([new SemanticFieldAssertionAdded(a)])));
        var pinnedIndex = SemanticTimestampIndex.For(history.Read(Time(30)));
        _ = SemanticTimestampIndex.For(initial);
        history.Publish(Guid.NewGuid(), Time(10), new(afterA, afterB, new([new SemanticFieldAssertionAdded(b)])));
        var early = history.Read(Time(10));
        CollectionAssert.AreEqual(new[] { b }, (await SemanticTimestampIndex.For(early).ScanFieldEvidenceAsync(term)).ToArray());
        Assert.AreEqual(b, early.Fields.Single());
        Assert.AreEqual(1L, early.AssertionStatistics.FieldCount);
        Assert.AreEqual(1L, early.QueryStatistics.PropertyCardinalities[term]);
        Assert.AreEqual(initial.Catalog.VocabularyVersion, early.Catalog.VocabularyVersion);
        Assert.AreEqual(0, early.Catalog.Terms.Count);
        history.Publish(Guid.NewGuid(), Time(20), new(afterB, afterB with
        {
            Version = new(3)
        },
            new([new SemanticFieldAssertionRetracted(b with { EndVersion = new(3) })])));
        Assert.AreEqual(0, history.Read(Time(20)).Fields.Count);
        var final = history.Read(Time(30));
        CollectionAssert.AreEqual(new[] { a }, (await SemanticTimestampIndex.For(final).ScanFieldEvidenceAsync(term)).ToArray());
        CollectionAssert.AreEqual(new[] { a }, (await pinnedIndex.ScanFieldEvidenceAsync(term)).ToArray());
        Assert.AreEqual(a, final.Fields.Single());
        Assert.AreEqual(1L, final.AssertionStatistics.FieldCount);
        Assert.AreEqual(1L, final.QueryStatistics.PropertyCardinalities[term]);
        Assert.AreEqual(term, final.Catalog.Terms.Single());
        history.Trim(Time(30));
        Assert.AreEqual(a, history.Read(Time(31)).Fields.Single());
    }

    [TestMethod]
    public void Subject_memberships_preserve_multiple_contributors_migration_and_old_views()
    {
        var concept = new SemanticTerm(new Uri("https://index.test/Item"), SemanticTermRole.Concept);
        var scheme = new SemanticIdentityScheme(new Uri("https://index.test/id"), "ID", SemanticValueKind.String);
        var subject = new SemanticEntityIdentity(concept, scheme, new StringSemanticValue("item"));
        var model = new SemanticModelReference(new Uri("https://index.test/model"), "1", SemanticModelKind.Vocabulary);
        var source = new Uri("https://index.test/source");
        var first = new SourceOwner(subject, source, "first");
        var second = new SourceOwner(subject, source, "second");
        var partition = new Uri("https://index.test/partition");
        var movedPartition = new Uri("https://index.test/moved");
        var provenance = new SemanticProvenance(source, "first", DateTimeOffset.UnixEpoch, model);
        var pinned = SourceMembershipIndex.Empty.SetItem(first, partition, provenance)
            .SetItem(second, partition, new SemanticProvenance(source, "second", DateTimeOffset.UnixEpoch, model));
        var moved = pinned.SetItem(first, movedPartition, provenance).Remove(second);
        Assert.AreEqual(2, pinned.GetSubjects([subject, subject]).Count());
        Assert.AreEqual(1, moved.GetSubjects([subject]).Count());
        Assert.AreEqual(movedPartition, moved.GetSubjects([subject]).Single().Value.SourcePartition);
        Assert.AreEqual(2, pinned.GetPartition(source, partition).Count);
        Assert.AreEqual(0, moved.GetPartition(source, partition).Count);
        Assert.IsFalse(moved.Remove(first).ContainsSubject(subject));
    }

    [TestMethod]
    public void Replacing_one_assertion_preserves_pinned_data_and_updates_only_its_statistics()
    {
        var concept = new SemanticTerm(new Uri("https://incremental.test/Item"), SemanticTermRole.Concept);
        var term = new SemanticTerm(new Uri("https://incremental.test/name"), SemanticTermRole.Property);
        var scheme = new SemanticIdentityScheme(new Uri("https://incremental.test/id"), "ID", SemanticValueKind.String);
        var model = new SemanticModelReference(new Uri("https://incremental.test/model"), "1", SemanticModelKind.Vocabulary);
        var field = new SemanticFieldEvidence(Guid.NewGuid(), new(concept, scheme, new StringSemanticValue("item")),
            term, new StringSemanticValue("before"), new(new Uri("https://incremental.test/source"), "item",
                DateTimeOffset.UnixEpoch, model), new(1), null);
        var initial = SemanticDatabaseState.Empty;
        var metadata = initial with
        {
            Version = new(1)
        };
        var pinned = new SemanticTimestampEffects(initial, metadata, new([new SemanticFieldAssertionAdded(field)])).Apply(initial);
        var replacement = field with
        {
            Value = new StringSemanticValue("after"),
            BeginVersion = new(2)
        };
        var changed = new SemanticTimestampEffects(metadata, metadata with
        {
            Version = new(2)
        },
            new([new SemanticFieldAssertionAdded(replacement)])).Apply(pinned);
        Assert.AreEqual(field, pinned.Fields.Single());
        Assert.AreEqual(replacement, changed.Fields.Single());
        Assert.AreEqual(1L, changed.QueryStatistics.PropertyCardinalities[term]);
        Assert.AreEqual(0L, changed.QueryStatistics.EstimateValue(term, field.Value));
        Assert.AreEqual(1L, changed.QueryStatistics.EstimateValue(term, replacement.Value));
        var removed = new SemanticTimestampEffects(metadata, metadata with
        {
            Version = new(3)
        },
            new([new SemanticFieldAssertionRetracted(replacement), new SemanticFieldAssertionRetracted(replacement)])).Apply(changed);
        Assert.AreEqual(0, removed.Fields.Count);
        Assert.AreEqual(0L, removed.AssertionStatistics.FieldCount);
        Assert.AreEqual(0, removed.QueryStatistics.PropertyCardinalities.Count);
    }
}
