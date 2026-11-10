using System;
using System.Numerics;
using System.Runtime.ExceptionServices;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Vectors.Drawing;
using MarkdownRenderer.Performance;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;

namespace MarkdownRenderer.Layout.Internal.Vectors;

/// <summary>
/// Bounded text fallback for vector scenes that could not acquire bounded
/// native drawing resources. The two text-direction descriptors belong to the
/// admission that deferred the scene
/// (<see cref="VectorNativeResourceAdmission.CapacityFallbackFormats"/>);
/// individual scenes retain bounded source/semantic strings, not
/// CanvasTextLayout objects.
/// </summary>
internal static class VectorSceneCapacityFallback
{
    internal const int MaximumCharacters = 256;
    private const double MinimumTextScale = 0.5d;
    private const double MaximumTextScale = 3d;

    internal static VectorNativeResourceAdmission? ResolveNativeAdmission(
        MarkdownLayoutContext context,
        VectorNativeResourceAdmission? explicitAdmission)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (explicitAdmission is not null)
            return explicitAdmission;

        IMarkdownPerformanceSessionInternal? session =
            context.PerformanceSession as IMarkdownPerformanceSessionInternal;
        return session is not null &&
            !session.IsDisposed &&
            context.UseProgressiveNativeSceneResidency &&
            session.Options.DeferOffscreenScenes
                ? session.VectorNativeAdmission
                : null;
    }

    /// <summary>
    /// Gets the fallback descriptor <paramref name="admission"/> prepared for
    /// <paramref name="flowDirection"/>. A scene without an admission was never
    /// capacity-deferred, so it has no fallback descriptor.
    /// </summary>
    internal static bool TryGetFormat(
        VectorNativeResourceAdmission? admission,
        FlowDirection flowDirection,
        out CanvasTextFormat format)
    {
        if (admission is null)
        {
            format = null!;
            return false;
        }

        return admission.CapacityFallbackFormats.TryGetFormat(flowDirection, out format);
    }

    /// <summary>
    /// Draws with the immutable shared text descriptor while honoring the
    /// platform text-scale factor. The scaled transform is local to this draw;
    /// the inverse-sized layout rectangle keeps its clipping and wrapping
    /// anchored to the original scene bounds. This path creates no retained
    /// CanvasTextLayout or new format descriptor. DrawText's transient native
    /// text work is bounded by MaximumCharacters and still requires profiling.
    /// </summary>
    internal static void DrawTextWithScale(
        CanvasDrawingSession drawingSession,
        string text,
        Rect bounds,
        Color foreground,
        CanvasTextFormat format,
        double textScaleFactor)
    {
        ArgumentNullException.ThrowIfNull(drawingSession);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(format);

        float scale = (float)Math.Clamp(
            double.IsFinite(textScaleFactor) ? textScaleFactor : 1d,
            MinimumTextScale,
            MaximumTextScale);
        if (scale == 1f)
        {
            drawingSession.DrawText(text, bounds, foreground, format);
            return;
        }

        Matrix3x2 originalTransform = drawingSession.Transform;
        float inverseScale = 1f / scale;
        var textBounds = new Rect(
            bounds.X,
            bounds.Y,
            bounds.Width * inverseScale,
            bounds.Height * inverseScale);
        Exception? drawFailure = null;
        try
        {
            // Match vector-scene transform composition: local coordinates are
            // scaled before the caller's existing viewport/DPI transform.
            drawingSession.Transform = Matrix3x2.CreateScale(
                scale,
                new Vector2((float)bounds.X, (float)bounds.Y)) * originalTransform;
            drawingSession.DrawText(text, textBounds, foreground, format);
        }
        catch (Exception exception)
        {
            drawFailure = exception;
            throw;
        }
        finally
        {
            Exception? transformRestoreFailure = null;
            try
            {
                drawingSession.Transform = originalTransform;
            }
            catch (Exception exception)
            {
                transformRestoreFailure = exception;
            }

            if (transformRestoreFailure is not null)
            {
                if (drawFailure is not null)
                {
                    throw new AggregateException(
                        "Capacity fallback painting and transform restoration both failed.",
                        drawFailure,
                        transformRestoreFailure);
                }

                ExceptionDispatchInfo.Capture(transformRestoreFailure).Throw();
            }
        }
    }

    internal static string CreateBlockText(
        MarkdownLayoutContext context,
        SourceSpan sourceRange,
        string? semanticText,
        string? accessibilityName,
        bool failed)
    {
        ArgumentNullException.ThrowIfNull(context);
        ReadOnlySpan<char> fallback = GetSourceCaption(context, sourceRange);
        if (fallback.IsEmpty)
            fallback = (semanticText ?? string.Empty).AsSpan().Trim();
        if (fallback.IsEmpty)
            fallback = (accessibilityName ?? string.Empty).AsSpan().Trim();
        string boundedFallback = Limit(fallback, MaximumCharacters);
        string status = context.ResolveString(
            MarkdownStringKeys.EmbeddedContentUnavailable,
            MarkdownLocalizedStrings.EmbeddedContentUnavailable);
        status = Limit((status ?? string.Empty).AsSpan().Trim(), MaximumCharacters / 2);
        if (status.Length == 0)
            status = MarkdownLocalizedStrings.EmbeddedContentUnavailable;

        if (boundedFallback.Length == 0)
            boundedFallback = status;
        if (!failed)
            return boundedFallback;
        if (string.Equals(boundedFallback, status, StringComparison.Ordinal))
            return status;

        int sourceBudget = MaximumCharacters - status.Length - Environment.NewLine.Length;
        if (sourceBudget <= 0 || boundedFallback.Length == 0)
            return Limit(status.AsSpan(), MaximumCharacters);

        string boundedCaption = Limit(boundedFallback.AsSpan(), sourceBudget);
        return string.Concat(status, Environment.NewLine, boundedCaption);
    }

    internal static string CreateInlineText(
        MarkdownLayoutContext context,
        string? semanticText,
        string? accessibilityName)
    {
        ArgumentNullException.ThrowIfNull(context);
        ReadOnlySpan<char> text = (semanticText ?? string.Empty).AsSpan().Trim();
        if (text.IsEmpty)
            text = (accessibilityName ?? string.Empty).AsSpan().Trim();
        if (text.IsEmpty)
        {
            string unavailable = context.ResolveString(
                MarkdownStringKeys.EmbeddedContentUnavailable,
                MarkdownLocalizedStrings.EmbeddedContentUnavailable);
            text = (unavailable ?? string.Empty).AsSpan().Trim();
        }
        return Limit(text, MaximumCharacters);
    }

    private static ReadOnlySpan<char> GetSourceCaption(
        MarkdownLayoutContext context,
        SourceSpan sourceRange)
    {
        string source = context.SourceMap.SourceText;
        if (sourceRange.Length <= 0 || source.Length == 0)
            return ReadOnlySpan<char>.Empty;

        int start = Math.Clamp(sourceRange.Start, 0, source.Length);
        int length = Math.Clamp(sourceRange.Length, 0, source.Length - start);
        return length > 0 ? source.AsSpan(start, length).Trim() : ReadOnlySpan<char>.Empty;
    }

    private static string Limit(ReadOnlySpan<char> text, int maximumLength)
    {
        if (maximumLength <= 0 || text.IsEmpty)
            return string.Empty;
        if (text.Length <= maximumLength)
            return text.ToString();

        // Reserve one UTF-16 code unit for the ellipsis. If the boundary would
        // split a valid surrogate pair, retreat one more code unit first.
        int prefixLength = maximumLength - 1;
        if (prefixLength > 0 &&
            char.IsHighSurrogate(text[prefixLength - 1]) &&
            char.IsLowSurrogate(text[prefixLength]))
        {
            prefixLength--;
        }

        return string.Concat(text[..prefixLength], "…");
    }
}
