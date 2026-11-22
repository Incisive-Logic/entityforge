using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class PhysicalPageSpaceAllocatorTests
{
    private static readonly PhysicalAccessMethodKind Ordered = new("ordered", 1);
    private static readonly PhysicalAccessMethodKind AppendMerge = new("append-merge", 1);

    [TestMethod]
    public void Successive_adjacent_reservations_do_not_accumulate_directory_boundaries()
    {
        var allocator = new PhysicalPageSpaceAllocator(new(0, []), [], 0);
        var id = new PhysicalAccessPathId(Guid.NewGuid());
        PhysicalPageSpaceDirectory? first = null;
        long generation = -1;
        for (int i = 0; i < 100; i++)
        {
            Guid transactionId = Guid.NewGuid();
            var transaction = allocator.BeginTransaction(transactionId);
            var scope = transaction.OpenAccessPath(id, Ordered, generation);
            var page = scope.AllocatePage();
            scope.Complete(page);
            var stage = transaction.Prepare();
            allocator.Publish(transactionId, () =>
{

});
            var entry = stage.Directory.Get(id);
            generation = entry.Generation;
            first ??= stage.Directory;
            Assert.AreEqual(1, entry.Extents.Count);
            Assert.AreEqual(i + 1L, entry.Extents[0].PageCount);
        }
        Assert.AreEqual(1L, first!.Get(id).Extents[0].PageCount);
        var directory = allocator.CaptureDirectory();
        var codec = new PhysicalPageSpaceDirectoryCodec();
        var encoding = allocator.BeginTransaction(Guid.NewGuid());
        var image = codec.Encode(directory, encoding, [], []);
        var decoded = codec.DecodeBranches(image.Branches.Select(branch => (branch.Key, branch.Value))).Single();
        Assert.AreEqual(1, decoded.Extents.Count);
        Assert.AreEqual(100L, decoded.Extents[0].PageCount);
    }

    [TestMethod]
    public void Reclamation_debt_requires_both_substantial_bytes_and_free_fraction()
    {
        const int PageSize = 4096;
        const long MinimumFreePages = 64L * 1024 * 1024 / PageSize;
        var small = new PhysicalPageSpaceAllocator(new(0, []), [], MinimumFreePages);
        Assert.IsFalse(small.HasReclamationDebt(PageSize)); // Page zero is reserved.
        var substantial = new PhysicalPageSpaceAllocator(new(0, []), [], MinimumFreePages + 1);
        Assert.IsTrue(substantial.HasReclamationDebt(PageSize));
        var id = new PhysicalAccessPathId(Guid.NewGuid());
        long ownedPages = MinimumFreePages * 9;
        var owned = new PhysicalAccessPathDirectoryEntry(id, Ordered, new(1), 1,
            [new PhysicalPageExtent(new(1), ownedPages)]);
        var dense = new PhysicalPageSpaceAllocator(new(1, [owned]), [], ownedPages + MinimumFreePages + 1);
        Assert.IsFalse(dense.HasReclamationDebt(PageSize));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => dense.HasReclamationDebt(0));
    }

    [TestMethod]
    public async Task Persist_extends_the_file_through_an_owned_trailing_page_without_an_after_image()
    {
        string path = Path.Combine(Path.GetTempPath(), $"entityforge-page-space-{Guid.NewGuid():N}.pages");
        try
        {
            await using BufferedPhysicalPageStore pages = await BufferedPhysicalPageStore.OpenLocalAsync(path);
            PhysicalPageSpace space = await PhysicalPageSpace.OpenAsync(pages);
            Guid transactionId = Guid.NewGuid();
            PhysicalPageSpaceTransaction transaction = space.BeginTransaction(transactionId);
            PhysicalAccessPathAllocationScope scope = transaction.OpenAccessPath(
                new(Guid.NewGuid()), Ordered, expectedGeneration: -1);
            PhysicalPageId root = scope.AllocatePage();
            scope.Complete(root);
            await pages.EnsureCapacityAsync(root.Value + 1);
            await pages.WriteAsync(root, new SlottedDataPage(pages.PageSize, root).Encode());

            _ = await space.PrepareAsync(transaction);
            await space.PersistAsync(transactionId, []);
            space.Publish(transactionId);
            await pages.FlushAsync();

            PhysicalPageOccupancySnapshot occupancy = await space.AnalyzeOccupancyAsync();
            Assert.AreEqual(await pages.GetPageCountAsync(), occupancy.TotalPageCount);
            Assert.AreEqual(1L, occupancy.AccessPathPageCount);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void One_transaction_allocates_exact_disjoint_pages_for_multiple_access_methods()
    {
        var allocator = new PhysicalPageSpaceAllocator(new(0, []), [], 0);
        Guid transactionId = Guid.NewGuid();
        PhysicalPageSpaceTransaction transaction = allocator.BeginTransaction(transactionId);
        PhysicalAccessPathAllocationScope ordered = transaction.OpenAccessPath(
            new(Guid.NewGuid()), Ordered, expectedGeneration: -1);
        PhysicalAccessPathAllocationScope appendMerge = transaction.OpenAccessPath(
            new(Guid.NewGuid()), AppendMerge, expectedGeneration: -1);
        PhysicalPageId orderedRoot = ordered.AllocatePage();
        _ = ordered.AllocatePage();
        PhysicalPageId appendMergeRoot = appendMerge.AllocatePage();
        ordered.Complete(orderedRoot);
        appendMerge.Complete(appendMergeRoot);

        PhysicalPageSpaceStage stage = transaction.Prepare();

        Assert.AreEqual(1, stage.Directory.Generation);
        Assert.AreEqual(1L, orderedRoot.Value);
        Assert.AreEqual(3L, appendMergeRoot.Value);
        Assert.AreEqual(2, stage.Reservations.Count);
    }

    [TestMethod]
    public void Vacuum_allocator_never_reuses_a_directory_branch_page()
    {
        var allocator = new PhysicalPageSpaceAllocator(new(4, []), [new(12)], 13);
        PhysicalPageSpaceTransaction transaction = allocator.BeginTransaction(
            Guid.NewGuid(), preferLowestFreePage: true);
        PhysicalAccessPathAllocationScope scope = transaction.OpenAccessPath(
            new(Guid.NewGuid()), Ordered, expectedGeneration: -1);

        PhysicalPageId allocated = scope.AllocatePage();

        Assert.AreEqual(1L, allocated.Value);
        Assert.AreNotEqual(12L, allocated.Value);
    }

    [TestMethod]
    public void Publish_advances_the_visible_directory_atomically()
    {
        var allocator = new PhysicalPageSpaceAllocator(new(0, []), [], 0);
        Guid transactionId = Guid.NewGuid();
        PhysicalPageSpaceTransaction transaction = allocator.BeginTransaction(transactionId);
        PhysicalAccessPathAllocationScope scope = transaction.OpenAccessPath(
            new(Guid.NewGuid()), Ordered, expectedGeneration: -1);
        PhysicalPageId root = scope.AllocatePage();
        scope.Complete(root);
        PhysicalPageSpaceStage stage = transaction.Prepare();

        Assert.AreEqual(0, allocator.CaptureDirectory().Generation);
        allocator.Publish(transactionId, () =>
{

});

        Assert.AreSame(stage.Directory, allocator.CaptureDirectory());
        Assert.AreEqual(1, allocator.CaptureDirectory().Get(scope.AccessPathId).Generation);
    }

    [TestMethod]
    public void Abandon_does_not_publish_allocated_pages()
    {
        var allocator = new PhysicalPageSpaceAllocator(new(0, []), [], 0);
        Guid transactionId = Guid.NewGuid();
        PhysicalPageSpaceTransaction transaction = allocator.BeginTransaction(transactionId);
        PhysicalAccessPathAllocationScope scope = transaction.OpenAccessPath(
            new(Guid.NewGuid()), Ordered, expectedGeneration: -1);
        scope.Complete(scope.AllocatePage());

        allocator.Abandon(transactionId);

        Assert.AreEqual(0, allocator.CaptureDirectory().Generation);
        Assert.IsEmpty(allocator.CaptureDirectory().Entries);
    }

    [TestMethod]
    public void Repeated_abandoned_preparations_do_not_advance_the_physical_tail()
    {
        const int Preparations = 100_000;
        const int PagesPerPreparation = 16;
        var allocator = new PhysicalPageSpaceAllocator(new(0, []), [], 0);
        var id = new PhysicalAccessPathId(Guid.NewGuid());
        for (int attempt = 0; attempt < Preparations; attempt++)
        {
            var transaction = allocator.BeginTransaction(Guid.NewGuid());
            var scope = transaction.OpenAccessPath(id, Ordered, -1);
            for (int page = 0; page < PagesPerPreparation; page++)
            {
                Assert.AreEqual(PhysicalPageSpaceDirectory.FirstAccessMethodPageId + page,
                    scope.AllocatePage().Value);
            }

            scope.Complete(new(PhysicalPageSpaceDirectory.FirstAccessMethodPageId));
            transaction.Prepare();
            allocator.Abandon(transaction.TransactionId, reclaimUnsubmittedTail: true);
        }
        var committed = allocator.BeginTransaction(Guid.NewGuid());
        var final = committed.OpenAccessPath(id, Ordered, -1);
        var root = final.AllocatePage();
        Assert.AreEqual(PhysicalPageSpaceDirectory.FirstAccessMethodPageId, root.Value);
        final.Complete(root);
        committed.Prepare();
        allocator.Publish(committed.TransactionId, () =>
{

});
        Assert.AreEqual(root, allocator.CaptureDirectory().Get(id).RootPage);
    }

    [TestMethod]
    public void Abandoned_tail_rewind_stops_at_a_concurrent_workspace_and_never_duplicates_its_pages()
    {
        var allocator = new PhysicalPageSpaceAllocator(new(0, []), [], 0);
        var transaction = allocator.BeginTransaction(Guid.NewGuid());
        var scope = transaction.OpenAccessPath(new(Guid.NewGuid()), Ordered, -1);
        var first = scope.AllocatePage();
        using var workspace = allocator.BeginWorkspace(Guid.NewGuid());
        var protectedPage = workspace.AllocatePage();
        var tail = scope.AllocatePage();
        scope.Complete(first);
        transaction.Prepare();
        allocator.Abandon(transaction.TransactionId, reclaimUnsubmittedTail: true);
        var next = allocator.BeginTransaction(Guid.NewGuid());
        var nextScope = next.OpenAccessPath(new(Guid.NewGuid()), Ordered, -1);
        Assert.AreEqual(tail, nextScope.AllocatePage());
        Assert.AreEqual(first, workspace.AllocatePage());
        Assert.AreEqual(tail.Value + 1, workspace.AllocatePage().Value);
        Assert.AreNotEqual(protectedPage, tail);
        allocator.Abandon(next.TransactionId);
    }

    [TestMethod]
    public void Incomplete_access_path_cannot_prepare_a_directory()
    {
        var allocator = new PhysicalPageSpaceAllocator(new(0, []), [], 0);
        PhysicalPageSpaceTransaction transaction = allocator.BeginTransaction(Guid.NewGuid());
        _ = transaction.OpenAccessPath(new(Guid.NewGuid()), Ordered, expectedGeneration: -1);

        Assert.ThrowsExactly<PhysicalPageSpaceException>(() => transaction.Prepare());
    }

    [TestMethod]
    public void Stale_generation_and_method_replacement_are_rejected()
    {
        var accessPathId = new PhysicalAccessPathId(Guid.NewGuid());
        var current = new PhysicalAccessPathDirectoryEntry(accessPathId, Ordered, new(1), 4,
            [new(new(1), 2)]);
        var allocator = new PhysicalPageSpaceAllocator(new(7, [current]), [], 3);
        PhysicalPageSpaceTransaction transaction = allocator.BeginTransaction(Guid.NewGuid());

        Assert.ThrowsExactly<PhysicalPageSpaceException>(() =>
            transaction.OpenAccessPath(accessPathId, Ordered, expectedGeneration: 3));
        Assert.ThrowsExactly<PhysicalPageSpaceException>(() =>
            transaction.OpenAccessPath(accessPathId, AppendMerge, expectedGeneration: 4));
    }


    [TestMethod]
    public void Fragmented_releases_preserve_exact_ownership_before_and_after_publication()
    {
        const int ExtentCount = 128;
        const int ExtentLength = 4;
        const int ExtentStride = 8;
        var id = new PhysicalAccessPathId(Guid.NewGuid());
        var extents = Enumerable.Range(0, ExtentCount)
            .Select(i => new PhysicalPageExtent(new(1L + i * ExtentStride), ExtentLength)).ToArray();
        var current = new PhysicalAccessPathDirectoryEntry(id, Ordered, new(1), 1, extents.Reverse().ToArray());
        var allocator = new PhysicalPageSpaceAllocator(new(1, [current]), [], extents[^1].EndExclusive);
        var transaction = allocator.BeginTransaction(Guid.NewGuid());
        var scope = transaction.OpenAccessPath(id, Ordered, 1);
        long[] released = extents.SelectMany((extent, i) => Enumerable.Range(0, ExtentLength)
            .Where(offset => i % 3 == 0 || (i % 3 == 1 ? offset == 0 || offset == ExtentLength - 1 : offset == 1))
            .Select(offset => extent.FirstPage.Value + offset)).Reverse().ToArray();
        foreach (long page in released)
        {
            scope.ReleasePage(new(page));
            scope.ReleasePage(new(page));
        }
        Assert.ThrowsExactly<PhysicalPageSpaceException>(() => scope.ReleasePage(new(5)));
        PhysicalPageId root = scope.AllocatePage();
        scope.Complete(root);
        PhysicalPageSpaceStage stage = transaction.Prepare();
        static long[] Pages(PhysicalAccessPathDirectoryEntry entry) => entry.Extents
            .SelectMany(extent => Enumerable.Range(0, checked((int)extent.PageCount))
                .Select(offset => extent.FirstPage.Value + offset)).ToArray();
        long[] before = Pages(current);
        long[] expected = before.Except(released).Append(root.Value).Order().ToArray();
        CollectionAssert.AreEqual(before, Pages(allocator.CaptureDirectory().Get(id)));
        CollectionAssert.AreEqual(expected, Pages(stage.Directory.Get(id)));
        allocator.Publish(transaction.TransactionId, () =>
{

});
        CollectionAssert.AreEqual(expected, Pages(allocator.CaptureDirectory().Get(id)));
    }

    [TestMethod]
    public async Task Retained_growth_debt_ignores_free_space_and_resets_after_compaction()
    {
        string path = Path.Combine(Path.GetTempPath(), $"entityforge-growth-debt-{Guid.NewGuid():N}.pages");
        try
        {
            await using var pages = await BufferedPhysicalPageStore.OpenLocalAsync(path);
            var space = await PhysicalPageSpace.OpenAsync(pages);
            var access = new PhysicalAccessPathId(Guid.NewGuid());
            long generation = -1;
            async Task AddOwnedPages(int count)
            {
                Guid id = Guid.NewGuid();
                var transaction = space.BeginTransaction(id);
                var scope = transaction.OpenAccessPath(access, Ordered, generation);
                var root = scope.AllocatePage();
                for (int i = 1; i < count; i++)
                {
                    scope.AllocatePage();
                }

                scope.Complete(root);
                await space.PrepareAsync(transaction);
                await space.PersistAsync(id, []);
                space.Publish(id);
                generation = space.CaptureDirectory().Get(access).Generation;
            }
            int pagesPer64MiB = 64 * 1024 * 1024 / pages.PageSize;
            await AddOwnedPages(pagesPer64MiB * 2);
            Assert.IsTrue(space.HasRetainedGrowthDebt);
            space.RecordCompactedOwnership();
            Assert.IsFalse(space.HasRetainedGrowthDebt);
            await AddOwnedPages(pagesPer64MiB / 2);
            Assert.IsFalse(space.HasRetainedGrowthDebt);
            await AddOwnedPages(pagesPer64MiB / 2);
            Assert.IsTrue(space.HasRetainedGrowthDebt);
            space.RecordCompactedOwnership();
            Assert.IsFalse(space.HasRetainedGrowthDebt);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void Checkpoint_authorization_reuses_only_covered_free_pages_and_does_not_repeat_debt()
    {
        const int PageSize = 4096;
        var allocator = new PhysicalPageSpaceAllocator(new(0, []), [], 32769);
        Assert.IsTrue(allocator.HasReclamationDebt(PageSize));
        allocator.AuthorizeCheckpointPageReuse();
        Assert.IsFalse(allocator.HasReclamationDebt(PageSize));
        using var workspace = allocator.BeginWorkspace(Guid.NewGuid());
        Assert.AreEqual(1L, workspace.AllocatePage().Value);
        var transaction = allocator.BeginTransaction(Guid.NewGuid());
        var scope = transaction.OpenAccessPath(new(Guid.NewGuid()), Ordered, -1);
        Assert.AreEqual(2L, scope.AllocatePage().Value);
        Assert.ThrowsException<PhysicalPageSpaceException>(() => allocator.AuthorizeCheckpointPageReuse());
        allocator.Abandon(transaction.TransactionId);
        // Even a previously authorized page is quarantined after an uncertain abandonment.
        transaction = allocator.BeginTransaction(Guid.NewGuid());
        scope = transaction.OpenAccessPath(new(Guid.NewGuid()), Ordered, -1);
        Assert.AreEqual(3L, scope.AllocatePage().Value);
        allocator.Abandon(transaction.TransactionId);
        workspace.Dispose();
        allocator.AuthorizeCheckpointPageReuse();
        transaction = allocator.BeginTransaction(Guid.NewGuid());
        scope = transaction.OpenAccessPath(new(Guid.NewGuid()), Ordered, -1);
        Assert.AreEqual(1L, scope.AllocatePage().Value);
    }

    [TestMethod]
    public void Releases_after_checkpoint_wait_for_the_next_checkpoint_authorization()
    {
        var id = new PhysicalAccessPathId(Guid.NewGuid());
        var current = new PhysicalAccessPathDirectoryEntry(id, Ordered, new(1), 1, [new(new(1), 2)]);
        var allocator = new PhysicalPageSpaceAllocator(new(1, [current]), [], 3);
        allocator.AuthorizeCheckpointPageReuse();
        var first = allocator.BeginTransaction(Guid.NewGuid());
        var replacement = first.OpenAccessPath(id, Ordered, 1);
        var root = replacement.AllocatePage();
        replacement.ReleasePage(new(1));
        replacement.Complete(root);
        first.Prepare();
        allocator.Publish(first.TransactionId, () =>
{

});
        var second = allocator.BeginTransaction(Guid.NewGuid());
        var scope = second.OpenAccessPath(id, Ordered, 2);
        Assert.AreEqual(4L, scope.AllocatePage().Value);
        allocator.Abandon(second.TransactionId);
        allocator.AuthorizeCheckpointPageReuse();
        var third = allocator.BeginTransaction(Guid.NewGuid());
        Assert.AreEqual(1L, third.OpenAccessPath(id, Ordered, 2).AllocatePage().Value);
    }

    [TestMethod]
    public void Routine_allocation_preserves_a_published_release_for_vacuum()
    {
        var accessPathId = new PhysicalAccessPathId(Guid.NewGuid());
        var current = new PhysicalAccessPathDirectoryEntry(accessPathId, Ordered, new(1), 1,
            [new(new(1), 2)]);
        var allocator = new PhysicalPageSpaceAllocator(new(1, [current]), [], 3);
        PhysicalPageSpaceTransaction first = allocator.BeginTransaction(Guid.NewGuid());
        PhysicalAccessPathAllocationScope replacement = first.OpenAccessPath(accessPathId, Ordered, 1);
        PhysicalPageId replacementRoot = replacement.AllocatePage();
        replacement.ReleasePage(new(1));
        replacement.Complete(replacementRoot);
        first.Prepare();
        allocator.Publish(first.TransactionId, () =>
{

});

        PhysicalPageSpaceTransaction second = allocator.BeginTransaction(Guid.NewGuid());
        PhysicalAccessPathAllocationScope next = second.OpenAccessPath(accessPathId, Ordered, 2);

        Assert.AreEqual(4L, next.AllocatePage().Value);
    }

    [TestMethod]
    public void Published_safe_releases_are_available_to_a_later_replacement_workspace()
    {
        var accessPathId = new PhysicalAccessPathId(Guid.NewGuid());
        var current = new PhysicalAccessPathDirectoryEntry(accessPathId, Ordered, new(1), 1,
            [new(new(1), 2)]);
        var allocator = new PhysicalPageSpaceAllocator(new(1, [current]), [], 3);
        PhysicalPageSpaceTransaction transaction = allocator.BeginTransaction(Guid.NewGuid());
        PhysicalAccessPathAllocationScope replacement = transaction.OpenAccessPath(accessPathId, Ordered, 1);
        PhysicalPageId replacementRoot = replacement.AllocatePage();
        replacement.ReleasePage(new(1));
        replacement.Complete(replacementRoot);
        transaction.Prepare();
        allocator.Publish(transaction.TransactionId, () =>
{

});

        using PhysicalPageWorkspace workspace = allocator.BeginWorkspace(Guid.NewGuid());

        Assert.AreEqual(1L, workspace.AllocatePage().Value);
    }

    [TestMethod]
    public void Abandoned_routine_allocations_remain_quarantined_until_vacuum()
    {
        var allocator = new PhysicalPageSpaceAllocator(new(0, []), [], 0);
        PhysicalPageSpaceTransaction abandoned = allocator.BeginTransaction(Guid.NewGuid());
        PhysicalAccessPathAllocationScope abandonedScope = abandoned.OpenAccessPath(
            new(Guid.NewGuid()), Ordered, -1);
        PhysicalPageId allocated = abandonedScope.AllocatePage();
        abandonedScope.Complete(allocated);
        allocator.Abandon(abandoned.TransactionId);

        PhysicalPageSpaceTransaction retry = allocator.BeginTransaction(Guid.NewGuid());
        PhysicalAccessPathAllocationScope retryScope = retry.OpenAccessPath(
            new(Guid.NewGuid()), Ordered, -1);

        Assert.AreNotEqual(allocated, retryScope.AllocatePage());
    }

    [TestMethod]
    public void Reopened_vacuum_allocator_reconstructs_unowned_file_gaps()
    {
        var accessPathId = new PhysicalAccessPathId(Guid.NewGuid());
        var current = new PhysicalAccessPathDirectoryEntry(accessPathId, Ordered, new(2), 1,
            [new(new(2), 2)]);
        var allocator = new PhysicalPageSpaceAllocator(new(1, [current]), [new(5)], 7);
        PhysicalPageSpaceTransaction transaction = allocator.BeginTransaction(
            Guid.NewGuid(), preferLowestFreePage: true);
        PhysicalAccessPathAllocationScope scope = transaction.OpenAccessPath(accessPathId, Ordered, 1);

        Assert.AreEqual(1L, scope.AllocatePage().Value);
        Assert.AreEqual(4L, scope.AllocatePage().Value);
        Assert.AreEqual(6L, scope.AllocatePage().Value);
    }

    [TestMethod]
    public void Reopened_vacuum_allocator_prefers_the_lowest_gap_and_preserves_locality_within_it()
    {
        var accessPathId = new PhysicalAccessPathId(Guid.NewGuid());
        var current = new PhysicalAccessPathDirectoryEntry(accessPathId, Ordered, new(3), 1,
            [new(new(3), 2)]);
        var allocator = new PhysicalPageSpaceAllocator(new(1, [current]), [new(11)], 12);
        PhysicalPageSpaceTransaction transaction = allocator.BeginTransaction(
            Guid.NewGuid(), preferLowestFreePage: true);
        PhysicalAccessPathAllocationScope scope = transaction.OpenAccessPath(accessPathId, Ordered, 1);

        Assert.AreEqual(1L, scope.AllocatePage().Value);
        Assert.AreEqual(2L, scope.AllocatePage().Value);
    }

    [TestMethod]
    public void Directory_infrastructure_is_tail_allocated_and_cannot_compete_for_reusable_gaps()
    {
        var accessPathId = new PhysicalAccessPathId(Guid.NewGuid());
        var current = new PhysicalAccessPathDirectoryEntry(accessPathId, Ordered, new(3), 1,
            [new(new(3), 2)]);
        var allocator = new PhysicalPageSpaceAllocator(new(1, [current]), [], 8);
        PhysicalPageSpaceTransaction transaction = allocator.BeginTransaction(Guid.NewGuid());

        PhysicalPageId directoryBranch = transaction.AllocateDirectoryPage();
        PhysicalAccessPathAllocationScope scope = transaction.OpenAccessPath(accessPathId, Ordered, 1);
        PhysicalPageId reusable = scope.AllocatePage();

        Assert.AreEqual(8L, directoryBranch.Value);
        Assert.AreEqual(9L, reusable.Value);
    }

    [TestMethod]
    public void Replacement_workspace_reuses_only_gaps_proven_free_at_open()
    {
        var accessPathId = new PhysicalAccessPathId(Guid.NewGuid());
        var current = new PhysicalAccessPathDirectoryEntry(accessPathId, Ordered, new(3), 1,
            [new(new(3), 2)]);
        var allocator = new PhysicalPageSpaceAllocator(new(1, [current]), [], 8);
        using PhysicalPageWorkspace workspace = allocator.BeginWorkspace(Guid.NewGuid());

        PhysicalPageId shadow = workspace.AllocatePage();
        PhysicalPageSpaceTransaction foreground = allocator.BeginTransaction(Guid.NewGuid());
        PhysicalAccessPathAllocationScope scope = foreground.OpenAccessPath(accessPathId, Ordered, 1);
        PhysicalPageId live = scope.AllocatePage();

        Assert.AreEqual(1L, shadow.Value);
        Assert.AreEqual(8L, live.Value);
    }

    [TestMethod]
    public void Superseded_directory_branches_are_quarantined_after_publication()
    {
        var allocator = new PhysicalPageSpaceAllocator(new(1, []), [new(1)], 2);
        PhysicalPageSpaceTransaction first = allocator.BeginTransaction(Guid.NewGuid());
        PhysicalAccessPathAllocationScope firstScope = first.OpenAccessPath(
            new(Guid.NewGuid()), Ordered, -1);
        PhysicalPageId root = firstScope.AllocatePage();
        firstScope.Complete(root);
        first.ReleaseInfrastructurePages([new(1)]);
        first.Prepare();

        Assert.AreNotEqual(1L, root.Value);
        allocator.Publish(first.TransactionId, () =>
{

});

        PhysicalPageSpaceTransaction second = allocator.BeginTransaction(Guid.NewGuid());
        PhysicalAccessPathAllocationScope secondScope = second.OpenAccessPath(
            new(Guid.NewGuid()), Ordered, -1);
        Assert.AreEqual(3L, secondScope.AllocatePage().Value);
    }

    [TestMethod]
    public void Untouched_optional_scope_can_be_cancelled_before_directory_preparation()
    {
        var allocator = new PhysicalPageSpaceAllocator(new(0, []), [], 0);
        PhysicalPageSpaceTransaction transaction = allocator.BeginTransaction(Guid.NewGuid());
        PhysicalAccessPathAllocationScope optional = transaction.OpenAccessPath(
            new(Guid.NewGuid()), Ordered, -1);

        transaction.CancelAccessPath(optional);
        PhysicalAccessPathAllocationScope required = transaction.OpenAccessPath(
            new(Guid.NewGuid()), Ordered, -1);
        PhysicalPageId root = required.AllocatePage();
        required.Complete(root);

        Assert.HasCount(1, transaction.Prepare().Reservations);
    }

    [TestMethod]
    public void Workspace_allocation_coexists_with_a_foreground_transaction_without_collisions()
    {
        var allocator = new PhysicalPageSpaceAllocator(new(0, []), [], 0);
        using PhysicalPageWorkspace workspace = allocator.BeginWorkspace(Guid.NewGuid());
        PhysicalPageId shadow = workspace.AllocatePage();
        PhysicalPageSpaceTransaction foreground = allocator.BeginTransaction(Guid.NewGuid());
        PhysicalAccessPathAllocationScope scope = foreground.OpenAccessPath(
            new(Guid.NewGuid()), Ordered, -1);

        PhysicalPageId live = scope.AllocatePage();

        Assert.AreNotEqual(shadow, live);
    }

    [TestMethod]
    public void Completed_workspace_is_adopted_by_one_generation_validated_directory_transaction()
    {
        var accessPathId = new PhysicalAccessPathId(Guid.NewGuid());
        var current = new PhysicalAccessPathDirectoryEntry(accessPathId, Ordered, new(2), 4,
            [new(new(2), 2)]);
        var allocator = new PhysicalPageSpaceAllocator(new(7, [current]), [], 4);
        using PhysicalPageWorkspace workspace = allocator.BeginWorkspace(Guid.NewGuid());
        PhysicalPageId leaf = workspace.AllocatePage();
        PhysicalPageId root = workspace.AllocatePage();
        workspace.Complete(root);
        PhysicalPageSpaceTransaction cutover = allocator.BeginTransaction(Guid.NewGuid());

        cutover.OpenReplacementAccessPath(accessPathId, Ordered, 4, workspace);
        PhysicalPageSpaceStage staged = cutover.Prepare();
        allocator.Publish(cutover.TransactionId, () =>
{

});

        PhysicalAccessPathDirectoryEntry replacement = staged.Directory.Entries.Single();
        Assert.AreEqual(5L, replacement.Generation);
        Assert.AreEqual(root, replacement.RootPage);
        Assert.IsTrue(replacement.Extents.Any(extent => extent.Contains(leaf)));
        Assert.IsTrue(replacement.Extents.Any(extent => extent.Contains(new(2))));
    }

    [TestMethod]
    public void Adopted_workspace_canonicalizes_fragmented_extents_and_identifies_reused_pages()
    {
        var accessPathId = new PhysicalAccessPathId(Guid.NewGuid());
        var current = new PhysicalAccessPathDirectoryEntry(accessPathId, Ordered, new(1), 1,
            [new(new(1), 2)]);
        var allocator = new PhysicalPageSpaceAllocator(new(1, [current]), [], 3);
        PhysicalPageSpaceTransaction retiring = allocator.BeginTransaction(Guid.NewGuid());
        PhysicalAccessPathAllocationScope retiringScope = retiring.OpenAccessPath(accessPathId, Ordered, 1);
        PhysicalPageId liveRoot = retiringScope.AllocatePage();
        retiringScope.ReleasePage(new(1));
        retiringScope.Complete(liveRoot);
        retiring.Prepare();
        allocator.Publish(retiring.TransactionId, () =>
{

});
        using PhysicalPageWorkspace workspace = allocator.BeginWorkspace(Guid.NewGuid());
        PhysicalPageId reused = workspace.AllocatePage();
        PhysicalPageId tail = workspace.AllocatePage();
        workspace.Complete(tail);
        PhysicalPageSpaceTransaction adoption = allocator.BeginTransaction(Guid.NewGuid());

        adoption.OpenReplacementAccessPath(accessPathId, Ordered, 2, workspace);
        PhysicalPageSpaceStage stage = adoption.Prepare();

        Assert.AreEqual(new PhysicalPageId(1), reused);
        CollectionAssert.AreEqual(new[] { reused }, adoption.AdoptedReusedPages.ToArray());
        PhysicalAccessPathDirectoryEntry replacement = stage.Directory.Get(accessPathId);
        Assert.IsTrue(replacement.Extents.Any(extent => extent.Contains(reused)));
        Assert.IsTrue(replacement.Extents.Any(extent => extent.Contains(new(2))));
        Assert.IsTrue(replacement.Extents.Any(extent => extent.Contains(tail)));
    }

    [TestMethod]
    public void Abandoned_workspace_pages_become_reusable()
    {
        var allocator = new PhysicalPageSpaceAllocator(new(0, []), [], 0);
        PhysicalPageId abandoned;
        using (PhysicalPageWorkspace workspace = allocator.BeginWorkspace(Guid.NewGuid()))
        {
            abandoned = workspace.AllocatePage();
        }

        PhysicalPageSpaceTransaction transaction = allocator.BeginTransaction(
            Guid.NewGuid(), preferLowestFreePage: true);
        PhysicalAccessPathAllocationScope scope = transaction.OpenAccessPath(
            new(Guid.NewGuid()), Ordered, -1);
        Assert.AreEqual(abandoned, scope.AllocatePage());
    }

    [TestMethod]
    public void Workspace_adoption_requires_the_exact_allocator_owned_instance()
    {
        Guid workspaceId = Guid.NewGuid();
        var first = new PhysicalPageSpaceAllocator(new(0, []), [], 0);
        var second = new PhysicalPageSpaceAllocator(new(0, []), [], 0);
        using PhysicalPageWorkspace owned = first.BeginWorkspace(workspaceId);
        using PhysicalPageWorkspace foreign = second.BeginWorkspace(workspaceId);
        PhysicalPageId ownedRoot = owned.AllocatePage();
        owned.Complete(ownedRoot);
        PhysicalPageId foreignRoot = foreign.AllocatePage();
        foreign.Complete(foreignRoot);
        Assert.AreEqual(ownedRoot, foreignRoot);
        Guid rejectedId = Guid.NewGuid();
        PhysicalPageSpaceTransaction rejected = first.BeginTransaction(rejectedId);

        Assert.ThrowsException<PhysicalPageSpaceException>(() =>
            rejected.OpenReplacementAccessPath(new(Guid.NewGuid()), Ordered, -1, foreign));
        first.Abandon(rejectedId);

        PhysicalPageSpaceTransaction correctFirst = first.BeginTransaction(Guid.NewGuid());
        correctFirst.OpenReplacementAccessPath(new(Guid.NewGuid()), Ordered, -1, owned);
        PhysicalPageSpaceTransaction correctSecond = second.BeginTransaction(Guid.NewGuid());
        correctSecond.OpenReplacementAccessPath(new(Guid.NewGuid()), Ordered, -1, foreign);
    }

    [TestMethod]
    public async Task Tail_trim_rejects_an_unpublished_workspace()
    {
        string path = Path.Combine(Path.GetTempPath(), $"entityforge-page-space-{Guid.NewGuid():N}.pages");
        try
        {
            await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
            PhysicalPageSpace space = await PhysicalPageSpace.OpenAsync(pages);
            using PhysicalPageWorkspace workspace = space.BeginReplacementWorkspace(Guid.NewGuid());
            workspace.AllocatePage();

            await Assert.ThrowsAsync<PhysicalPageSpaceException>(async () =>
                await space.TrimUnownedTailAsync());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task Snapshot_operation_gate_defers_release_until_admitted_work_drains()
    {
        int releases = 0;
        var gate = new SnapshotOperationGate(() => Interlocked.Increment(ref releases));
        IDisposable admitted = gate.Enter(this);

        ValueTask closing = gate.CloseAsync();

        Assert.IsFalse(closing.IsCompleted);
        Assert.AreEqual(0, releases);
        Assert.ThrowsException<ObjectDisposedException>(() => gate.Enter(this));
        admitted.Dispose();
        await closing;
        Assert.AreEqual(1, releases);
        gate.Close();
        Assert.AreEqual(1, releases);
    }

    [TestMethod]
    public async Task Publication_keeps_trim_excluded_until_infrastructure_is_installed()
    {
        var allocator = new PhysicalPageSpaceAllocator(new(0, []), [], 0);
        Guid transactionId = Guid.NewGuid();
        PhysicalPageSpaceTransaction transaction = allocator.BeginTransaction(transactionId);
        PhysicalAccessPathAllocationScope scope = transaction.OpenAccessPath(
            new(Guid.NewGuid()), Ordered, -1);
        scope.Complete(scope.AllocatePage());
        transaction.Prepare();
        var enteredPublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishPublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task publication = Task.Run(() => allocator.Publish(transactionId, () =>
        {
            enteredPublication.SetResult();
            finishPublication.Task.GetAwaiter().GetResult();
        }));
        await enteredPublication.Task;

        Task<PhysicalPageSpaceTrimLease> trimming = Task.Run(allocator.BeginTrim);
        await Task.Delay(50);
        Assert.IsFalse(trimming.IsCompleted);
        finishPublication.SetResult();
        await publication;
        using PhysicalPageSpaceTrimLease lease = await trimming;
        Assert.AreEqual(1L, lease.Directory.Generation);
    }

    [TestMethod]
    public void Disposing_an_adopted_workspace_cannot_abandon_its_same_id_successor()
    {
        Guid workspaceId = Guid.NewGuid();
        var allocator = new PhysicalPageSpaceAllocator(new(0, []), [], 0);
        PhysicalPageWorkspace first = allocator.BeginWorkspace(workspaceId);
        first.Complete(first.AllocatePage());
        PhysicalPageSpaceTransaction adoption = allocator.BeginTransaction(Guid.NewGuid());
        adoption.OpenReplacementAccessPath(new(Guid.NewGuid()), Ordered, -1, first);
        PhysicalPageWorkspace successor = allocator.BeginWorkspace(workspaceId);
        PhysicalPageId successorRoot = successor.AllocatePage();

        first.Dispose();
        successor.Complete(successorRoot);

        Assert.AreEqual(successorRoot, successor.RootPage);
        successor.Dispose();
    }
}
