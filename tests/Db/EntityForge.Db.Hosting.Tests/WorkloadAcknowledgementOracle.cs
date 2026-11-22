using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EntityForge.Db.Hosting.Tests;

// Test-owned evidence only. Expected values come from acknowledged inputs,
// never from database reads. Checkpoint requires all client actors to be idle.
internal sealed class WorkloadAcknowledgementOracle : IDisposable
{
    internal enum Boundary
    {
        BeforeReplace,
        AfterReplace,
        BeforeDelete,
        AfterDelete
    }

    internal sealed record State(int Rows, int Clients, long Generation, long Acknowledged, int[] Values);

    private sealed record Snapshot(string Payload, string Sha256);

    private sealed record Entry(int Actor, int Key, int Value, Guid Operation);

    private const long MaximumJournalBytes = 8 * 1024 * 1024;
    private readonly string _directory;
    private readonly FileStream?[] _journals;
    private readonly State _state;
    private long _acknowledged;
    private bool _closed;

    internal WorkloadAcknowledgementOracle(string directory, int rows, int clients)
    {
        if (rows is < 1 or > 1_000_000 || clients is < 1 or > 32 || rows % clients != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rows));
        }
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        if (!File.Exists(Path.Combine(_directory, "state.json")))
        {
            if (Directory.EnumerateFileSystemEntries(_directory).Any())
            {
                throw new InvalidDataException("Oracle state is missing from a non-empty directory.");
            }
            Publish(new(rows, clients, 0, 0, new int[rows]), null);
        }
        _state = Recover(_directory);
        if (_state.Rows != rows || _state.Clients != clients)
        {
            throw new InvalidDataException("Oracle settings differ from its persisted state.");
        }
        _acknowledged = _state.Acknowledged;
        _journals = new FileStream[clients];
    }

    // Exactly one caller per actor; different actors own disjoint keys.
    internal void Append(int actor, int key, int value, Guid operation)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (actor < 0 || actor >= _state.Clients || key < 0 || key >= _state.Rows || key % _state.Clients != actor ||
            value != checked(_state.Values[key] + 1) || operation == Guid.Empty)
        {
            throw new InvalidDataException("Invalid acknowledged oracle operation.");
        }
        string name = (_state.Generation + 1).ToString("D10", CultureInfo.InvariantCulture) + "-" + actor.ToString("D2", CultureInfo.InvariantCulture) + ".jsonl";
        FileStream stream = _journals[actor] ??= new(Path.Combine(_directory, name), FileMode.Append, FileAccess.Write, FileShare.Read);
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Entry(actor, key, value, operation)) + "\n");
        if (stream.Length + bytes.Length > MaximumJournalBytes)
        {
            throw new IOException("Oracle journal budget exhausted; checkpoint at a quiescent window boundary.");
        }
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        _state.Values[key] = value;
        Interlocked.Increment(ref _acknowledged);
    }

    internal void Checkpoint(Action<Boundary>? observe = null)
    {
        Dispose();
        State recovered = Recover(_directory);
        if (recovered.Acknowledged != _acknowledged || !recovered.Values.SequenceEqual(_state.Values))
        {
            throw new InvalidDataException("Acknowledged journal differs from the independent input state.");
        }
        Publish(recovered with
        {
            Generation = checked(_state.Generation + 1)
        }, observe);
        // Construct a new oracle after checkpoint (or interruption). This avoids
        // continuing with an in-memory generation whose publication was ambiguous.
    }

    private void Publish(State state, Action<Boundary>? observe)
    {
        string payload = JsonSerializer.Serialize(state);
        var envelope = new Snapshot(payload, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
        string temporary = Path.Combine(_directory, "state.pending");
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, envelope);
            stream.Flush(flushToDisk: true);
        }
        observe?.Invoke(Boundary.BeforeReplace);
        File.Move(temporary, Path.Combine(_directory, "state.json"), overwrite: true);
        observe?.Invoke(Boundary.AfterReplace);
        observe?.Invoke(Boundary.BeforeDelete);
        foreach (string journal in Directory.EnumerateFiles(_directory, "*.jsonl"))
        {
            if (Generation(journal) <= state.Generation)
            {
                File.Delete(journal);
            }
        }
        observe?.Invoke(Boundary.AfterDelete);
    }

    internal static State Recover(string directory)
    {
        string path = Path.Combine(directory, "state.json");
        if (new FileInfo(path).Length > 32 * 1024 * 1024)
        {
            throw new InvalidDataException("Oracle snapshot exceeds its budget.");
        }
        Snapshot snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path)) ?? throw new InvalidDataException("Missing oracle snapshot.");
        if (snapshot.Sha256 != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.Payload))))
        {
            throw new InvalidDataException("Oracle snapshot checksum mismatch.");
        }
        State state = JsonSerializer.Deserialize<State>(snapshot.Payload) ?? throw new InvalidDataException("Missing oracle state.");
        if (state.Rows is < 1 or > 1_000_000 || state.Clients is < 1 or > 32 || state.Rows % state.Clients != 0 ||
            state.Values.Length != state.Rows || state.Generation < 0 || state.Acknowledged < 0)
        {
            throw new InvalidDataException("Invalid oracle snapshot shape.");
        }
        long acknowledged = state.Acknowledged;
        foreach (string journal in Directory.EnumerateFiles(directory, "*.jsonl").Order(StringComparer.Ordinal))
        {
            long generation = Generation(journal);
            if (generation <= state.Generation)
            {
                continue; // Already covered by the published snapshot.
            }
            if (generation != state.Generation + 1 || new FileInfo(journal).Length > MaximumJournalBytes)
            {
                throw new InvalidDataException("Invalid oracle journal generation or size.");
            }
            foreach (string line in File.ReadLines(journal))
            {
                Entry entry = JsonSerializer.Deserialize<Entry>(line) ?? throw new InvalidDataException("Missing oracle entry.");
                if (entry.Actor < 0 || entry.Actor >= state.Clients || entry.Key < 0 || entry.Key >= state.Rows ||
                    entry.Key % state.Clients != entry.Actor || entry.Value != checked(state.Values[entry.Key] + 1) || entry.Operation == Guid.Empty)
                {
                    throw new InvalidDataException("Invalid oracle journal sequence.");
                }
                state.Values[entry.Key] = entry.Value;
                acknowledged++;
            }
        }
        return state with
        {
            Acknowledged = acknowledged
        };
    }

    private static long Generation(string path) => Int64.Parse(Path.GetFileName(path).Split('-')[0], CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _closed = true;
        foreach (FileStream? journal in _journals)
        {
            journal?.Dispose();
        }
    }
}
