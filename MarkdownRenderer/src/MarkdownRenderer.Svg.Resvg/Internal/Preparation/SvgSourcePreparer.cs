using System.Security.Cryptography;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Svg.Resvg.Internal.StaticSnapshots;

namespace MarkdownRenderer.Svg.Resvg.Internal.Preparation;

/// <summary>
/// The host-side source pipeline behind
/// <see cref="ResvgMarkdownSvgRenderer.PrepareSource"/>: inert-DOCTYPE
/// neutralization, the authored-source resource budget, static snapshotting
/// and revalidation of a rewritten snapshot.
/// </summary>
internal static class SvgSourcePreparer
{
    /// <summary>Validates, snapshots and revalidates one host source.</summary>
    /// <param name="source">The authored SVG bytes.</param>
    /// <param name="maxSourceBytes">The renderer's configured source ceiling.</param>
    /// <param name="cancellationToken">Cancels preparation.</param>
    /// <returns>The admitted static snapshot, or the rejection.</returns>
    internal static MarkdownSvgSourcePreparation Prepare(
        byte[] source,
        int maxSourceBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] prepared;
        SvgResourceBudgetResult budget;
        MarkdownSvgSourceInfo? preparedInfo = null;
        try
        {
            // An inert W3C SVG DOCTYPE (published public/system pair,
            // no internal subset, UTF-8) is replaced by equal-length
            // whitespace in a copy. Every later stage, including the direct
            // preflight and the worker, still rejects every DTD it sees.
            byte[] authored = SvgInertDoctype.Neutralize(source, cancellationToken);

            // Static snapshotting is a normalization step, not an admission
            // policy. Validate the authored source first so unsupported active
            // content cannot disappear while its remaining artwork is admitted.
            // This also applies the existing bounded recursive policy to nested
            // SVG data before any rewrite can change that closure.
            SvgResourceBudgetResult originalBudget = SvgResourceBudget.ValidateWithMetadata(
                authored,
                cancellationToken,
                out MarkdownSvgSourceInfo? originalInfo);
            cancellationToken.ThrowIfCancellationRequested();
            if (!originalBudget.Accepted)
                return RejectPreparedSource(source, originalBudget.Reason ?? "invalid-content");

            // The lowered source policy applies to sanitized output, not raw
            // bytes. The hard source ceiling and active-content policy have
            // already been enforced against the original source above.
            prepared = SvgStaticSnapshot.Create(authored, cancellationToken, maxSourceBytes);
            if (prepared.Length > maxSourceBytes)
            {
                budget = SvgResourceBudgetResult.Reject("input-bytes");
            }
            else if (ReferenceEquals(prepared, authored))
            {
                // The original validation is authoritative when normalization
                // leaves the source unchanged; reuse its matching metadata and
                // avoid a second complete resource walk on the common path.
                budget = SvgResourceBudgetResult.Success;
                preparedInfo = originalInfo;
            }
            else
            {
                budget = SvgResourceBudget.ValidateWithMetadata(
                    prepared,
                    cancellationToken,
                    out preparedInfo);
            }
        }
        catch (SvgStaticSnapshotResourceException exception)
        {
            // Stop serialization before an over-budget buffer is allocated.
            // Never return a partial snapshot or retry with executable input.
            return RejectPreparedSource(source, exception.Reason);
        }
        if (budget.Accepted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // An accepted validation always reads the source information.
            return MarkdownSvgSourcePreparation.Admit(prepared, preparedInfo!);
        }

        return RejectPreparedSource(source, budget.Reason ?? "invalid-content");
    }

    private static MarkdownSvgSourcePreparation RejectPreparedSource(byte[] source, string reason)
    {
        if (SvgPreflightAuditEvents.Log.IsEnabled())
        {
            SvgPreflightAuditEvents.Log.Rejected(
                reason,
                source.Length,
                Convert.ToHexString(SHA256.HashData(source)));
        }
        MarkdownSvgFailureReason failureReason = IsResourceLimitReason(reason)
            ? MarkdownSvgFailureReason.ResourceLimitExceeded
            : MarkdownSvgFailureReason.UnsupportedContent;
        return MarkdownSvgSourcePreparation.Reject(
            failureReason,
            $"The SVG was rejected by host preflight ({reason}).");
    }

    private static bool IsResourceLimitReason(string reason) =>
        reason.Contains("limit", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("budget", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("large", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("depth", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("count", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("bytes", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("complexity", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("size", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("length", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("deadline", StringComparison.OrdinalIgnoreCase) ||
        reason.Equals("embedded-image-data", StringComparison.OrdinalIgnoreCase);
}
