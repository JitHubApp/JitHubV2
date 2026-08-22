using System;
#if STORE_ENGAGEMENT_AVAILABLE
using Microsoft.Services.Store.Engagement;
#endif

namespace JitHub.Services;

public interface IStoreTelemetrySink
{
    bool IsAvailable { get; }

    string AvailabilityStatus { get; }

    void TrackEvent(string name);
}

public sealed class StoreTelemetrySink : IStoreTelemetrySink
{
#if STORE_ENGAGEMENT_AVAILABLE
    private readonly StoreServicesCustomEventLogger? _logger;
#endif
    private readonly string _availabilityStatus;

    public StoreTelemetrySink()
    {
#if STORE_ENGAGEMENT_AVAILABLE
        try
        {
            _logger = StoreServicesCustomEventLogger.GetDefault();
            _availabilityStatus = _logger is null
                ? "store_engagement_logger_unavailable"
                : "available";
        }
        catch (Exception exception)
        {
            _logger = null;
            _availabilityStatus = exception.GetType().Name;
        }
#else
        _availabilityStatus = "store_engagement_architecture_unavailable";
#endif
    }

    public bool IsAvailable
    {
        get
        {
#if STORE_ENGAGEMENT_AVAILABLE
            return _logger is not null;
#else
            return false;
#endif
        }
    }

    public string AvailabilityStatus => _availabilityStatus;

    public void TrackEvent(string name)
    {
        if (!TelemetrySanitizer.IsStoreEventAllowed(name))
        {
            return;
        }

#if STORE_ENGAGEMENT_AVAILABLE
        try
        {
            _logger?.Log(name);
        }
        catch
        {
        }
#endif
    }
}
