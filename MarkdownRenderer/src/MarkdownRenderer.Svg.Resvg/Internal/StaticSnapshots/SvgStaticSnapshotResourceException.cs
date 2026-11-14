using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;

namespace MarkdownRenderer.Svg.Resvg.Internal.StaticSnapshots;

internal sealed class SvgStaticSnapshotResourceException : Exception
{
    internal const string OutputBytesReason = "static-snapshot-output-bytes";
    internal const string ElementDepthReason = "element-depth";
    internal const string ElementCountReason = "element-count";
    internal const string AttributeCountReason = "attribute-count";

    internal SvgStaticSnapshotResourceException()
        : base("The static SVG snapshot exceeds the configured source-byte ceiling.")
    {
        Reason = OutputBytesReason;
    }

    internal SvgStaticSnapshotResourceException(string reason)
        : base("The static SVG snapshot exceeds a configured structural resource ceiling.")
    {
        Reason = reason;
    }

    internal string Reason { get; }
}
