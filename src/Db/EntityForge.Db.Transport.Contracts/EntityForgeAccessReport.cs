namespace EntityForge.Db.Transport.Contracts;

/// <summary>Secret-free observation of the authority for the current HTTP credential. Null means unreported.</summary>
/// <param name="Version">Version of the access-report transport framing.</param>
/// <param name="Authentication">The authentication mechanism reported by the host.</param>
/// <param name="Role">Reported role, or null when the host does not disclose one.</param>
/// <param name="CanRead">Whether read access is reported; null means unknown.</param>
/// <param name="CanWrite">Whether write access is reported; null means unknown.</param>
/// <param name="CanAdminister">Whether maintenance authority is reported; null means unknown.</param>
/// <param name="IsHttps">Whether the host observed an HTTPS request.</param>
/// <param name="DatabaseIncarnation">Identity of the database against which access was inspected.</param>
public sealed record EntityForgeAccessReport(int Version, string Authentication, string? Role,
    bool? CanRead, bool? CanWrite, bool? CanAdminister, bool IsHttps, Guid DatabaseIncarnation);
