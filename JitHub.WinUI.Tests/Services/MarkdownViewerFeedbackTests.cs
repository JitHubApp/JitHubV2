using System;
using System.Collections.Generic;
using JitHub.Services;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using NSubstitute;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class MarkdownViewerFeedbackTests
{
    [Fact]
    public void RemoteImageConsent_DefaultsToTheHostPolicyAndGrantsPerDocument()
    {
        var harness = new FeedbackHarness { AllowsByDefault = false };
        var source = new MarkdownDocumentSource("owner/repo/README.md");
        harness.Feedback.ActivateDocument(source);

        Assert.False(harness.Feedback.AllowsThirdPartyRemoteImages);

        harness.Feedback.GrantRemoteImages();

        Assert.True(harness.Feedback.AllowsThirdPartyRemoteImages);
        Assert.Equal(["close-remote", "apply-grant"], harness.Presenter.Calls[^2..]);
        Assert.Equal(TelemetryTaxonomy.Actions.LoadRemoteImages, harness.LastEvent("action"));
        Assert.Equal("allowed", harness.LastEvent("result"));

        harness.Feedback.ResetForHostReuse();
        Assert.False(harness.Feedback.AllowsThirdPartyRemoteImages);

        harness.AllowsByDefault = true;
        Assert.True(harness.Feedback.AllowsThirdPartyRemoteImages);
    }

    [Fact]
    public void BlockedImage_IsIgnoredWhenTheHostAllowsThirdPartyImages()
    {
        var harness = new FeedbackHarness { AllowsByDefault = true };

        harness.Feedback.OnImageUnavailable("https://example.com/a.png", MarkdownImageUnavailableReason.RemoteContentBlocked);

        Assert.Empty(harness.Presenter.Calls);
        Assert.Empty(harness.Observer.Calls);
        Assert.Empty(harness.Events);
    }

    [Fact]
    public void RemoteImageNotice_StaysClosedForTheDocumentAfterTheReaderDismissesIt()
    {
        var harness = new FeedbackHarness { AllowsByDefault = false };

        harness.Feedback.OnImageUnavailable("https://example.com/a.png", MarkdownImageUnavailableReason.RemoteContentBlocked);
        Assert.Equal(1, harness.Presenter.Count("open-remote"));
        Assert.True(harness.Presenter.LastOfferedLoadImages);

        harness.Feedback.OnRemoteImageNoticeClosed(closedByReader: true);
        harness.Feedback.OnImageUnavailable("https://example.com/b.png", MarkdownImageUnavailableReason.RemoteContentBlocked);

        // The content still updates, but the notice does not reopen over the
        // reading region for the same document.
        Assert.Equal(2, harness.Presenter.Count("update-remote"));
        Assert.Equal(1, harness.Presenter.Count("open-remote"));

        harness.Feedback.ActivateDocument(new MarkdownDocumentSource("owner/other/README.md"));
        harness.Feedback.OnImageUnavailable("https://example.com/c.png", MarkdownImageUnavailableReason.RemoteContentBlocked);
        Assert.Equal(2, harness.Presenter.Count("open-remote"));

        harness.Feedback.OnRemoteImageNoticeClosed(closedByReader: true);
        harness.Feedback.ResetForHostReuse();
        harness.Feedback.OnImageUnavailable("https://example.com/d.png", MarkdownImageUnavailableReason.RemoteContentBlocked);
        Assert.Equal(3, harness.Presenter.Count("open-remote"));
    }

    [Fact]
    public void RemoteImageNotice_ProgrammaticCloseDoesNotCountAsDismissal()
    {
        var harness = new FeedbackHarness { AllowsByDefault = false };
        harness.Feedback.OnImageUnavailable("https://example.com/a.png", MarkdownImageUnavailableReason.Offline);

        harness.Feedback.OnRemoteImageNoticeClosed(closedByReader: false);
        harness.Feedback.OnImageUnavailable("https://example.com/b.png", MarkdownImageUnavailableReason.Offline);

        Assert.Equal(2, harness.Presenter.Count("open-remote"));
        Assert.False(harness.Presenter.LastOfferedLoadImages);
    }

    [Fact]
    public void ImageUnavailable_ReportsEachReasonOncePerDocument()
    {
        var harness = new FeedbackHarness { AllowsByDefault = false };

        harness.Feedback.OnImageUnavailable("a", MarkdownImageUnavailableReason.MeteredConnection);
        harness.Feedback.OnImageUnavailable("b", MarkdownImageUnavailableReason.MeteredConnection);
        harness.Feedback.OnImageUnavailable("c", MarkdownImageUnavailableReason.InsecureRemoteContent);

        Assert.Equal(2, harness.Events.Count);
        Assert.Equal("deferred", harness.Events[0].Properties["policy"]);
        Assert.Equal(TelemetryTaxonomy.Results.Deferred, harness.Events[0].Properties["result"]);
        Assert.Equal("suppressed", harness.Events[1].Properties["policy"]);
        Assert.Equal(TelemetryTaxonomy.Results.Rejected, harness.Events[1].Properties["result"]);
        Assert.Equal(3, harness.Observer.ImagesUnavailable.Count);
        Assert.All(harness.Observer.ImagesUnavailable, static entry => Assert.Equal("markdown-test", entry.AutomationId));

        harness.Feedback.ActivateDocument(null);
        harness.Feedback.OnImageUnavailable("d", MarkdownImageUnavailableReason.MeteredConnection);
        Assert.Equal(3, harness.Events.Count);
    }

    [Fact]
    public void ImageUnavailable_OtherReasonsAreReportedWithoutANotice()
    {
        var harness = new FeedbackHarness { AllowsByDefault = false };

        harness.Feedback.OnImageUnavailable("a", MarkdownImageUnavailableReason.Unavailable);

        Assert.Equal(TelemetryTaxonomy.Results.Unavailable, harness.LastEvent("result"));
        Assert.Empty(harness.Presenter.Calls);
    }

    [Fact]
    public void ImageUnavailable_EditorPreviewUsesACompactNoticeWithoutAMessage()
    {
        var harness = new FeedbackHarness { AllowsByDefault = false, HostKind = "EditorPreview" };

        harness.Feedback.OnImageUnavailable("a", MarkdownImageUnavailableReason.Offline);

        Assert.Equal("Images unavailable offline", harness.Presenter.LastTitle);
        Assert.Equal(string.Empty, harness.Presenter.LastMessage);
    }

    [Fact]
    public void ImageUnavailable_ReadingHostsExplainTheReason()
    {
        var harness = new FeedbackHarness { AllowsByDefault = false, HostKind = "RepositoryReadme" };

        harness.Feedback.OnImageUnavailable("a", MarkdownImageUnavailableReason.InsecureRemoteContent);

        Assert.Equal("Remote images are protected", harness.Presenter.LastTitle);
        Assert.Equal("An image used insecure HTTP and cannot be loaded.", harness.Presenter.LastMessage);
        Assert.False(harness.Presenter.LastOfferedLoadImages);
    }

    [Fact]
    public void RemoteImagePolicyPromotion_ClosesAStaleConsentPrompt()
    {
        var harness = new FeedbackHarness { AllowsByDefault = false };

        harness.Feedback.OnRemoteImagePolicyChanged();
        Assert.Empty(harness.Presenter.Calls);

        harness.AllowsByDefault = true;
        harness.Feedback.OnRemoteImagePolicyChanged();
        Assert.Equal(["close-remote"], harness.Presenter.Calls);
    }

    [Fact]
    public void RenderFailure_ShowsTheErrorNoticeAndReportsOncePerDocumentUnlessRetried()
    {
        var harness = new FeedbackHarness();
        var failure = new InvalidOperationException("Render failed.");

        harness.Feedback.OnRenderFailed(failure);
        harness.Feedback.OnRenderFailed(failure);

        Assert.Equal([("markdown-test", (Exception)failure), ("markdown-test", failure)], harness.Observer.RenderFailures);
        Assert.Equal(2, harness.Presenter.Count("error-open"));
        Assert.Single(harness.Events);
        Assert.Equal("markdown.error", harness.Events[0].Name);
        Assert.Equal(TelemetryTaxonomy.Actions.Render, harness.Events[0].Properties["action"]);
        Assert.Equal("background", harness.Events[0].Properties["source"]);

        harness.Feedback.Retry();
        Assert.Equal(["error-closed", "rebuild"], harness.Presenter.Calls[^2..]);
        Assert.Equal(TelemetryTaxonomy.Results.Queued, harness.LastEvent("result"));

        harness.Feedback.OnRenderFailed(failure);
        Assert.Equal("markdown.error", harness.Events[^1].Name);
        Assert.Equal(TelemetryTaxonomy.Actions.Retry, harness.Events[^1].Properties["action"]);

        harness.Feedback.OnMarkdownChanged();
        harness.Feedback.OnRenderFailed(failure);
        Assert.Equal(TelemetryTaxonomy.Actions.Render, harness.Events[^1].Properties["action"]);
    }

    [Fact]
    public void RenderCompleted_ClosesTheErrorNoticeAndReportsASuccessfulRetry()
    {
        var harness = new FeedbackHarness();

        harness.Feedback.OnRenderCompleted();
        Assert.Equal(["error-closed"], harness.Presenter.Calls);
        Assert.Empty(harness.Events);

        harness.Feedback.Retry();
        harness.Feedback.OnRenderCompleted();
        Assert.Equal(TelemetryTaxonomy.Actions.Retry, harness.LastEvent("action"));
        Assert.Equal(TelemetryTaxonomy.Results.Success, harness.LastEvent("result"));

        int events = harness.Events.Count;
        harness.Feedback.OnRenderCompleted();
        Assert.Equal(events, harness.Events.Count);
    }

    [Fact]
    public void RendererDisposalFailure_KeepsItsNoticeAndSuppressesRenderFeedback()
    {
        var harness = new FeedbackHarness();
        harness.Presenter.IsRendererDisposalFaulted = true;

        harness.Feedback.OnRenderFailed(new InvalidOperationException());
        harness.Feedback.OnRenderCompleted();
        harness.Feedback.Retry();
        harness.Feedback.ActivateDocument(null);
        harness.Feedback.ResetForHostReuse();

        // Only the remote-image notice closes; the disposal failure notice,
        // which reuses the render-error notice, stays up.
        Assert.Equal(["close-remote", "close-remote"], harness.Presenter.Calls);
        Assert.Empty(harness.Events);
        Assert.Empty(harness.Observer.RenderFailures);
    }

    [Fact]
    public void ReaderActions_ReportDisclosureAndCopyResults()
    {
        var harness = new FeedbackHarness();

        harness.Feedback.OnDisclosureToggled(isExpanded: true);
        harness.Feedback.OnDisclosureToggled(isExpanded: false);
        harness.Feedback.OnCopyCompleted(copiedCodeBlock: true, succeeded: true);
        harness.Feedback.OnCopyCompleted(copiedCodeBlock: false, succeeded: false);

        Assert.Equal(
            [
                (TelemetryTaxonomy.Actions.ToggleDetails, TelemetryTaxonomy.Results.Expanded),
                (TelemetryTaxonomy.Actions.ToggleDetails, TelemetryTaxonomy.Results.Collapsed),
                (TelemetryTaxonomy.Actions.CopyCode, TelemetryTaxonomy.Results.Success),
                (TelemetryTaxonomy.Actions.CopySelection, TelemetryTaxonomy.Results.Error),
            ],
            harness.Events.ConvertAll(static e => (e.Properties["action"], e.Properties["result"])));
        Assert.All(harness.Events, static e =>
        {
            Assert.Equal("markdown.action.executed", e.Name);
            Assert.Equal("markdown", e.Properties["feature"]);
            Assert.Equal(TelemetryTaxonomy.Sources.User, e.Properties["source"]);
        });
    }

    private sealed class FeedbackHarness
    {
        public FeedbackHarness()
        {
            Presenter = new RecordingPresenter(this);
            ITelemetryService telemetry = Substitute.For<ITelemetryService>();
            telemetry
                .When(static service => service.TrackEvent(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string?>?>()))
                .Do(call => Events.Add(new RecordedEvent(
                    call.ArgAt<string>(0),
                    new Dictionary<string, string?>(call.ArgAt<IReadOnlyDictionary<string, string?>>(1)!))));
            Feedback = new MarkdownViewerFeedback(
                Presenter,
                Observer,
                new MarkdownViewerTelemetry(telemetry, () => HostKind));
        }

        public bool AllowsByDefault { get; set; } = true;

        public string? HostKind { get; set; }

        public RecordingPresenter Presenter { get; }

        public RecordingMarkdownHostObserver Observer { get; } = new();

        public List<RecordedEvent> Events { get; } = [];

        public MarkdownViewerFeedback Feedback { get; }

        public string? LastEvent(string property) => Events[^1].Properties[property];
    }

    private sealed record RecordedEvent(string Name, Dictionary<string, string?> Properties);

    private sealed class RecordingPresenter : IMarkdownViewerNoticePresenter
    {
        private readonly FeedbackHarness _harness;

        public RecordingPresenter(FeedbackHarness harness)
        {
            _harness = harness;
        }

        public List<string> Calls { get; } = [];

        public string? LastTitle { get; private set; }

        public string? LastMessage { get; private set; }

        public bool LastOfferedLoadImages { get; private set; }

        public string? HostKind => _harness.HostKind;

        public string AutomationId => "markdown-test";

        public bool AllowsThirdPartyRemoteImagesByDefault => _harness.AllowsByDefault;

        public bool IsRendererDisposalFaulted { get; set; }

        public int Count(string call) => Calls.FindAll(entry => entry == call).Count;

        public void UpdateRemoteImageNotice(string title, string message, bool offerLoadImages)
        {
            Calls.Add("update-remote");
            LastTitle = title;
            LastMessage = message;
            LastOfferedLoadImages = offerLoadImages;
        }

        public void OpenRemoteImageNotice() => Calls.Add("open-remote");

        public void CloseRemoteImageNotice() => Calls.Add("close-remote");

        public void SetRenderErrorNoticeOpen(bool isOpen) => Calls.Add(isOpen ? "error-open" : "error-closed");

        public void ApplyRemoteImageGrant() => Calls.Add("apply-grant");

        public void RequestRebuild() => Calls.Add("rebuild");
    }
}
