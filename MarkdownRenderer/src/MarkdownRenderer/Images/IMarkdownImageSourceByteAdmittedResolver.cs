using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Images;

/// <summary>
/// Optional resolver capability used by a progressive performance session.
/// Resolvers without this interface continue to use <see cref="IMarkdownImageResolver.ResolveAsync"/>.
/// </summary>
public interface IMarkdownImageSourceByteAdmittedResolver : IMarkdownImageResolver
{
    /// <summary>
    /// Resolves an image while admitting each actual source-buffer lifetime.
    /// A speculative request whose required single buffer exceeds
    /// <see cref="IMarkdownImageSourceByteAdmission.MaximumReservationBytes"/>
    /// is deferred by <see cref="MarkdownImageSourceDeferredException"/>;
    /// a later visible resolution starts independently and remains authoritative.
    /// </summary>
    ValueTask<MarkdownImageResolution> ResolveWithSourceByteAdmissionAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken);
}
