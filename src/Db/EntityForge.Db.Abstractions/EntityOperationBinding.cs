using System.Security.Cryptography;
using System.Text.Json;

namespace EntityForge.Db.Mapping;

// A database-owned request-to-plan binding. The plan is opaque and profile-specific.
internal enum EntityOperationSubmission
{
    Captured, Prepared
}

internal sealed record EntityOperationBinding(Guid OperationId, string RequestFingerprint, byte[] Plan,
    EntityOperationSubmission Submission = EntityOperationSubmission.Captured, bool Aborted = false, string? AbortedPlanDigest = null, EntityOperationCommitted? Committed = null);
