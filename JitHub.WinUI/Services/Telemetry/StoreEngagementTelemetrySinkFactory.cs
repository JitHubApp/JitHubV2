using System;
#if STORE_ENGAGEMENT_AVAILABLE
using Microsoft.Services.Store.Engagement;
using JitHub.Core.Services.Telemetry;
#endif

namespace JitHub.Services;

/// <summary>
/// Connects <see cref="StoreTelemetrySink"/> to the Microsoft Store engagement
/// logger. The Store Engagement framework ships only for the architectures the
/// app project defines <c>STORE_ENGAGEMENT_AVAILABLE</c> for, so the native
/// logger is resolved here, in the app, and the sink reports why it is
/// unavailable everywhere else.
/// </summary>
internal static class StoreEngagementTelemetrySinkFactory
{
    public static StoreTelemetrySink Create()
    {
#if STORE_ENGAGEMENT_AVAILABLE
        StoreServicesCustomEventLogger? logger;
        try
        {
            logger = StoreServicesCustomEventLogger.GetDefault();
        }
        catch (Exception exception)
        {
            return new StoreTelemetrySink(log: null, exception.GetType().Name);
        }

        return logger is null
            ? new StoreTelemetrySink(log: null, "store_engagement_logger_unavailable")
            : new StoreTelemetrySink(logger.Log, "available");
#else
        return new StoreTelemetrySink(log: null, "store_engagement_architecture_unavailable");
#endif
    }
}
