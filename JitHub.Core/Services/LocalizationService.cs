using System;
using JitHub.WinUI.Helpers;

namespace JitHub.Services;

public sealed class LocalizationService
{
    public string GetString(string resourceKey)
    {
        if (string.IsNullOrWhiteSpace(resourceKey))
        {
            return string.Empty;
        }

        return LocalizedResourceText.GetString(resourceKey, resourceKey);
    }

    public string GetStringOrDefault(string resourceKey, string fallback) =>
        LocalizedResourceText.GetString(resourceKey, fallback);

    public string Format(string resourceKey, params object?[] arguments) =>
        LocalizedResourceText.Format(resourceKey, resourceKey, arguments);
}
