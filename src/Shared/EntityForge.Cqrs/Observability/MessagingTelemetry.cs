using System.Diagnostics;

using EntityForge.Telemetry;

namespace EntityForge.Cqrs.Observability;

internal static class MessagingTelemetry
{
    private static readonly ActivitySource _activitySource =
        new(ActivitySourceNames.ActivitySourceName);

    public static Activity StartProducerActivity(
        string name,
        TelemetryLevel level,
        string system,
        string protocol,
        string destination,
        int? messageCount = null)
    {
        if (!EntityForgeTelemetry.IsEnabled(level))
        {
            return null;
        }

        Activity activity = Activity.Current is null && TelemetryPropagationContext.TryGetCurrent(out var parent)
            ? _activitySource.StartActivity(name, ActivityKind.Producer, parent)
            : _activitySource.StartActivity(name, ActivityKind.Producer);
        activity?.SetTag(ActivityTagNames.MessagingSystem, system);
        activity?.SetTag(ActivityTagNames.MessagingProtocol, protocol);
        activity?.SetTag(ActivityTagNames.MessagingDestination, destination);
        activity?.SetTag(ActivityTagNames.MessagingDestinationKind, "topic");
        activity?.SetTag(ActivityTagNames.MessagingOperation, "publish");
        activity?.SetTag(ActivityTagNames.MessagingBatchMessageCount, messageCount);
        return activity;
    }

    public static void InjectCurrentContext(IEnumerable<IMessage> messages)
    {
        string activityId = TelemetryPropagationContext.GetCurrentActivityId();
        if (activityId == null)
        {
            return;
        }

        foreach (IMessage message in messages)
        {
            message.TelemetryActivityId = activityId;
        }
    }
}
