using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Diagnostics.Metrics;
using EntityForge.Db.AccessPaths;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed partial class AppendMergeLookupWorkAuditTests
{
    [TestMethod]
    public async Task Recovery_stream_decodes_one_record_at_a_time_and_validates_range_tail()
    {
        var values = new ValidatingStringCodec();
        var codec = new AppendMergeRunCodec<int, string>(new IntCodec(), values);
        var writer = new AppendMergeRunWriter<int, string>(codec, new());
        var entries = Enumerable.Range(0, 100).Select(key =>
            new AppendMergeRunEntry<int, string>(AppendMergeMutationKind.Add, 1, key, "value")).ToArray();
        var image = writer.Write(entries, 1, new Allocator());
        Assert.AreEqual(1, image.Pages.Count);
        var reader = new AppendMergeRunReader<int, string>(new Pages(image.Pages), codec);
        await using (var stream = reader.ReadStreamAsync(image.Descriptor, OrderedIndexRange<int>.All,
            Comparer<int>.Default, StringComparer.Ordinal, default).GetAsyncEnumerator())
        {
            Assert.IsTrue(await stream.MoveNextAsync());
            Assert.AreEqual(1, values.Decodes, "Opening a run must not hydrate its whole page.");
            Assert.AreEqual(0, stream.Current.Key);
            Assert.IsTrue(await stream.MoveNextAsync());
            Assert.AreEqual(2, values.Decodes);
        }
        entries[^1] = new(AppendMergeMutationKind.Add, 1, 99, "!invalid");
        var corrupt = writer.Write(entries, 2, new Allocator());
        var corruptReader = new AppendMergeRunReader<int, string>(new Pages(corrupt.Pages), codec);
        await Assert.ThrowsExactlyAsync<PhysicalPageCorruptionException>(async () =>
        {
            await foreach (var entry in corruptReader.ReadStreamAsync(corrupt.Descriptor,
                OrderedIndexRange<int>.Through(0), Comparer<int>.Default, StringComparer.Ordinal, default))
            {
            }
        });
    }

    [TestMethod]
    public async Task Compaction_priority_merge_preserves_versions_and_bounds_run_selection_work()
    {
        const int RunCount = 64, KeysPerRun = 64, SharedKey = 100000;
        var codec = new AppendMergeRunCodec<int, string>(new IntCodec(), new ValidatingStringCodec());
        var writer = new AppendMergeRunWriter<int, string>(codec, new());
        var allocator = new Allocator();
        var inputs = Enumerable.Range(0, RunCount).Select(run =>
            Enumerable.Range(0, KeysPerRun).Select(key => new AppendMergeRunEntry<int, string>(
                AppendMergeMutationKind.Add, RunCount - run, key * RunCount + run, "value"))
                .Append(new(run % 3 == 0 ? AppendMergeMutationKind.Remove : AppendMergeMutationKind.Add,
                    RunCount - run, SharedKey, "shared")).ToArray()).ToArray();
        var runs = inputs.Select((entries, index) => writer.Write(entries, RunCount - index, allocator)).ToArray();
        var images = runs.SelectMany(run => run.Pages).ToDictionary(pair => pair.Key, pair => pair.Value);
        var comparisons = new CountingComparer();
        var executor = new AppendMergeCompactionExecutor<int, string>(new(new Pages(images), codec), writer, comparisons, StringComparer.Ordinal);
        foreach (long floor in new long[] { 0, RunCount / 2, RunCount })
        {
            foreach (bool complete in new[] { false, true })
            {
                comparisons.Count = 0;
                var result = (await executor.ExecuteAsync(runs.Select(run => run.Descriptor).ToArray(), RunCount + 1,
                    floor, complete, allocator, default))!;
                long work = comparisons.Count;
                var observed = new List<AppendMergeRunEntry<int, string>>();
                await foreach (var entry in new AppendMergeRunReader<int, string>(new Pages(result.Pages), codec).ReadAsync(result.Descriptor, default))
                {
                    observed.Add(entry);
                }

                var expected = inputs.SelectMany(entries => entries).GroupBy(entry => (entry.Key, entry.Value))
                    .OrderBy(group => group.Key.Key).ThenBy(group => group.Key.Value, StringComparer.Ordinal)
                    .SelectMany(group =>
                    {
                        var ordered = group.OrderBy(entry => entry.Version).ToArray();
                        var baseline = ordered.LastOrDefault(entry => entry.Version <= floor);
                        return (baseline.Version > 0 && (!complete || baseline.Kind == AppendMergeMutationKind.Add)
                            ? new[] { baseline } : []).Concat(ordered.Where(entry => entry.Version > floor));
                    }).ToArray();
                CollectionAssert.AreEqual(expected, observed.ToArray());
                // The old minimum selection alone visits all live runs for each disjoint key.
                long linearMinimum = (long)KeysPerRun * RunCount * (RunCount - 1);
                Assert.IsTrue(work < linearMinimum / 2, $"Unexpected selection work: {work}");
                Console.WriteLine($"Compaction floor={floor}, complete={complete}: comparisons={work}, old minimum-selection lower bound={linearMinimum}");
            }
        }

        using var cancelled = new CancellationTokenSource();
        comparisons.OnCompare = () =>
{
    if (comparisons.Count > 100)
    {
        cancelled.Cancel();
    }

};
        comparisons.Count = 0;
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await executor.ExecuteAsync(
            runs.Select(run => run.Descriptor).ToArray(), RunCount + 1, 0, true, allocator, cancelled.Token));
        comparisons.OnCompare = null;
        Assert.IsNotNull(await executor.ExecuteAsync(runs.Select(run => run.Descriptor).ToArray(), RunCount + 1, 0, true, allocator, default));
    }

    private sealed class CountingComparer : IComparer<int>
    {
        public long Count;
        public Action? OnCompare;

        public int Compare(int left, int right)
        {
            Count++;
            OnCompare?.Invoke();
            return left.CompareTo(right);
        }
    }

    [TestMethod]
    [DataRow(16000)]
    [DataRow(100000)]
    public async Task Recovery_stream_matches_lookup_oracle_and_reads_each_run_page_once(int count)
    {
        int Count = count;
        var codec = new AppendMergeRunCodec<int, string>(new IntCodec(), new ValidatingStringCodec());
        var writer = new AppendMergeRunWriter<int, string>(codec, new());
        var allocator = new Allocator();
        var first = writer.Write(Enumerable.Range(0, Count).Select(key =>
            new AppendMergeRunEntry<int, string>(AppendMergeMutationKind.Add, 1, key, new string('x', 96))).ToArray(), 1, allocator);
        var second = writer.Write(Enumerable.Range(0, Count).Where(key => key % 3 == 0).Select(key =>
            new AppendMergeRunEntry<int, string>(AppendMergeMutationKind.Remove, 2, key, new string('x', 96))).ToArray(), 2, allocator);
        var third = writer.Write(Enumerable.Range(0, Count).Where(key => key % 5 == 0).Select(key =>
            new AppendMergeRunEntry<int, string>(AppendMergeMutationKind.Add, 3, key, new string('x', 96))).ToArray(), 3, allocator);
        var images = first.Pages.Concat(second.Pages).Concat(third.Pages).ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (long version in new long[] { 1, 2, 3 })
        {
            var pages = new Pages(images);
            var reader = new AppendMergeRunReader<int, string>(pages, codec);
            using var snapshot = new AppendMergeSnapshot<int, string>(codec, reader, 3,
                [third.Descriptor, second.Descriptor, first.Descriptor], Comparer<int>.Default,
                StringComparer.Ordinal, version, () =>
{

});
            var oracle = await snapshot.FindUniqueAsync(Enumerable.Range(0, Count).ToArray());
            pages.Reads = 0;
            var observed = new List<KeyValuePair<int, string>>();
            long allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
            await foreach (var entry in snapshot.ReadVisibleAsync())
            {
                observed.Add(entry);
            }

            long streamAllocation = GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
            CollectionAssert.AreEqual(oracle.ToArray(), observed.ToArray());
            Assert.AreEqual(images.Count, pages.Reads, "Streaming must read each physical page once for a complete pass.");
            int streamingReads = pages.Reads;
            pages.Reads = 0;
            const int Batches = 16;
            for (int batch = 0; batch < Batches; batch++)
            {
                await snapshot.FindUniqueAsync(Enumerable.Range(0, Count).Where(key => key % Batches == batch).ToArray());
            }

            Assert.IsTrue(pages.Reads > streamingReads * 4, "The structural oracle must expose repeated scan work.");
            Console.WriteLine($"Recovery keys={Count}, ceiling={version}: old batched reads={pages.Reads}, streaming reads={streamingReads}, process allocation including owned results={streamAllocation}");
            using var cancel = new CancellationTokenSource();
            int emitted = 0;
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var entry in snapshot.ReadVisibleAsync(cancellationToken: cancel.Token))
                {
                    if (++emitted == 20)
                    {
                        cancel.Cancel();
                    }
                }
            });
            var later = new List<KeyValuePair<int, string>>();
            await foreach (var entry in snapshot.ReadVisibleAsync())
            {
                later.Add(entry);
            }

            CollectionAssert.AreEqual(observed.ToArray(), later.ToArray());
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            {
                var independent = new List<KeyValuePair<int, string>>();
                await foreach (var entry in snapshot.ReadVisibleAsync())
                {
                    independent.Add(entry);
                }

                return independent.ToArray();
            }));
            foreach (var independent in concurrent)
            {
                CollectionAssert.AreEqual(observed.ToArray(), independent);
            }

            var ranged = new List<KeyValuePair<int, string>>();
            await foreach (var entry in snapshot.ReadVisibleAsync(OrderedIndexRange<int>.Between(350, 950)))
            {
                ranged.Add(entry);
            }

            CollectionAssert.AreEqual(observed.Where(pair => pair.Key >= 350 && pair.Key <= 950).ToArray(), ranged.ToArray());
        }
    }

    [TestMethod]
    public async Task Recovery_stream_rejects_corruption_and_unsorted_runs_and_releases_admission()
    {
        var codec = new AppendMergeRunCodec<int, string>(new IntCodec(), new ValidatingStringCodec());
        var writer = new AppendMergeRunWriter<int, string>(codec, new());
        foreach (var entries in new[]
        {
            new AppendMergeRunEntry<int, string>[] { new(AppendMergeMutationKind.Add, 1, 1, "valid"), new(AppendMergeMutationKind.Add, 1, 2, "!invalid") },
            new AppendMergeRunEntry<int, string>[] { new(AppendMergeMutationKind.Add, 1, 2, "valid"), new(AppendMergeMutationKind.Add, 1, 1, "valid") }
        })
        {
            var image = writer.Write(entries, 1, new Allocator());
            bool released = false;
            var snapshot = new AppendMergeSnapshot<int, string>(codec,
                new AppendMergeRunReader<int, string>(new Pages(image.Pages), codec), 1,
                [image.Descriptor], Comparer<int>.Default, StringComparer.Ordinal, null, () => released = true);
            await Assert.ThrowsExactlyAsync<PhysicalPageCorruptionException>(async () =>
            {
                await foreach (var item in snapshot.ReadVisibleAsync())
                {
                }
            });
            await snapshot.DisposeAsync();
            Assert.IsTrue(released);
        }
    }

    [TestMethod]
    public async Task Optional_lookup_work_counts_complete_pages_without_changing_owned_results()
    {
        Assert.IsNull(AppendMergeLookupTelemetry.Begin(AppendMergeLookupRole.Register("atom_directory"), 10));
        var counts = new Dictionary<string, long>();
        using var listener = LookupListener(counts);
        const int EntryCount = 100;
        var codec = new AppendMergeRunCodec<int, string>(new IntCodec(), new ValidatingStringCodec());
        var entries = Enumerable.Range(0, EntryCount).Select(key =>
            new AppendMergeRunEntry<int, string>(AppendMergeMutationKind.Add, 7, key, key.ToString("D5"))).ToArray();
        var image = new AppendMergeRunWriter<int, string>(codec, new()).Write(entries, 1, new Allocator());
        Assert.AreEqual(1, image.Descriptor.Pages.Count);
        var reader = new AppendMergeRunReader<int, string>(new Pages(image.Pages), codec);
        int[] requested = Enumerable.Range(20, 10).ToArray();
        var control = await reader.FindManyAsync(image.Descriptor, requested, Comparer<int>.Default, CancellationToken.None);
        counts.Clear();
        using (var scope = AppendMergeLookupTelemetry.Begin(AppendMergeLookupRole.Register("atom_directory"), requested.Length))
        {
            var observed = await reader.FindManyAsync(image.Descriptor, requested, Comparer<int>.Default, CancellationToken.None);
            foreach (int key in requested)
            {
                CollectionAssert.AreEqual(control[key].ToArray(), observed[key].ToArray());
            }

            scope!.Complete();
        }
        Assert.AreEqual(10L, counts["atom_directory/RequestedKeys"]);
        Assert.AreEqual(1L, counts["atom_directory/DenseRuns"]);
        Assert.AreEqual(1L, counts["atom_directory/BoundaryHits"]);
        Assert.AreEqual(0L, counts["atom_directory/BoundaryMisses"]);
        Assert.AreEqual(1L, counts["atom_directory/LookupPages"]);
        Assert.AreEqual(100L, counts["atom_directory/VisitedRecords"]);
        Assert.AreEqual(10L, counts["atom_directory/DecodedRecords"]);
        Assert.AreEqual(90L, counts["atom_directory/ValidatedUnmatchedRecords"]);
        Assert.AreEqual(0L, counts["atom_directory/IncompleteScopes"]);
        counts.Clear();
        using (AppendMergeLookupTelemetry.Begin(AppendMergeLookupRole.Register("atom_payload"), requested.Length))
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await reader.FindManyAsync(image.Descriptor, requested, Comparer<int>.Default, canceled.Token));
        }
        Assert.AreEqual(1L, counts["atom_payload/IncompleteScopes"]);
        Assert.AreEqual(0L, counts["atom_payload/VisitedRecords"]);
        counts.Clear();
        await reader.FindManyAsync(image.Descriptor, requested, Comparer<int>.Default, CancellationToken.None);
        Assert.AreEqual(0, counts.Count, "Disposed scope must not attribute later reads.");
        entries[50] = entries[50] with
        {
            Value = "!invalid"
        };
        var corrupt = new AppendMergeRunWriter<int, string>(codec, new()).Write(entries, 1, new Allocator());
        var corruptReader = new AppendMergeRunReader<int, string>(new Pages(corrupt.Pages), codec);
        using (AppendMergeLookupTelemetry.Begin(AppendMergeLookupRole.Register("atom_directory"), requested.Length))
        {
            await Assert.ThrowsExactlyAsync<PhysicalPageCorruptionException>(async () =>
                await corruptReader.FindManyAsync(corrupt.Descriptor, requested, Comparer<int>.Default, CancellationToken.None));
        }
        Assert.AreEqual(1L, counts["atom_directory/IncompleteScopes"]);
        Assert.AreEqual(0L, counts["atom_directory/VisitedRecords"], "Only completely validated pages are counted.");
    }

    [TestMethod]
    public async Task Optional_lookup_scopes_keep_concurrent_roles_and_physical_layers_independent()
    {
        var counts = new Dictionary<string, long>();
        using var listener = LookupListener(counts);
        async Task Read(AppendMergeLookupRole role, int bytes)
        {
            using var scope = AppendMergeLookupTelemetry.Begin(role, bytes);
            await Task.Yield();
            using var device = new PhysicalPageTelemetry();
            using var buffer = new PhysicalPageTelemetry("buffer");
            device.Read(bytes, TimeSpan.Zero);
            buffer.Read(bytes, TimeSpan.Zero);
            buffer.Hit();
            buffer.Miss();
            buffer.Eviction();
            scope!.Complete();
        }
        await Task.WhenAll(Read(AppendMergeLookupRole.Register("atom_directory"), 17), Read(AppendMergeLookupRole.Register("atom_payload"), 23));
        foreach (var (stage, bytes) in new[] { ("atom_directory", 17), ("atom_payload", 23) })
        {
            Assert.AreEqual((long)bytes, counts[stage + "/DeviceBytes"]);
            Assert.AreEqual((long)bytes, counts[stage + "/BufferBytes"]);
            Assert.AreEqual(1L, counts[stage + "/DeviceReads"]);
            Assert.AreEqual(1L, counts[stage + "/CacheHits"]);
            Assert.AreEqual(1L, counts[stage + "/Scopes"]);
            Assert.AreEqual(0L, counts[stage + "/IncompleteScopes"]);
        }
    }

    private static MeterListener LookupListener(Dictionary<string, long> counts)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Name == "entityforge.db.lookup.work")
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            string stage = "", work = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "entityforge.db.stage")
                {
                    stage = (string)tag.Value!;
                }

                if (tag.Key == "entityforge.db.lookup.work")
                {
                    work = (string)tag.Value!;
                }
            }
            lock (counts)
            {
                counts[stage + "/" + work] = counts.GetValueOrDefault(stage + "/" + work) + value;
            }
        });
        listener.Start();
        return listener;
    }

    [TestMethod]
    public async Task Optional_lookup_work_preserves_sparse_validation_and_default_decode_fallback()
    {
        var counts = new Dictionary<string, long>();
        using var listener = LookupListener(counts);
        foreach (bool validating in new[] { false, true })
        {
            IOrderedIndexCodec<string> values = validating ? new ValidatingStringCodec() : new CountingStringCodec();
            var codec = new AppendMergeRunCodec<int, string>(new IntCodec(), values);
            var entries = Enumerable.Range(0, 100).Select(key =>
                new AppendMergeRunEntry<int, string>(AppendMergeMutationKind.Add, 7, key,
                    key.ToString("D5") + new string('x', 2048))).ToArray();
            var image = new AppendMergeRunWriter<int, string>(codec, new()).Write(entries, 1, new Allocator());
            Assert.IsTrue(AppendMergeRunReader<int, string>.UsesSparseBatch(1, image.Descriptor.Pages.Count));
            var reader = new AppendMergeRunReader<int, string>(new Pages(image.Pages), codec);
            counts.Clear();
            using (var scope = AppendMergeLookupTelemetry.Begin(AppendMergeLookupRole.Register("atom_payload"), 1))
            {
                var result = await reader.FindManyAsync(image.Descriptor, [51], Comparer<int>.Default, CancellationToken.None);
                CollectionAssert.AreEqual(new[] { entries[51] }, result[51].ToArray());
                scope!.Complete();
            }
            Assert.AreEqual(1L, counts["atom_payload/SparseRuns"]);
            Assert.IsTrue(counts["atom_payload/BoundaryMisses"] > 0);
            Assert.AreEqual(counts["atom_payload/VisitedRecords"],
                counts["atom_payload/DecodedRecords"] + counts["atom_payload/ValidatedUnmatchedRecords"]);
            if (validating)
            {
                Assert.AreEqual(1L, counts["atom_payload/DecodedRecords"]);
                Assert.IsTrue(counts["atom_payload/ValidatedUnmatchedRecords"] > 0);
            }
            else
            {
                Assert.AreEqual(0L, counts["atom_payload/ValidatedUnmatchedRecords"]);
                Assert.AreEqual(counts["atom_payload/VisitedRecords"], counts["atom_payload/DecodedRecords"]);
            }
        }
    }

    [TestMethod]
    public async Task Optional_lookup_probes_preserve_all_run_removals_and_snapshot_version_ceiling()
    {
        var counts = new Dictionary<string, long>();
        using var listener = LookupListener(counts);
        var codec = new AppendMergeRunCodec<int, string>(new IntCodec(), new ValidatingStringCodec());
        var writer = new AppendMergeRunWriter<int, string>(codec, new());
        var allocator = new Allocator();
        var added = writer.Write([new(AppendMergeMutationKind.Add, 1, 11, "value")], 1, allocator);
        var removed = writer.Write([new(AppendMergeMutationKind.Remove, 2, 11, "value")], 2, allocator);
        var pages = added.Pages.Concat(removed.Pages).ToDictionary(pair => pair.Key, pair => pair.Value);
        var reader = new AppendMergeRunReader<int, string>(new Pages(pages), codec);
        foreach (long version in new long[] { 1, 2 })
        {
            using var snapshot = new AppendMergeSnapshot<int, string>(codec, reader, 2,
                [removed.Descriptor, added.Descriptor], Comparer<int>.Default, StringComparer.Ordinal, version, () =>
{

});
            counts.Clear();
            using (var scope = AppendMergeLookupTelemetry.Begin(AppendMergeLookupRole.Register("atom_directory"), 2))
            {
                var result = await snapshot.FindUniqueAsync([11, 22]);
                Assert.AreEqual(version == 1, result.ContainsKey(11));
                if (version == 1)
                {
                    Assert.AreEqual("value", result[11]);
                }

                Assert.IsFalse(result.ContainsKey(22));
                scope!.Complete();
            }
            Assert.AreEqual(snapshot.RunProbeCount, counts["atom_directory/RunProbes"]);
            Assert.AreEqual(2L, counts["atom_directory/RunProbes"]);
            Assert.AreEqual(2L, counts["atom_directory/CandidateKeys"]);
            Assert.AreEqual(2L, counts["atom_directory/VisitedRecords"]);
        }
    }

    [TestMethod]
    public async Task Boundary_keys_validate_values_without_constructing_them()
    {
        const int EntryCount = 1000;
        var values = new ValidatingStringCodec();
        var codec = new AppendMergeRunCodec<int, string>(new IntCodec(), values);
        var entries = Enumerable.Range(0, EntryCount).Select(key =>
            new AppendMergeRunEntry<int, string>(AppendMergeMutationKind.Add, 7, key,
                key.ToString("D5") + new string('x', 128))).ToArray();
        var image = new AppendMergeRunWriter<int, string>(codec, new()).Write(entries, 1, new Allocator());
        var reader = new AppendMergeRunReader<int, string>(new Pages(image.Pages), codec);
        MethodInfo method = reader.GetType().GetMethod("ReadBoundaryAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (PhysicalPageId page in image.Descriptor.Pages)
        {
            var boundary = await (ValueTask<AppendMergePageBoundary<int>>)method.Invoke(reader,
                [image.Descriptor, page, CancellationToken.None])!;
            var records = SlottedDataPage.ReadOccupiedRecords(image.Pages[page], page);
            Assert.AreEqual(codec.Decode(records[0].Span).Key, boundary.FirstKey);
            Assert.AreEqual(codec.Decode(records[^1].Span).Key, boundary.LastKey);
        }
        int oracleDecodes = image.Descriptor.Pages.Count * 2;
        Console.WriteLine($"Boundary value decodes={values.Decodes - oracleDecodes}; validations={values.Validations}");
        Assert.AreEqual(oracleDecodes, values.Decodes);
        Assert.AreEqual(oracleDecodes, values.Validations);

        entries[0] = entries[0] with
        {
            Value = "!invalid"
        };
        image = new AppendMergeRunWriter<int, string>(codec, new()).Write(entries, 1, new Allocator());
        reader = new AppendMergeRunReader<int, string>(new Pages(image.Pages), codec);
        await Assert.ThrowsExactlyAsync<PhysicalPageCorruptionException>(async () =>
            await (ValueTask<AppendMergePageBoundary<int>>)method.Invoke(reader,
                [image.Descriptor, image.Descriptor.Pages[0], CancellationToken.None])!);

        var fallback = new AppendMergeRunCodec<int, string>(new IntCodec(), new NonValidatingStringCodec());
        Assert.AreEqual(entries[^1].Key, fallback.DecodeBoundaryKey(fallback.Encode(entries[^1])));
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() =>
            fallback.DecodeBoundaryKey(fallback.Encode(entries[0])));
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() =>
            codec.DecodeBoundaryKey(codec.Encode(entries[0])));
    }

    [TestMethod]
    public async Task Explicit_validation_bounds_value_decoding_and_rejects_unmatched_corruption()
    {
        const int EntryCount = 1000;
        const int RequestStride = 10;
        const int CorruptKey = 5;
        const int SpanningKey = 500;
        const int AdditionalSpanningValues = 50;
        foreach (int payloadLength in new[] { 128, 2048 })
        {
            var values = new ValidatingStringCodec();
            var codec = new AppendMergeRunCodec<int, string>(new IntCodec(), values);
            var entries = Enumerable.Range(0, EntryCount).Select(key =>
                new AppendMergeRunEntry<int, string>(AppendMergeMutationKind.Add, 7, key,
                    key.ToString("D5") + new string('x', payloadLength))).ToList();
            entries.AddRange(Enumerable.Range(0, AdditionalSpanningValues).Select(index =>
                new AppendMergeRunEntry<int, string>(AppendMergeMutationKind.Add, 7, SpanningKey,
                    index.ToString("D5") + new string('y', payloadLength))));
            entries = entries.OrderBy(entry => entry.Key).ThenBy(entry => entry.Value, StringComparer.Ordinal).ToList();
            var expected = entries.GroupBy(entry => entry.Key).ToDictionary(group => group.Key, group => group.ToArray());
            var image = new AppendMergeRunWriter<int, string>(codec, new()).Write(entries, 1, new Allocator());
            var reader = new AppendMergeRunReader<int, string>(new Pages(image.Pages), codec);
            int[] requested = Enumerable.Range(0, EntryCount / RequestStride).Select(index => index * RequestStride).ToArray();
            await reader.FindManyAsync(image.Descriptor, requested, Comparer<int>.Default, CancellationToken.None);
            values.Decodes = values.Validations = 0;
            var result = await reader.FindManyAsync(image.Descriptor, requested, Comparer<int>.Default, CancellationToken.None);
            foreach (int key in requested)
            {
                CollectionAssert.AreEqual(expected[key], result[key].ToArray());
            }

            Assert.AreEqual(AdditionalSpanningValues + 1, result[SpanningKey].Count);
            Console.WriteLine($"Validated lookup payload={payloadLength}: {values.Decodes} decoded values, {values.Validations} validated unmatched values.");
            Assert.AreEqual(requested.Length + AdditionalSpanningValues, values.Decodes);
            Assert.IsTrue(values.Validations > 0);

            const int SparseStride = 100;
            int[] sparseRequested = Enumerable.Range(0, EntryCount / SparseStride).Select(index => index * SparseStride).ToArray();
            await reader.FindManyAsync(image.Descriptor, sparseRequested, Comparer<int>.Default, CancellationToken.None);
            values.Decodes = values.Validations = 0;
            var sparse = await reader.FindManyAsync(image.Descriptor, sparseRequested, Comparer<int>.Default, CancellationToken.None);
            foreach (int key in sparseRequested)
            {
                CollectionAssert.AreEqual(expected[key], sparse[key].ToArray());
            }

            Assert.AreEqual(sparseRequested.Length + AdditionalSpanningValues, values.Decodes);
            Assert.IsTrue(values.Validations > 0);

            using var duringRead = new CancellationTokenSource();
            values.OnValidate = duringRead.Cancel;
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await reader.FindManyAsync(image.Descriptor, requested, Comparer<int>.Default, duringRead.Token));
            values.OnValidate = null;
            var afterCancellation = await reader.FindManyAsync(image.Descriptor, requested,
                Comparer<int>.Default, CancellationToken.None);
            foreach (int key in requested)
            {
                CollectionAssert.AreEqual(expected[key], afterCancellation[key].ToArray());
            }

            entries[CorruptKey] = entries[CorruptKey] with
            {
                Value = "!invalid"
            };
            image = new AppendMergeRunWriter<int, string>(codec, new()).Write(entries, 1, new Allocator());
            reader = new AppendMergeRunReader<int, string>(new Pages(image.Pages), codec);
            await Assert.ThrowsExactlyAsync<PhysicalPageCorruptionException>(async () =>
                await reader.FindManyAsync(image.Descriptor, [0, EntryCount - 1], Comparer<int>.Default, CancellationToken.None));

            var fallback = new AppendMergeRunReader<int, string>(new Pages(image.Pages),
                new AppendMergeRunCodec<int, string>(new IntCodec(), new NonValidatingStringCodec()));
            await Assert.ThrowsExactlyAsync<PhysicalPageCorruptionException>(async () =>
                await fallback.FindManyAsync(image.Descriptor, [0, EntryCount - 1], Comparer<int>.Default, CancellationToken.None));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await reader.FindManyAsync(image.Descriptor, requested, Comparer<int>.Default, cancellation.Token));
        }
    }

    [TestMethod]
    public void Filtered_codec_rejects_unmatched_headers_and_keeps_matched_payloads_owned()
    {
        const int VersionOffset = sizeof(byte) * 2;
        const int KeyLengthOffset = VersionOffset + sizeof(long);
        var values = new OwnedBytesCodec();
        var codec = new AppendMergeRunCodec<int, byte[]>(new IntCodec(), values);
        byte[] encoded = codec.Encode(new(AppendMergeMutationKind.Add, 7, 13, [1, 2, 3]));
        var match = codec.DecodeForLookup(encoded, _ => true, out int key)!.Value;
        Assert.AreEqual(13, key);
        match.Value[0] = 99;
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, codec.Decode(encoded).Value);
        Assert.IsNull(codec.DecodeForLookup(encoded, _ => false, out _));
        Assert.AreEqual(1, values.Validations);
        foreach (string corruption in new[] { "kind", "version", "bounds" })
        {
            byte[] invalid = encoded.ToArray();
            if (corruption == "kind")
            {
                invalid[1] = byte.MaxValue;
            }

            if (corruption == "version")
            {
                BinaryPrimitives.WriteInt64LittleEndian(invalid.AsSpan(VersionOffset), 0);
            }

            if (corruption == "bounds")
            {
                BinaryPrimitives.WriteInt32LittleEndian(invalid.AsSpan(KeyLengthOffset), int.MaxValue);
            }

            Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => codec.DecodeForLookup(invalid, _ => false, out _));
        }
        Assert.AreEqual(1, values.Validations, "Invalid headers must fail before value validation.");
    }

    [TestMethod]
    public async Task Dense_and_sparse_paths_preserve_results_while_measuring_page_and_decode_work()
    {
        const int EntryCount = 20_000;
        const int SpanningKey = EntryCount / 2;
        const int SpanningValues = 51;
        const int Seed = 20261006;
        foreach (int payloadLength in new[] { 128, 2048 })
        {
            var values = new CountingStringCodec();
            var codec = new AppendMergeRunCodec<int, string>(new IntCodec(), values);
            var entries = Enumerable.Range(0, EntryCount).Select(key =>
                new AppendMergeRunEntry<int, string>(AppendMergeMutationKind.Add, 7, key,
                    key.ToString("D5") + new string('x', payloadLength))).ToList();
            entries.AddRange(Enumerable.Range(1, SpanningValues - 1).Select(index =>
                new AppendMergeRunEntry<int, string>(AppendMergeMutationKind.Add, 7, SpanningKey,
                    index.ToString("D5") + new string('y', payloadLength))));
            entries = entries.OrderBy(entry => entry.Key).ThenBy(entry => entry.Value, StringComparer.Ordinal).ToList();
            var image = new AppendMergeRunWriter<int, string>(codec, new()).Write(entries, 1, new Allocator());
            var expected = entries.GroupBy(entry => entry.Key).ToDictionary(group => group.Key, group => group.ToArray());
            foreach (int requestCount in new[] { 64, 256 })
            {
                var random = new Random(Seed);
                int[] requested = Enumerable.Range(0, requestCount).Select(_ => random.Next(EntryCount))
                    .Append(SpanningKey).Append(-1).Append(EntryCount).Distinct().Order().ToArray();
                foreach (string mode in new[] { "Dense", "Sparse" })
                {
                    var pages = new Pages(image.Pages);
                    var reader = new AppendMergeRunReader<int, string>(pages, codec);
                    MethodInfo method = reader.GetType().GetMethod($"Populate{mode}BatchAsync",
                        BindingFlags.Instance | BindingFlags.NonPublic)!;
                    foreach (string lifetime in new[] { "cold", "warm" })
                    {
                        var result = new SortedDictionary<int, List<AppendMergeRunEntry<int, string>>>();
                        foreach (int key in requested)
                        {
                            result.Add(key, []);
                        }

                        pages.Reads = values.Decodes = 0;
                        await (ValueTask)method.Invoke(reader, [image.Descriptor,
                            image.Descriptor.Pages,
                            result,
                            Comparer<int>.Default,
                            CancellationToken.None])!;
                        int reads = pages.Reads, decodes = values.Decodes;
                        foreach (int key in requested)
                        {
                            CollectionAssert.AreEqual(expected.GetValueOrDefault(key, []), result[key].ToArray());
                        }

                        Assert.AreEqual(SpanningValues, result[SpanningKey].Count);
                        Console.WriteLine($"payload={payloadLength}; pages={image.Descriptor.Pages.Count}; " +
                            $"requests={requested.Length}; selectedSparse={AppendMergeRunReader<int, string>.UsesSparseBatch(requested.Length, image.Descriptor.Pages.Count)}; " +
                            $"mode={mode}; lifetime={lifetime}; reads={reads}; decodedRecords={decodes}");
                    }
                }
            }
        }
    }

    private sealed class Allocator : IPhysicalPageAllocator
    {
        private long _next = PhysicalPageSpaceDirectory.FirstAccessMethodPageId;

        public PhysicalPageId AllocatePage() => new(_next++);
    }

    private sealed class IntCodec : IOrderedIndexCodec<int>
    {
        public byte[] Encode(int value)
        {
            byte[] bytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            return bytes;
        }

        public int Decode(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadInt32BigEndian(value);
    }

    private sealed class CountingStringCodec : IOrderedIndexCodec<string>
    {
        public int Decodes;

        public byte[] Encode(string value) => Encoding.UTF8.GetBytes(value);

        public string Decode(ReadOnlySpan<byte> value)
        {
            Decodes++;
            return Encoding.UTF8.GetString(value);
        }
    }

    private sealed class ValidatingStringCodec : IValidatingOrderedIndexCodec<string>
    {
        public int Decodes, Validations;
        public Action? OnValidate;

        public byte[] Encode(string value) => Encoding.UTF8.GetBytes(value);

        public string Decode(ReadOnlySpan<byte> value)
        {
            Decodes++;
            Check(value);
            return Encoding.UTF8.GetString(value);
        }

        public void Validate(ReadOnlySpan<byte> value)
        {
            Validations++;
            Check(value);
            OnValidate?.Invoke();
        }

        private static void Check(ReadOnlySpan<byte> value)
        {
            if (!value.IsEmpty && value[0] == (byte)'!')
            {
                throw new PhysicalPageCorruptionException("Invalid audit value.");
            }
        }
    }

    private sealed class NonValidatingStringCodec : IOrderedIndexCodec<string>
    {
        private readonly ValidatingStringCodec _inner = new();

        public byte[] Encode(string value) => _inner.Encode(value);

        public string Decode(ReadOnlySpan<byte> value) => _inner.Decode(value);
    }

    private sealed class OwnedBytesCodec : IValidatingOrderedIndexCodec<byte[]>
    {
        public int Validations;

        public byte[] Encode(byte[] value) => value.ToArray();

        public byte[] Decode(ReadOnlySpan<byte> value) => value.ToArray();

        public void Validate(ReadOnlySpan<byte> value)
        {
            Validations++;
        }
    }

    private sealed class Pages(IReadOnlyDictionary<PhysicalPageId, ReadOnlyMemory<byte>> images) : IResizablePhysicalPageStore
    {
        public int Reads;

        public int PageSize => PhysicalPageFileOptions.DefaultPageSize;

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(PhysicalPageId id, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Reads++;
            return ValueTask.FromResult(images[id]);
        }

        public ValueTask WriteAsync(PhysicalPageId id, ReadOnlyMemory<byte> page, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask FlushAsync(CancellationToken token = default) => ValueTask.CompletedTask;

        public ValueTask<long> GetPageCountAsync(CancellationToken token = default) => ValueTask.FromResult(images.Keys.Max(id => id.Value) + 1);

        public ValueTask EnsureCapacityAsync(long count, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask TruncateAsync(long count, CancellationToken token = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
