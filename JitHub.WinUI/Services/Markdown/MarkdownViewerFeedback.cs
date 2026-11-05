using System;
using System.Collections.Generic;
using JitHub.Markdown;
using JitHub.WinUI.Helpers;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

/// <summary>
/// Owns a Markdown host's per-document feedback state: remote-image consent,
/// the remote-image and render-error notices, retry tracking, and the
/// telemetry that reports them.
/// </summary>
internal sealed class MarkdownViewerFeedback
{
    private readonly IMarkdownViewerNoticePresenter _presenter;
    private readonly IMarkdownHostObserver _observer;
    private readonly MarkdownViewerTelemetry _telemetry;
    private readonly MarkdownRemoteContentConsent _remoteContentConsent = new();
    private readonly HashSet<MarkdownImageUnavailableReason> _reportedImageUnavailableReasons = [];
    private bool _remoteImageNoticeDismissed;
    private bool _renderFailureReportedForDocument;
    private bool _retryRenderPending;

    /// <param name="presenter">The host that shows the notices.</param>
    /// <param name="observer">The host milestone observer.</param>
    /// <param name="telemetry">The host's telemetry.</param>
    public MarkdownViewerFeedback(
        IMarkdownViewerNoticePresenter presenter,
        IMarkdownHostObserver observer,
        MarkdownViewerTelemetry telemetry)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    }

    /// <summary>Gets whether the renderer may load secure third-party images for the current document.</summary>
    public bool AllowsThirdPartyRemoteImages =>
        _presenter.AllowsThirdPartyRemoteImagesByDefault || _remoteContentConsent.IsGranted;

    /// <summary>Starts feedback for a newly active document source.</summary>
    /// <param name="source">The new document source, or <see langword="null"/> for an anonymous document.</param>
    public void ActivateDocument(MarkdownDocumentSource? source)
    {
        _remoteContentConsent.Activate(source);
        ResetDocumentFeedback();
    }

    /// <summary>Forgets every per-document decision when the host is reused for unrelated content.</summary>
    public void ResetForHostReuse()
    {
        _remoteContentConsent.ResetForHostReuse();
        ResetDocumentFeedback();
    }

    /// <summary>
    /// Called after the host's default remote-image policy changed. A promoted
    /// policy must not leave a stale consent prompt visible while images rebuild.
    /// </summary>
    public void OnRemoteImagePolicyChanged()
    {
        if (AllowsThirdPartyRemoteImages)
            _presenter.CloseRemoteImageNotice();
    }

    /// <summary>Called when the host assigns different Markdown to its renderer.</summary>
    public void OnMarkdownChanged()
    {
        _renderFailureReportedForDocument = false;
        _retryRenderPending = false;
        _presenter.SetRenderErrorNoticeOpen(false);
    }

    /// <summary>Reports an image the renderer could not load and shows the matching notice.</summary>
    /// <param name="source">The image source as written in the document.</param>
    /// <param name="reason">Why the image was unavailable.</param>
    public void OnImageUnavailable(string source, MarkdownImageUnavailableReason reason)
    {
        if (reason == MarkdownImageUnavailableReason.RemoteContentBlocked &&
            AllowsThirdPartyRemoteImages)
        {
            return;
        }

        _observer.OnImageUnavailable(_presenter.AutomationId, source, reason);

        if (_reportedImageUnavailableReasons.Add(reason))
        {
            TrackImageUnavailable(reason);
        }

        if (reason is not (MarkdownImageUnavailableReason.RemoteContentBlocked or
            MarkdownImageUnavailableReason.InsecureRemoteContent or
            MarkdownImageUnavailableReason.Offline or
            MarkdownImageUnavailableReason.MeteredConnection))
        {
            return;
        }

        bool compactNotice = MarkdownHostContract.Parse(_presenter.HostKind) == MarkdownHostKind.EditorPreview;
        string message = GetRemoteImageNoticeMessage(reason);
        string title = GetRemoteImageNoticeTitle(reason, compactNotice);
        _presenter.UpdateRemoteImageNotice(
            title,
            compactNotice ? string.Empty : message,
            offerLoadImages: reason is MarkdownImageUnavailableReason.RemoteContentBlocked or
                MarkdownImageUnavailableReason.MeteredConnection);
        // The notice overlays the top of the reading region; once the reader
        // closes it, later images in the same document do not reopen it.
        if (!_remoteImageNoticeDismissed)
        {
            _presenter.OpenRemoteImageNotice();
        }
    }

    private void TrackImageUnavailable(MarkdownImageUnavailableReason reason)
    {
        _telemetry.TrackMarkdownEvent(
            "markdown.resource.unavailable",
            action: null,
            result: reason switch
            {
                MarkdownImageUnavailableReason.RemoteContentBlocked or
                MarkdownImageUnavailableReason.InsecureRemoteContent => TelemetryTaxonomy.Results.Rejected,
                MarkdownImageUnavailableReason.Offline or
                MarkdownImageUnavailableReason.MeteredConnection => TelemetryTaxonomy.Results.Deferred,
                _ => TelemetryTaxonomy.Results.Unavailable,
            },
            resource: "remote_image",
            policy: reason switch
            {
                MarkdownImageUnavailableReason.Offline or
                MarkdownImageUnavailableReason.MeteredConnection => "deferred",
                MarkdownImageUnavailableReason.RemoteContentBlocked or
                MarkdownImageUnavailableReason.InsecureRemoteContent => "suppressed",
                _ => null,
            });
    }

    private static string GetRemoteImageNoticeMessage(MarkdownImageUnavailableReason reason) =>
        reason switch
        {
            MarkdownImageUnavailableReason.InsecureRemoteContent =>
                LocalizedResourceText.GetString(
                    "Markdown.RemoteImage.InsecureMessage",
                    "An image used insecure HTTP and cannot be loaded."),
            MarkdownImageUnavailableReason.Offline =>
                LocalizedResourceText.GetString(
                    "Markdown.RemoteImage.OfflineMessage",
                    "An image is not cached and cannot be loaded while offline."),
            MarkdownImageUnavailableReason.MeteredConnection =>
                LocalizedResourceText.GetString(
                    "Markdown.RemoteImage.MeteredMessage",
                    "Automatic image loading is paused on this metered connection."),
            _ => LocalizedResourceText.GetString(
                "Markdown.RemoteImage.PrivacyMessage",
                "External images can reveal your IP address and request timing to another site."),
        };

    private static string GetRemoteImageNoticeTitle(MarkdownImageUnavailableReason reason, bool compactNotice) =>
        compactNotice
            ? reason switch
            {
                MarkdownImageUnavailableReason.Offline => LocalizedResourceText.GetString(
                    "Markdown.RemoteImage.OfflineCompactTitle",
                    "Images unavailable offline"),
                MarkdownImageUnavailableReason.MeteredConnection => LocalizedResourceText.GetString(
                    "Markdown.RemoteImage.MeteredCompactTitle",
                    "Images paused on this connection"),
                MarkdownImageUnavailableReason.InsecureRemoteContent => LocalizedResourceText.GetString(
                    "Markdown.RemoteImage.InsecureCompactTitle",
                    "Insecure image blocked"),
                _ => LocalizedResourceText.GetString(
                    "Markdown.RemoteImage.BlockedCompactTitle",
                    "External images blocked"),
            }
            : LocalizedResourceText.GetString(
                "Markdown.RemoteImage.ProtectedTitle",
                "Remote images are protected");

    /// <summary>Called when the remote-image notice closed.</summary>
    /// <param name="closedByReader">Whether the reader closed it with its close button.</param>
    public void OnRemoteImageNoticeClosed(bool closedByReader)
    {
        if (closedByReader)
        {
            _remoteImageNoticeDismissed = true;
        }
    }

    /// <summary>Called when the renderer finished a render.</summary>
    public void OnRenderCompleted()
    {
        if (_presenter.IsRendererDisposalFaulted)
            return;

        _presenter.SetRenderErrorNoticeOpen(false);
        if (!_retryRenderPending)
        {
            return;
        }

        _retryRenderPending = false;
        _telemetry.TrackMarkdownEvent(
            "markdown.action.executed",
            TelemetryTaxonomy.Actions.Retry,
            TelemetryTaxonomy.Results.Success);
    }

    /// <summary>Called when the renderer failed to render.</summary>
    /// <param name="exception">The render failure.</param>
    public void OnRenderFailed(Exception exception)
    {
        if (_presenter.IsRendererDisposalFaulted)
            return;

        _observer.OnRenderFailed(_presenter.AutomationId, exception);
        _presenter.SetRenderErrorNoticeOpen(true);
        bool wasRetry = _retryRenderPending;
        _retryRenderPending = false;
        if (_renderFailureReportedForDocument && !wasRetry)
        {
            return;
        }

        _renderFailureReportedForDocument = true;
        _telemetry.TrackMarkdownEvent(
            "markdown.error",
            wasRetry ? TelemetryTaxonomy.Actions.Retry : TelemetryTaxonomy.Actions.Render,
            TelemetryTaxonomy.Results.Failed,
            errorKind: TelemetryTaxonomy.ErrorKinds.Unexpected,
            source: "background");
    }

    /// <summary>Retries a failed render at the reader's request.</summary>
    public void Retry()
    {
        if (_presenter.IsRendererDisposalFaulted)
            return;

        _presenter.SetRenderErrorNoticeOpen(false);
        _retryRenderPending = true;
        _telemetry.TrackMarkdownEvent(
            "markdown.action.executed",
            TelemetryTaxonomy.Actions.Retry,
            TelemetryTaxonomy.Results.Queued);
        _presenter.RequestRebuild();
    }

    /// <summary>Grants remote-image consent for the current document at the reader's request.</summary>
    public void GrantRemoteImages()
    {
        _remoteContentConsent.Grant();
        _presenter.CloseRemoteImageNotice();
        _presenter.ApplyRemoteImageGrant();

        _telemetry.TrackMarkdownEvent(
            "markdown.action.executed",
            TelemetryTaxonomy.Actions.LoadRemoteImages,
            "allowed",
            resource: "remote_image");
    }

    /// <summary>Records a reader toggling a details disclosure.</summary>
    /// <param name="isExpanded">Whether the disclosure is now expanded.</param>
    public void OnDisclosureToggled(bool isExpanded) =>
        _telemetry.TrackMarkdownEvent(
            "markdown.action.executed",
            TelemetryTaxonomy.Actions.ToggleDetails,
            isExpanded ? TelemetryTaxonomy.Results.Expanded : TelemetryTaxonomy.Results.Collapsed);

    /// <summary>Records a completed copy action.</summary>
    /// <param name="copiedCodeBlock">Whether a code block, rather than a selection, was copied.</param>
    /// <param name="succeeded">Whether the clipboard accepted the content.</param>
    public void OnCopyCompleted(bool copiedCodeBlock, bool succeeded) =>
        _telemetry.TrackMarkdownEvent(
            "markdown.action.executed",
            copiedCodeBlock
                ? TelemetryTaxonomy.Actions.CopyCode
                : TelemetryTaxonomy.Actions.CopySelection,
            succeeded ? TelemetryTaxonomy.Results.Success : TelemetryTaxonomy.Results.Error);

    private void ResetDocumentFeedback()
    {
        _reportedImageUnavailableReasons.Clear();
        _remoteImageNoticeDismissed = false;
        _renderFailureReportedForDocument = false;
        _retryRenderPending = false;
        _presenter.CloseRemoteImageNotice();
        if (!_presenter.IsRendererDisposalFaulted)
        {
            _presenter.SetRenderErrorNoticeOpen(false);
        }
    }
}
