using System;
using System.Threading;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.Performance;

/// <summary>Creates bounded visible-band text for a detached vector-scene box.</summary>
internal static class MarkdownDetachedSceneFallback
{
    private const int MaximumCharacters = 256;
    private const float MaximumDimension = 4096f;

    /// <summary>Optional preparation orchestration; the box alone owns adopted native layouts.</summary>
    internal static bool PrepareDetachedFallback(
        this VectorSceneBox box,
        CancellationToken cancellationToken,
        Func<VectorSceneBox, CancellationToken, CanvasTextLayout?> factory,
        bool replaceExisting = false,
        NativeResourceRetirement? nativeRetirement = null,
        Action<IDisposable>? nativeDisposer = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ObjectDisposedException.ThrowIf(box.IsDisposed, box);
        cancellationToken.ThrowIfCancellationRequested();
        double width = box.HorizontalViewport;
        double height = box.ContentBounds.Height;
        if (!box.IsGraphDetached || (!replaceExisting && box.HasDetachedFallback) ||
            !double.IsFinite(width) || width <= 0 || !double.IsFinite(height) || height <= 0)
            return false;

        // Participating scenes use one borrowed process-wide DrawText format
        // and a bounded source caption. Do not create a per-box CanvasTextLayout
        // outside the native-scene admission ledger. Existing fault-injection
        // and default eager contracts remain on the legacy factory path.
        if (box.NativeAdmission is not null)
        {
            if (factory == CreateFailed)
                box.MarkGraphRebindFailed();
            return box.PrepareCapacityFallback(cancellationToken);
        }

        // Reserve custody before invoking either the built-in or a supplied
        // factory. If native cleanup fails, this record can be published
        // without allocating a retirement queue node on the failure path.
        NativeResourceRetirementRecord retirement = (nativeRetirement ?? NativeResourceRetirement.Shared).Reserve(
            resourceGroupCount: 0,
            temporarySlotCount: 1,
            disposer: nativeDisposer);
        if (!box.TryBeginDetachedFallbackCandidate(retirement))
        {
            retirement.Publish();
            return false;
        }

        CanvasTextLayout? candidate = null;
        bool adopted = false;
        Exception? primaryFailure = null;
        try
        {
            candidate = factory(box, cancellationToken);
            if (candidate is not null)
                retirement.SetTemporary(0, candidate);
            adopted = box.TryAdoptDetachedFallback(ref candidate, cancellationToken);
            return adopted;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                if (candidate is not null)
                {
                    try
                    {
                        retirement.Disposer(candidate);
                        retirement.SetTemporary(0, null);
                        candidate = null;
                    }
                    catch (Exception cleanupFailure)
                    {
                        // The candidate never became box-owned. Transfer its
                        // pre-reserved slot before letting this temporary owner go.
                        retirement.SetTemporary(0, candidate);
                        candidate = null;
                        retirement.Publish();
                        if (primaryFailure is not null)
                        {
                            throw new AggregateException(
                                "Detached fallback preparation and candidate cleanup both failed.",
                                primaryFailure,
                                cleanupFailure);
                        }
                        throw;
                    }
                }

                if (!adopted)
                    retirement.Publish();
            }
            finally
            {
                // Keep the per-box preparation gate held through every cleanup
                // attempt and record publication, including injected faults.
                box.EndDetachedFallbackCandidate(retirement);
            }
        }
    }

    internal static CanvasTextLayout? Create(VectorSceneBox box, CancellationToken cancellationToken) =>
        CreateCore(box, cancellationToken, forceFailureStatus: false);

    internal static CanvasTextLayout? CreateFailed(VectorSceneBox box, CancellationToken cancellationToken) =>
        CreateCore(box, cancellationToken, forceFailureStatus: true);

    private static CanvasTextLayout? CreateCore(
        VectorSceneBox box,
        CancellationToken cancellationToken,
        bool forceFailureStatus)
    {
        ArgumentNullException.ThrowIfNull(box);
        cancellationToken.ThrowIfCancellationRequested();
        if (!box.IsGraphDetached)
            return null;

        NativeResourceRetirementRecord retirement = box.GetDetachedFallbackCandidateRecord()
            ?? throw new InvalidOperationException(
                "Detached fallback creation must run inside a reserved preparation scope.");

        MarkdownLayoutContext context = box.LayoutContext;
        string text = CreateText(box, context, forceFailureStatus || box.GraphRebindFailed);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        ElementStyle style = box.ResolvedStyle;
        float fontSize = float.IsFinite(style.FontSize) && style.FontSize > 0 ? style.FontSize : 12f;
        float lineHeight = float.IsFinite(style.LineHeightMultiplier) && style.LineHeightMultiplier > 0
            ? style.LineHeightMultiplier
            : 1f;
        float width = Math.Clamp((float)box.HorizontalViewportBounds.Width, 1f, MaximumDimension);
        float height = Math.Clamp((float)Math.Min(
            box.ContentBounds.Height,
            Math.Max(1f, fontSize * lineHeight * 3f)), 1f, MaximumDimension);

        cancellationToken.ThrowIfCancellationRequested();
        CanvasTextLayout? candidate = null;
        try
        {
            using (SharedCanvasTextFormatCache.Lease format = SharedCanvasTextFormatCache.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Left,
                context.FlowDirection,
                context.Language))
            {
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                candidate = CanvasTextLayoutFactory.Create(
                    context.ResourceCreator,
                    text,
                    format.Format,
                    width,
                    height,
                    context.LayoutDiagnostics);
#else
                candidate = new CanvasTextLayout(
                    context.ResourceCreator,
                    text,
                    format.Format,
                    width,
                    height);
#endif
            }

            // Record ownership immediately after the native factory returns,
            // before options or cancellation checks can fail.
            retirement.SetTemporary(0, candidate);

            candidate.Options = CanvasDrawTextOptions.EnableColorFont;
            cancellationToken.ThrowIfCancellationRequested();
            CanvasTextLayout result = candidate;
            candidate = null;
            return result;
        }
        catch (Exception primaryFailure)
        {
            if (candidate is null)
                throw;
            try
            {
                retirement.Disposer(candidate);
                retirement.SetTemporary(0, null);
                candidate = null;
            }
            catch (Exception cleanupFailure)
            {
                retirement.SetTemporary(0, candidate);
                candidate = null;
                throw new AggregateException(
                    "Detached fallback creation and native layout cleanup both failed.",
                    primaryFailure,
                    cleanupFailure);
            }
            throw;
        }
    }

    private static string CreateText(VectorSceneBox box, MarkdownLayoutContext context, bool failed)
    {
        string? fallback = null;
        string source = context.SourceMap.SourceText;
        SourceSpan range = box.SourceRange;
        if (range.Length > 0 && source.Length > 0)
        {
            int start = Math.Clamp(range.Start, 0, source.Length);
            int length = Math.Clamp(range.Length, 0, source.Length - start);
            if (length > 0)
                fallback = source.Substring(start, Math.Min(length, MaximumCharacters));
        }

        if (string.IsNullOrWhiteSpace(fallback))
            fallback = box.SemanticText;
        if (string.IsNullOrWhiteSpace(fallback))
            fallback = box.AccessibilityName;
        fallback = string.IsNullOrWhiteSpace(fallback)
            ? string.Empty
            : Limit(fallback.Trim(), MaximumCharacters);
        if (!failed)
            return fallback;

        string status = context.ResolveString(
            MarkdownStringKeys.EmbeddedContentUnavailable,
            MarkdownLocalizedStrings.EmbeddedContentUnavailable);
        status = Limit(string.IsNullOrWhiteSpace(status)
            ? MarkdownLocalizedStrings.EmbeddedContentUnavailable
            : status.Trim(), MaximumCharacters / 2);
        int sourceBudget = MaximumCharacters - status.Length - Environment.NewLine.Length;
        return sourceBudget > 0 && fallback.Length > 0
            ? string.Concat(status, Environment.NewLine, Limit(fallback, sourceBudget))
            : status;
    }

    private static string Limit(string text, int maximumLength)
    {
        if (text.Length <= maximumLength)
            return text;
        return maximumLength <= 1 ? text[..maximumLength] : text[..(maximumLength - 1)] + "…";
    }
}
