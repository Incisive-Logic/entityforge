using System.Text.Json;

namespace EntityForge.Db.Mapping;

internal static class EntityPreparationToken
{
    private const int MaximumBytes = 2048;
    private const int FormatVersion = 1;

    private static ReadOnlySpan<byte> Prefix => "EFPREP01"u8;

    internal static bool IsToken(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Prefix);

    internal static byte[] Encode(EntityPreparedHandle handle, DatabaseProfileId profile) =>
        Prefix.ToArray().Concat(JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = FormatVersion,
            profile = profile.ToString(),
            id = handle.Id,
            database = handle.DatabaseIncarnation,
            operation = handle.OperationId,
            expires = handle.ExpiresAtUtc
        })).ToArray();

    internal static EntityPreparedHandle Decode(byte[] bytes, Guid database, Guid operation, DatabaseProfileId profile)
    {
        try
        {
            if (!IsToken(bytes) || bytes.Length > MaximumBytes)
            {
                throw new InvalidDataException("Invalid retained preparation token.");
            }

            using var document = JsonDocument.Parse(bytes.AsMemory(Prefix.Length));
            var value = document.RootElement;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in value.EnumerateObject())
            {
                if (!names.Add(member.Name))
                {
                    throw new InvalidDataException("Duplicate token member.");
                }
            }

            if (names.Count != 6 || value.GetProperty("version").GetInt32() != FormatVersion || value.GetProperty("profile").GetString() != profile.ToString())
            {
                throw new InvalidDataException("Invalid preparation token framing.");
            }

            var handle = new EntityPreparedHandle(value.GetProperty("id").GetGuid(), value.GetProperty("database").GetGuid(),
                value.GetProperty("operation").GetGuid(), value.GetProperty("expires").GetDateTimeOffset());
            if (database == Guid.Empty || operation == Guid.Empty || handle.Id == Guid.Empty || handle.DatabaseIncarnation != database ||
                handle.OperationId != operation || handle.ExpiresAtUtc == default || handle.ExpiresAtUtc.Offset != TimeSpan.Zero)
            {
                throw new InvalidDataException("The preparation token differs from its destination or operation.");
            }

            return handle;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new InvalidDataException("Malformed preparation token.", error);
        }
    }
}
