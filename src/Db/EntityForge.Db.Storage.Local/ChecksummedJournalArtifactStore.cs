using System.Security.Cryptography;
using EntityForge.Db;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Storage.Local;

internal sealed class ChecksummedJournalArtifactStore
{
    private const int ChecksumBytes = 32;
    private const int FileBufferSize = 4096;
    private const int StreamingWriteBufferSize = 64 * 1024;
    private const int ReplacementAttempts = 8;
    private const string TemporarySuffix = ".new";
    private static readonly TimeSpan InitialReplacementRetryDelay = TimeSpan.FromMilliseconds(10);
    private readonly string _directory;

    public ChecksummedJournalArtifactStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = System.IO.Path.GetFullPath(directory);
        DurableDirectory.Create(_directory);
    }

    public IEnumerable<string> EnumerateNames(string pattern) =>
        Directory.EnumerateFiles(_directory, pattern).Select(file => System.IO.Path.GetFileName(file));

    public bool Exists(string name)
    {
        FileAttributes attributes;
        try
        {
            // File.Exists suppresses access errors and reports directories as
            // absent. Optional recovery metadata must not disappear that way:
            // recovery may otherwise discard its referenced catalog deltas.
            attributes = File.GetAttributes(Path(name));
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new EntityForgeDbFormatException($"Change-journal artifact '{name}' is a directory, not a file.");
        }
        return true;
    }

    internal void SyncDirectory() => DurableDirectory.Sync(_directory);

    public async ValueTask WriteAsync(string name, byte[] payload, bool replace,
        CancellationToken cancellationToken)
    {
        string path = Path(name);
        string target = replace ? path + TemporarySuffix : path;
        byte[] content = Encode(payload);
        await using (var stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                         FileBufferSize, FileOptions.Asynchronous))
        {
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            stream.Flush(true);
        }
        if (replace)
        {
            await ReplaceAsync(target, path, cancellationToken).ConfigureAwait(false);
        }

        DurableDirectory.Sync(_directory);
    }

    public async ValueTask<JournalArtifactWriteResult> WriteStreamingAsync(string name, bool replace,
        Func<Stream, IncrementalHash, CancellationToken, ValueTask> writePayload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writePayload);
        string path = Path(name);
        string target = replace ? path + TemporarySuffix : path;
        bool published = false;
        try
        {
            byte[] checksum;
            await using (var stream = new FileStream(target, FileMode.Create, FileAccess.Write,
                             FileShare.None, StreamingWriteBufferSize,
                             FileOptions.Asynchronous))
            {
                using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    await writePayload(stream, hash, cancellationToken).ConfigureAwait(false);
                    checksum = hash.GetHashAndReset();
                    await stream.WriteAsync(checksum, cancellationToken).ConfigureAwait(false);
                    stream.Flush(true);
                }
            }
            if (replace)
            {
                await ReplaceAsync(target, path, cancellationToken).ConfigureAwait(false);
            }

            DurableDirectory.Sync(_directory);
            published = true;
            return new(new FileInfo(path).Length, Convert.ToHexString(checksum));
        }
        finally
        {
            if (!published)
            {
                try
                {
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                }
                catch (Exception)
                { /* A stale unpublished artifact is ignored during recovery. */
                }
            }
        }
    }

    public async ValueTask<long> VerifyRawAsync(string name, CancellationToken cancellationToken)
    {
        const int transferBufferBytes = 64 * 1024;
        await using FileStream stream = OpenRawRead(name);
        if (stream.Length <= ChecksumBytes)
        {
            throw new EntityForgeDbFormatException("A journal artifact is incomplete.");
        }

        long payloadLength = stream.Length - ChecksumBytes;
        long remaining = payloadLength;
        byte[] buffer = new byte[transferBufferBytes];
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (remaining != 0)
        {
            int requested = (int)Math.Min(buffer.Length, remaining);
            int read = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EntityForgeDbFormatException("A journal artifact is incomplete.");
            }

            hash.AppendData(buffer.AsSpan(0, read));
            remaining -= read;
        }
        byte[] expected = new byte[ChecksumBytes];
        await stream.ReadExactlyAsync(expected, cancellationToken).ConfigureAwait(false);
        if (!hash.GetHashAndReset().AsSpan().SequenceEqual(expected))
        {
            throw new EntityForgeDbFormatException("A journal artifact checksum is invalid.");
        }

        return payloadLength;
    }

    public FileStream OpenRawRead(string name) => new(Path(name), FileMode.Open, FileAccess.Read,
        FileShare.Read, FileBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public async ValueTask<byte[]> ReadAsync(string name, CancellationToken cancellationToken)
    {
        byte[] content = await ReadRawAsync(name, cancellationToken).ConfigureAwait(false);
        if (content.Length <= ChecksumBytes)
        {
            throw new EntityForgeDbFormatException("A journal artifact is incomplete.");
        }

        int payloadLength = content.Length - ChecksumBytes;
        if (!SHA256.HashData(content.AsSpan(0, payloadLength)).AsSpan()
                .SequenceEqual(content.AsSpan(payloadLength)))
        {
            throw new EntityForgeDbFormatException("A journal artifact checksum is invalid.");
        }

        return content.AsSpan(0, payloadLength).ToArray();
    }

    public ValueTask<byte[]> ReadRawAsync(string name, CancellationToken cancellationToken) =>
        new(File.ReadAllBytesAsync(Path(name), cancellationToken));

    public async ValueTask<long> CopyRawToAsync(string name, string destination,
        CancellationToken cancellationToken)
    {
        const int transferBufferBytes = 64 * 1024;
        await using var source = new FileStream(Path(name), FileMode.Open, FileAccess.Read,
            FileShare.Read, transferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write,
            FileShare.None, transferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(target, transferBufferBytes, cancellationToken).ConfigureAwait(false);
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        target.Flush(true);
        return source.Length;
    }

    public void EnsureExists(string name)
    {
        if (!Exists(name))
        {
            throw new EntityForgeDbFormatException($"Published change-journal artifact '{name}' is missing.");
        }
    }

    public void Delete(string name)
    {
        string path = Path(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void DeleteTemporaryArtifacts()
    {
        foreach (string temporary in Directory.EnumerateFiles(_directory, $"*{TemporarySuffix}"))
        {
            File.Delete(temporary);
        }
    }

    public static byte[] Encode(byte[] payload)
    {
        byte[] result = new byte[payload.Length + ChecksumBytes];
        payload.CopyTo(result, 0);
        SHA256.HashData(payload).CopyTo(result, payload.Length);
        return result;
    }

    private static async ValueTask ReplaceAsync(string source, string destination,
        CancellationToken cancellationToken)
    {
        TimeSpan delay = InitialReplacementRetryDelay;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException &&
                                              attempt < ReplacementAttempts)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay += delay;
            }
        }
    }

    private string Path(string name) => System.IO.Path.Combine(_directory, name);
}

internal readonly record struct JournalArtifactWriteResult(long Bytes, string Checksum);
