using System;
using JitHub.Markdown;
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

    /// <summary>Creates a scoped automation id; see <see cref="AutomationScopedId.Create"/>.</summary>
    public static string CreateScopedId(string prefix, string? scope, string? suffix = null) =>
        AutomationScopedId.Create(prefix, scope, suffix);
}
