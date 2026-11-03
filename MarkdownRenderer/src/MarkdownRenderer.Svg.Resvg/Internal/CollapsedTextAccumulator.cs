using System.Linq;
using System.Text;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed class CollapsedTextAccumulator(CancellationToken cancellationToken)
{
    private const int MaximumLength = 16_384;
    private readonly StringBuilder _builder = new(capacity: 256);
    private bool _pendingSpace;
    private int _charactersUntilCancellationCheck = SvgCssScanner.CssCancellationCheckInterval;

    internal bool IsTooLong { get; private set; }

    internal bool Append(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return true;

        foreach (char character in value)
        {
            CheckCancellation();
            if (char.IsWhiteSpace(character))
            {
                _pendingSpace = _builder.Length > 0;
                continue;
            }

            int additionalLength = 1 + (_pendingSpace ? 1 : 0);
            if (_builder.Length > MaximumLength - additionalLength)
            {
                IsTooLong = true;
                return false;
            }

            if (_pendingSpace)
            {
                _builder.Append(' ');
                _pendingSpace = false;
            }
            _builder.Append(character);
        }

        return true;
    }

    internal bool AppendBoundary() => Append(" ");

    internal void ThrowIfCancellationRequested()
    {
        cancellationToken.ThrowIfCancellationRequested();
    }

    public override string ToString()
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _builder.ToString();
    }

    private void CheckCancellation()
    {
        if (--_charactersUntilCancellationCheck == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _charactersUntilCancellationCheck = SvgCssScanner.CssCancellationCheckInterval;
        }
    }
}
