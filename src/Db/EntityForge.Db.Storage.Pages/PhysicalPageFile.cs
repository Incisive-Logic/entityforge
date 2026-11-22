using Microsoft.Win32.SafeHandles;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Storage.Pages;

/// <summary>Stores fixed-size physical pages in a randomly accessed local file.</summary>
/// <remarks>This layer performs byte I/O, not page-format validation or transactional publication.
/// Callers coordinate overlapping reads/writes, WAL ordering, resizing and disposal.
/// A completed write is not a durable commit; use the database durability coordinator.</remarks>
public sealed class PhysicalPageFile : IResizablePhysicalPageStore, IPhysicalPageTelemetrySource,
    IPhysicalPageOwnedReader
{
    private readonly SafeFileHandle _handle;
    private readonly SemaphoreSlim _lengthGate = new(1, 1);
    private bool _disposed;
    private readonly bool _ownsTelemetry;
    private readonly PhysicalPageWriteback? _writeback;

    private PhysicalPageFile(SafeFileHandle handle, PhysicalPageFileOptions options,
        PhysicalPageTelemetry telemetry, bool ownsTelemetry, string path)
    {
        _handle = handle;
        PageSize = options.PageSize;
        Telemetry = telemetry;
        _ownsTelemetry = ownsTelemetry;
        ImageCommitment = new(path);
        if (OperatingSystem.IsLinux() && Environment.Is64BitProcess)
        {
            _writeback = new((offset, length) => DurableFile.TryStartWriteback(handle, offset, length));
        }
    }

    /// <summary>Gets the fixed size of each page in bytes.</summary>
    public int PageSize
    {
        get;
    }

    /// <summary>Gets the image commitment that tracks writes and resizes performed through this instance.</summary>
    /// <remarks>External file modifications are not tracked by this object.</remarks>
    public PhysicalImageCommitment ImageCommitment
    {
        get;
    }

    internal PhysicalPageTelemetry Telemetry
    {
        get;
    }

    PhysicalPageTelemetry IPhysicalPageTelemetrySource.Telemetry => Telemetry;

    /// <summary>Opens an existing page file or creates a new one, creating its parent directory if needed.</summary>
    /// <param name="path">File path; resolved to an absolute path.</param>
    /// <param name="options">Page-format options, or the defaults when null.</param>
    /// <param name="cancellationToken">Checked before opening the file.</param>
    /// <returns>An owned page file that the caller must dispose.</returns>
    /// <remarks>Opens for read/write with read sharing. Opening alone does not validate existing page contents or file alignment.</remarks>
    /// <exception cref="PhysicalPageIoException">The file or parent directory cannot be opened or synchronized.</exception>
    public static ValueTask<PhysicalPageFile> OpenAsync(string path, PhysicalPageFileOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string fullPath = Path.GetFullPath(path);
            DurableDirectory.Create(Path.GetDirectoryName(fullPath)!);
            SafeFileHandle handle = File.OpenHandle(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess);
            try
            {
                DurableDirectory.Sync(Path.GetDirectoryName(fullPath)!);
                return ValueTask.FromResult(new PhysicalPageFile(handle, options ?? new(), new PhysicalPageTelemetry(), true, fullPath));
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PhysicalPageIoException("The physical page file could not be opened.", exception);
        }
    }

    /// <summary>Reads exactly one page into independently owned memory.</summary>
    /// <param name="pageId">Page whose byte offset is its ID multiplied by the page size.</param>
    /// <param name="cancellationToken">Cancels asynchronous reads.</param>
    /// <returns>A complete copy unaffected by later writes through this file instance.</returns>
    /// <exception cref="PhysicalPageCorruptionException">End of file is reached before a complete page is read.</exception>
    /// <exception cref="PhysicalPageIoException">The underlying read fails.</exception>
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(PhysicalPageId pageId,
        CancellationToken cancellationToken = default)
        => await ReadOwnedAsync(pageId, cancellationToken).ConfigureAwait(false);

    async ValueTask<byte[]> IPhysicalPageOwnedReader.ReadOwnedAsync(PhysicalPageId pageId,
        CancellationToken cancellationToken) => await ReadOwnedAsync(pageId, cancellationToken).ConfigureAwait(false);

    private async ValueTask<byte[]> ReadOwnedAsync(PhysicalPageId pageId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        byte[] page = GC.AllocateUninitializedArray<byte>(PageSize);
        try
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int total = 0;
            while (total < page.Length)
            {
                int read = await RandomAccess.ReadAsync(_handle, page.AsMemory(total),
                    checked((pageId.Value * (long)PageSize) + total), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new PhysicalPageCorruptionException(
                    $"Page {pageId.Value} ended after {total} of {PageSize} bytes.");
                }

                total += read;
            }
            Telemetry.Read(page.Length, System.Diagnostics.Stopwatch.GetElapsedTime(started));
            return page;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PhysicalPageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PhysicalPageIoException($"Page {pageId.Value} could not be read.", exception);
        }
    }

    /// <summary>Writes exactly one page, tracking the changed image range.</summary>
    /// <param name="pageId">Target physical page.</param>
    /// <param name="page">Exactly <see cref="PageSize"/> bytes, kept unchanged until the operation completes.</param>
    /// <param name="cancellationToken">Cancels the asynchronous file write.</param>
    /// <remarks>Does not validate page encoding or flush a durable commit. A failure or cancellation does not imply that no bytes changed.</remarks>
    /// <exception cref="ArgumentException">The input is not exactly one page.</exception>
    /// <exception cref="PhysicalPageIoException">The underlying write or writeback fails.</exception>
    public async ValueTask WriteAsync(PhysicalPageId pageId, ReadOnlyMemory<byte> page,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (page.Length != PageSize)
        {
            throw new ArgumentException("A physical write must contain exactly one page.", nameof(page));
        }

        try
        {
            _writeback?.ThrowIfFailed();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            using var mutation = ImageCommitment.BeginWrite(checked(pageId.Value * (long)PageSize), page.Length);
            await RandomAccess.WriteAsync(_handle, page, checked(pageId.Value * (long)PageSize), cancellationToken)
                .ConfigureAwait(false);
            if (_writeback is not null)
            {
                await _writeback.WrittenAsync(checked(pageId.Value * (long)PageSize), page.Length)
                    .ConfigureAwait(false);
            }

            Telemetry.Write(page.Length, System.Diagnostics.Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PhysicalPageIoException($"Page {pageId.Value} could not be written.", exception);
        }
    }

    /// <summary>Synchronizes file data using the platform durability primitive and checks writeback failures.</summary>
    /// <param name="cancellationToken">Checked before the synchronous durability call; does not interrupt it.</param>
    /// <remarks>Callers must coordinate concurrent mutations and required WAL durability before flushing pages.</remarks>
    /// <exception cref="PhysicalPageIoException">Durable synchronization or writeback fails.</exception>
    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            _writeback?.ThrowIfFailed();
            DurableFile.SyncData(_handle);
            _writeback?.ThrowIfFailed();
            Telemetry.Flush(System.Diagnostics.Stopwatch.GetElapsedTime(started));
            return ValueTask.CompletedTask;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PhysicalPageIoException("The physical page file could not be flushed durably.", exception);
        }
    }

    /// <summary>Reads the current file length as a count of complete pages.</summary>
    /// <param name="cancellationToken">Checked before reading the file length.</param>
    /// <returns>The aligned file length divided by <see cref="PageSize"/>.</returns>
    /// <exception cref="PhysicalPageCorruptionException">The file length is not a multiple of the page size.</exception>
    public ValueTask<long> GetPageCountAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            long length = RandomAccess.GetLength(_handle);
            if (length % PageSize != 0)
            {
                throw new PhysicalPageCorruptionException(
                    $"The physical page file length {length} is not aligned to {PageSize} bytes.");
            }

            return ValueTask.FromResult(length / PageSize);
        }
        catch (PhysicalPageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PhysicalPageIoException("The physical page-file length could not be read.", exception);
        }
    }

    /// <summary>Grows the file when needed to contain at least the requested number of pages.</summary>
    /// <param name="pageCount">Nonnegative minimum page count.</param>
    /// <param name="cancellationToken">Cancels waiting for the resize lock.</param>
    /// <remarks>Never shrinks the file. New space is not initialized as valid encoded pages, and the resize is not durably flushed here.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The page count is negative.</exception>
    /// <exception cref="OverflowException">The byte length cannot be represented.</exception>
    public async ValueTask EnsureCapacityAsync(long pageCount, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (pageCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageCount));
        }

        await _lengthGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long requestedLength = checked(pageCount * PageSize);
            long previousLength = RandomAccess.GetLength(_handle);
            if (previousLength < requestedLength)
            {
                using var mutation = ImageCommitment.BeginResize(previousLength, requestedLength);
                RandomAccess.SetLength(_handle, requestedLength);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PhysicalPageIoException("The physical page file could not be grown.", exception);
        }
        finally
        {
            _lengthGate.Release();
        }
    }

    /// <summary>Sets the file length to exactly the requested number of pages.</summary>
    /// <param name="pageCount">Nonnegative target page count.</param>
    /// <param name="cancellationToken">Cancels waiting for the resize lock.</param>
    /// <remarks>Removes trailing data when shrinking; a larger count extends the file.
    /// Does not initialize encoded pages or durably flush the resize. Callers coordinate ownership and recovery safety.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The page count is negative.</exception>
    /// <exception cref="OverflowException">The byte length cannot be represented.</exception>
    public async ValueTask TruncateAsync(long pageCount, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (pageCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageCount));
        }

        await _lengthGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long length = checked(pageCount * PageSize);
            using var mutation = ImageCommitment.BeginResize(RandomAccess.GetLength(_handle), length);
            RandomAccess.SetLength(_handle, length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PhysicalPageIoException("The physical page file could not be truncated.", exception);
        }
        finally
        {
            _lengthGate.Release();
        }
    }

    /// <summary>Closes the file, image commitment and owned telemetry once.</summary>
    /// <remarks>Does not call the durable flush operation. Complete active I/O and the required durability protocol before disposal.</remarks>
    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            ImageCommitment.Close();
            _handle.Dispose();
            _lengthGate.Dispose();
            if (_ownsTelemetry)
            {
                Telemetry.Dispose();
            }
        }
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
