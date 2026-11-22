namespace EntityForge.Db.Mapping;

internal static class EntityCapturedBatch
{
    internal static void ValidateCount(int count)
    {
        if (count is < 1 or > EntityRecordDeliveryCodec.MaximumBatchOperations)
        {
            throw new InvalidOperationException("The mapped batch is empty or exceeds its operation limit.");
        }
    }

    internal static EntityCapturedRequest.Metadata Validate(IReadOnlyList<EntityCapturedRequest> requests)
    {
        ValidateCount(requests.Count);
        var first = requests[0].Description;
        long bytes = 0;
        foreach (var request in requests)
        {
            var metadata = request.Description;
            if (metadata.DatabaseIncarnation != first.DatabaseIncarnation || metadata.OperationId != first.OperationId ||
                metadata.Manifest.Fingerprint != first.Manifest.Fingerprint || metadata.ExpectedVersion != first.ExpectedVersion)
            {
                throw new InvalidOperationException("A mapped batch must share one destination, operation, mapping and version guard.");
            }

            bytes += request.Export().Length;
            if (bytes > EntityRecordDeliveryCodec.MaximumPayloadBytes)
            {
                throw new InvalidOperationException("The captured batch exceeds its byte limit.");
            }
        }
        return first;
    }
}
