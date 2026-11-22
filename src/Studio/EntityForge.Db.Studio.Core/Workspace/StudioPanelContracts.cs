using EntityForge.UI.Workspace;

namespace EntityForge.Db.Studio.Workspace;

public static class StudioPanelContracts
{
    public static PanelContract For(string moduleKey) => moduleKey switch
    {
        StudioModuleKeys.Query => new(null, ["query-results"]),
        StudioModuleKeys.Results => new("query-results", ["entity-selection"]) { SourceTypeLabel = "Query" },
        StudioModuleKeys.Evidence => new("entity-selection", []) { SourceTypeLabel = "Results" },
        _ => new(null, []),
    };
}
