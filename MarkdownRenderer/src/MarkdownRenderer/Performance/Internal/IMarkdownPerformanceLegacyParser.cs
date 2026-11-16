using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Parsing.Internal;

namespace MarkdownRenderer.Performance.Internal;

internal interface IMarkdownPerformanceLegacyParser
{
    Task<ParsedMarkdown?> ParseLegacyMarkdownAsync(
        string source,
        MarkdownExtensionRegistry registry,
        CancellationToken cancellationToken);
}
