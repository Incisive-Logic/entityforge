using EntityForge.Db.Studio.Modules;
using EntityForge.UI.Workspace;

namespace EntityForge.Db.Studio.Workspace;

public static class StudioModuleKeys
{
    public const string Explorer = "database-explorer";
    public const string Vocabulary = "semantic-vocabulary";
    public const string Query = "query-editor";
    public const string Results = "query-results";
    public const string Evidence = "evidence-inspector";
    public const string Status = "database-status";
    public const string Storage = "storage-profile";
    public const string Telemetry = "telemetry";
    public const string Profiler = "query-profiler";
    public const string Optimizer = "semantic-optimizer";
    public const string Transactions = "transactions";
    public const string Security = "security";
    public const string Audit = "security-audit";
}

/// <summary>Creates the recoverable product default while leaving every tab user-editable.</summary>
public static class StudioWorkspaceDefaults
{
    public static StudioModuleRegistry CreateRegistry() => new(
    [
        Module(StudioModuleKeys.Explorer, "Database Browser", "▦", "Browse data structures and start queries.", StudioModuleCategory.Data),
        Module(StudioModuleKeys.Query, "Query", "⌘", "Profile-aware Semantic, GQL and SPARQL editor.", StudioModuleCategory.Data),
        Module(StudioModuleKeys.Results, "Results", "▤", "Entity documents, tables and native result sets.", StudioModuleCategory.Data),
        Module(StudioModuleKeys.Evidence, "Evidence", "◇", "Assertion provenance and source inspection.", StudioModuleCategory.Data),
        Module(StudioModuleKeys.Status, "Status", "●", "Readiness, topology and storage state.", StudioModuleCategory.Operations),
        Module(StudioModuleKeys.Storage, "Storage & Maintenance", "▥", "Checkpoints, verified backups and server storage diagnostics.", StudioModuleCategory.Operations),
        Module(StudioModuleKeys.Profiler, "Query Profiler", "⌁", "Inspect captured query traces and execution stages.", StudioModuleCategory.Operations),
        Module(StudioModuleKeys.Telemetry, "Telemetry", "⌁", "Retained history and live database metrics from OpenTelemetry instruments.", StudioModuleCategory.Operations),
        Module(StudioModuleKeys.Optimizer, "Semantic Optimizer", "◈", "Evidence-led physical property placement.", StudioModuleCategory.Operations),
        Module(StudioModuleKeys.Transactions, "Operation Lookup", "⇄", "Resolve a known application operation on this database.", StudioModuleCategory.Operations),
        Module(StudioModuleKeys.Security, "Security", "⬡", "Connection identity, permissions and TLS policy.", StudioModuleCategory.Security),
    ]);

    public static WorkspaceDocument Create() => new(
    [
        new WorkspaceTab("data", "Data", "database", DataLayout())
        {
            ResponsivePreferences = new(["data-query", "data-results", "data-browser"], "data-query")
        },
        new WorkspaceTab("operations", "Operations", "operations", OperationsLayout()),
        new WorkspaceTab("telemetry", "Telemetry", "operations", new(new WorkspaceModuleNode("telemetry-live", StudioModuleKeys.Telemetry))),
        new WorkspaceTab("profiler", "Query Profiler", "operations", new(new WorkspaceModuleNode("query-profiler", StudioModuleKeys.Profiler))),
        new WorkspaceTab("security", "Security", "security", SecurityLayout()),
    ], "data")
    {
        PanelConnections =
        [
            new(new("data", "data-results"), new("data", "data-query"))
        ]
    };

    private static WorkspaceLayout DataLayout() => new(new WorkspaceSplitNode("data-root",
        WorkspaceSplitOrientation.Vertical, 0.2,
        new WorkspaceModuleNode("data-browser", StudioModuleKeys.Explorer),
        new WorkspaceSplitNode("data-query-results", WorkspaceSplitOrientation.Vertical, 0.43,
            new WorkspaceModuleNode("data-query", StudioModuleKeys.Query),
            new WorkspaceModuleNode("data-results", StudioModuleKeys.Results))));

    private static WorkspaceLayout OperationsLayout() => new(new WorkspaceSplitNode("operations-root",
        WorkspaceSplitOrientation.Vertical, 0.34,
        new WorkspaceModuleNode("operations-status", StudioModuleKeys.Status),
        new WorkspaceSplitNode("operations-detail", WorkspaceSplitOrientation.Horizontal, 0.58,
            new WorkspaceModuleNode("operations-storage", StudioModuleKeys.Storage),
            new WorkspaceSplitNode("operations-administration", WorkspaceSplitOrientation.Vertical, 0.58,
                new WorkspaceModuleNode("operations-optimizer", StudioModuleKeys.Optimizer),
                new WorkspaceModuleNode("operations-transactions", StudioModuleKeys.Transactions)))));

    private static WorkspaceLayout SecurityLayout() => new(new WorkspaceModuleNode("security-policy", StudioModuleKeys.Security));

    private static StudioModuleDescriptor Module(string key, string name, string icon, string description,
        StudioModuleCategory category) => new(key, name, icon, description, category, RequiresConnection: true);
}
