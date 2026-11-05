using System;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace JitHub.Markdown;

/// <summary>
/// Routes a clicked Markdown link: rejects unsafe targets, opens supported
/// GitHub routes in the app, and launches everything else externally.
/// </summary>
public sealed class MarkdownLinkActivator
{
    private readonly IMarkdownRouteNavigator _navigator;
    private readonly IMarkdownLinkLauncher _launcher;
    private readonly IMarkdownHostObserver _observer;
    private readonly MarkdownViewerTelemetry _telemetry;

    /// <param name="navigator">Opens in-app GitHub routes.</param>
    /// <param name="launcher">Launches external links.</param>
    /// <param name="observer">The host milestone observer, which may capture the activation.</param>
    /// <param name="telemetry">The host's telemetry.</param>
    public MarkdownLinkActivator(
        IMarkdownRouteNavigator navigator,
        IMarkdownLinkLauncher launcher,
        IMarkdownHostObserver observer,
        MarkdownViewerTelemetry telemetry)
    {
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    }

    /// <summary>Activates a link the reader clicked.</summary>
    /// <param name="url">The link target as written in the document.</param>
    /// <param name="baseUri">The document's base URI.</param>
    /// <param name="documentSource">The document's source, which scopes relative links.</param>
    /// <param name="automationId">The host's automation id.</param>
    /// <returns>A task that completes when the link has been routed.</returns>
    public async Task ActivateAsync(
        string? url,
        Uri baseUri,
        MarkdownDocumentSource? documentSource,
        string automationId)
    {
        if (!MarkdownLinkNavigationPolicy.TryResolveLaunchUri(
                url,
                baseUri,
                documentSource,
                out Uri? uri,
                out bool mayNavigateInternally) || uri is null)
        {
            _telemetry.TrackMarkdownEvent("markdown.action.executed", MarkdownTelemetryValues.Actions.OpenLink, MarkdownTelemetryValues.Results.Rejected, resource: "link");
            return;
        }

        MarkdownGitHubRoute route = MarkdownLinkNavigationPolicy.ClassifyGitHubRoute(uri);
        string disposition = mayNavigateInternally && route.Kind is (MarkdownGitHubRouteKind.User or MarkdownGitHubRouteKind.Repository or MarkdownGitHubRouteKind.Issue or MarkdownGitHubRouteKind.PullRequest) ? route.Kind.ToString().ToLowerInvariant() : "external-browser";
        if (_observer.TryCaptureLinkRoute(automationId, uri, disposition))
        {
            _telemetry.TrackMarkdownEvent("markdown.action.executed", MarkdownTelemetryValues.Actions.OpenLink, MarkdownTelemetryValues.Results.Success, resource: "link");
            return;
        }

        if (mayNavigateInternally && _navigator.TryOpen(uri))
        {
            _telemetry.TrackMarkdownEvent("markdown.action.executed", MarkdownTelemetryValues.Actions.OpenLink, MarkdownTelemetryValues.Results.Success, resource: "link");
            return;
        }

        try
        {
            bool launched = await _launcher.LaunchAsync(uri);
            _telemetry.TrackMarkdownEvent("markdown.action.executed", MarkdownTelemetryValues.Actions.OpenLink, launched ? MarkdownTelemetryValues.Results.Launched : MarkdownTelemetryValues.Results.Failed, resource: "link");
        }
        catch (Exception)
        {
            _telemetry.TrackMarkdownEvent("markdown.error", MarkdownTelemetryValues.Actions.OpenLink, MarkdownTelemetryValues.Results.Failed, resource: "link", errorKind: MarkdownTelemetryValues.ErrorKinds.Launch);
        }
    }
}
