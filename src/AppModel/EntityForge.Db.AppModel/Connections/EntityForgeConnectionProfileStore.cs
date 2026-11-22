using System.Text.Json;

namespace EntityForge.Db.AppModel.Connections;

/// <summary>Persists reusable connection addresses and metadata without bearer credentials.</summary>
public interface IEntityForgeConnectionProfileStore
{
    /// <summary>Loads saved connection profiles; an absent store may return an empty list.</summary>
    ValueTask<IReadOnlyList<EntityForgeConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists normalized connection profiles with unique identifiers.</summary>
    ValueTask SaveAsync(IReadOnlyList<EntityForgeConnectionProfile> profiles, CancellationToken cancellationToken = default);
}

/// <summary>Persists reusable host addresses only; bearer credentials never cross this boundary.</summary>
public sealed class JsonEntityForgeConnectionProfileStore(string path) : IEntityForgeConnectionProfileStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<EntityForgeConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        await using FileStream stream = File.OpenRead(path);
        EntityForgeConnectionProfile[] profiles = await JsonSerializer.DeserializeAsync<EntityForgeConnectionProfile[]>(
            stream, Options, cancellationToken).ConfigureAwait(false) ?? [];
        return profiles.Select(profile => profile.Normalize()).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(IReadOnlyList<EntityForgeConnectionProfile> profiles,
        CancellationToken cancellationToken = default)
    {
        EntityForgeConnectionProfile[] normalized = profiles.Select(profile => profile.Normalize()).ToArray();
        if (normalized.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new ArgumentException("Connection profile identifiers must be unique.", nameof(profiles));
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string pending = path + ".pending";
            await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(normalized, Options), cancellationToken)
                .ConfigureAwait(false);
            File.Move(pending, path, true);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
