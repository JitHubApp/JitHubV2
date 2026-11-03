using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal readonly struct SvgResourceBudgetResult
{
    public SvgResourceBudgetResult(bool Accepted, string? Reason)
    {
        this.Accepted = Accepted;
        this.Reason = Reason;
    }

    public bool Accepted { get; }
    public string? Reason { get; }

    public static SvgResourceBudgetResult Success => new(true, null);

    public static SvgResourceBudgetResult Reject(string reason) => new(false, reason);
}
