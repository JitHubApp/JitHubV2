using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.Performance;

internal interface IMarkdownPerformanceLegacyParser
{
    Task<ParsedMarkdown?> ParseLegacyMarkdownAsync(
        string source,
        MarkdownExtensionRegistry registry,
        CancellationToken cancellationToken);
}
