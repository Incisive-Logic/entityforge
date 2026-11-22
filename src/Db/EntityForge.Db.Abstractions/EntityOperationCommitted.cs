using System.Security.Cryptography;

namespace EntityForge.Db.Mapping;

internal sealed record EntityOperationCommitted(int FormatVersion, string EnvelopeDigest,
    string PreparedFingerprint, string NativeCodecId, int NativeCodecVersion, string NativeHash,
    DatabaseCommitReceipt Receipt, byte[] ProfileReceipt)
{
    internal const int CurrentFormat = 1;

    internal static EntityOperationCommitted Create(byte[] envelope, byte[] preparedIdentity,
        string codecId, int codecVersion, byte[] nativePayload, DatabaseCommitReceipt receipt,
        byte[]? profileReceipt = null) => new(CurrentFormat, Digest(envelope), Digest(preparedIdentity),
            codecId, codecVersion, Digest(nativePayload), receipt, profileReceipt ?? []);

    internal static string Digest(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value));

    internal bool Equivalent(EntityOperationCommitted other) =>
        other is not null && FormatVersion == other.FormatVersion && EnvelopeDigest == other.EnvelopeDigest &&
        PreparedFingerprint == other.PreparedFingerprint && NativeCodecId == other.NativeCodecId &&
        NativeCodecVersion == other.NativeCodecVersion && NativeHash == other.NativeHash &&
        Receipt == other.Receipt && (ProfileReceipt is null ? other.ProfileReceipt is null :
            other.ProfileReceipt is not null && ProfileReceipt.AsSpan().SequenceEqual(other.ProfileReceipt));

    internal DatabaseCommitReceipt ReplayReceipt() => new(Receipt.DatabaseIncarnation, Receipt.TransactionId,
        Receipt.Profile, Receipt.Version, DatabaseCommitOutcome.IdempotentReplay, Receipt.ChangeCursor, Receipt.EffectCount);
}
