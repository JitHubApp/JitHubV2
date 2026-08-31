using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace JitHub.WinUI.Helpers;

internal static class AppMotionTokens
{
    private const string MediumDurationResourceKey = "AppMediumDuration";
    private const int MaximumMergedDictionaryDepth = 32;
    private static readonly TimeSpan MediumDurationFallback = TimeSpan.FromMilliseconds(180);
    private static readonly Point ShyHeaderOpacityTransitionProgress = new(0.3, 0.3);
    private static long _mediumDurationTicks;
    private static int _resolutionFailureReported;

    public static TimeSpan MediumDuration
    {
        get
        {
            long cachedTicks = Interlocked.Read(ref _mediumDurationTicks);
            if (cachedTicks > 0)
            {
                return TimeSpan.FromTicks(cachedTicks);
            }

            if (!TryResolveDuration(MediumDurationResourceKey, out TimeSpan duration))
            {
                return MediumDurationFallback;
            }

            Interlocked.CompareExchange(ref _mediumDurationTicks, duration.Ticks, 0);
            return duration;
        }
    }

    public static Point ShyHeaderOpacityTransitionProgressKey =>
        ShyHeaderOpacityTransitionProgress;

    private static bool TryResolveDuration(string resourceKey, out TimeSpan duration)
    {
        duration = default;
        ResourceDictionary? resources = Application.Current?.Resources;
        if (resources is null)
        {
            ReportResolutionFailureOnce($"Application resources were unavailable for {resourceKey}.");
            return false;
        }

        try
        {
            object? value = FindResource(resources, resourceKey, depth: 0);
            if (TryGetPositiveDuration(value, out duration))
            {
                return true;
            }

            string detail = value is null
                ? "missing"
                : "not a positive Duration, TimeSpan, or finite seconds value";
            ReportResolutionFailureOnce($"Motion token {resourceKey} was {detail}.");
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException or
            COMException)
        {
            if (Interlocked.Exchange(ref _resolutionFailureReported, 1) == 0)
            {
                HandledFailureReporter.Report(exception, "ui-motion-token-resolve");
            }
        }

        return false;
    }

    private static object? FindResource(ResourceDictionary resources, string resourceKey, int depth)
    {
        if (depth > MaximumMergedDictionaryDepth)
        {
            throw new InvalidOperationException(
                $"Motion token {resourceKey} exceeded the merged resource dictionary depth limit.");
        }

        object? directValue;
        try
        {
            directValue = resources[resourceKey];
        }
        catch (KeyNotFoundException)
        {
            directValue = null;
        }

        if (directValue is not null)
        {
            return directValue;
        }

        for (int index = resources.MergedDictionaries.Count - 1; index >= 0; index--)
        {
            object? value = FindResource(
                resources.MergedDictionaries[index],
                resourceKey,
                depth + 1);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryGetPositiveDuration(object? value, out TimeSpan duration)
    {
        duration = value switch
        {
            TimeSpan timeSpan when timeSpan > TimeSpan.Zero => timeSpan,
            Duration xamlDuration when xamlDuration.HasTimeSpan &&
                xamlDuration.TimeSpan > TimeSpan.Zero => xamlDuration.TimeSpan,
            double seconds when double.IsFinite(seconds) &&
                seconds > 0 &&
                seconds <= TimeSpan.MaxValue.TotalSeconds => TimeSpan.FromSeconds(seconds),
            _ => default
        };

        return duration > TimeSpan.Zero;
    }

    private static void ReportResolutionFailureOnce(string detail)
    {
        if (Interlocked.Exchange(ref _resolutionFailureReported, 1) == 0)
        {
            HandledFailureReporter.Report(detail, "ui-motion-token-resolve");
        }
    }
}
