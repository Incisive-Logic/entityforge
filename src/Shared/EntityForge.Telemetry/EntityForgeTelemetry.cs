using System.Diagnostics;

namespace EntityForge.Telemetry;

/// <summary>
/// Vendor-neutral .NET diagnostics sources used by Entity Forge runtime code.
/// Listeners and exporters are configured by the hosting application.
/// </summary>
public static class EntityForgeTelemetry
{
    /// <summary>The ActivitySource name used for EntityForge tracing.</summary>
    public const string ActivitySourceName = "EntityForge";
    /// <summary>The activity tag key that identifies the telemetry level.</summary>
    public const string TelemetryLevelTagName = "entityforge.telemetry.level";

    /// <summary>Gets the shared activity source used by EntityForge instrumentation.</summary>
    public static ActivitySource ActivitySource
    {
        get;
    }
    =
        new(ActivitySourceName);

    private static int _maximumLevel = (int)TelemetryLevel.Background;

    /// <summary>
    /// Gets the most detailed telemetry level currently enabled.
    /// </summary>
    public static TelemetryLevel MaximumLevel =>
        (TelemetryLevel)Volatile.Read(ref _maximumLevel);

    /// <summary>
    /// Configures the most detailed telemetry level that runtime instrumentation may create.
    /// </summary>
    public static void ConfigureMaximumLevel(TelemetryLevel maximumLevel)
    {
        if (!Enum.IsDefined(maximumLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLevel));
        }

        Volatile.Write(ref _maximumLevel, (int)maximumLevel);
    }

    /// <summary>
    /// Returns whether instrumentation at the specified detail level is enabled.
    /// </summary>
    public static bool IsEnabled(TelemetryLevel telemetryLevel) =>
        (int)telemetryLevel <= Volatile.Read(ref _maximumLevel);

    /// <summary>Starts a traced operation when a listener requests it, using the supplied activity context and tags.</summary>
    public static Activity StartActivity(
        string operationName,
        ActivityKind kind = ActivityKind.Internal,
        TelemetryLevel telemetryLevel = TelemetryLevel.Background,
        string parentActivityId = null,
        IEnumerable<KeyValuePair<string, object>> tags = null,
        IEnumerable<ActivityLink> links = null)
    {
        if (!IsEnabled(telemetryLevel))
        {
            return null;
        }

        var activityTags = new ActivityTagsCollection
        {
            { TelemetryLevelTagName, telemetryLevel.ToString().ToLowerInvariant() }
        };

        if (tags != null)
        {
            foreach (KeyValuePair<string, object> tag in tags)
            {
                activityTags.Add(tag.Key, tag.Value);
            }
        }

        Activity activity;
        if (ActivityHelper.TryParseActivityContext(
            parentActivityId,
            out ActivityContext explicitParentContext))
        {
            activity = ActivitySource.CreateActivity(
                operationName,
                kind,
                explicitParentContext,
                activityTags,
                links);
        }
        else if (Activity.Current == null &&
            TelemetryPropagationContext.TryGetCurrent(
                out ActivityContext propagatedParentContext))
        {
            activity = ActivitySource.CreateActivity(
                operationName,
                kind,
                propagatedParentContext,
                activityTags,
                links);
        }
        else
        {
            activity = ActivitySource.CreateActivity(
                operationName,
                kind,
                parentId: null,
                tags: activityTags,
                links: links);
        }

        activity?.Start();
        return activity;
    }
}
