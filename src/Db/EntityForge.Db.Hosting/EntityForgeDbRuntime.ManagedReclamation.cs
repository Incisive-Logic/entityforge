using System.Security.Cryptography;
using System.Text.Json;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Hosting;

internal sealed partial class EntityForgeDbRuntime
{
    private static readonly System.Diagnostics.DiagnosticListener ReclamationDiagnostics = new("EntityForge.Db.Hosting.ManagedReclamation");

    private static void ObserveReclamation(string stage, int deletedFiles = 0)
    {
        if (ReclamationDiagnostics.IsEnabled(stage))
        {
            ReclamationDiagnostics.Write(stage, deletedFiles);
        }
    }

    private sealed record ManagedRetirement(int Format, Guid StoreId, Guid Incarnation,
        string Wal, string Checkpoint, string Manifest, string Pages, string Journal);

    private sealed record ManagedRetirementEnvelope(ManagedRetirement Retirement, string Sha256);

    internal sealed record ManagedReclamationResult(Guid StoreId, Guid Incarnation, int DeletedFiles, long DeletedBytes);

    private const int MaximumRetirementFiles = 65536;

    internal async Task<ManagedReclamationResult> ReclaimManagedGqlStoreAsync(Guid storeId, Guid incarnation,
        CancellationToken cancellationToken = default)
    {
        await _gqlMutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GqlManagedCatalog.Entry entry;
            string directory;
            lock (_gqlGraphGate)
            {
                RequireManagedGraphHost();
                var snapshot = (_managedCatalog ?? throw new InvalidOperationException("No managed graph catalog is open.")).InspectRetention();
                entry = snapshot.Retired.SingleOrDefault(item => item.StoreId == storeId && item.Incarnation == incarnation)
                    ?? throw new ArgumentException("Reclamation requires an exact retired store identity.");
                directory = ManagedStorePath(_managedRoot!, storeId);
            }
            var expected = RetirementFor(entry);
            ValidateRetirementNames(expected);
            string owner = Path.Combine(directory, expected.Wal + ".owner");
            RequireRegularRetirementFile(owner);
            // Never create or unlink the ownership file, including on retries.
            using var ownership = new FileStream(owner, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (LocalDatabaseRetirement.Exists(directory))
            {
                ReadRetirement(directory, expected);
            }
            else
            {
                if (await InspectRetiredIdentityAsync(directory, entry, cancellationToken).ConfigureAwait(false) != "retired-verified")
                {
                    throw new InvalidDataException("The retired store identity cannot be verified.");
                }
            }
            // Validate the complete bounded file set before publishing intent or
            // deleting anything. Unrecognised operator files are never swept up.
            var files = RetirementPayloads(directory, expected, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!LocalDatabaseRetirement.Exists(directory))
            {
                string marker = Path.Combine(directory, LocalDatabaseRetirement.FileName);
                string temporary = marker + ".new";
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ManagedRetirementEnvelope(expected, RetirementDigest(expected)));
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(true);
                    ObserveReclamation("marker-flushed");
                }
                File.Move(temporary, marker, overwrite: false);
                ObserveReclamation("marker-published");
            }
            // Also persist a marker left by an interrupted publication before
            // any payload deletion. A failed sync cannot authorize deletion.
            DurableDirectory.Sync(directory);
            ObserveReclamation("intent-synced");
            int deleted = 0;
            long deletedBytes = 0;
            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.GetFileName(file) == LocalDatabaseRetirement.FileName + ".new" && !File.Exists(file))
                {
                    continue;
                }
                RequireRegularRetirementFile(file);
                long bytes = new FileInfo(file).Length;
                File.Delete(file);
                deleted++;
                deletedBytes = checked(deletedBytes + bytes);
                ObserveReclamation("payload-deleted", deleted);
            }
            string journal = Path.Combine(directory, expected.Journal);
            if (Directory.Exists(journal))
            {
                RejectManagedLinks(journal);
                Directory.Delete(journal, recursive: false);
                ObserveReclamation("journal-removed", deleted);
            }
            DurableDirectory.Sync(directory);
            ObserveReclamation("complete", deleted);
            return new(storeId, incarnation, deleted, deletedBytes);
        }
        finally
        {
            _gqlMutation.Release();
        }
    }

    private ManagedRetirement RetirementFor(GqlManagedCatalog.Entry entry) => new(1, entry.StoreId, entry.Incarnation,
        _options.CommitLogFileName, _options.CheckpointFileName, _options.ManifestFileName,
        _options.PageFileName, _options.ChangeJournalFileName);

    private async Task<bool> HasManagedStoreCapacityAsync(string root, GqlManagedCatalog.RetentionSnapshot snapshot, CancellationToken cancellationToken)
    {
        var retired = snapshot.Retired.ToDictionary(entry => entry.StoreId);
        int total = 0, payloads = 0;
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The retained identity registry bounds known store generations.
            if (++total > GqlManagedCatalog.MaximumStoreIdentities + 64)
            {
                return false;
            }
            string name = Path.GetFileName(directory);
            if (!new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                Guid.TryParseExact(name, "N", out Guid id) && name == id.ToString("N") &&
                retired.TryGetValue(id, out var entry) && LocalDatabaseRetirement.Exists(directory) &&
                await InspectRetiredStoreAsync(directory, entry, cancellationToken).ConfigureAwait(false) == "reclaimed")
            {
                continue;
            }
            if (++payloads >= 64)
            {
                return false;
            }
        }
        return true;
    }

    private static string RetirementDigest(ManagedRetirement value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private static void ValidateRetirementNames(ManagedRetirement marker)
    {
        string[] names = [marker.Wal, marker.Wal + ".owner", marker.Wal + ".checkpoint-replacement",
            marker.Checkpoint, marker.Checkpoint + ".new", marker.Manifest, marker.Manifest + ".new",
            marker.Pages, marker.Journal, LocalDatabaseRetirement.FileName, LocalDatabaseRetirement.FileName + ".new"];
        if (names.Any(name => Path.GetFileName(name) != name || Path.IsPathRooted(name)) ||
            names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
        {
            throw new InvalidDataException("The artifact layout is unsafe for retirement.");
        }
    }

    private static void ReadRetirement(string directory, ManagedRetirement expected)
    {
        string path = Path.Combine(directory, LocalDatabaseRetirement.FileName);
        RequireRegularRetirementFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > 4096)
        {
            throw new InvalidDataException("Invalid retirement metadata length.");
        }
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        try
        {
            var envelope = JsonSerializer.Deserialize<ManagedRetirementEnvelope>(bytes);
            if (envelope?.Retirement != expected || envelope.Sha256 != RetirementDigest(expected))
            {
                throw new InvalidDataException("The retirement record does not match the retired store and artifact layout.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid retirement metadata.", exception);
        }
    }

    private static void RequireRegularRetirementFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new InvalidDataException("Retirement cannot traverse links or unexpected directories.");
        }
    }

    private static List<string> RetirementPayloads(string directory, ManagedRetirement marker, CancellationToken cancellationToken)
    {
        ValidateRetirementNames(marker);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            marker.Wal, marker.Wal + ".checkpoint-replacement", marker.Checkpoint, marker.Checkpoint + ".new",
            marker.Manifest, marker.Manifest + ".new", marker.Pages, LocalDatabaseRetirement.FileName + ".new"
        };
        var result = new List<string>();
        foreach (string path in Directory.EnumerateFileSystemEntries(directory).Take(32))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);
            if (name == marker.Journal)
            {
                RejectManagedLinks(path);
                if (!Directory.Exists(path))
                {
                    throw new InvalidDataException("The change journal is not a directory.");
                }
                foreach (string artifact in Directory.EnumerateFileSystemEntries(path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (result.Count >= MaximumRetirementFiles || !IsRetirementJournalFile(Path.GetFileName(artifact)))
                    {
                        throw new InvalidDataException("The journal contains unknown artifacts or exceeds the bounded retirement inventory.");
                    }
                    RequireRegularRetirementFile(artifact);
                    result.Add(artifact);
                }
            }
            else
            {
                RequireRegularRetirementFile(path);
                if (name == marker.Wal + ".owner" || name == LocalDatabaseRetirement.FileName)
                {
                    continue;
                }
                if (!allowed.Contains(name))
                {
                    throw new InvalidDataException("The retired store contains unknown artifacts.");
                }
                result.Add(path);
            }
        }
        // There are fewer than 32 recognised top-level names. Refuse any flood,
        // including one whose first page happens to consist of recognised names.
        if (Directory.EnumerateFileSystemEntries(directory).Take(32).Count() >= 32)
        {
            throw new InvalidDataException("The retired store contains too many artifacts.");
        }
        return result;
    }

    private static bool IsRetirementJournalFile(string name)
    {
        if (name.EndsWith(".new", StringComparison.Ordinal))
        {
            name = name[..^4];
        }
        if (name is "manifest.efcm" or "catalog-head.efch" or "publication.efcp")
        {
            return true;
        }
        static bool Digits(ReadOnlySpan<char> value) => value.Length == 20 && value.IndexOfAnyExceptInRange('0', '9') < 0;
        return name.Length == 46 && name[20] == '-' && name.EndsWith(".efcb", StringComparison.Ordinal) &&
                Digits(name.AsSpan(0, 20)) && Digits(name.AsSpan(21, 20)) ||
            name.Length == 33 && name.StartsWith("catalog-", StringComparison.Ordinal) &&
                name.EndsWith(".efcd", StringComparison.Ordinal) && Digits(name.AsSpan(8, 20));
    }
}
