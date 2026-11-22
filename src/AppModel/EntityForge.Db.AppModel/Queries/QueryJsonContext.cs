using System.Text.Json;
using System.Text.Json.Serialization;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.AppModel.Queries;

internal sealed record SnapshotLeaseRequest(int DurationSeconds, long? ExpectedRevision = null);

internal sealed record SnapshotLeaseResponse(Guid LeaseId, long SnapshotVersion, DateTimeOffset ExpiresAtUtc, long Revision);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SnapshotLeaseRequest))]
[JsonSerializable(typeof(SnapshotLeaseResponse))]
[JsonSerializable(typeof(EntityProjectionTransportResponse))]
internal sealed partial class QueryJsonContext : JsonSerializerContext;
