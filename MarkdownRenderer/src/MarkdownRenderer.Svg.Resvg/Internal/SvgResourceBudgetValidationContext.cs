using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed class SvgResourceBudgetValidationContext
{
    private readonly long _started;
    private readonly CancellationToken _cancellationToken;

    public SvgResourceBudgetValidationContext(long started, CancellationToken cancellationToken)
    {
        _started = started;
        _cancellationToken = cancellationToken;
    }

    public int ElementCount { get; set; }
    public int AttributeCount { get; set; }
    public int FilterPrimitiveCount { get; set; }
    public int TextNodeCount { get; set; }
    public long TextCharacters { get; set; }
    public long PathCharacters { get; set; }
    public long TransformCharacters { get; set; }
    public long EmbeddedCompressedBytes { get; set; }
    public long DecodedEmbeddedImageBytes { get; set; }
    public long ComplexityUnits { get; set; }
    public HashSet<string> SeenEmbeddedResources { get; } = new(StringComparer.Ordinal);

    public void ThrowIfStopped()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(_started) > SvgResourceBudget.ValidationDeadline)
            throw new SvgValidationDeadlineException();
    }
}
