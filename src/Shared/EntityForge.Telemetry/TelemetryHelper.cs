using EntityForge.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EntityForge.Telemetry
{
    /// <summary>Converts and attaches trace identity information to activities.</summary>
    public static class ActivityHelper
    {
        private const string OpenTelemetryStatusCode = "otel.status_code";
        private const string OpenTelemetryStatusDescription = "otel.status_description";

        /// <summary>Marks an activity as failed and attaches the supplied failure details.</summary>
        public static void SetError(this Activity activity, string error)
        {
            if (activity == null)
            {
                return;
            }

            activity.SetStatus(ActivityStatusCode.Error, error);
            activity.SetTag(OpenTelemetryStatusCode, "ERROR");
            activity.SetTag(OpenTelemetryStatusDescription, error);
        }

        /// <summary>Attempts to parse propagated trace information into an activity context.</summary>
        public static bool TryParseActivityContext(
            string activityId,
            out ActivityContext activityContext)
        {
            return ActivityContext.TryParse(
                activityId,
                traceState: null,
                isRemote: true,
                out activityContext);
        }

        /// <summary>Captures an activity's trace and span identity in a transportable value.</summary>
        public static ITelemetryActivityInfo AsActivityInfo(this Activity activity)
        {
            return ActivityInfo.Create(activity?.TraceId, activity?.SpanId);
        }

        /// <summary>Attaches the supplied trace identity information to the activity context.</summary>
        public static void SetActivityInfo(this Activity activity, ITelemetryActivityInfo activityInfo)
        {
            if (activity == null)
            {
                return;
            }

            activityInfo.TraceId = activity.TraceId;
            activityInfo.SpanId = activity.SpanId;
        }

        /// <summary>Creates an activity link from the supplied trace identity information.</summary>
        public static ActivityLink? AsActivityLink(this ITelemetryActivityInfo activityInfo)
        {
            if (!activityInfo.TraceId.HasValue || !activityInfo.SpanId.HasValue)
            {
                return null;
            }

            return new ActivityLink(new ActivityContext(activityInfo.TraceId.Value, activityInfo.SpanId.Value, ActivityTraceFlags.None));
        }
    }

    /// <summary>Classifies the importance or verbosity of telemetry events.</summary>
    public enum TelemetryLevel
    {
        /// <summary>
        /// Telemetry that is sufficiently infrequent so that it can be run for normal everyday health monitoring
        /// </summary>
        Background,

        /// <summary>
        /// For more detailed telemetry that is not turned on normally, but does not necessarily impede performance
        /// </summary>
        Detailed,

        /// <summary>
        /// Highly detailed and frequent telemetry only for temporary profiling, where performance is impeded
        /// </summary>
        Deep
    }

    /// <summary>Exposes trace and span identifiers for propagating activity identity.</summary>
    public interface ITelemetryActivityInfo
    {
        /// <summary>Gets the distributed trace identifier.</summary>
        ActivityTraceId? TraceId
        {
            get; set;
        }

        /// <summary>Gets the span identifier within the trace.</summary>
        ActivitySpanId? SpanId
        {
            get; set;
        }
    }

    /// <summary>Carries trace and span identity independently of a live activity instance.</summary>
    public class ActivityInfo : ITelemetryActivityInfo
    {
        /// <summary>Creates trace identity information from the supplied trace and span identifiers.</summary>
        protected ActivityInfo()
        {
        }

        /// <summary>Creates activity identity information from the supplied tracing context.</summary>
        public static ITelemetryActivityInfo Create(ActivityTraceId? traceId, ActivitySpanId? spanId)
        {
            if (!traceId.HasValue)
            {
                return null;
            }

            return new ActivityInfo() { TraceId = traceId, SpanId = spanId };
        }

        /// <summary>Gets the distributed trace identifier.</summary>
        public ActivityTraceId? TraceId
        {
            get; set;
        }

        /// <summary>Gets the span identifier.</summary>
        public ActivitySpanId? SpanId
        {
            get; set;
        }
    }

    //public static class TelemetryHelper
    //{

    //    public static bool IsActive;
    //    public static TelemetryLevel MaximumTelemetryLevel;

    //    private static ActivitySource _ActivitySource;
    //    private static Meter _MeterSource;

    //    static TelemetryHelper()
    //    {
    //        // Note: For future-proofing, use W3C format IDs as these are more likely
    //        // to be supported by other components (as opposed to old .NET hierarchy IDs)
    //        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
    //        Activity.ForceDefaultIdFormat = true;
    //    }

    //    public static void StartTraceListener(string serviceName)
    //    {
    //        ArgumentException.ThrowIfNullOrEmpty(serviceName, nameof(serviceName));

    //        if (IsActive) return;

    //        _ActivitySource = new ActivitySource(serviceName);

    //        var activityListener = new ActivityListener()
    //        {
    //            ShouldListenTo = (s) => true,
    //        };

    //        ActivitySource.AddActivityListener(activityListener);
    //    }

    //    public static void StartMeterListener(string serviceName)
    //    {
    //        ArgumentException.ThrowIfNullOrEmpty(serviceName, nameof(serviceName));

    //        if (IsActive) return;

    //        _MeterSource = new Meter(serviceName);
    //    }

    //    public static Activity StartActivity(string operationName, string methodName, TelemetryLevel telemetryLevel,
    //        string[] linkedActivityIds = null, string parentActivityId = null,
    //        (string, object)[] tags = null, ActivityKind kind = ActivityKind.Internal)
    //    {
    //        if (!IsActive) return null;      // No telemetry active - just return out

    //        if (telemetryLevel > MaximumTelemetryLevel) return null;        // Beyond maximum telemetry level - just return out

    //        ActivityTagsCollection defaultTags = new ActivityTagsCollection();
    //        defaultTags.Add("Method Name", methodName);

    //        if (tags != null)
    //        {
    //            foreach (var tag in tags)
    //            {
    //                defaultTags.Add(tag.Item1, tag.Item2);
    //            }
    //        }

    //        Activity activity = _ActivitySource.CreateActivity(
    //            operationName,
    //            kind,
    //            parentId: parentActivityId,
    //            tags: defaultTags,
    //            links: linkedActivityIds?.Where(l => ActivityContext.TryParse(l, null, out ActivityContext context)).Select(l => new ActivityLink(ActivityContext.Parse(l, null))));

    //        if (activity == null) return null;

    //        // Start the activity
    //        activity.Start();

    //        return activity;
    //    }
    //}
}
