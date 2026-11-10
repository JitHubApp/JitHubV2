using System;
using System.Collections.Generic;
using JitHub.Markdown.Hosting;
using JitHub.Markdown.Localization;
using JitHub.Markdown.Telemetry;
using JitHub.Markdown.Telemetry.Internal;
using MarkdownRenderer.Images;

namespace JitHub.Markdown.Notices;

/// <summary>
/// Owns a Markdown host's per-document feedback state: remote-image consent,
/// the remote-image and render-error notices, retry tracking, and the
/// telemetry that reports them.
/// </summary>
public sealed class MarkdownViewerFeedback
{
    private readonly IMarkdownViewerNoticePresenter _presenter;
    private readonly MarkdownViewerTelemetry _telemetry;
    private readonly IMarkdownResourceText _text;
    private readonly MarkdownRemoteContentConsent _remoteContentConsent = new();
    private readonly HashSet<MarkdownImageUnavailableReason> _reportedImageUnavailableReasons = [];
    private bool _remoteImageNoticeDismissed;
    private bool _renderFailureReportedForDocument;
    private bool _retryRenderPending;

    /// <param name="presenter">The host that shows the notices.</param>
    /// <param name="telemetry">The host's telemetry.</param>
    /// <param name="text">The app's localized notice text.</param>
    public MarkdownViewerFeedback(
        IMarkdownViewerNoticePresenter presenter,
        MarkdownViewerTelemetry telemetry,
        IMarkdownResourceText text)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _text = text ?? throw new ArgumentNullException(nameof(text));
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
                MarkdownImageUnavailableReason.InsecureRemoteContent => MarkdownTelemetryValues.Results.Rejected,
                MarkdownImageUnavailableReason.Offline or
                MarkdownImageUnavailableReason.MeteredConnection => MarkdownTelemetryValues.Results.Deferred,
                _ => MarkdownTelemetryValues.Results.Unavailable,
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

    private string GetRemoteImageNoticeMessage(MarkdownImageUnavailableReason reason) =>
        reason switch
        {
            MarkdownImageUnavailableReason.InsecureRemoteContent =>
                Text(
                    "Markdown.RemoteImage.InsecureMessage",
                    "An image used insecure HTTP and cannot be loaded."),
            MarkdownImageUnavailableReason.Offline =>
                Text(
                    "Markdown.RemoteImage.OfflineMessage",
                    "An image is not cached and cannot be loaded while offline."),
            MarkdownImageUnavailableReason.MeteredConnection =>
                Text(
                    "Markdown.RemoteImage.MeteredMessage",
                    "Automatic image loading is paused on this metered connection."),
            _ => Text(
                "Markdown.RemoteImage.PrivacyMessage",
                "External images can reveal your IP address and request timing to another site."),
        };

    private string GetRemoteImageNoticeTitle(MarkdownImageUnavailableReason reason, bool compactNotice) =>
        compactNotice
            ? reason switch
            {
                MarkdownImageUnavailableReason.Offline => Text(
                    "Markdown.RemoteImage.OfflineCompactTitle",
                    "Images unavailable offline"),
                MarkdownImageUnavailableReason.MeteredConnection => Text(
                    "Markdown.RemoteImage.MeteredCompactTitle",
                    "Images paused on this connection"),
                MarkdownImageUnavailableReason.InsecureRemoteContent => Text(
                    "Markdown.RemoteImage.InsecureCompactTitle",
                    "Insecure image blocked"),
                _ => Text(
                    "Markdown.RemoteImage.BlockedCompactTitle",
                    "External images blocked"),
            }
            : Text(
                "Markdown.RemoteImage.ProtectedTitle",
                "Remote images are protected");

    private string Text(string resourceKey, string fallback) =>
        _text.GetString(resourceKey, fallback, culture: null);

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
            MarkdownTelemetryValues.Actions.Retry,
            MarkdownTelemetryValues.Results.Success);
    }

    /// <summary>Called when the renderer failed to render.</summary>
    /// <param name="exception">The render failure.</param>
    public void OnRenderFailed(Exception exception)
    {
        if (_presenter.IsRendererDisposalFaulted)
            return;

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
            wasRetry ? MarkdownTelemetryValues.Actions.Retry : MarkdownTelemetryValues.Actions.Render,
            MarkdownTelemetryValues.Results.Failed,
            errorKind: MarkdownTelemetryValues.ErrorKinds.Unexpected,
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
            MarkdownTelemetryValues.Actions.Retry,
            MarkdownTelemetryValues.Results.Queued);
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
            MarkdownTelemetryValues.Actions.LoadRemoteImages,
            "allowed",
            resource: "remote_image");
    }

    /// <summary>Records a reader toggling a details disclosure.</summary>
    /// <param name="isExpanded">Whether the disclosure is now expanded.</param>
    public void OnDisclosureToggled(bool isExpanded) =>
        _telemetry.TrackMarkdownEvent(
            "markdown.action.executed",
            MarkdownTelemetryValues.Actions.ToggleDetails,
            isExpanded ? MarkdownTelemetryValues.Results.Expanded : MarkdownTelemetryValues.Results.Collapsed);

    /// <summary>Records a completed copy action.</summary>
    /// <param name="copiedCodeBlock">Whether a code block, rather than a selection, was copied.</param>
    /// <param name="succeeded">Whether the clipboard accepted the content.</param>
    public void OnCopyCompleted(bool copiedCodeBlock, bool succeeded) =>
        _telemetry.TrackMarkdownEvent(
            "markdown.action.executed",
            copiedCodeBlock
                ? MarkdownTelemetryValues.Actions.CopyCode
                : MarkdownTelemetryValues.Actions.CopySelection,
            succeeded ? MarkdownTelemetryValues.Results.Success : MarkdownTelemetryValues.Results.Error);

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
