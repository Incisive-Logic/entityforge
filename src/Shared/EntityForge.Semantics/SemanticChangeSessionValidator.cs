namespace EntityForge.Semantics;

/// <summary>Stable diagnostic codes for structural semantic-session validation.</summary>
public static class SemanticSessionValidationCodes
{
    /// <summary>Start, page or close session identities disagree.</summary>
    public const string SessionMismatch = "semantic.session-mismatch";

    /// <summary>Pages are not contiguous and zero-based in submission order.</summary>
    public const string PageSequence = "semantic.page-sequence";

    /// <summary>A change identity appears more than once in the session.</summary>
    public const string DuplicateChange = "semantic.duplicate-change";

    /// <summary>A change's provenance identifies a different source system from the session.</summary>
    public const string SourceSystemMismatch = "semantic.source-system-mismatch";

    /// <summary>A source-snapshot session contains a change kind incompatible with complete source state.</summary>
    public const string SnapshotChangeKind = "semantic.snapshot-change-kind";

    /// <summary>Declared close counts disagree with the supplied pages or changes.</summary>
    public const string CountMismatch = "semantic.count-mismatch";

    /// <summary>The close time precedes the session start.</summary>
    public const string InvalidCloseTime = "semantic.invalid-close-time";
}

/// <summary>Checks structural consistency of a complete Semantic change-session envelope.</summary>
public sealed class SemanticChangeSessionValidator
{
    /// <summary>Checks session identities, contiguous page numbering, change uniqueness, source provenance and close metadata.</summary>
    /// <param name="start">The session start declaration.</param>
    /// <param name="pages">The complete page sequence in submission order.</param>
    /// <param name="close">The session close declaration.</param>
    /// <returns>All collected session-validation issues; this does not write changes or validate them against a vocabulary.</returns>
    /// <exception cref="ArgumentNullException">The start, pages or close argument is null.</exception>
    public SemanticValidationResult Validate(
        SemanticChangeSessionStart start,
        IEnumerable<SemanticChangeSessionPage> pages,
        SemanticChangeSessionClose close)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(close);
        var materializedPages = pages.ToArray();
        var issues = new List<SemanticValidationIssue>();

        if (close.SessionId != start.SessionId || materializedPages.Any(page => page.SessionId != start.SessionId))
        {
            issues.Add(Issue("sessionId", "Session envelope identifiers do not match.", SemanticSessionValidationCodes.SessionMismatch));
        }

        for (var index = 0; index < materializedPages.Length; index++)
        {
            if (materializedPages[index].PageNumber != index)
            {
                issues.Add(Issue(
                    $"pages[{index}].pageNumber",
                    "Semantic pages must be contiguous and zero-based.",
                    SemanticSessionValidationCodes.PageSequence));
            }
        }

        var changes = materializedPages.SelectMany(page => page.Changes).ToArray();
        if (changes.Select(change => change.ChangeId).Distinct().Count() != changes.Length)
        {
            issues.Add(Issue("pages", "A semantic session cannot repeat a change identifier.", SemanticSessionValidationCodes.DuplicateChange));
        }

        foreach (var change in changes.Where(change => !SameAbsoluteUri(
                     change.Provenance.SourceSystem,
                     start.Source.SourceSystem)))
        {
            issues.Add(new SemanticValidationIssue(
                "pages.changes.provenance.sourceSystem",
                "Change provenance must belong to the session source system.",
                SemanticSessionValidationCodes.SourceSystemMismatch,
                subject: change.Subject));
        }

        if (start.Kind is SemanticChangeSessionKind.SourceSnapshot)
        {
            foreach (var change in changes.Where(change => change.Kind is not SemanticEntityChangeKind.ReplaceSourceState))
            {
                issues.Add(new SemanticValidationIssue(
                    "pages.changes.kind",
                    "A source snapshot may contain only replace-source-state changes.",
                    SemanticSessionValidationCodes.SnapshotChangeKind,
                    subject: change.Subject));
            }
        }

        if (close.PageCount != materializedPages.Length || close.ChangeCount != changes.Length)
        {
            issues.Add(Issue("close", "Declared session counts do not match the supplied pages.", SemanticSessionValidationCodes.CountMismatch));
        }

        if (close.ClosedAt < start.StartedAt)
        {
            issues.Add(Issue("close.closedAt", "A semantic session cannot close before it starts.", SemanticSessionValidationCodes.InvalidCloseTime));
        }

        return new SemanticValidationResult(issues.AsReadOnly());
    }

    private static SemanticValidationIssue Issue(string path, string message, string code) =>
        new(path, message, code);

    private static bool SameAbsoluteUri(Uri left, Uri right) =>
        string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.Ordinal);
}
