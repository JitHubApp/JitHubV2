using System;

namespace MarkdownRenderer.Parsing.Internal.SafeHtml;

/// <summary>
/// Successful tag scans advance the caller beyond every inspected
/// character. Only malformed candidates can make those scans overlap, so
/// cap their aggregate work at twice the admitted input length. This keeps
/// worst-case parser work linear without weakening any existing input,
/// node, depth, attribute, or tag ceiling.
/// </summary>
internal struct SafeHtmlScanBudget
{
    private long _remaining;

    internal SafeHtmlScanBudget(int inputLength)
    {
        _remaining = Math.Max(1L, 2L * Math.Max(0, inputLength));
        IsExhausted = false;
    }

    internal bool IsExhausted { get; private set; }

    internal bool TryConsume()
    {
        if (_remaining <= 0)
        {
            IsExhausted = true;
            return false;
        }

        _remaining--;
        return true;
    }
}
