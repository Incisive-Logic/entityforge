namespace EntityForge.Db.Mapping;

internal sealed record EntityPreparedHandle(Guid Id, Guid DatabaseIncarnation, Guid OperationId, DateTimeOffset ExpiresAtUtc);
