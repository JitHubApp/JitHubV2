using System;
using System.Reflection;

namespace JitHub.Services;

public interface IStoreTelemetrySink
{
    bool IsAvailable { get; }

    string AvailabilityStatus { get; }

    void TrackEvent(string name);
}

public sealed class StoreTelemetrySink : IStoreTelemetrySink
{
    private readonly object? _logger;
    private readonly MethodInfo? _logMethod;
    private readonly string _availabilityStatus;

    public StoreTelemetrySink()
    {
        try
        {
            Type? loggerType = Type.GetType(
                "Microsoft.Services.Store.Engagement.StoreServicesCustomEventLogger, Microsoft.Services.Store.Engagement",
                throwOnError: false);
            if (loggerType is null)
            {
                _availabilityStatus = "store_engagement_type_unavailable";
                return;
            }

            MethodInfo? defaultMethod = loggerType?.GetMethod("GetDefault", BindingFlags.Public | BindingFlags.Static);
            _logger = defaultMethod?.Invoke(null, null);
            _logMethod = loggerType?.GetMethod("Log", [typeof(string)]);
            _availabilityStatus = IsAvailable ? "available" : "store_engagement_logger_unavailable";
        }
        catch (Exception ex)
        {
            _logger = null;
            _logMethod = null;
            _availabilityStatus = ex.GetType().Name;
        }
    }

    public bool IsAvailable => _logger is not null && _logMethod is not null;

    public string AvailabilityStatus => _availabilityStatus;

    public void TrackEvent(string name)
    {
        if (!IsAvailable || !TelemetrySanitizer.IsStoreEventAllowed(name))
        {
            return;
        }

        try
        {
            _logMethod!.Invoke(_logger, [name]);
        }
        catch
        {
        }
    }
}
