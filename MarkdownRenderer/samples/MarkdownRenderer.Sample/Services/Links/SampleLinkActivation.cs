using System;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Sample.Services.Automation;

namespace MarkdownRenderer.Sample.Services.Links;

/// <summary>
/// Handles link activation for every view: reserved fixture links and
/// disallowed schemes are handled in the sample, web and mail links open in
/// the shell, and each outcome is published to the link-activation probe.
/// </summary>
internal sealed class SampleLinkActivation(SampleAutomationStatus status)
{
    internal async void OnLinkClick(object? sender, MarkdownLinkClickEventArgs e)
    {
        string activation = string.Concat(
            "link:",
            e.InputKind.ToString(),
            ":",
            e.Url,
            ":",
            e.Action ?? string.Empty);

        if (SampleLinkRouting.IsInertFixtureUri(e.Uri))
        {
            e.Handled = true;
            status.SetLinkActivation(activation + ":sample-handled");
            return;
        }

        if (!SampleLinkRouting.IsAllowedExternalUri(e.Uri))
        {
            e.Handled = true;
            status.SetLinkActivation(activation + ":blocked");
            return;
        }

        e.Handled = true;
        try
        {
            bool launched = await Windows.System.Launcher.LaunchUriAsync(e.Uri);
            status.SetLinkActivation(activation + (launched ? ":launched" : ":launch-failed"));
        }
        catch
        {
            status.SetLinkActivation(activation + ":launch-failed");
        }
    }
}
