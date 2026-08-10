using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;

namespace JitHub.WinUI.Helpers;

internal static class AutomationIdentity
{
    public static void Apply(DependencyObject element, string automationId, string automationName)
    {
        ArgumentNullException.ThrowIfNull(element);
        AutomationProperties.SetAutomationId(element, automationId);
        AutomationProperties.SetName(element, automationName);
    }

    public static string CreateScopedId(string prefix, string? scope, string? suffix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        string normalizedScope = string.IsNullOrWhiteSpace(scope) ? "unknown" : scope.Trim();
        string readableScope = Sanitize(normalizedScope, 36);
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedScope)))[..12];
        string id = $"{Sanitize(prefix, 36)}_{readableScope}_{digest}";
        return string.IsNullOrWhiteSpace(suffix)
            ? id
            : $"{id}_{Sanitize(suffix, 28)}";
    }

    private static string Sanitize(string value, int maximumLength)
    {
        StringBuilder result = new(Math.Min(value.Length, maximumLength));
        foreach (char character in value)
        {
            if (result.Length == maximumLength)
            {
                break;
            }

            if (char.IsLetterOrDigit(character))
            {
                result.Append(character);
            }
            else if (result.Length > 0 && result[^1] != '_')
            {
                result.Append('_');
            }
        }

        return result.Length == 0 ? "item" : result.ToString().TrimEnd('_');
    }
}
