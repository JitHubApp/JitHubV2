using MarkdownRenderer;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Performance;

const string source = "# Native Markdown\n\n- [x] accessible\n\n| Pack | State |\n|---|---|\n| HTML | safe |\n\n<span>native</span>";

MarkdownEngine strictGfm = new MarkdownEngineBuilder()
    .UseGitHubFlavoredMarkdown()
    .Build();
MarkdownEngine githubReadme = new MarkdownEngineBuilder()
    .UseGitHubReadme()
    .Build();

var strictDocument = await strictGfm.ParseAsync(source).ConfigureAwait(false);
var githubDocument = await githubReadme.ParseAsync(source).ConfigureAwait(false);
SafeHtmlCapabilityDescriptor safeHtml = SafeHtmlFeature.Capabilities;
using var performanceSession = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);

if (strictDocument.GetHeadings().Count != 1 ||
    githubDocument.GetHeadings().Count != 1 ||
    strictGfm.Profile.Id != MarkdownProfiles.GfmStrict.Id ||
    githubReadme.Profile.Id != MarkdownProfiles.GitHubReadme.Id ||
    safeHtml.ExecutesScript ||
    safeHtml.AppliesCssLayout ||
    safeHtml.ExposesBrowserDom ||
    safeHtml.HasFilesystemAccess ||
    safeHtml.HasDirectNetworkAccess ||
    safeHtml.LinkRouting != SafeHtmlExternalResourceRouting.HostMediated ||
    safeHtml.ImageRouting != SafeHtmlExternalResourceRouting.HostMediated ||
    !safeHtml.UsesHalfOpenUtf16SourceRanges ||
    performanceSession.GetSnapshot().SourceCacheBytes != 0)
{
    return 1;
}

Console.WriteLine(
    $"{strictGfm.Profile.Id};{githubReadme.Profile.Id};safe-html-v{safeHtml.ContractVersion}");
return 0;
